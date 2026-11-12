using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Debate;

/// <summary>An agent of the template the form offers as a participant: whether it takes part, and with which role.</summary>
public sealed partial class ParticipantChoiceViewModel : ObservableObject
{
    private readonly Action _changed;

    public ParticipantChoiceViewModel(AgentDefinition agent, DebateRole role, Action changed)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(changed);
        Agent = agent;
        _changed = changed;
        _selectedRole = Roles.First(r => r.Value == role);
        Role = role;
    }

    public AgentDefinition Agent { get; }

    public string Name => $"{Agent.Id} · {Agent.Name}";

    public string Where => NewDebateViewModel.Where(Agent.Model);

    /// <summary>What a screen reader says of the role box of this agent.</summary>
    public string RoleName => Texts.Format("Debate_NewView_RoleName", Name);

    // Built on each read, not once: a static list would keep the language it was first built in (records compare by value, so
    // a selection still matches).
    public static IReadOnlyList<Option<DebateRole>> Roles => [.. Enum.GetValues<DebateRole>().Select(r => new Option<DebateRole>(r, DebateTexts.Role(r)))];

    public IReadOnlyList<Option<DebateRole>> RoleOptions => Roles;

    [ObservableProperty]
    private bool _isIncluded;

    [ObservableProperty]
    private Option<DebateRole> _selectedRole;

    /// <summary>The role chosen last (a combo box may push null while it is torn down, which is never a choice).</summary>
    public DebateRole Role { get; private set; }

    partial void OnIsIncludedChanged(bool value) => _changed();

    partial void OnSelectedRoleChanged(Option<DebateRole> value)
    {
        if (value is not null)
        {
            Role = value.Value;
            _changed();
        }
    }

    /// <summary>What a screen reader says of an agent offered as a participant (V34): its id and its name.</summary>
    public override string ToString() => Name;
}

/// <summary>A judge of the template the form offers; every judge is a candidate unless you leave it out.</summary>
public sealed partial class JudgeChoiceViewModel : ObservableObject
{
    private readonly Action _changed;

    public JudgeChoiceViewModel(AgentDefinition agent, Action changed)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(changed);
        Agent = agent;
        _changed = changed;
    }

    public AgentDefinition Agent { get; }

    public string Name => $"{Agent.Id} · {Agent.Name}";

    public string Where => NewDebateViewModel.Where(Agent.Model);

    [ObservableProperty]
    private bool _isIncluded = true;

    partial void OnIsIncludedChanged(bool value) => _changed();

    /// <summary>What a screen reader says of a judge offered (V34): its id and its name.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// A criterion you write for a debate that chooses (D-07): its text, whether it is mandatory and how it is checked. In those
/// modes there is no code to test, so only the judge's assessment and a person's review are offered (D-25).
/// </summary>
public sealed partial class DebateCriterionViewModel : ObservableObject
{
    private readonly Action _changed;

    public DebateCriterionViewModel(string id, string text, VerificationMethod method, Action changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(changed);
        Id = id;
        _text = text;
        _changed = changed;
        _selectedMethod = Methods.FirstOrDefault(m => m.Value == method) ?? Methods[0];
        Method = _selectedMethod.Value;
    }

    public static IReadOnlyList<Option<VerificationMethod>> Methods =>
    [
        new(VerificationMethod.JudgeAssessment, Texts.Get("Debate_Method_JudgeAssessment")),
        new(VerificationMethod.HumanReview, Texts.Get("Debate_Method_HumanReview")),
    ];

    public IReadOnlyList<Option<VerificationMethod>> MethodOptions => Methods;

    public string Id { get; }

    /// <summary>What a screen reader says of the text box of this criterion.</summary>
    public string TextName => Texts.Format("Debate_NewView_CriterionTextName", Id);

    /// <summary>What a screen reader says of the box that says how this criterion is checked.</summary>
    public string MethodName => Texts.Format("Debate_NewView_CriterionMethodName", Id);

    /// <summary>What a screen reader says of the button that removes this criterion.</summary>
    public string RemoveName => Texts.Format("Debate_NewView_RemoveName", Id);

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isMandatory = true;

    [ObservableProperty]
    private Option<VerificationMethod> _selectedMethod;

    public VerificationMethod Method { get; private set; }

    partial void OnTextChanged(string value) => _changed();

    partial void OnIsMandatoryChanged(bool value) => _changed();

    partial void OnSelectedMethodChanged(Option<VerificationMethod> value)
    {
        if (value is not null)
        {
            Method = value.Value;
            _changed();
        }
    }

    /// <summary>The criterion as a requirement of its own: its text is the statement and its only acceptance criterion.</summary>
    internal Requirement ToRequirement() => new(
        new RequirementId(Id),
        Text.Trim(),
        IsMandatory ? RequirementPriority.Mandatory : RequirementPriority.Desirable,
        [new AcceptanceCriterion("c1", Text.Trim(), Method)]);

    /// <summary>What a screen reader says of a criterion of the debate (V34): its id and its text.</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Text) ? Id : $"{Id}: {Text}";
}

/// <summary>A run of the project that can be the subject of a debate.</summary>
public sealed record RunChoice(RunId Id, string Label, string RequirementSetHash)
{
    /// <summary>What a screen reader says of the choice (A1-bis): its label, never the members of the record.</summary>
    public override string ToString() => Label;
}

/// <summary>A debate the form opened: its run, the proposal you approved (none for a debate resumed), and what the room needs to know of it.</summary>
public sealed record OpenedDebate(IDebateRun Run, DebateProposal? Proposal, DebateRoomContext Context);

/// <summary>
/// «Nuevo debate» (docs/15 D-41 and D-06): the mode with the roles it needs, the question, the criteria (with the methods
/// the mode admits) or the requirements its subject brings, the subject (none, HEAD or a run), the participants of the
/// template with their role, the judges and the disposition of the judgment that <see cref="DebateJudgingRules"/> computes,
/// explained (a waiver in the warning colour), and the limits. «Preparar» asks the coordinator for the exact configuration
/// and the estimate without writing anything; «Abrir debate» asks your approval, bound to what was shown, and opens it.
/// Changing anything after preparing withdraws the proposal: what you approve is always what is on screen.
/// </summary>
public sealed partial class NewDebateViewModel : ObservableObject
{
    /// <summary>
    /// The criteria a «Revisión de arquitectura» starts with (D-07); you add yours. They become the statements of frozen
    /// requirements that the judge and the debaters receive, so they are never translated (docs/17 D-48).
    /// </summary>
    public static IReadOnlyList<string> ArchitectureCriteria { get; } =
    [
        "Mantenibilidad: el diseño se entiende, se prueba y se cambia sin romper lo demás.",
        "Seguridad: el diseño no abre superficies nuevas sin mitigarlas.",
        "Rendimiento: el diseño cumple lo que se le pide sin coste desproporcionado.",
        "Coste: el diseño no gasta más cuota, tiempo o dinero de lo necesario.",
        "Complejidad: el diseño es el más simple que resuelve el problema.",
    ];

    private readonly DebateScreenBackend _backend;
    private readonly TimeProvider _time;
    private ProjectContext? _project;
    private FrozenRequirementSet? _runRequirements;
    private string? _runRequirementsProblem;
    private bool _loading;

    public NewDebateViewModel(DebateScreenBackend backend, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        _backend = backend;
        _time = time;
        _selectedMode = Modes[0];
        _selectedSubject = SubjectOptions[0];
        _selectedJudging = JudgingKinds[0];
        Criteria.CollectionChanged += (_, _) => Changed();
        Subjects = [.. SubjectsFor(_selectedMode.Value)];
    }

    /// <summary>Raised once a debate was opened (after your approval): the document follows it in the room.</summary>
    public event EventHandler<OpenedDebate>? Opened;

    public IReadOnlyList<Option<DebateMode>> Modes { get; } = [.. Enum.GetValues<DebateMode>().Select(m => new Option<DebateMode>(m, DebateTexts.Mode(m)))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHelp))]
    private Option<DebateMode> _selectedMode;

    public string ModeHelp => DebateTexts.ModeHelp(SelectedMode?.Value ?? DebateMode.Free);

    [ObservableProperty]
    private string _question = string.Empty;

    /// <summary>The criteria you write, one requirement each, in the modes that choose.</summary>
    public ObservableCollection<DebateCriterionViewModel> Criteria { get; } = [];

    /// <summary>The subjects the mode admits.</summary>
    public IReadOnlyList<Option<DebateSubjectKind>> Subjects { get; private set; }

    private static IReadOnlyList<Option<DebateSubjectKind>> SubjectOptions =>
    [
        new(DebateSubjectKind.None, Texts.Get("Debate_SubjectOption_None")),
        new(DebateSubjectKind.Commit, Texts.Get("Debate_SubjectOption_Commit")),
        new(DebateSubjectKind.Run, Texts.Get("Debate_SubjectOption_Run")),
    ];

    [ObservableProperty]
    private Option<DebateSubjectKind> _selectedSubject;

    public ObservableCollection<RunChoice> Runs { get; } = [];

    /// <summary>The subject is a run: the list of runs is shown.</summary>
    public bool ShowsRuns => SelectedSubject?.Value == DebateSubjectKind.Run;

    [ObservableProperty]
    private RunChoice? _selectedRun;

    public ObservableCollection<ParticipantChoiceViewModel> Participants { get; } = [];

    public ObservableCollection<JudgeChoiceViewModel> Judges { get; } = [];

    public IReadOnlyList<Option<DebateJudgingKind>> JudgingKinds { get; } = [.. Enum.GetValues<DebateJudgingKind>().Select(k => new Option<DebateJudgingKind>(k, DebateTexts.Judging(k)))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JudgingHelp))]
    private Option<DebateJudgingKind> _selectedJudging;

    public string JudgingHelp => DebateTexts.JudgingHelp(SelectedJudging?.Value ?? DebateJudgingKind.IndependentJudge);

    /// <summary>What <see cref="DebateJudgingRules.Possible"/> allows with these participants and judges, or that the coordinator computes it (a run's producers).</summary>
    public string JudgingHint { get; private set; } = string.Empty;

    /// <summary>The chosen disposition does not fit what was chosen: said in the warning colour.</summary>
    public bool JudgingHintIsWarning { get; private set; }

    [ObservableProperty]
    private string _maxRounds = DebateLimits.Default.MaxRounds.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _maxMinutes = ((int)DebateLimits.Default.MaxDuration.TotalMinutes).ToString(CultureInfo.InvariantCulture);

    /// <summary>The token limit; empty is no limit.</summary>
    [ObservableProperty]
    private string _maxTokens = (DebateLimits.Default.MaxTotalTokens ?? 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>The criteria are yours: a mode that chooses, about nothing or about the commit.</summary>
    public bool WritesCriteria => RequirementsKind == RequirementsSource.Criteria;

    /// <summary>Where the requirements of this debate come from, in one line.</summary>
    public string RequirementsText { get; private set; } = string.Empty;

    /// <summary>Why the debate cannot be prepared as it is, one line each; empty when it can.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProposal))]
    [NotifyPropertyChangedFor(nameof(ArrangementText))]
    [NotifyPropertyChangedFor(nameof(WaiverText))]
    [NotifyPropertyChangedFor(nameof(ProducersText))]
    [NotifyPropertyChangedFor(nameof(EstimateText))]
    [NotifyPropertyChangedFor(nameof(ProposalProblems))]
    [NotifyPropertyChangedFor(nameof(Notes))]
    [NotifyPropertyChangedFor(nameof(ApprovalText))]
    [NotifyPropertyChangedFor(nameof(ApprovalLine))]
    [NotifyPropertyChangedFor(nameof(ProposalHash))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private DebateProposal? _proposal;

    public bool HasProposal => Proposal is not null;

    /// <summary>The disposition the coordinator computed: its kind and its judges.</summary>
    public string? ArrangementText => Proposal is { } proposal
        ? $"{DebateTexts.Judging(proposal.Judging.Kind)}: {string.Join(" | ", proposal.Judging.Judges.Select(VerdictPanelViewModel.JudgeLine))}"
        : null;

    /// <summary>The waiver of the rule of the participants the proposal carries, in the warning colour; null without one.</summary>
    public string? WaiverText => Proposal is { } proposal ? DebateTexts.Waiver([.. proposal.Judging.WaivedParticipantVendors]) : null;

    public string? ProducersText => Proposal is { Judging.ProducerVendors.Count: > 0 } proposal
        ? Texts.Format("Debate_New_Producers", string.Join(", ", proposal.Judging.ProducerVendors.Select(DebateTexts.Vendor).Order(StringComparer.Ordinal)))
        : null;

    /// <summary>What the debate may spend at most (D-21), with its origin: an estimate of Agent Studio, never exact.</summary>
    public string? EstimateText => Proposal is { } proposal && MetricText.Tokens(proposal.Estimate.Tokens) is var tokens
        ? Texts.Format("Debate_New_Estimate", DebateTexts.Plural(proposal.Estimate.MaxAgentRuns, "Debate_Count_AgentRun"), tokens.Shown, tokens.Origin)
        : null;

    public IReadOnlyList<string> ProposalProblems => Proposal is { } shown ? DebateTexts.Painted(shown.Problems, shown.ProblemTexts) : [];

    public IReadOnlyList<string> Notes => Proposal is { } shown ? DebateTexts.Painted(shown.Estimate.Notes, shown.Estimate.NoteTexts) : [];

    /// <summary>The text of the approval «Abrir debate» asks for.</summary>
    public string? ApprovalText => Proposal?.ApprovalText;

    /// <summary>The approval «Abrir debate» asks for, introduced.</summary>
    public string? ApprovalLine => ApprovalText is { } text ? Texts.Format("Debate_Form_ApprovalAsked", text) : null;

    public string? ProposalHash => Proposal is { } proposal ? Texts.Format("Debate_New_ProposalHash", DebateTexts.Short(proposal.HeaderHash)) : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrepareCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddCriterionCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    private enum RequirementsSource
    {
        Criteria,
        Run,
        Fixed,
    }

    private RequirementsSource RequirementsKind =>
        (SelectedMode?.Value, SelectedSubject?.Value) switch
        {
            (DebateMode.OrchestratorReview, _) => RequirementsSource.Fixed,
            (_, DebateSubjectKind.Run) => RequirementsSource.Run,
            _ => RequirementsSource.Criteria,
        };

    /// <summary>Reads the template and the runs of the project. Writes nothing.</summary>
    public async Task LoadAsync(ProjectContext project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        _loading = true;
        ErrorMessage = null;
        try
        {
            Participants.Clear();
            Judges.Clear();
            if (_backend.Roster is { } roster)
            {
                var agents = await roster.ListAsync(project.ProjectId, ct).ConfigureAwait(true);
                foreach (var agent in agents.OrderBy(a => a.Id.Value, StringComparer.Ordinal))
                {
                    if (agent.Role == AgentRole.Judge)
                    {
                        Judges.Add(new JudgeChoiceViewModel(agent, Changed));
                    }
                    else if (agent.Role is not (AgentRole.Auditor or AgentRole.Orchestrator))
                    {
                        Participants.Add(new ParticipantChoiceViewModel(agent, DefaultRole(agent.Role), Changed));
                    }
                }
            }

            Runs.Clear();
            if (_backend.Runs is { } runs)
            {
                foreach (var run in (await runs.ListAsync(project.ProjectId, ct).ConfigureAwait(true)).OrderByDescending(r => r.CreatedAt))
                {
                    var when = TimeZoneInfo.ConvertTime(run.CreatedAt, _time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                    Runs.Add(new RunChoice(run.Id, $"{run.Id} · {when}", run.RequirementSetHash));
                }
            }
        }
#pragma warning disable CA1031 // The stores are behind ports: a failure is shown in the form, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Debate_New_Error_Load", DebateTexts.Reason(ex));
        }
        finally
        {
            _loading = false;
        }

        ResetCriteria();
        Changed();
    }

    [RelayCommand(CanExecute = nameof(CanEditCriteria))]
    private void AddCriterion()
    {
        var used = Criteria.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var next = Enumerable.Range(1, Criteria.Count + 1).Select(CriterionId).First(id => !used.Contains(id));
        Criteria.Add(new DebateCriterionViewModel(next, string.Empty, VerificationMethod.JudgeAssessment, Changed));
    }

    [RelayCommand]
    private void RemoveCriterion(DebateCriterionViewModel? criterion)
    {
        if (criterion is not null)
        {
            Criteria.Remove(criterion);
        }
    }

    private bool CanEditCriteria() => !IsBusy;

    /// <summary>«Preparar»: the coordinator checks the request and computes the disposition, the estimate and the hash you will approve. Nothing is written.</summary>
    [RelayCommand(CanExecute = nameof(CanPrepare))]
    private async Task PrepareAsync()
    {
        if (_backend.Coordinator is not { } coordinator || BuildRequest() is not { } request)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = Texts.Get("Debate_New_Status_Preparing");
        try
        {
            Proposal = await coordinator.PrepareAsync(request, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = Proposal.Problems.Count == 0
                ? Texts.Get("Debate_New_Status_Prepared")
                : Texts.Get("Debate_New_Status_Problems");
        }
#pragma warning disable CA1031 // The coordinator is behind a port: a failure is shown in the form, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Proposal = null;
            StatusMessage = null;
            ErrorMessage = Texts.Format("Debate_New_Error_Prepare", DebateTexts.Reason(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPrepare() => !IsBusy && _project is not null && _backend.Coordinator is not null && Problems.Count == 0;

    /// <summary>«Abrir debate»: asks your approval (bound to the hash shown) and, with it, opens the debate.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (_backend.Coordinator is not { } coordinator || Proposal is not { } proposal)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = Texts.Get("Debate_Status_WaitingApproval");
        try
        {
            var run = await coordinator.OpenAsync(proposal, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = Texts.Format("Debate_New_Status_Opened", run.Id);
            var context = new DebateRoomContext(proposal.Request.Question, proposal.Request.Requirements, proposal.Judging.Kind, [.. proposal.Judging.WaivedParticipantVendors], proposal.Judging.Judges);
            Opened?.Invoke(this, new OpenedDebate(run, proposal, context));
        }
#pragma warning disable CA1031 // The coordinator is behind a port: a refusal (an approval denied, a busy project) is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = null;
            ErrorMessage = Texts.Format("Debate_New_Error_Open", DebateTexts.Reason(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanOpen() => !IsBusy && Proposal is { Problems.Count: 0 } && _backend.Coordinator is not null;

    // ---- what changes withdraws the proposal ----

    partial void OnSelectedModeChanged(Option<DebateMode> value)
    {
        if (value is null)
        {
            return;
        }

        Subjects = [.. SubjectsFor(value.Value)];
        OnPropertyChanged(nameof(Subjects));
        if (!Subjects.Any(s => s.Value == SelectedSubject?.Value))
        {
            SelectedSubject = Subjects[0];
        }

        foreach (var participant in Participants)
        {
            if (!RoleFits(value.Value, participant.Role))
            {
                participant.SelectedRole = ParticipantChoiceViewModel.Roles.First(r => RoleFits(value.Value, r.Value));
            }
        }

        ResetCriteria();
        Changed();
    }

    partial void OnQuestionChanged(string value) => Changed();

    partial void OnSelectedSubjectChanged(Option<DebateSubjectKind> value) => _ = RequirementsChangedAsync();

    partial void OnSelectedRunChanged(RunChoice? value) => _ = RequirementsChangedAsync();

    partial void OnSelectedJudgingChanged(Option<DebateJudgingKind> value) => Changed();

    partial void OnMaxRoundsChanged(string value) => Changed();

    partial void OnMaxMinutesChanged(string value) => Changed();

    partial void OnMaxTokensChanged(string value) => Changed();

    /// <summary>The subject or the run changed: the requirements of a run are read again.</summary>
    private async Task RequirementsChangedAsync()
    {
        _runRequirements = null;
        _runRequirementsProblem = null;
        Changed();
        if (RequirementsKind != RequirementsSource.Run || SelectedRun is not { } run || _project is not { } project)
        {
            return;
        }

        if (_backend.Requirements is not { } store)
        {
            _runRequirementsProblem = Texts.Get("Debate_New_Problem_NoRequirementStore");
            Changed();
            return;
        }

        try
        {
            var set = await store.FindAsync(project.ProjectId, run.RequirementSetHash, CancellationToken.None).ConfigureAwait(true);
            if (SelectedRun == run)
            {
                _runRequirements = set;
                _runRequirementsProblem = set is null ? Texts.Format("Debate_New_Problem_RunRequirementsGone", run.Id, DebateTexts.Short(run.RequirementSetHash)) : null;
            }
        }
#pragma warning disable CA1031 // The store is behind a port: a failure is shown in the form, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _runRequirementsProblem = Texts.Format("Debate_New_Problem_RunRequirementsFailed", run.Id, DebateTexts.Reason(ex));
        }

        Changed();
    }

    private void Changed()
    {
        if (_loading)
        {
            return;
        }

        Proposal = null;
        Problems = Validate();
        RequirementsText = RequirementsLine();
        (JudgingHint, JudgingHintIsWarning) = Hint();
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(RequirementsText));
        OnPropertyChanged(nameof(WritesCriteria));
        OnPropertyChanged(nameof(ShowsRuns));
        OnPropertyChanged(nameof(JudgingHint));
        OnPropertyChanged(nameof(JudgingHintIsWarning));
        PrepareCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
    }

    // ---- the request ----

    /// <summary>The request on screen, or null (with the problems) when it is not complete.</summary>
    internal DebateRequest? BuildRequest()
    {
        if (_project is not { } project || Problems.Count > 0 || Requirements() is not { } requirements || Limits() is not { } limits)
        {
            return null;
        }

        var subject = new DebateSubject(SelectedSubject.Value, SelectedSubject.Value == DebateSubjectKind.Run ? SelectedRun?.Id.Value : null);
        return new DebateRequest(
            project,
            SelectedMode.Value,
            Question.Trim(),
            requirements,
            subject,
            [.. Participants.Where(p => p.IsIncluded).Select(p => new DebateParticipantRequest(p.Agent.Id, p.Role))],
            [.. Judges.Where(j => j.IsIncluded).Select(j => j.Agent.Id)],
            SelectedJudging.Value,
            limits);
    }

    private FrozenRequirementSet? Requirements()
    {
        switch (RequirementsKind)
        {
            case RequirementsSource.Fixed:
                return _backend.FixedRequirements?.Invoke(SelectedMode.Value);
            case RequirementsSource.Run:
                return _runRequirements;
            default:
                try
                {
                    return FrozenRequirementSet.Freeze([.. Criteria.Where(c => !string.IsNullOrWhiteSpace(c.Text)).Select(c => c.ToRequirement())], _time.GetUtcNow());
                }
                catch (DomainRuleViolationException)
                {
                    return null;
                }
        }
    }

    private DebateLimits? Limits()
    {
        if (!int.TryParse(MaxRounds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rounds) || rounds < 1
            || !int.TryParse(MaxMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 1)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(MaxTokens))
        {
            return new DebateLimits(rounds, TimeSpan.FromMinutes(minutes));
        }

        return long.TryParse(MaxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) && tokens > 0
            ? new DebateLimits(rounds, TimeSpan.FromMinutes(minutes), tokens)
            : null;
    }

    private List<string> Validate()
    {
        var problems = new List<string>();
        if (_project is null)
        {
            problems.Add(Texts.Get("Debate_New_Problem_NoProject"));
            return problems;
        }

        if (_backend.Coordinator is null)
        {
            problems.Add(Texts.Get("Debate_New_Problem_NoCoordinator"));
        }

        var mode = SelectedMode?.Value ?? DebateMode.Free;
        if (string.IsNullOrWhiteSpace(Question))
        {
            problems.Add(Texts.Get("Debate_New_Problem_NoQuestion"));
        }

        if (_backend.Roster is null)
        {
            problems.Add(Texts.Get("Debate_New_Problem_NoRoster"));
        }
        else
        {
            var included = Participants.Where(p => p.IsIncluded).ToList();
            if (included.Count < 2)
            {
                problems.Add(Texts.Get("Debate_New_Problem_TwoParticipants"));
            }

            problems.AddRange(RoleProblems(mode, included.Select(p => p.Role).ToList()));
            if (!Judges.Any(j => j.IsIncluded))
            {
                problems.Add(Texts.Get(Judges.Count == 0 ? "Debate_Problem_NoJudgeInRoster" : "Debate_Problem_KeepAJudge"));
            }
        }

        if (SelectedSubject?.Value == DebateSubjectKind.Run && SelectedRun is null)
        {
            problems.Add(Texts.Get(_backend.Runs is null
                ? "Debate_New_Problem_NoRunRegistry"
                : Runs.Count == 0 ? "Debate_New_Problem_NoRuns" : "Debate_New_Problem_PickRun"));
        }

        switch (RequirementsKind)
        {
            case RequirementsSource.Fixed when _backend.FixedRequirements?.Invoke(mode) is null:
                problems.Add(Texts.Get("Debate_New_Problem_NoFixedRequirements"));
                break;
            case RequirementsSource.Run when SelectedRun is not null && _runRequirements is null:
                problems.Add(_runRequirementsProblem ?? Texts.Get("Debate_New_Problem_ReadingRequirements"));
                break;
            case RequirementsSource.Criteria when !Criteria.Any(c => !string.IsNullOrWhiteSpace(c.Text)):
                problems.Add(Texts.Get("Debate_New_Problem_NoCriterion"));
                break;
        }

        if (Limits() is null)
        {
            problems.Add(Texts.Get("Debate_Problem_Limits"));
        }

        return problems;
    }

    /// <summary>The roles each mode needs (D-07), as the domain checks them when the debate opens.</summary>
    private static IEnumerable<string> RoleProblems(DebateMode mode, IReadOnlyList<DebateRole> roles)
    {
        if (roles.Count == 0)
        {
            yield break;
        }

        switch (mode)
        {
            case DebateMode.Adversarial or DebateMode.ImplementationAudit when !roles.Contains(DebateRole.Proponent) || !roles.Contains(DebateRole.Critic):
                yield return Texts.Get("Debate_New_Problem_NeedProponentCritic");
                break;
            case DebateMode.Tribunal when roles.Any(r => r != DebateRole.Panelist):
                yield return Texts.Get("Debate_New_Problem_TribunalOnly");
                break;
            case DebateMode.OrchestratorReview when roles.Contains(DebateRole.Proponent):
                yield return Texts.Get("Debate_New_Problem_ReviewersOnly");
                break;
        }
    }

    private static bool RoleFits(DebateMode mode, DebateRole role) => mode switch
    {
        DebateMode.Tribunal => role == DebateRole.Panelist,
        DebateMode.OrchestratorReview => role != DebateRole.Proponent,
        _ => true,
    };

    private (string Text, bool Warning) Hint()
    {
        var mode = SelectedMode?.Value ?? DebateMode.Free;
        var subject = SelectedSubject?.Value ?? DebateSubjectKind.None;
        if (subject == DebateSubjectKind.Run)
        {
            return (Texts.Get("Debate_New_Hint_Run"), false);
        }

        var participants = Participants.Where(p => p.IsIncluded).Select(p => new DebateParticipant(p.Agent.Id, p.Role, p.Agent.Model)).ToList();
        var candidates = Judges.Where(j => j.IsIncluded).Select(j => new JudgeIdentity(j.Agent.Id, j.Agent.Model)).ToList();
        if (participants.Count == 0 || candidates.Count == 0)
        {
            return (Texts.Get("Debate_New_Hint_Choose"), false);
        }

        var possible = DebateJudgingRules.Possible(mode, subject, participants, new HashSet<VendorId>(), candidates);
        if (possible.Count == 0)
        {
            return (Texts.Get("Debate_New_Hint_Nobody"), true);
        }

        var kinds = possible.Select(a => a.Kind).Distinct().ToList();
        var text = Texts.Format("Debate_New_Hint_Fits", string.Join("; ", kinds.Select(k => $"{DebateTexts.Judging(k)} ({string.Join(" | ", possible.Where(a => a.Kind == k).Select(a => string.Join(" + ", a.Judges.Select(j => j.Agent.Value))).Distinct())})")));
        return SelectedJudging?.Value is { } chosen && !kinds.Contains(chosen)
            ? (Texts.Format("Debate_New_Hint_NotFit", DebateTexts.Judging(chosen), text), true)
            : (text, false);
    }

    private string RequirementsLine() => RequirementsKind switch
    {
        RequirementsSource.Fixed => _backend.FixedRequirements?.Invoke(SelectedMode.Value) is { } set
            ? Texts.Format("Debate_New_Req_Fixed", DebateTexts.Plural(set.Requirements.Count, "Debate_Count_Requirement"), DebateTexts.Short(set.ContentHash), string.Join(", ", set.Requirements.Select(r => r.Id.Value)))
            : Texts.Get("Debate_New_Req_FixedNone"),
        RequirementsSource.Run => _runRequirements is { } frozen
            ? Texts.Format("Debate_New_Req_Run", DebateTexts.Plural(frozen.Requirements.Count, "Debate_Count_Requirement"), DebateTexts.Short(frozen.ContentHash))
            : Texts.Get("Debate_New_Req_RunPick"),
        _ => Texts.Get("Debate_New_Req_Criteria"),
    };

    private void ResetCriteria()
    {
        var mode = SelectedMode?.Value ?? DebateMode.Free;
        var defaults = mode == DebateMode.ArchitectureReview ? ArchitectureCriteria : [];
        var kept = Criteria.Where(c => !string.IsNullOrWhiteSpace(c.Text) && !ArchitectureCriteria.Contains(c.Text)).Select(c => (c.Text, c.Method, c.IsMandatory)).ToList();
        Criteria.Clear();
        foreach (var text in defaults)
        {
            Criteria.Add(new DebateCriterionViewModel(CriterionId(Criteria.Count + 1), text, VerificationMethod.JudgeAssessment, Changed));
        }

        foreach (var (text, method, mandatory) in kept)
        {
            Criteria.Add(new DebateCriterionViewModel(CriterionId(Criteria.Count + 1), text, method, Changed) { IsMandatory = mandatory });
        }

        if (Criteria.Count == 0)
        {
            Criteria.Add(new DebateCriterionViewModel(CriterionId(1), string.Empty, VerificationMethod.JudgeAssessment, Changed));
        }
    }

    private static IEnumerable<Option<DebateSubjectKind>> SubjectsFor(DebateMode mode) => mode switch
    {
        DebateMode.ImplementationAudit or DebateMode.OrchestratorReview => SubjectOptions.Where(s => s.Value == DebateSubjectKind.Run),
        DebateMode.Tribunal => SubjectOptions,
        _ => SubjectOptions.Where(s => s.Value != DebateSubjectKind.Run),
    };

    private static DebateRole DefaultRole(AgentRole role) => role switch
    {
        AgentRole.Critic or AgentRole.Tester => DebateRole.Critic,
        _ => DebateRole.Proponent,
    };

    private static string CriterionId(int number) => "D-" + number.ToString("00", CultureInfo.InvariantCulture);

    internal static string Where(ModelBinding model) =>
        $"{DebateTexts.Provider(model.Provider)} · {DebateTexts.Vendor(model.Vendor)} · {(string.IsNullOrWhiteSpace(model.Model) ? Texts.Get("Debate_Model_Default") : model.Model)}";
}
