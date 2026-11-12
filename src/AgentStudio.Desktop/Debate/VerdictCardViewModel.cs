using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// What surrounds the verdicts of a card (docs/15 D-42): its title, the disposition of the judgment and its waivers (a
/// debate), the labels the judge did not see, the frozen requirements the verdicts were judged against (to name each one
/// and to mark «valoración del juez»), whether the debate chooses among its contributions, how the review of a run or an
/// audit ended, and the proposals «Decidir yo» can choose. <see cref="AwaitingHuman"/> says that a debate waits for a
/// person before the judgment was combined (its outcome so far is <see cref="PendingOutcome"/>). <see cref="Title"/> is for the
/// screen, in the language of the interface; <see cref="RequestLabel"/> is how «Crear tareas correctivas» names the verdict in
/// the request the orchestrator receives, fixed in Spanish like every prompt of the protocol (docs/17 D-48, F14-4); without
/// it the request uses the title, which is only right while the title is not translated.
/// </summary>
public sealed record VerdictCardContext(
    string Title,
    DebateJudgingKind? Judging = null,
    IReadOnlyList<VendorId>? WaivedVendors = null,
    IReadOnlyList<DebateParticipantView>? Participants = null,
    FrozenRequirementSet? Requirements = null,
    bool Choice = false,
    string? Disposition = null,
    IReadOnlyList<string>? Proposals = null,
    bool AwaitingHuman = false,
    string? PendingOutcome = null,
    string? RequestLabel = null);

/// <summary>
/// What a card can do, each only when it is given: «Impugnar» a stored verdict (by id), «Decidir yo» when the judgment
/// needs a person (the decision, the proposal chosen and the comment) and «Crear tareas correctivas» (the request for the
/// orchestrator, with the corrective actions as data).
/// </summary>
public sealed record VerdictCardActions(
    Func<string, Task>? Challenge = null,
    Func<VerdictDecision?, string?, string, Task>? Decide = null,
    Func<string, Task>? CreateCorrectiveTasks = null);

/// <summary>One requirement as a verdict assessed it.</summary>
/// <param name="Statement">The literal text of the requirement, from the frozen set; null when the set could not be read.</param>
/// <param name="JudgeAssessment">«valoración del juez, no verificable por pruebas» (D-25), or null.</param>
public sealed record AssessmentRow(string Glyph, string Status, Tone Tone, string Requirement, string? Statement, string Rationale, string Evidence, string? JudgeAssessment)
{
    /// <summary>The text of the requirement, or that it is not available.</summary>
    public string StatementShown => Statement ?? Texts.Get("Verdict_View_StatementUnavailable");

    /// <summary>What a screen reader says of a requirement as a verdict assessed it (V34): the requirement and its status.</summary>
    public override string ToString() => $"{Requirement}: {Status}";
}

/// <summary>One finding of a verdict, with its severity, category and what it cites.</summary>
public sealed record FindingRow(string Severity, Tone Tone, string Category, string Description, string References)
{
    /// <summary>What a screen reader says of a finding of a verdict (V34): its severity, its category and its description.</summary>
    public override string ToString() => $"{Severity} · {Category}: {Description}";
}

/// <summary>What the judge of a debate said of one proposal (D-25).</summary>
public sealed record ComparisonRow(string Proposal, string Strengths, string Weaknesses)
{
    /// <summary>What a screen reader says of what the judge said of a proposal (V34): the proposal.</summary>
    public override string ToString() => Proposal;
}

/// <summary>A label of the blind dossier and who was behind it (D-15): the correspondence the judge did not see.</summary>
public sealed record LabelRow(string Label, string Role, string Agent, string Where)
{
    /// <summary>What a screen reader says of a label of the blind dossier (V34): the label, its role and its agent.</summary>
    public override string ToString() => $"{Label} · {Role} · {Agent}";
}

/// <summary>A rule of the validator a verdict broke: in Spanish, its stable code and the validator's own message.</summary>
public sealed record IssueRow(string Text, string Code, string Detail)
{
    /// <summary>What a screen reader says of a rule a verdict broke (V34): the rule in Spanish.</summary>
    public override string ToString() => Text;
}

/// <summary>
/// One verdict as the card shows it (docs/03 §6): judge, decision, confidence, validation with its issues, whether it is a
/// correction, the proposal it chose and how it compared them, each requirement, the findings, risks, pending objections
/// and corrective actions, and «Impugnar».
/// </summary>
public sealed partial class VerdictPanelViewModel : ObservableObject
{
    private readonly Func<string, Task>? _challenge;

    internal VerdictPanelViewModel(JudgedVerdict judged, VerdictCardContext context, TimeZoneInfo zone, Func<string, Task>? challenge, string? supersedes)
    {
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(zone);
        _challenge = challenge;
        var verdict = judged.Verdict;
        Id = judged.Id;
        JudgeText = JudgeLine(verdict.Judge);
        DecisionText = VerdictTexts.Decision(verdict.Decision);
        DecisionTone = judged.Violations.Count == 0 ? VerdictTexts.ToneOf(verdict.Decision) : Tone.Warning;
        Summary = verdict.Summary;
        ConfidenceText = verdict.Confidence is { } confidence
            ? Texts.Format("Verdict_Confidence_Line", VerdictTexts.Confidence(confidence.Level), confidence.Justification)
            : Texts.Get("Verdict_Confidence_None");
        Issues = [.. judged.Violations.Select(v => new IssueRow(VerdictTexts.Violation(v.Code), v.Code.ToString(), v.Message))];
        ValidationText = judged.Violations.Count switch
        {
            0 => Texts.Get("Verdict_Validation_None"),
            var count => DebateTexts.Plural(count, "Verdict_Validation"),
        };
        ValidationTone = judged.Violations.Count == 0 ? Tone.Success : Tone.Error;
        CorrectedText = judged.Corrected
            ? Texts.Get("Verdict_Corrected_ByJudge")
            : supersedes is { Length: > 0 } previous ? Texts.Format("Verdict_Corrected_Supersedes", previous) : null;
        HumanText = Texts.Get(verdict.RequiresHumanIntervention ? "Verdict_Human_Needed" : "Verdict_Human_No");
        IssuedText = Texts.Format("Verdict_Issued", TimeZoneInfo.ConvertTime(verdict.IssuedAt, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        if (verdict.Resolution is { } resolution)
        {
            ChosenProposalText = resolution.ChosenProposal is { Length: > 0 } chosen ? Texts.Format("Verdict_Chosen_Of", chosen) : Texts.Get("Verdict_Chosen_None");
            Comparison = [.. (resolution.Comparison ?? []).Where(c => c is not null).Select(c => new ComparisonRow(c.Proposal, c.Strengths, c.Weaknesses))];
        }

        Assessments = [.. (verdict.Assessments ?? []).Select(a => Row(a, context))];
        var satisfied = verdict.Assessments?.Count(a => a.Status == AssessmentStatus.Satisfied) ?? 0;
        var unmet = verdict.Assessments?.Count(a => a.Status == AssessmentStatus.NotSatisfied) ?? 0;
        var unverifiable = verdict.Assessments?.Count(a => a.Status == AssessmentStatus.NotVerifiable) ?? 0;
        CountsText = $"✔ {DebateTexts.Plural(satisfied, "Verdict_Count_Satisfied")} · ✖ {DebateTexts.Plural(unmet, "Verdict_Count_Unmet")} · ? {DebateTexts.Plural(unverifiable, "Verdict_Count_Unverifiable")}";
        Findings = [.. (verdict.Findings ?? []).Select(f => new FindingRow(
            VerdictTexts.Severity(f.Severity),
            VerdictTexts.ToneOf(f.Severity),
            VerdictTexts.Category(f.Category),
            f.Description,
            References(f)))];
        Risks = [.. verdict.Risks ?? []];
        PendingObjections = [.. verdict.PendingObjections ?? []];
        CorrectiveActions = [.. verdict.CorrectiveActions ?? []];
    }

    public string Id { get; }

    /// <summary>What a screen reader says of this verdict.</summary>
    public string VerdictName => Texts.Format("Verdict_View_VerdictName", Id);

    /// <summary>What a screen reader says of «Impugnar» on this verdict.</summary>
    public string ChallengeName => Texts.Format("Verdict_View_ChallengeName", Id);

    /// <summary>Who judged: the agent, its program, its company and the model the program reported (Codex reports none).</summary>
    public string JudgeText { get; }

    public string DecisionText { get; }

    public Tone DecisionTone { get; }

    public string Summary { get; }

    public string ConfidenceText { get; }

    public string ValidationText { get; }

    public Tone ValidationTone { get; }

    public IReadOnlyList<IssueRow> Issues { get; }

    public bool HasIssues => Issues.Count > 0;

    /// <summary>That it is the judge's correction, or which stored verdict it replaces; null for a first verdict.</summary>
    public string? CorrectedText { get; }

    public string HumanText { get; }

    public string IssuedText { get; }

    /// <summary>The proposal the judge of a debate chose, or that it chose none; null outside the debates.</summary>
    public string? ChosenProposalText { get; }

    public IReadOnlyList<ComparisonRow> Comparison { get; } = [];

    public string CountsText { get; }

    /// <summary>The title of the list of requirements, with the counts.</summary>
    public string CountsLine => Texts.Format("Verdict_View_RequirementsCounts", CountsText);

    public IReadOnlyList<AssessmentRow> Assessments { get; }

    public IReadOnlyList<FindingRow> Findings { get; }

    public IReadOnlyList<string> Risks { get; }

    public IReadOnlyList<string> PendingObjections { get; }

    public IReadOnlyList<string> CorrectiveActions { get; }

    public bool HasComparison => Comparison.Count > 0;

    public bool HasFindings => Findings.Count > 0;

    public bool HasRisks => Risks.Count > 0;

    public bool HasPendingObjections => PendingObjections.Count > 0;

    public bool HasCorrectiveActions => CorrectiveActions.Count > 0;

    /// <summary>«Impugnar» is offered: the verdict is stored and the screen can open a challenge.</summary>
    public bool CanBeChallenged => _challenge is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChallengeCommand))]
    private bool _isChallenging;

    [ObservableProperty]
    private string? _challengeError;

    /// <summary>«Impugnar»: opens the challenge of this verdict (a revision debate); nothing is written until you confirm it there.</summary>
    [RelayCommand(CanExecute = nameof(CanChallenge))]
    private async Task ChallengeAsync()
    {
        if (_challenge is not { } challenge)
        {
            return;
        }

        IsChallenging = true;
        ChallengeError = null;
        try
        {
            await challenge(Id).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The challenge is behind a port: a failure is shown on the card, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ChallengeError = Texts.Format("Verdict_Error_Challenge", DebateTexts.Reason(ex));
        }
        finally
        {
            IsChallenging = false;
        }
    }

    private bool CanChallenge() => _challenge is not null && !IsChallenging;

    internal static string JudgeLine(JudgeIdentity? judge) => judge?.Model is { } model
        ? $"{judge.Agent} · {DebateTexts.Provider(model.Provider)} · {DebateTexts.Vendor(model.Vendor)} · {(string.IsNullOrWhiteSpace(model.Model) ? Texts.Get("Verdict_Judge_NoModel") : model.Model)}"
        : Texts.Get("Verdict_Judge_NoIdentity");

    private static AssessmentRow Row(RequirementAssessment assessment, VerdictCardContext context)
    {
        var requirement = context.Requirements?.Find(assessment.Requirement);
        var judgeAssessment = context.Choice
            && requirement is { Criteria.Count: > 0 }
            && requirement.Criteria.All(c => c.Method == VerificationMethod.JudgeAssessment);
        var evidence = assessment.Evidence is { Count: > 0 } cited ? Texts.Format("Verdict_Row_Evidence", string.Join(", ", cited)) : Texts.Get("Verdict_Row_NoEvidence");
        return new AssessmentRow(
            VerdictTexts.Glyph(assessment.Status),
            VerdictTexts.Status(assessment.Status),
            VerdictTexts.ToneOf(assessment.Status),
            assessment.Requirement.Value,
            requirement?.Statement,
            assessment.Rationale,
            evidence,
            judgeAssessment ? Texts.Get("Verdict_Row_JudgeAssessment") : null);
    }

    private static string References(Finding finding)
    {
        var parts = new List<string>();
        if (finding.Evidence is { Count: > 0 } evidence)
        {
            parts.Add(Texts.Format("Verdict_Ref_Evidence", string.Join(", ", evidence)));
        }

        if (finding.DecisionRefs is { Count: > 0 } entries)
        {
            parts.Add(Texts.Format("Verdict_Ref_Entries", string.Join(", ", entries.Select(e => "#" + e.ToString(CultureInfo.InvariantCulture)))));
        }

        return parts.Count == 0 ? Texts.Get("Verdict_Ref_None") : string.Join(" · ", parts);
    }

    /// <summary>What a screen reader says of a verdict of the card (V34): its decision and its judge.</summary>
    public override string ToString() => $"{DecisionText} · {JudgeText}";
}

/// <summary>
/// The card of a verdict (docs/15 D-42, docs/03 §6), common to runs, debates and audits: the outcome with its rule, the
/// proposal chosen, the disposition of the judgment and its waivers (in the warning colour), the verdicts (two side by side
/// in a cross-vendor panel), the correspondence of the labels the judge did not see, and the actions: «Impugnar» on each
/// stored verdict, «Decidir yo» when the judgment needs a person and «Crear tareas correctivas». Nothing is decided here
/// that a port does not record.
/// </summary>
public sealed partial class VerdictCardViewModel : ObservableObject
{
    private readonly VerdictCardActions _actions;
    private readonly VerdictCardContext _context;

    /// <param name="verdicts">The verdicts of the judgment: one, or one per judge of a panel.</param>
    /// <param name="outcome">How a debate combined them (null for the review of a run and for an audit).</param>
    /// <param name="context">What surrounds them.</param>
    /// <param name="zone">The local time of the dates shown.</param>
    /// <param name="actions">What the card can do; each action only when given.</param>
    /// <param name="supersedes">For stored verdicts: the verdict each one replaces, by id.</param>
    public VerdictCardViewModel(
        IReadOnlyList<JudgedVerdict> verdicts,
        PanelOutcome? outcome,
        VerdictCardContext context,
        TimeZoneInfo zone,
        VerdictCardActions? actions = null,
        IReadOnlyDictionary<string, string>? supersedes = null)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(zone);
        _actions = actions ?? new VerdictCardActions();
        _context = context;
        Title = context.Title;
        Outcome = outcome;
        Verdicts = [.. verdicts.Select(v => new VerdictPanelViewModel(v, context, zone, _actions.Challenge, supersedes?.GetValueOrDefault(v.Id)))];
        (OutcomeText, OutcomeTone) = Describe(verdicts, outcome, context);
        ChosenProposalText = outcome is { } decided && (decided.ChosenProposal is not null || verdicts.Any(v => v.Verdict.Resolution is not null))
            ? decided.ChosenProposal is { Length: > 0 } chosen ? Texts.Format("Verdict_Chosen_Of", chosen) : Texts.Get("Verdict_Chosen_None")
            : null;
        if (context.Judging is { } judging)
        {
            JudgingText = Texts.Format("Verdict_Judging_Line", DebateTexts.Judging(judging));
            JudgingHelp = DebateTexts.JudgingHelp(judging);
        }

        WaiverText = DebateTexts.Waiver(context.WaivedVendors ?? []);
        Labels = [.. (context.Participants ?? []).Select(p => new LabelRow(
            p.Label,
            DebateTexts.Role(p.Role),
            p.Agent.Value,
            $"{DebateTexts.Provider(p.Provider)} · {DebateTexts.Vendor(p.Vendor)} · {(string.IsNullOrWhiteSpace(p.Model) ? Texts.Get("Debate_Model_Default") : p.Model)}"))];
        RequirementsNote = context.Requirements is null && verdicts.Count > 0
            ? Texts.Get("Verdict_Requirements_Note")
            : null;
        NeedsHuman = outcome?.NeedsHuman ?? context.AwaitingHuman;
        CorrectiveActions = [.. verdicts.SelectMany(v => v.Verdict.CorrectiveActions ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal)];
        ProposalOptions = [new(null, Texts.Get("Verdict_Option_NoProposal")), .. (context.Proposals ?? []).Select(p => new Option<string?>(p, p))];
        _selectedDecision = DecisionOptions[^1];
        _selectedProposal = ProposalOptions[0];
    }

    public string Title { get; }

    /// <summary>How a debate combined its verdicts; null for a run or an audit.</summary>
    public PanelOutcome? Outcome { get; }

    public string OutcomeText { get; }

    public Tone OutcomeTone { get; }

    /// <summary>The proposal the debate settled on, or that none was; null outside the debates.</summary>
    public string? ChosenProposalText { get; }

    public string? JudgingText { get; }

    public string? JudgingHelp { get; }

    /// <summary>The waiver of the rule of the participants, shown in the warning colour; null when there is none.</summary>
    public string? WaiverText { get; }

    public string? Disposition => _context.Disposition;

    /// <summary>How the review ended, introduced; null for what is not a review.</summary>
    public string? DispositionLine => Disposition is { } text ? Texts.Format("Verdict_View_Disposition", text) : null;

    public IReadOnlyList<VerdictPanelViewModel> Verdicts { get; }

    /// <summary>Two verdicts side by side (a cross-vendor panel).</summary>
    public bool IsPanel => Verdicts.Count > 1;

    public bool HasVerdicts => Verdicts.Count > 0;

    public IReadOnlyList<LabelRow> Labels { get; }

    public bool HasLabels => Labels.Count > 0;

    public string? RequirementsNote { get; }

    // ---- «Decidir yo» (D-26) ----

    /// <summary>The judgment did not settle it: the verdicts disagree, are invalid or did not come.</summary>
    public bool NeedsHuman { get; }

    /// <summary>«Decidir yo» is offered: the judgment needs a person and there is a debate in course to record it.</summary>
    public bool CanDecide => NeedsHuman && _actions.Decide is not null && !Decided;

    public IReadOnlyList<Option<VerdictDecision?>> DecisionOptions { get; } =
    [
        new(VerdictDecision.Approved, Texts.Get("Verdict_Option_Approve")),
        new(VerdictDecision.ChangesRequested, Texts.Get("Verdict_Option_AskChanges")),
        new(VerdictDecision.Rejected, Texts.Get("Verdict_Option_Reject")),
        new(null, Texts.Get("Verdict_Option_LeaveUnresolved")),
    ];

    public IReadOnlyList<Option<string?>> ProposalOptions { get; }

    [ObservableProperty]
    private Option<VerdictDecision?> _selectedDecision;

    [ObservableProperty]
    private Option<string?> _selectedProposal;

    /// <summary>Why you decide what you decide: recorded with the decision, so it cannot be empty.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecideCommand))]
    private string _comment = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecideCommand))]
    [NotifyPropertyChangedFor(nameof(CanDecide))]
    private bool _isDeciding;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecideCommand))]
    [NotifyPropertyChangedFor(nameof(CanDecide))]
    private bool _decided;

    [ObservableProperty]
    private string? _decisionStatus;

    /// <summary>«Decidir yo»: records your resolution (<c>debate.human-resolution</c>, bound to the verdicts) and closes the debate with the rule «decisión tuya».</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitDecision))]
    private async Task DecideAsync()
    {
        if (_actions.Decide is not { } decide || !CanSubmitDecision())
        {
            return;
        }

        IsDeciding = true;
        ErrorMessage = null;
        try
        {
            await decide(SelectedDecision?.Value, SelectedProposal?.Value, Comment.Trim()).ConfigureAwait(true);
            Decided = true;
            DecisionStatus = SelectedDecision?.Value is { } decision
                ? SelectedProposal?.Value is { } proposal
                    ? Texts.Format("Verdict_Decided_WithProposal", VerdictTexts.Decision(decision), proposal)
                    : Texts.Format("Verdict_Decided_Line", VerdictTexts.Decision(decision))
                : Texts.Get("Verdict_Decided_Unresolved");
        }
#pragma warning disable CA1031 // The debate is behind a port: a failure is shown on the card, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Verdict_Error_Decide", DebateTexts.Reason(ex));
        }
        finally
        {
            IsDeciding = false;
        }
    }

    private bool CanSubmitDecision() => CanDecide && !IsDeciding && !string.IsNullOrWhiteSpace(Comment);

    // ---- «Crear tareas correctivas» ----

    /// <summary>The corrective actions of every verdict, without repeats.</summary>
    public IReadOnlyList<string> CorrectiveActions { get; }

    /// <summary>Offered when the verdicts ask for corrective actions and the shell can open the orchestrator.</summary>
    public bool CanCreateCorrectiveTasks => CorrectiveActions.Count > 0 && _actions.CreateCorrectiveTasks is not null;

    /// <summary>
    /// The request the orchestrator receives: the corrective actions as data, never as orders. It is a prompt of the protocol
    /// (docs/17 D-48): <see cref="ProtocolTexts"/> writes it in Spanish whatever the language of the interface, and the verdict
    /// is named by <see cref="VerdictCardContext.RequestLabel"/>, not by the title of the screen.
    /// </summary>
    public string CorrectiveRequest =>
        ProtocolTexts.CorrectiveRequest(_context.RequestLabel ?? Title, Verdicts.Select(v => v.Id), CorrectiveActions);

    [RelayCommand(CanExecute = nameof(CanCreateCorrectiveTasks))]
    private async Task CreateCorrectiveTasksAsync()
    {
        if (_actions.CreateCorrectiveTasks is not { } create || CorrectiveActions.Count == 0)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            await create(CorrectiveRequest).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The shell is behind a port: a failure is shown on the card, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = Texts.Format("Verdict_Error_Orchestrator", DebateTexts.Reason(ex));
        }
    }

    [ObservableProperty]
    private string? _errorMessage;

    private static (string Text, Tone Tone) Describe(IReadOnlyList<JudgedVerdict> verdicts, PanelOutcome? outcome, VerdictCardContext context)
    {
        if (outcome is null && context.AwaitingHuman)
        {
            return (Texts.Format("Verdict_Outcome_Pending", context.PendingOutcome ?? DebateTexts.Outcome(DebateOutcome.Unresolved)), Tone.Warning);
        }

        if (outcome is { } panel)
        {
            var parts = new List<string> { DebateTexts.Outcome(panel.Outcome) };
            if (panel.Decision is { } decision)
            {
                parts.Add(VerdictTexts.Decision(decision));
            }

            parts.Add(DebateTexts.Rule(panel.Rule));
            if (panel.Confidence is { } confidence)
            {
                parts.Add(Texts.Format(panel.Rule == JudgingPanel.Agree ? "Verdict_Outcome_ConfidenceLowest" : "Verdict_Outcome_Confidence", VerdictTexts.Confidence(confidence)));
            }

            var tone = panel.NeedsHuman ? Tone.Warning
                : panel.Decision is { } decided ? VerdictTexts.ToneOf(decided)
                : Tone.Neutral;
            return (string.Join(" · ", parts), tone);
        }

        if (verdicts.Count == 0)
        {
            return (Texts.Get("Verdict_Outcome_None"), Tone.Neutral);
        }

        var single = verdicts[0];
        var valid = single.Violations.Count == 0;
        var text = Texts.Format(valid ? "Verdict_Outcome_Valid" : "Verdict_Outcome_Invalid", VerdictTexts.Decision(single.Verdict.Decision));
        return (text, valid ? VerdictTexts.ToneOf(single.Verdict.Decision) : Tone.Warning);
    }
}
