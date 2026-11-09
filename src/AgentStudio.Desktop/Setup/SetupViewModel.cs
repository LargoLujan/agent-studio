using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Setup;

/// <summary>
/// The configurator of a project (docs/17 §4.6, D-26 and D-27; ADR-0055): a wizard of seven steps and a summary. Each step shows
/// what the repository and the project say, proposes without network and without an agent, lets the person change the proposal
/// and can be skipped. Nothing is written until «Aplicar», and then each part goes through the gate of its own screen
/// (<see cref="ProjectSetupService.ApplyAsync"/>): the template by the roster, whose hash withdraws the approval of the plan;
/// the profile and any capability of the fixed barrier by Windows Hello; the skills as decisions of the person.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every text comes from <c>SetupStrings</c> through <see cref="IUiTextProvider"/> (docs/17 D-47); the layers below give codes.</item>
/// <item>What is on by default is only what the person reviews anyway: the template additions and a profile that would change.
/// A model, a skill, the brief and the budget are off until the person takes them.</item>
/// <item>After «Aplicar» the owner is told (<see cref="Applied"/>): if the template changed (<see cref="SetupApplyResult.RosterChanged"/>)
/// it has to read it again and withdraw the approval of the plan that waits.</item>
/// </list>
/// </remarks>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ProjectSetupService _service;
    private readonly IUiTextProvider? _texts;
    private ProjectContext? _project;
    private SetupProposal? _proposal;
    private string _initialBrief = string.Empty;
    private ProjectRunOptions _currentBudget = ProjectRunOptions.Defaults;
    private bool _loading;

    public SetupViewModel(ProjectSetupService service, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _texts = texts;
        Steps =
        [
            .. Enum.GetValues<SetupStep>().Select(s => new SetupStepViewModel(s, SetupTexts.Step(Text, s), s is not (SetupStep.Project or SetupStep.Summary))),
        ];
        Sizes = [.. Enum.GetValues<SetupSize>().Select(s => new SetupSizeChoice(s, SetupTexts.Size(Text, s)))];
        _selectedSize = Sizes[0];
        Steps[0].IsCurrent = true;
        ResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
        SummaryLines.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsSummaryEmpty));
            OnPropertyChanged(nameof(CanApplyNow));
        };
        foreach (var step in Steps.Where(s => s.CanSkip))
        {
            step.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SetupStepViewModel.IsIncluded))
                {
                    OnInclusionChanged();
                }
            };
        }
    }

    private IUiTextProvider Text => _texts ?? Texts.Current;

    /// <summary>Raised after «Aplicar» with what each part did: the owner reads the template again when it changed.</summary>
    public event EventHandler<SetupApplyResult>? Applied;

    /// <summary>The eight steps, in order.</summary>
    public IReadOnlyList<SetupStepViewModel> Steps { get; }

    /// <summary>The step on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirst), nameof(IsLast), nameof(CurrentTitle))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand))]
    private SetupStep _step;

    public bool IsFirst => Step == SetupStep.Project;

    public bool IsLast => Step == SetupStep.Summary;

    public string CurrentTitle => SetupTexts.Step(Text, Step);

    /// <summary>The project the configurator was opened for; null when none is open.</summary>
    public bool HasProject => _project is not null;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What the screen says while it works, or empty.</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>Why something could not be done, or empty.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>What the person should know after «Aplicar» (the template changed, the entry of the registry), or empty.</summary>
    [ObservableProperty]
    private string _notice = string.Empty;

    // ---- step 1: the project ----

    /// <summary>What the repository says: stack, size, branch, changes, remotes and main folders.</summary>
    public ObservableCollection<string> ProjectLines { get; } = [];

    // ---- step 2: the brief ----

    /// <summary>The brief the person writes, over a skeleton of sections when the project has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BriefCharacters))]
    private string _briefText = string.Empty;

    public string BriefCharacters => Text.Format("Setup_Brief_Characters", BriefText.Length);

    // ---- step 3: the team ----

    public IReadOnlyList<SetupSizeChoice> Sizes { get; }

    /// <summary>The size of the template; choosing another one proposes its template again.</summary>
    [ObservableProperty]
    private SetupSizeChoice _selectedSize;

    public ObservableCollection<SetupAgentRowViewModel> TeamRows { get; } = [];

    // ---- step 4: the models ----

    public ObservableCollection<SetupModelRowViewModel> ModelRows { get; } = [];

    /// <summary>Whether any agent has a recommendation it does not follow yet.</summary>
    public bool HasRecommendations => ModelRows.Any(r => r.CanAccept);

    // ---- step 5: the verification profile ----

    /// <summary>The profile in force and the one proposed, with the command of each step.</summary>
    public ObservableCollection<string> VerificationLines { get; } = [];

    /// <summary>«Sin perfil propuesto», «Es el que ya tienes»… or empty.</summary>
    [ObservableProperty]
    private string _verificationStatus = string.Empty;

    // ---- step 6: the skills ----

    public ObservableCollection<SetupSkillRowViewModel> SkillRows { get; } = [];

    // ---- step 7: the budget and the work without a person ----

    [ObservableProperty]
    private string _maxAgentRunsText = string.Empty;

    [ObservableProperty]
    private string _maxParallelTasksText = string.Empty;

    /// <summary>The time per attempt in minutes; empty is the engine's own.</summary>
    [ObservableProperty]
    private string _timeMinutesText = string.Empty;

    /// <summary>Why the values of the budget cannot be used, or empty.</summary>
    [ObservableProperty]
    private string _budgetProblem = string.Empty;

    // ---- the summary and the result ----

    /// <summary>Everything that changes, with what is there now next to it.</summary>
    public ObservableCollection<string> SummaryLines { get; } = [];

    /// <summary>What each part did after «Aplicar».</summary>
    public ObservableCollection<string> ResultLines { get; } = [];

    /// <summary>The summary has nothing to apply (every step skipped or already as proposed), once a project is read.</summary>
    public bool IsSummaryEmpty => _project is not null && SummaryLines.Count == 0;

    /// <summary>There is a result to show after «Aplicar».</summary>
    public bool HasResults => ResultLines.Count > 0;

    /// <summary>Whether the profile or a capability of the barrier is going to ask Windows Hello.</summary>
    [ObservableProperty]
    private bool _willAskHello;

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The step as a list item, for a screen that shows its include switch.</summary>
    public SetupStepViewModel StepOf(SetupStep step) => Steps[(int)step];

    /// <summary>Reads the project and fills every step with its proposal. Writes nothing.</summary>
    public async Task LoadAsync(ProjectContext? project)
    {
        _project = project;
        OnPropertyChanged(nameof(HasProject));
        ErrorMessage = string.Empty;
        if (project is null)
        {
            ErrorMessage = Text.Get("Setup_No_Project");
            return;
        }

        await ProposeAsync(null).ConfigureAwait(true);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private async Task ProposeAsync(SetupSize? size)
    {
        if (_project is not { } project)
        {
            return;
        }

        IsBusy = true;
        BusyText = Text.Get("Setup_Loading");
        _loading = true;
        try
        {
            _proposal = await _service.ProposeAsync(project, size, CancellationToken.None).ConfigureAwait(true);
            Fill(_proposal, keepBrief: size is not null);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = Text.Format("Setup_Load_Failed", ex.GetType().Name);
        }
        finally
        {
            _loading = false;
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void Fill(SetupProposal proposal, bool keepBrief)
    {
        var text = Text;
        FillProject(proposal.Project, text);

        if (!keepBrief)
        {
            _initialBrief = proposal.Brief.Current?.Text ?? Skeleton(proposal.Brief.SkeletonSections);
            BriefText = _initialBrief;
            StepOf(SetupStep.Brief).IsIncluded = false;
        }

        TeamRows.Clear();
        foreach (var change in proposal.Team.Changes)
        {
            var kind = change.IsAdded ? "Setup_Team_Added" : change.IsChange ? "Setup_Team_Changed" : "Setup_Team_Same";
            TeamRows.Add(new SetupAgentRowViewModel(change, change.After.Name, SetupTexts.AgentLine(text, change.After), text.Get(kind)));
        }

        SelectedSize = Sizes.First(s => s.Size == proposal.Team.Size);
        StepOf(SetupStep.Team).IsIncluded = proposal.Team.Changes.Any(c => c.IsChange);

        ModelRows.Clear();
        foreach (var model in proposal.Models)
        {
            ModelRows.Add(ModelRow(model, text));
        }

        OnPropertyChanged(nameof(HasRecommendations));
        StepOf(SetupStep.Models).IsIncluded = false;

        FillVerification(proposal.Verification, text);

        SkillRows.Clear();
        var names = proposal.Team.Changes.ToDictionary(c => c.After.Id, c => c.After.Name);
        foreach (var skill in proposal.Skills)
        {
            SkillRows.Add(new SetupSkillRowViewModel(skill, names.GetValueOrDefault(skill.Agent, skill.Agent.Value), string.Join(" · ", skill.Reasons), skill.AlreadyActive));
        }

        StepOf(SetupStep.Skills).IsIncluded = false;
        foreach (var row in SkillRows.Where(r => !r.AlreadyActive))
        {
            row.PropertyChanged += (_, _) =>
            {
                StepOf(SetupStep.Skills).IsIncluded = SkillRows.Any(r => r.IsChosen && !r.AlreadyActive);
                OnInclusionChanged();
            };
        }

        _currentBudget = proposal.Budget;
        MaxAgentRunsText = proposal.Budget.MaxAgentRuns.ToString(CultureInfo.InvariantCulture);
        MaxParallelTasksText = proposal.Budget.MaxParallelTasks.ToString(CultureInfo.InvariantCulture);
        TimeMinutesText = proposal.Budget.TimePerAttempt is { } time ? ((int)time.TotalMinutes).ToString(CultureInfo.InvariantCulture) : string.Empty;
        StepOf(SetupStep.Budget).IsIncluded = false;
        BudgetProblem = string.Empty;

        if (proposal.Problems.Count > 0)
        {
            ErrorMessage = string.Join(" ", proposal.Problems.Select(text.Render));
        }

        RefreshSummary();
    }

    private void FillProject(SetupProjectProposal project, IUiTextProvider text)
    {
        ProjectLines.Clear();
        ProjectLines.Add(text.Format("Setup_Project_Stack", SetupTexts.Stack(text, project.Stack)));
        ProjectLines.Add(text.Format(project.FilesTruncated ? "Setup_Project_Size_Truncated" : "Setup_Project_Size", SetupTexts.Size(text, project.Size), project.Files));
        ProjectLines.Add(project.Branch is null
            ? text.Get("Setup_Project_Branch_None")
            : text.Format("Setup_Project_Branch", project.Branch, project.HeadShort ?? "-"));
        ProjectLines.Add(text.Get(project.HasUncommittedChanges ? "Setup_Project_Uncommitted" : "Setup_Project_Clean"));
        foreach (var remote in project.Remotes)
        {
            ProjectLines.Add(text.Format("Setup_Project_Remote", remote.Name, remote.Url));
        }

        if (project.MainFolders.Count > 0)
        {
            ProjectLines.Add(text.Format("Setup_Project_Folders", string.Join(", ", project.MainFolders)));
        }

        ProjectLines.Add(text.Get("Setup_Project_Nothing_Run"));
    }

    private SetupModelRowViewModel ModelRow(SetupModelProposal proposal, IUiTextProvider text)
    {
        var agent = proposal.Agent;
        var current = SetupTexts.Power(text, agent.Model.Model, agent.Effort);
        if (proposal.Recommendation is not { } recommendation)
        {
            return new SetupModelRowViewModel(proposal, agent.Name, current, null, text.Get("Setup_Models_None"), string.Empty);
        }

        var recommended = text.Format(
            "Setup_Models_Recommended",
            recommendation.Model,
            SetupTexts.Tier(text, recommendation.Tier),
            SetupTexts.Effort(text, recommendation.Effort));
        var why = proposal.Reason is { } reason ? text.Format("Setup_Models_Why", text.Render(reason)) : null;
        var row = new SetupModelRowViewModel(proposal, agent.Name, current, recommended, why, proposal.Follows ? text.Get("Setup_Models_Follows") : string.Empty);
        row.PropertyChanged += (_, _) =>
        {
            StepOf(SetupStep.Models).IsIncluded = ModelRows.Any(r => r.IsAccepted);
            OnInclusionChanged();
        };
        return row;
    }

    private void FillVerification(SetupVerificationProposal verification, IUiTextProvider text)
    {
        VerificationLines.Clear();
        if (verification.Current is { } current)
        {
            VerificationLines.Add(text.Format("Setup_Verification_Current", current.Name, current.ContentHash[..12]));
            VerificationLines.AddRange(current.Steps.Select(s => Command(text, s)));
        }
        else
        {
            VerificationLines.Add(text.Get("Setup_Verification_Current_None"));
        }

        if (verification.Proposed is { } proposed)
        {
            VerificationLines.Add(text.Format("Setup_Verification_Proposed", proposed.Name, proposed.ContentHash[..12]));
            VerificationLines.AddRange(proposed.Steps.Select(s => Command(text, s)));
        }

        VerificationStatus = verification.CurrentUnreadable ? text.Get("Setup_Verification_Unreadable")
            : verification.Proposed is null ? text.Render(UserText.Of(verification.NoProposalCode ?? "setup.verification.no-stack"))
            : verification.WouldChange ? text.Get("Setup_Verification_Hello_Note")
            : text.Get("Setup_Verification_Same");
        StepOf(SetupStep.Verification).IsIncluded = verification.WouldChange;
    }

    private static string Command(IUiTextProvider text, VerificationStep step) =>
        text.Format("Setup_Verification_Step_Line", step.Id, step.Executable + " " + string.Join(' ', step.Arguments));

    private string Skeleton(IReadOnlyList<string> sections) =>
        string.Join("\n\n", sections.Select(code => "## " + Text.Render(UserText.Of(code)) + "\n")) + "\n";

    // ---- inclusion and summary ----

    partial void OnBriefTextChanged(string value)
    {
        if (!_loading)
        {
            StepOf(SetupStep.Brief).IsIncluded = !string.IsNullOrWhiteSpace(value) && !string.Equals(value, _initialBrief, StringComparison.Ordinal);
        }
    }

    partial void OnMaxAgentRunsTextChanged(string value) => BudgetEdited();

    partial void OnMaxParallelTasksTextChanged(string value) => BudgetEdited();

    partial void OnTimeMinutesTextChanged(string value) => BudgetEdited();

    private void BudgetEdited()
    {
        if (_loading)
        {
            return;
        }

        var parsed = ParseBudget(out var problem);
        BudgetProblem = problem is null ? string.Empty : Text.Render(UserText.Of(problem));
        StepOf(SetupStep.Budget).IsIncluded = parsed is not null && parsed != _currentBudget;
    }

    partial void OnSelectedSizeChanged(SetupSizeChoice value)
    {
        if (!_loading && _proposal is not null && value.Size != _proposal.Team.Size)
        {
            _ = ProposeAsync(value.Size);
        }
    }

    partial void OnStepChanged(SetupStep value)
    {
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Step == value;
        }

        if (value == SetupStep.Summary)
        {
            RefreshSummary();
        }
    }

    private void OnInclusionChanged()
    {
        OnPropertyChanged(nameof(HasRecommendations));
        RefreshSummary();
    }

    /// <summary>«Usar esta recomendación» for every agent that has one it does not follow.</summary>
    [RelayCommand]
    private void UseAllRecommended()
    {
        foreach (var row in ModelRows.Where(r => r.CanAccept))
        {
            row.IsAccepted = true;
        }

        StepOf(SetupStep.Models).IsIncluded = ModelRows.Any(r => r.IsAccepted);
    }

    /// <summary>The numbers of the budget as the person typed them; null with the code of what is wrong when they cannot be used.</summary>
    private ProjectRunOptions? ParseBudget(out string? problem)
    {
        problem = null;
        if (!int.TryParse(MaxAgentRunsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var runs))
        {
            problem = "setup.budget.agent-runs-out-of-range";
            return null;
        }

        if (!int.TryParse(MaxParallelTasksText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parallel))
        {
            problem = "setup.budget.parallel-tasks-out-of-range";
            return null;
        }

        TimeSpan? time = null;
        if (!string.IsNullOrWhiteSpace(TimeMinutesText))
        {
            if (!int.TryParse(TimeMinutesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes > (int)ProjectRunOptions.MaxTimePerAttempt.TotalMinutes)
            {
                problem = "setup.budget.time-out-of-range";
                return null;
            }

            time = TimeSpan.FromMinutes(minutes);
        }

        var options = new ProjectRunOptions(runs, parallel, time);
        problem = options.Validate() is { Count: > 0 } problems ? problems[0] : null;
        return problem is null ? options : null;
    }

    /// <summary>What the summary lists: each included step, with what is there now next to what it becomes.</summary>
    private void RefreshSummary()
    {
        SummaryLines.Clear();
        WillAskHello = false;
        if (_proposal is not { } proposal)
        {
            ApplyCommand.NotifyCanExecuteChanged();
            return;
        }

        var text = Text;
        if (StepOf(SetupStep.Brief).IsIncluded)
        {
            var before = proposal.Brief.Current is { } brief ? text.Format("Setup_Summary_Brief_Before", brief.Text.Length) : text.Get("Setup_Summary_Brief_None");
            SummaryLines.Add(Line(text, SetupStep.Brief, before, text.Format("Setup_Summary_Brief_After", BriefText.Trim().Length)));
        }

        var roster = SelectedRoster();
        if (roster is not null)
        {
            var current = proposal.Team.Changes.Where(c => !c.IsAdded).ToDictionary(c => c.After.Id, c => c.After);
            foreach (var agent in roster)
            {
                if (!current.TryGetValue(agent.Id, out var before))
                {
                    SummaryLines.Add(Line(text, SetupStep.Team, text.Get("Setup_Summary_Absent"), text.Format("Setup_Summary_Agent_Added", agent.Name, SetupTexts.AgentLine(text, agent))));
                }
                else if (RosterHash.Of([before]) != RosterHash.Of([agent]))
                {
                    SummaryLines.Add(Line(text, SetupStep.Models, $"{agent.Name}: {SetupTexts.Power(text, before.Model.Model, before.Effort)}", SetupTexts.Power(text, agent.Model.Model, agent.Effort)));
                }
            }

            WillAskHello |= SetupCapabilities.Granted([.. current.Values], roster).Count > 0;
        }

        if (StepOf(SetupStep.Verification).IsIncluded && proposal.Verification.Proposed is { } profile && proposal.Verification.WouldChange)
        {
            var before = proposal.Verification.Current is { } now ? text.Format("Setup_Summary_Profile", now.Name, now.ContentHash[..12]) : text.Get("Setup_Summary_Absent");
            SummaryLines.Add(Line(text, SetupStep.Verification, before, text.Format("Setup_Summary_Profile", profile.Name, profile.ContentHash[..12])));
            WillAskHello = true;
        }

        foreach (var skill in SkillRows.Where(r => StepOf(SetupStep.Skills).IsIncluded && r.IsChosen && !r.AlreadyActive))
        {
            SummaryLines.Add(Line(text, SetupStep.Skills, text.Get("Setup_Summary_Inactive"), $"{skill.SkillName}: {skill.AgentName}"));
        }

        if (StepOf(SetupStep.Budget).IsIncluded && ParseBudget(out _) is { } budget)
        {
            SummaryLines.Add(Line(text, SetupStep.Budget, Budget(text, _currentBudget), Budget(text, budget)));
        }

        ApplyCommand.NotifyCanExecuteChanged();
    }

    private static string Line(IUiTextProvider text, SetupStep step, string before, string after) =>
        text.Format("Setup_Summary_Line", SetupTexts.Step(text, step), before, after);

    private static string Budget(IUiTextProvider text, ProjectRunOptions options) =>
        text.Format(
            "Setup_Summary_Budget",
            options.MaxAgentRuns,
            options.MaxParallelTasks,
            options.TimePerAttempt is { } time ? text.Format("Setup_Summary_Minutes", (int)time.TotalMinutes) : text.Get("Setup_Summary_Engine_Time"));

    /// <summary>
    /// The template to store: the agents of step 3 when it is included (those the project already has otherwise) with the
    /// recommendation of each agent the person accepted in step 4; null when neither step has anything to apply.
    /// </summary>
    private IReadOnlyList<AgentDefinition>? SelectedRoster()
    {
        if (_proposal is not { } proposal)
        {
            return null;
        }

        var teamIncluded = StepOf(SetupStep.Team).IsIncluded;
        var accepted = StepOf(SetupStep.Models).IsIncluded
            ? ModelRows.Where(r => r.IsAccepted && r.Proposal.Recommendation is not null).ToDictionary(r => r.Proposal.Agent.Id, r => r.Proposal.Recommendation!)
            : [];
        if (!teamIncluded && accepted.Count == 0)
        {
            return null;
        }

        var agents = new List<AgentDefinition>();
        foreach (var change in proposal.Team.Changes)
        {
            if (change.IsAdded && !teamIncluded)
            {
                continue;
            }

            agents.Add(accepted.TryGetValue(change.After.Id, out var recommendation) ? SetupProposer.WithRecommendation(change.After, recommendation) : change.After);
        }

        // The service writes only the agents that differ from the stored template: an unchanged one is not news.
        return agents.Count > 0 ? agents : null;
    }

    // ---- navigation ----

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step = (SetupStep)((int)Step - 1);

    private bool CanGoBack() => !IsFirst && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => Step = (SetupStep)((int)Step + 1);

    private bool CanGoNext() => !IsLast && !IsBusy;

    /// <summary>Goes to a step of the list.</summary>
    [RelayCommand]
    private void GoTo(SetupStepViewModel? item)
    {
        if (item is not null && !IsBusy)
        {
            Step = item.Step;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    // ---- «Aplicar» ----

    /// <summary>Whether there is something to apply: a part is selected and what is selected can be used.</summary>
    public bool CanApplyNow => !IsBusy && _project is not null && SummaryLines.Count > 0 && (!StepOf(SetupStep.Budget).IsIncluded || ParseBudget(out _) is not null);

    private bool CanApply() => CanApplyNow;

    /// <summary>The selection the person reviewed, as the service takes it. A step that is skipped goes as null.</summary>
    public SetupSelection BuildSelection()
    {
        var profile = StepOf(SetupStep.Verification).IsIncluded && _proposal?.Verification.WouldChange == true ? _proposal.Verification.Proposed : null;
        var skills = StepOf(SetupStep.Skills).IsIncluded
            ? SkillRows.Where(r => r.IsChosen && !r.AlreadyActive).Select(r => new SetupSkillChoice(r.Agent, r.Skill)).ToList()
            : null;
        return new SetupSelection(
            StepOf(SetupStep.Brief).IsIncluded && !string.IsNullOrWhiteSpace(BriefText) ? BriefText : null,
            SelectedRoster(),
            profile,
            skills,
            StepOf(SetupStep.Budget).IsIncluded ? ParseBudget(out _) : null);
    }

    /// <summary>
    /// «Aplicar»: writes what is selected, each part by its usual gate. The profile and any capability of the fixed barrier ask
    /// Windows Hello first; whatever it does not confirm is reported as denied and nothing of it is written.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        var selection = BuildSelection();
        IsBusy = true;
        BusyText = Text.Get("Setup_Applying");
        ErrorMessage = string.Empty;
        Notice = string.Empty;
        try
        {
            var result = await _service.ApplyAsync(project, selection, CancellationToken.None).ConfigureAwait(true);
            ShowResult(result);
            Applied?.Invoke(this, result);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = Text.Format("Setup_Apply_Failed", ex.GetType().Name);
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }

        // The project as it is now: what was applied is no longer a proposal.
        var lines = ResultLines.ToList();
        var notice = Notice;
        await LoadAsync(project).ConfigureAwait(true);
        ResultLines.Clear();
        ResultLines.AddRange(lines);
        Notice = notice;
    }

    private void ShowResult(SetupApplyResult result)
    {
        var text = Text;
        ResultLines.Clear();
        foreach (var part in result.Parts.Where(p => p.Outcome != SetupPartOutcome.Skipped))
        {
            var step = SetupTexts.Step(text, part.Step);
            var outcome = SetupTexts.Outcome(text, part.Outcome);
            ResultLines.Add(part.Detail is { } detail ? text.Format("Setup_Result_Detail", step, outcome, text.Render(detail)) : text.Format("Setup_Result_Line", step, outcome));
        }

        var notes = new List<string>();
        if (result.RosterChanged)
        {
            notes.Add(text.Get("Setup_Result_Roster_Changed"));
        }

        if (result.Recorded)
        {
            notes.Add(text.Get("Setup_Result_Recorded"));
        }
        else if (result.RecordProblem is { } problem)
        {
            notes.Add(text.Render(problem));
        }

        Notice = string.Join(" ", notes);
    }
}

internal static class ObservableCollectionExtensions
{
    public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
