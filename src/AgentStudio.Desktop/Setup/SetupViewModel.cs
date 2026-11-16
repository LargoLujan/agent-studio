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
using AgentStudio.Domain.Evidence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Setup;

/// <summary>
/// The configurator of a project (docs/17 §4.6, D-26 and D-27; ADR-0055; docs/24 §3.4, wave 1): a wizard of seven steps and a
/// summary. Each step shows what the repository and the project say, proposes without network and without an agent, lets the
/// person change the proposal right there (the model and the effort of each agent, the commands of the check, the limits) and can
/// be skipped. Nothing is written until «Aplicar», and then each part goes through the gate of its own screen
/// (<see cref="ProjectSetupService.ApplyAsync"/>): the template by the roster, whose hash withdraws the approval of the plan; the
/// commands of the check and any capability of the fixed barrier by Windows Hello; the skills as decisions of the person.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every text comes from <c>SetupStrings</c> through <see cref="IUiTextProvider"/> (docs/17 D-47); the layers below give codes.</item>
/// <item>What is on by default is only what the person reviews anyway: the template additions and commands that would change.
/// Changing something in a step turns it on; a model, a skill, the brief and the limits are off until the person changes them.</item>
/// <item>The summary shows every step as it will be, with what is saved and what stays as it is (<see cref="SummaryBlocks"/>);
/// <see cref="SummaryLines"/> are only what changes, and «Aplicar» is offered while there is any. With nothing to save it offers
/// «Ir a trabajar».</item>
/// <item>After «Aplicar» the owner is told (<see cref="Applied"/>): if the template changed (<see cref="SetupApplyResult.RosterChanged"/>)
/// it has to read it again and withdraw the approval of the plan that waits. When every part went well it is asked to take the
/// person to work (<see cref="WorkRequested"/>), as «Ir a trabajar» does.</item>
/// <item>«Probarla ahora» runs the commands being edited once, confined, through <see cref="SetupCheckTrial"/>; it is offered only
/// when the shell composes it.</item>
/// </list>
/// </remarks>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ProjectSetupService _service;
    private readonly IUiTextProvider? _texts;
    private readonly SetupCheckTrial? _trial;
    private readonly Func<string, IReadOnlyList<SetupProviderLimit>>? _providerLimits;
    private ProjectContext? _project;
    private SetupProposal? _proposal;
    private string _initialBrief = string.Empty;
    private ProjectRunOptions _currentBudget = ProjectRunOptions.Defaults;
    private string _checkName = SetupChecks.OwnProfileName;
    private CancellationTokenSource? _trialStop;
    private bool _loading;

    /// <param name="trial">«Probarla ahora»; without it the button is not offered.</param>
    /// <param name="providerLimits">How many agents each program runs at once, for the root of a project; without it the line is not shown.</param>
    public SetupViewModel(ProjectSetupService service, IUiTextProvider? texts = null, SetupCheckTrial? trial = null, Func<string, IReadOnlyList<SetupProviderLimit>>? providerLimits = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _texts = texts;
        _trial = trial;
        _providerLimits = providerLimits;
        Steps =
        [
            .. Enum.GetValues<SetupStep>().Select(s => new SetupStepViewModel(s, SetupTexts.Step(Text, s), s is not (SetupStep.Project or SetupStep.Summary))),
        ];
        Sizes = [.. Enum.GetValues<SetupSize>().Select(s => new SetupSizeChoice(s, SetupTexts.Size(Text, s)))];
        _selectedSize = Sizes[0];
        Steps[0].IsCurrent = true;
        ResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
        TrialLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTrialLines));
        SummaryLines.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsSummaryEmpty));
            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(CanApplyNow));
            GoToWorkCommand.NotifyCanExecuteChanged();
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

    /// <summary>
    /// Raised by «Ir a trabajar», and after an «Aplicar» in which every part went well: the shell takes the person to the
    /// orchestrator of the project, once it has read what was applied.
    /// </summary>
    public event EventHandler<SetupWorkRequestedEventArgs>? WorkRequested;

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

    /// <summary>Whether any agent has a recommendation the row does not say yet.</summary>
    public bool HasRecommendations => ModelRows.Any(r => r.CanUseRecommendation);

    // ---- step 5: the check ----

    /// <summary>The commands of the check, one row each, as the person edits them.</summary>
    public ObservableCollection<SetupCheckRowViewModel> CheckRows { get; } = [];

    /// <summary>Whether the commands can be edited: never over a file of the person that cannot be used.</summary>
    [ObservableProperty]
    private bool _canEditChecks = true;

    /// <summary>The project has commands in force: «Volver a las que tienes ahora» is offered.</summary>
    public bool HasCurrentChecks => _proposal?.Verification.Current is not null && CanEditChecks;

    /// <summary>Commands were proposed for the stack: «Volver a las propuestas» is offered.</summary>
    public bool HasProposedChecks => _proposal?.Verification.Proposed is not null && CanEditChecks;

    /// <summary>«Son las que ya tienes», «Al aplicar se pedirá Windows Hello»… or empty.</summary>
    [ObservableProperty]
    private string _verificationStatus = string.Empty;

    /// <summary>Why the commands, as a whole, cannot be used; empty when they can or when a row already says why.</summary>
    [ObservableProperty]
    private string _checkProblem = string.Empty;

    /// <summary>«Probarla ahora» is composed.</summary>
    public bool HasTrial => _trial is not null;

    /// <summary>The commands are running in «Probarla ahora».</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TryChecksCommand), nameof(StopTrialCommand), nameof(ApplyCommand), nameof(GoToWorkCommand))]
    private bool _isTrying;

    /// <summary>What «Probarla ahora» says as a whole: running, it works, it does not, or why it could not run.</summary>
    [ObservableProperty]
    private string _trialStatus = string.Empty;

    /// <summary>The trial ended and every command went well; false when it did not; null before any or while it runs.</summary>
    [ObservableProperty]
    private bool? _trialPassed;

    /// <summary>How each command went, and the tests that failed.</summary>
    public ObservableCollection<string> TrialLines { get; } = [];

    public bool HasTrialLines => TrialLines.Count > 0;

    /// <summary>The last lines the commands wrote (untrusted, cleaned), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrialOutput))]
    private string _trialOutput = string.Empty;

    public bool HasTrialOutput => TrialOutput.Length > 0;

    // ---- step 6: the skills ----

    public ObservableCollection<SetupSkillRowViewModel> SkillRows { get; } = [];

    // ---- step 7: the limits ----

    [ObservableProperty]
    private string _maxAgentRunsText = string.Empty;

    [ObservableProperty]
    private string _maxParallelTasksText = string.Empty;

    /// <summary>The time per attempt in minutes; empty is the engine's own.</summary>
    [ObservableProperty]
    private string _timeMinutesText = string.Empty;

    /// <summary>Why the values of the limits cannot be used, or empty.</summary>
    [ObservableProperty]
    private string _budgetProblem = string.Empty;

    /// <summary>How many agents each program of the team runs at once at most, from the adapters; empty when it cannot be told.</summary>
    [ObservableProperty]
    private string _providerLimitsText = string.Empty;

    // ---- the summary and the result ----

    /// <summary>Everything that changes, with what is there now next to it: what «Aplicar» would write.</summary>
    public ObservableCollection<string> SummaryLines { get; } = [];

    /// <summary>Every step as it will be after «Aplicar», saved or as it is, with «Cambiar».</summary>
    public ObservableCollection<SetupSummaryBlock> SummaryBlocks { get; } = [];

    /// <summary>What each part did after «Aplicar».</summary>
    public ObservableCollection<string> ResultLines { get; } = [];

    /// <summary>The summary has nothing to apply (every step skipped or already as proposed), once a project is read.</summary>
    public bool IsSummaryEmpty => _project is not null && SummaryLines.Count == 0;

    /// <summary>There is something to apply: «Aplicar los cambios» is offered instead of «Ir a trabajar».</summary>
    public bool HasChanges => !IsSummaryEmpty;

    /// <summary>There is a result to show after «Aplicar».</summary>
    public bool HasResults => ResultLines.Count > 0;

    /// <summary>Whether the commands of the check or a capability of the barrier are going to ask Windows Hello.</summary>
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
        GoToWorkCommand.NotifyCanExecuteChanged();
        TryChecksCommand.NotifyCanExecuteChanged();
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

        RefreshSummary();
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
            var row = new SetupModelRowViewModel(model, _service.ModelCatalog, text);
            row.Changed += (_, _) => ModelsEdited();
            ModelRows.Add(row);
        }

        OnPropertyChanged(nameof(HasRecommendations));
        StepOf(SetupStep.Models).IsIncluded = false;

        FillVerification(proposal.Verification);

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
        ProviderLimitsText = ProviderLimits(proposal, text);

        if (proposal.Problems.Count > 0)
        {
            ErrorMessage = string.Join(" ", proposal.Problems.Select(text.Render));
        }
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

    /// <summary>«Claude Code, 2 a la vez · Codex, 2 a la vez» for the programs of the team, as the adapters fix it; empty without the source.</summary>
    private string ProviderLimits(SetupProposal proposal, IUiTextProvider text)
    {
        if (_providerLimits is null || _project is not { } project)
        {
            return string.Empty;
        }

        var used = proposal.Team.Changes.Select(c => c.After.Model.Provider).ToHashSet();
        List<SetupProviderLimit> limits;
        try
        {
            limits = [.. _providerLimits(project.RootPath).Where(l => used.Contains(l.Provider))];
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            // The line only informs: without the programs of the project it is not shown.
            return string.Empty;
        }

        return limits.Count == 0
            ? string.Empty
            : text.Format("Setup_Budget_Providers", string.Join(" · ", limits.Select(l => text.Format("Setup_Budget_Provider", SetupTexts.Provider(l.Provider), l.Limit))));
    }

    // ---- the check ----

    private void FillVerification(SetupVerificationProposal verification)
    {
        CanEditChecks = !verification.CurrentUnreadable;
        OnPropertyChanged(nameof(HasCurrentChecks));
        OnPropertyChanged(nameof(HasProposedChecks));
        LoadChecks(verification.Proposed ?? verification.Current);
        TrialLines.Clear();
        TrialOutput = string.Empty;
        TrialStatus = string.Empty;
        TrialPassed = null;
        ChecksEdited(initial: true);
    }

    /// <summary>The rows of <paramref name="profile"/>, or the blank ones for a project whose technology was not recognised.</summary>
    private void LoadChecks(VerificationProfile? profile)
    {
        var text = Text;
        _checkName = profile?.Name ?? SetupChecks.OwnProfileName;
        var steps = profile?.Steps ?? SetupChecks.Blank(text.Get("Setup_Verification_Step_build"), text.Get("Setup_Verification_Step_test"));
        CheckRows.Clear();
        foreach (var step in steps)
        {
            var row = new SetupCheckRowViewModel(step, text);
            row.Changed += (_, _) => ChecksEdited(initial: false);
            CheckRows.Add(row);
        }
    }

    /// <summary>The profile the rows make, or null with why when they cannot be used (or every line is empty).</summary>
    private VerificationProfile? EditedChecks(out string? problem) => SetupChecks.Profile(_checkName, CheckRows, Text, out problem);

    /// <summary>The edited commands would change the ones in force (and they are not a file of the person that cannot be used).</summary>
    private bool ChecksWouldChange(VerificationProfile? edited) =>
        edited is not null && _proposal is { } proposal && !proposal.Verification.CurrentUnreadable && proposal.Verification.Current?.ContentHash != edited.ContentHash;

    private void ChecksEdited(bool initial)
    {
        if (_proposal is not { } proposal)
        {
            return;
        }

        var text = Text;
        var edited = EditedChecks(out var problem);
        CheckProblem = problem is not null && CheckRows.All(r => r.Problem.Length == 0) ? problem : string.Empty;
        VerificationStatus = proposal.Verification.CurrentUnreadable ? text.Get("Setup_Verification_Unreadable")
            : problem is not null ? string.Empty
            : edited is null && proposal.Verification.Proposed is null && proposal.Verification.Current is null ? text.Render(UserText.Of(proposal.Verification.NoProposalCode ?? "setup.verification.no-stack"))
            : edited is null ? text.Get("Setup_Verification_None")
            : ChecksWouldChange(edited) ? text.Get("Setup_Verification_Hello_Note")
            : text.Get("Setup_Verification_Same");

        // Changing a command turns the step on; one that cannot be used keeps it on, so «Aplicar» waits until it can.
        var on = !proposal.Verification.CurrentUnreadable && (problem is not null ? !initial : ChecksWouldChange(edited));
        if (StepOf(SetupStep.Verification).IsIncluded != on)
        {
            StepOf(SetupStep.Verification).IsIncluded = on;
        }
        else
        {
            OnInclusionChanged();
        }

        TryChecksCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Volver a las que tienes ahora»: the rows of the commands in force.</summary>
    [RelayCommand]
    private void RestoreCurrentChecks()
    {
        if (_proposal?.Verification.Current is { } current && CanEditChecks)
        {
            LoadChecks(current);
            ChecksEdited(initial: false);
        }
    }

    /// <summary>«Volver a las propuestas»: the rows of the commands proposed for the stack.</summary>
    [RelayCommand]
    private void RestoreProposedChecks()
    {
        if (_proposal?.Verification.Proposed is { } proposed && CanEditChecks)
        {
            LoadChecks(proposed);
            ChecksEdited(initial: false);
        }
    }

    private bool CanTryChecks() => _trial is not null && _project is not null && !IsTrying && !IsBusy && EditedChecks(out _) is not null;

    /// <summary>
    /// «Probarla ahora»: the commands as they are on screen, run once on a copy of the last commit, isolated and without network,
    /// with the verifier of the runs (<see cref="SetupCheckTrial"/>). Nothing is saved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTryChecks))]
    private async Task TryChecksAsync()
    {
        if (_trial is null || _project is not { } project || EditedChecks(out _) is not { } profile)
        {
            return;
        }

        var text = Text;
        using var stop = new CancellationTokenSource();
        _trialStop = stop;
        IsTrying = true;
        TrialPassed = null;
        TrialLines.Clear();
        TrialOutput = string.Empty;
        TrialStatus = text.Get("Setup_Verification_Trying");
        try
        {
            var result = await _trial.RunAsync(project, profile, stop.Token).ConfigureAwait(true);
            ShowTrial(result, profile, text);
        }
        catch (OperationCanceledException)
        {
            TrialStatus = text.Get("Setup_Verification_Trial_Stopped");
        }
        catch (UserTextException refused)
        {
            TrialStatus = text.Render(refused.Text);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            TrialStatus = text.Format("Setup_Verification_Trial_Not_Run", ex.GetType().Name);
        }
        finally
        {
            _trialStop = null;
            IsTrying = false;
        }
    }

    private bool CanStopTrial() => IsTrying;

    /// <summary>«Parar la prueba»: the running command is stopped (its whole tree of processes).</summary>
    [RelayCommand(CanExecute = nameof(CanStopTrial))]
    private void StopTrial() => _trialStop?.Cancel();

    private void ShowTrial(SetupCheckTrialResult result, VerificationProfile profile, IUiTextProvider text)
    {
        string Label(string id) => CheckRows.FirstOrDefault(r => r.Step.Id == id)?.Label ?? id;

        TrialPassed = result.Passed;
        TrialStatus = text.Get(result.Passed ? "Setup_Verification_Trial_Passed" : "Setup_Verification_Trial_Failed");
        foreach (var step in result.Steps)
        {
            TrialLines.Add(step.Passed ? text.Format("Setup_Verification_Trial_Step_Passed", Label(step.StepId))
                : step.TimedOut ? text.Format("Setup_Verification_Trial_Step_TimedOut", Label(step.StepId))
                : text.Format("Setup_Verification_Trial_Step_Failed", Label(step.StepId), step.ExitCode));
            if (profile.Find(step.StepId)?.Kind == EvidenceKind.TestRun)
            {
                TrialLines.Add(step.Tests is { } tests
                    ? text.Format("Setup_Verification_Trial_Tests", tests.Passed, tests.Failed, tests.Skipped)
                    : text.Get("Setup_Verification_Trial_No_Report"));
            }

            foreach (var failed in step.FailedTests.Take(5))
            {
                TrialLines.Add(text.Format("Setup_Verification_Trial_Failed_Test", failed.Name));
            }
        }

        if (result.NotReached.Count > 0)
        {
            TrialLines.Add(text.Format("Setup_Verification_Trial_Not_Reached", string.Join(", ", result.NotReached.Select(Label))));
        }

        if (result.NotIsolated.Count > 0)
        {
            TrialLines.Add(text.Format("Setup_Verification_Trial_Not_Isolated", string.Join(", ", result.NotIsolated.Select(Label))));
        }

        TrialOutput = string.Join(Environment.NewLine, result.Output);
    }

    private static string Command(VerificationStep step) => SetupCommandLine.Render(step.Executable, step.Arguments);

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
        if (!_loading)
        {
            RefreshSummary();
        }
    }

    /// <summary>A model or an effort changed: the step is on while any row says something other than what the agent has.</summary>
    private void ModelsEdited()
    {
        if (_loading)
        {
            return;
        }

        var on = ModelRows.Any(r => r.IsEdited);
        if (StepOf(SetupStep.Models).IsIncluded != on)
        {
            StepOf(SetupStep.Models).IsIncluded = on;
        }
        else
        {
            OnInclusionChanged();
        }
    }

    /// <summary>«Usar todas las recomendadas»: each row takes the recommendation it does not say yet.</summary>
    [RelayCommand]
    private void UseAllRecommended()
    {
        foreach (var row in ModelRows.Where(r => r.CanUseRecommendation).ToList())
        {
            row.UseRecommendedCommand.Execute(null);
        }

        ModelsEdited();
    }

    /// <summary>The numbers of the limits as the person typed them; null with the code of what is wrong when they cannot be used.</summary>
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

    /// <summary>
    /// What the summary shows: <see cref="SummaryLines"/>, each included step with what is there now next to what it becomes, and
    /// <see cref="SummaryBlocks"/>, every step as it will be, from the same selection «Aplicar» takes.
    /// </summary>
    private void RefreshSummary()
    {
        SummaryLines.Clear();
        SummaryBlocks.Clear();
        WillAskHello = false;
        if (_proposal is not { } proposal)
        {
            ApplyCommand.NotifyCanExecuteChanged();
            return;
        }

        var text = Text;
        var selection = BuildSelection();
        var changes = Enum.GetValues<SetupStep>().ToDictionary(s => s, _ => new List<string>());
        void Change(SetupStep step, string before, string after)
        {
            SummaryLines.Add(text.Format("Setup_Summary_Line", SetupTexts.Step(text, step), before, after));
            changes[step].Add(text.Format("Setup_Summary_Change_Line", before, after));
        }

        // The brief.
        var briefBefore = proposal.Brief.Current is { } brief ? text.Format("Setup_Summary_Brief_Before", brief.Text.Length) : text.Get("Setup_Summary_Brief_None");
        if (selection.BriefText is { } briefText)
        {
            Change(SetupStep.Brief, briefBefore, text.Format("Setup_Summary_Brief_After", briefText.Trim().Length));
        }

        // The team and the models.
        var current = proposal.Team.Changes.Where(c => !c.IsAdded).ToDictionary(c => c.After.Id, c => c.After);
        var roster = selection.Roster;
        if (roster is not null)
        {
            foreach (var agent in roster)
            {
                if (!current.TryGetValue(agent.Id, out var before))
                {
                    Change(SetupStep.Team, text.Get("Setup_Summary_Absent"), text.Format("Setup_Summary_Agent_Added", agent.Name, SetupTexts.AgentLine(text, agent)));
                }
                else if (RosterHash.Of([before]) != RosterHash.Of([agent]))
                {
                    Change(SetupStep.Models, $"{agent.Name}: {SetupTexts.Power(text, before.Model.Model, before.Effort)}", SetupTexts.Power(text, agent.Model.Model, agent.Effort));
                }
            }

            WillAskHello |= SetupCapabilities.Granted([.. current.Values], roster).Count > 0;
        }

        var team = roster ?? [.. current.Values];

        // The commands of the check.
        if (selection.Profile is { } profile)
        {
            var before = proposal.Verification.Current is { } now ? text.Format("Setup_Summary_Check_Count", now.Steps.Count) : text.Get("Setup_Summary_Check_None");
            Change(SetupStep.Verification, before, text.Format("Setup_Summary_Check_New", profile.Steps.Count));
            WillAskHello = true;
        }

        var checks = selection.Profile ?? proposal.Verification.Current;

        // The skills.
        foreach (var skill in selection.Skills ?? [])
        {
            var row = SkillRows.First(r => r.Agent == skill.Agent && r.Skill == skill.Skill);
            Change(SetupStep.Skills, text.Get("Setup_Summary_Inactive"), $"{row.SkillName}: {row.AgentName}");
        }

        // The limits.
        if (selection.Options is { } options)
        {
            Change(SetupStep.Budget, Budget(text, _currentBudget), Budget(text, options));
        }

        Block(SetupStep.Brief, [selection.BriefText is { } written ? text.Format("Setup_Summary_Brief_After", written.Trim().Length) : briefBefore]);
        Block(SetupStep.Team, [.. team.Select(a => text.Format("Setup_Summary_Item", a.Name, SetupTexts.AgentLine(text, a)))]);
        Block(SetupStep.Models, [.. team.Select(a => text.Format("Setup_Summary_Item", a.Name, SetupTexts.Power(text, a.Model.Model, a.Effort)))]);
        Block(SetupStep.Verification, checks is null
            ? [text.Get("Setup_Summary_Check_None")]
            : [.. checks.Steps.Select(s => text.Format("Setup_Summary_Item", CheckRows.FirstOrDefault(r => r.Step.Id == s.Id)?.Label ?? s.Title, Command(s)))]);
        var skills = SkillRows.Where(r => r.AlreadyActive || (selection.Skills ?? []).Any(c => c.Agent == r.Agent && c.Skill == r.Skill)).ToList();
        Block(SetupStep.Skills, skills.Count == 0 ? [text.Get("Setup_Summary_Skills_None")] : [.. skills.Select(r => text.Format("Setup_Summary_Skill", r.SkillName, r.AgentName))]);
        Block(SetupStep.Budget, [Budget(text, selection.Options ?? _currentBudget)]);

        ApplyCommand.NotifyCanExecuteChanged();
        GoToWorkCommand.NotifyCanExecuteChanged();

        void Block(SetupStep step, IReadOnlyList<string> lines)
        {
            var saves = changes[step].Count > 0;
            var title = SetupTexts.Step(text, step);
            SummaryBlocks.Add(new SetupSummaryBlock(
                step,
                title,
                saves,
                text.Get(saves ? "Setup_Summary_Saves" : "Setup_Summary_Stays"),
                lines,
                changes[step],
                text.Format("Setup_Summary_Change_Name", title)));
        }
    }

    private static string Budget(IUiTextProvider text, ProjectRunOptions options) =>
        text.Format(
            "Setup_Summary_Budget",
            options.MaxAgentRuns,
            options.MaxParallelTasks,
            options.TimePerAttempt is { } time ? text.Format("Setup_Summary_Minutes", (int)time.TotalMinutes) : text.Get("Setup_Summary_Engine_Time"));

    /// <summary>
    /// The template to store: the agents of step 3 when it is included (those the project already has otherwise) with the model
    /// and the effort the person left in step 4; null when neither step has anything to apply.
    /// </summary>
    private IReadOnlyList<AgentDefinition>? SelectedRoster()
    {
        if (_proposal is not { } proposal)
        {
            return null;
        }

        var teamIncluded = StepOf(SetupStep.Team).IsIncluded;
        var edited = StepOf(SetupStep.Models).IsIncluded
            ? ModelRows.Where(r => r.IsEdited && r.IsValid).ToDictionary(r => r.Proposal.Agent.Id, r => r.Edited)
            : [];
        if (!teamIncluded && edited.Count == 0)
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

            agents.Add(edited.TryGetValue(change.After.Id, out var agent) ? agent : change.After);
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

    /// <summary>«Cambiar» of a block of the summary: back to its step.</summary>
    [RelayCommand]
    private void ChangeStep(SetupStep step)
    {
        if (!IsBusy)
        {
            Step = step;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        GoToWorkCommand.NotifyCanExecuteChanged();
        TryChecksCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanEditChecksChanged(bool value)
    {
        OnPropertyChanged(nameof(HasCurrentChecks));
        OnPropertyChanged(nameof(HasProposedChecks));
    }

    // ---- «Aplicar» and «Ir a trabajar» ----

    /// <summary>
    /// Whether there is something to apply: a part is selected and what is selected can be used (the limits, every model typed and
    /// the commands of the check), and no trial holds the project.
    /// </summary>
    public bool CanApplyNow =>
        !IsBusy && !IsTrying && _project is not null && SummaryLines.Count > 0
        && (!StepOf(SetupStep.Budget).IsIncluded || ParseBudget(out _) is not null)
        && (!StepOf(SetupStep.Models).IsIncluded || ModelRows.All(r => r.IsValid))
        && (!StepOf(SetupStep.Verification).IsIncluded || ChecksProblem() is null);

    private string? ChecksProblem()
    {
        EditedChecks(out var problem);
        return problem;
    }

    private bool CanApply() => CanApplyNow;

    /// <summary>The selection the person reviewed, as the service takes it. A step that is skipped goes as null.</summary>
    public SetupSelection BuildSelection()
    {
        var edited = StepOf(SetupStep.Verification).IsIncluded ? EditedChecks(out _) : null;
        var profile = ChecksWouldChange(edited) ? edited : null;
        var skills = StepOf(SetupStep.Skills).IsIncluded
            ? SkillRows.Where(r => r.IsChosen && !r.AlreadyActive).Select(r => new SetupSkillChoice(r.Agent, r.Skill)).ToList()
            : null;
        return new SetupSelection(
            StepOf(SetupStep.Brief).IsIncluded && !string.IsNullOrWhiteSpace(BriefText) ? BriefText : null,
            SelectedRoster(),
            profile,
            skills is { Count: > 0 } ? skills : null,
            StepOf(SetupStep.Budget).IsIncluded ? ParseBudget(out _) : null);
    }

    /// <summary>
    /// «Aplicar»: writes what is selected, each part by its usual gate. The commands of the check and any capability of the fixed
    /// barrier ask Windows Hello first; whatever it does not confirm is reported as denied and nothing of it is written. When every
    /// part went well the person is taken to work (<see cref="WorkRequested"/>); otherwise the result stays on screen.
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
        SetupApplyResult result;
        try
        {
            result = await _service.ApplyAsync(project, selection, CancellationToken.None).ConfigureAwait(true);
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

        if (WentWell(result))
        {
            var confirmation = Text.Get("Setup_Applied_Confirmation");
            Notice = string.IsNullOrEmpty(notice) ? confirmation : confirmation + " " + notice;
            WorkRequested?.Invoke(this, new SetupWorkRequestedEventArgs(confirmation));
        }
    }

    /// <summary>The one rule of when «Aplicar» leads to work: no part was denied or failed, and the registry was written when it had to be.</summary>
    public static bool WentWell(SetupApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Parts.All(p => p.Outcome is SetupPartOutcome.Applied or SetupPartOutcome.Unchanged or SetupPartOutcome.Skipped)
            && result.RecordProblem is null;
    }

    private bool CanGoToWork() => !IsBusy && !IsTrying && IsSummaryEmpty;

    /// <summary>«Ir a trabajar»: with nothing to save, the orchestrator of the project, as after «Aplicar».</summary>
    [RelayCommand(CanExecute = nameof(CanGoToWork))]
    private void GoToWork() => WorkRequested?.Invoke(this, new SetupWorkRequestedEventArgs(null));

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
