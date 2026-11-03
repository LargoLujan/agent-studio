using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Debate;

/// <summary>What a debate is about (docs/15 D-08): nothing, the commit HEAD of the user's repository, a finished run, or a verdict that is challenged.</summary>
public enum DebateSubjectKind
{
    None,
    Commit,
    Run,
    Verdict,
}

/// <summary>The three dispositions of the judgment of a debate (docs/15 D-13).</summary>
public enum DebateJudgingKind
{
    /// <summary>One judge of a vendor that neither produced the subject nor takes part: ADR-0006 as it is, with no waiver.</summary>
    IndependentJudge,

    /// <summary>
    /// One judge of each vendor of the participants, in a debate that chooses: the rule of the participants is waived for
    /// every one of them, and when the judges disagree a person decides. A waiver, never independence regained (ADR-0027).
    /// </summary>
    CrossVendorPanel,

    /// <summary>One judge of a vendor of the participants that did not produce the work, in a debate that reviews it: the rule of the participants is waived for that vendor.</summary>
    NonProducerJudge,
}

/// <summary>
/// Who judges a debate and with which waivers (docs/15 D-12 and D-13). <see cref="ProducerVendors"/> are the vendors that
/// produced its subject (rule P, ADR-0014: never waived); <see cref="WaivedParticipantVendors"/> are the vendors of the
/// participants for which the rule of the participants (rule Q, ADR-0006) is explicitly waived, a waiver the header, the
/// log and the approval record. Two arrangements are equal when their kind, judges (in order) and vendor sets are.
/// </summary>
public sealed record JudgingArrangement(
    DebateJudgingKind Kind,
    IReadOnlyList<JudgeIdentity> Judges,
    IReadOnlySet<VendorId> ProducerVendors,
    IReadOnlySet<VendorId> WaivedParticipantVendors)
{
    /// <summary>
    /// The vendors no judge of this debate may run on, as the dossier carries them (<see cref="JudgeDossier.ExcludedVendors"/>):
    /// the producers, and every vendor of a participant that was not waived (P ∪ (participants − waived)).
    /// </summary>
    public IReadOnlySet<VendorId> ExcludedVendors(IReadOnlyList<DebateParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var excluded = new HashSet<VendorId>(ProducerVendors ?? new HashSet<VendorId>());
        excluded.UnionWith(participants.Select(p => p.Model.Vendor).Where(v => WaivedParticipantVendors?.Contains(v) != true));
        return excluded;
    }

    public bool Equals(JudgingArrangement? other) =>
        other is not null
        && Kind == other.Kind
        && (Judges ?? []).SequenceEqual(other.Judges ?? [])
        && SameSet(ProducerVendors, other.ProducerVendors)
        && SameSet(WaivedParticipantVendors, other.WaivedParticipantVendors);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        foreach (var judge in Judges ?? [])
        {
            hash.Add(judge);
        }

        foreach (var vendor in (ProducerVendors ?? new HashSet<VendorId>()).Select(v => v.Value).Order(StringComparer.Ordinal))
        {
            hash.Add(vendor, StringComparer.Ordinal);
        }

        hash.Add('|');
        foreach (var vendor in (WaivedParticipantVendors ?? new HashSet<VendorId>()).Select(v => v.Value).Order(StringComparer.Ordinal))
        {
            hash.Add(vendor, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static bool SameSet(IReadOnlySet<VendorId>? left, IReadOnlySet<VendorId>? right) =>
        (left ?? new HashSet<VendorId>()).SetEquals(right ?? new HashSet<VendorId>());
}

/// <summary>
/// Who may judge a debate (docs/15 D-12 and D-13). Pure: the coordinator, the challenge service and the screen ask it with
/// the mode, the subject, the participants, the producers of the subject and the judges of the template.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Rule P, the producers</b> (ADR-0014): no judge runs on a vendor that produced the subject. Never waived.</item>
/// <item><b>Rule Q, the participants</b> (ADR-0006): no judge runs on the vendor of a participant unless that vendor is
/// explicitly waived. In the modes that choose, the contributions are the work being judged, so there Q is the rule of the
/// producers of that work.</item>
/// <item><b>The families.</b> A debate chooses among its contributions (free, adversarial, architecture review and tribunal,
/// never with a run as subject) or reviews work that others produced (implementation audit, orchestrator review, a
/// tribunal about a run). A cross-vendor panel only judges a debate that chooses; a non-producer judge only one that
/// reviews. A challenge (subject <see cref="DebateSubjectKind.Verdict"/>) is judged like the object of the verdict it
/// challenges (D-39), which the domain cannot see: it is offered both families and the challenge service picks, while P and
/// the shape of each disposition still hold.</item>
/// </list>
/// </remarks>
public static class DebateJudgingRules
{
    public const string JudgeRequired = "debate.judge-required";
    public const string DuplicateJudge = "debate.duplicate-judge";
    public const string JudgeNotParticipant = "debate.judge-not-participant";
    public const string JudgeProducer = "debate.judge-producer";
    public const string JudgeParticipantNotWaived = "debate.judge-participant-not-waived";
    public const string WaiverOfProducer = "debate.waiver-of-producer";
    public const string WaiverNotParticipant = "debate.waiver-not-participant";
    public const string SingleJudge = "debate.single-judge";
    public const string PanelShape = "debate.panel-shape";
    public const string PanelInReview = "debate.panel-in-review";
    public const string NonProducerShape = "debate.non-producer-shape";
    public const string NonProducerInChoice = "debate.non-producer-in-choice";

    /// <summary>
    /// Whether a debate chooses among its contributions (free, adversarial, architecture review and tribunal, about nothing
    /// or about a commit) rather than reviewing work others produced. A challenge answers false: the family of a challenge
    /// is that of the verdict it challenges, which only the caller knows, and false is the side whose rules are those of
    /// Phase 3 (<see cref="DebateDossierContext.Choice"/>).
    /// </summary>
    public static bool IsChoice(DebateMode mode, DebateSubjectKind subject) =>
        mode is DebateMode.Free or DebateMode.Adversarial or DebateMode.ArchitectureReview or DebateMode.Tribunal
        && subject is DebateSubjectKind.None or DebateSubjectKind.Commit;

    /// <summary>
    /// Every arrangement the rules allow with these judges, the default first: the independent judges (in the order of the
    /// candidates), then the cross-vendor panels (one candidate of each vendor of the participants, in the ordinal order of
    /// the vendors, every combination in the order of the candidates), then the non-producer judges. None of them seats a
    /// participant, a judge of a producer, or waives more than the vendors of its judges. Empty when nobody may judge.
    /// </summary>
    public static IReadOnlyList<JudgingArrangement> Possible(
        DebateMode mode,
        DebateSubjectKind subject,
        IReadOnlyList<DebateParticipant> participants,
        IReadOnlySet<VendorId> producers,
        IReadOnlyList<JudgeIdentity> candidates)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(producers);
        ArgumentNullException.ThrowIfNull(candidates);

        var participantAgents = participants.Select(p => p.Agent).ToHashSet();
        var participantVendors = participants.Select(p => p.Model.Vendor).ToHashSet();
        var eligible = candidates
            .Where(c => c?.Model is not null && !participantAgents.Contains(c.Agent) && !producers.Contains(c.Model.Vendor))
            .DistinctBy(c => c.Agent)
            .ToList();
        var choice = IsChoice(mode, subject) || subject == DebateSubjectKind.Verdict;
        var review = !IsChoice(mode, subject);
        var possible = new List<JudgingArrangement>();

        foreach (var judge in eligible.Where(c => !participantVendors.Contains(c.Model.Vendor)))
        {
            possible.Add(new JudgingArrangement(DebateJudgingKind.IndependentJudge, [judge], Copy(producers), new HashSet<VendorId>()));
        }

        if (choice && participantVendors.Count >= 2)
        {
            var byVendor = participantVendors
                .OrderBy(v => v.Value, StringComparer.Ordinal)
                .Select(v => eligible.Where(c => c.Model.Vendor == v).ToList())
                .ToList();
            if (byVendor.All(judges => judges.Count > 0))
            {
                foreach (var panel in Combinations(byVendor))
                {
                    possible.Add(new JudgingArrangement(DebateJudgingKind.CrossVendorPanel, panel, Copy(producers), Copy(participantVendors)));
                }
            }
        }

        if (review)
        {
            foreach (var judge in eligible.Where(c => participantVendors.Contains(c.Model.Vendor)))
            {
                possible.Add(new JudgingArrangement(DebateJudgingKind.NonProducerJudge, [judge], Copy(producers), new HashSet<VendorId> { judge.Model.Vendor }));
            }
        }

        return possible;
    }

    /// <summary>
    /// The stable codes of every rule the arrangement breaks for this debate, in a fixed order and each once; empty when it
    /// is allowed. The rules common to every disposition come first (a judge at all, once each, never a participant, P, Q
    /// and the waivers), then those of its kind.
    /// </summary>
    public static IReadOnlyList<string> Violations(JudgingArrangement arrangement, DebateMode mode, DebateSubjectKind subject, IReadOnlyList<DebateParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(participants);

        var judges = (arrangement.Judges ?? []).Where(j => j?.Model is not null).ToList();
        var producers = arrangement.ProducerVendors ?? new HashSet<VendorId>();
        var waived = arrangement.WaivedParticipantVendors ?? new HashSet<VendorId>();
        var participantAgents = participants.Select(p => p.Agent).ToHashSet();
        var participantVendors = participants.Select(p => p.Model.Vendor).ToHashSet();
        var judgeVendors = judges.Select(j => j.Model.Vendor).ToList();
        var codes = new List<string>();
        void Check(bool broken, string code)
        {
            if (broken && !codes.Contains(code))
            {
                codes.Add(code);
            }
        }

        Check(judges.Count == 0 || judges.Count != (arrangement.Judges ?? []).Count, JudgeRequired);
        Check(judges.Select(j => j.Agent).Distinct().Count() != judges.Count, DuplicateJudge);
        Check(judges.Any(j => participantAgents.Contains(j.Agent)), JudgeNotParticipant);
        Check(judgeVendors.Any(producers.Contains), JudgeProducer);
        Check(judgeVendors.Any(v => participantVendors.Contains(v) && !waived.Contains(v)), JudgeParticipantNotWaived);
        Check(waived.Any(producers.Contains), WaiverOfProducer);
        Check(waived.Any(v => !participantVendors.Contains(v)), WaiverNotParticipant);

        var challenge = subject == DebateSubjectKind.Verdict;
        switch (arrangement.Kind)
        {
            case DebateJudgingKind.IndependentJudge:
                Check(judges.Count > 1, SingleJudge);
                break;
            case DebateJudgingKind.CrossVendorPanel:
                Check(
                    participantVendors.Count < 2 || judgeVendors.Distinct().Count() != judges.Count || !judgeVendors.ToHashSet().SetEquals(participantVendors),
                    PanelShape);
                Check(!challenge && !IsChoice(mode, subject), PanelInReview);
                break;
            case DebateJudgingKind.NonProducerJudge:
                Check(judges.Count > 1, SingleJudge);
                Check(
                    judges.Count == 1 && (!participantVendors.Contains(judgeVendors[0]) || !waived.SetEquals([judgeVendors[0]])),
                    NonProducerShape);
                Check(!challenge && IsChoice(mode, subject), NonProducerInChoice);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arrangement), arrangement.Kind, "Unknown kind of judging arrangement.");
        }

        return codes;
    }

    private static HashSet<VendorId> Copy(IEnumerable<VendorId> vendors) => [.. vendors];

    /// <summary>Every way of taking one judge of each list, in order: the first list varies slowest.</summary>
    private static IEnumerable<IReadOnlyList<JudgeIdentity>> Combinations(IReadOnlyList<List<JudgeIdentity>> lists)
    {
        IEnumerable<IReadOnlyList<JudgeIdentity>> partial = [[]];
        foreach (var list in lists)
        {
            var current = partial;
            partial = current.SelectMany(prefix => list.Select(judge => (IReadOnlyList<JudgeIdentity>)[.. prefix, judge])).ToList();
        }

        return partial;
    }
}

/// <summary>
/// What the judgment of a debate decided (docs/15 D-14 and D-26): the outcome, the decision and the proposal chosen (both
/// null when nothing was decided), and the rule that gave them (<see cref="JudgingPanel.Single"/>,
/// <see cref="JudgingPanel.Agree"/>, <see cref="JudgingPanel.Disagree"/>, <see cref="JudgingPanel.Invalid"/> or, for the
/// decision of a person, <see cref="JudgingPanel.User"/>). <see cref="Confidence"/> is the confidence of the verdict that
/// decided, the lower of the two when a panel agrees.
/// </summary>
public sealed record PanelOutcome(DebateOutcome Outcome, VerdictDecision? Decision, string? ChosenProposal, string Rule)
{
    public ConfidenceLevel? Confidence { get; init; }

    /// <summary>A person must look: the verdicts did not settle it, or they could not be had (D-26: «Decidir yo»).</summary>
    public bool NeedsHuman => Outcome is DebateOutcome.Unresolved or DebateOutcome.EscalatedToUser;
}

/// <summary>How the verdicts of a debate combine (docs/15 D-14). Pure.</summary>
public static class JudgingPanel
{
    /// <summary>The one judge of the debate decided.</summary>
    public const string Single = "single";

    /// <summary>The judges of a panel agree on the decision and the proposal.</summary>
    public const string Agree = "agree";

    /// <summary>The judges of a panel disagree, or one of them gave no valid verdict: «no resuelto», a person decides.</summary>
    public const string Disagree = "disagree";

    /// <summary>The verdicts cannot be combined: none, one too many, of another judge, or about different material.</summary>
    public const string Invalid = "invalid";

    /// <summary>A person decided («Decidir yo»).</summary>
    public const string User = "user";

    /// <summary>The rules a debate can be closed with.</summary>
    public static readonly IReadOnlyList<string> Rules = [Single, Agree, Disagree, Invalid, User];

    /// <summary>
    /// Combines the verdicts of the judges of <paramref name="arrangement"/>, each with whether the validator accepted it
    /// (after its correction, if it had one):
    /// <list type="bullet">
    /// <item>one judge: its valid verdict decides; an invalid one, none or one of another judge needs a person;</item>
    /// <item>a panel: a verdict of each judge, all valid, echoing the same head of the log and the same requirements, and
    /// agreeing on the decision and the proposal, decide, with the lower confidence. Verdicts about different heads (or
    /// requirements), a judge without a verdict or a verdict of someone else are an invalid set, never an agreement; any
    /// other case (a disagreement, an invalid verdict) is «no resuelto», for a person.</item>
    /// </list>
    /// Judges are compared by agent, provider and vendor, never by model (<see cref="SameJudge"/>).
    /// </summary>
    public static PanelOutcome Combine(JudgingArrangement arrangement, IReadOnlyList<(Verdict Verdict, bool Valid)> verdicts)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(verdicts);

        var judges = arrangement.Judges ?? [];
        var invalid = new PanelOutcome(DebateOutcome.EscalatedToUser, null, null, Invalid);
        if (verdicts.Count == 0 || verdicts.Count != judges.Count || verdicts.Any(v => v.Verdict?.Judge?.Model is null))
        {
            return invalid;
        }

        var seated = verdicts.Select(v => judges.FirstOrDefault(j => SameJudge(j, v.Verdict.Judge))).ToList();
        if (seated.Any(j => j is null) || seated.Distinct().Count() != seated.Count)
        {
            return invalid;
        }

        if (arrangement.Kind != DebateJudgingKind.CrossVendorPanel)
        {
            var (verdict, valid) = verdicts[0];
            return valid ? Decided(verdict, Single, verdict.Confidence?.Level) : invalid;
        }

        if (verdicts.Select(v => v.Verdict.DecisionLogHeadHash).Distinct(StringComparer.Ordinal).Count() != 1
            || verdicts.Select(v => v.Verdict.RequirementSetHash).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            return invalid;
        }

        var first = verdicts[0].Verdict;
        var agree = verdicts.All(v => v.Valid)
            && verdicts.All(v => v.Verdict.Decision == first.Decision)
            && verdicts.All(v => string.Equals(v.Verdict.Resolution?.ChosenProposal, first.Resolution?.ChosenProposal, StringComparison.Ordinal));
        return agree
            ? Decided(first, Agree, verdicts.Select(v => v.Verdict.Confidence?.Level).Min())
            : new PanelOutcome(DebateOutcome.Unresolved, null, null, Disagree);
    }

    /// <summary>The decision of a person (D-26, «Decidir yo»): a decision of the verdict's kind, or none, which leaves the debate unresolved.</summary>
    public static PanelOutcome DecidedByUser(VerdictDecision? decision, string? chosenProposal) =>
        decision is { } decided
            ? new PanelOutcome(OutcomeOf(decided), decided, chosenProposal, User)
            : new PanelOutcome(DebateOutcome.Unresolved, null, chosenProposal, User);

    /// <summary>The outcome of a debate a decision gives: approving, asking for changes and rejecting resolve it.</summary>
    public static DebateOutcome OutcomeOf(VerdictDecision decision) => decision switch
    {
        VerdictDecision.Approved or VerdictDecision.ChangesRequested or VerdictDecision.Rejected => DebateOutcome.Resolved,
        VerdictDecision.Unresolved => DebateOutcome.Unresolved,
        VerdictDecision.EscalateToHuman => DebateOutcome.EscalatedToUser,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown verdict decision."),
    };

    /// <summary>
    /// Whether two identities are the same judge: the same agent on the same provider and vendor. Never the model: the
    /// system puts in a verdict the model the session of the CLI reported, which is not the alias configured (D-26).
    /// </summary>
    public static bool SameJudge(JudgeIdentity? left, JudgeIdentity? right) =>
        left?.Model is not null && right?.Model is not null
        && left.Agent == right.Agent
        && left.Model.Provider == right.Model.Provider
        && left.Model.Vendor == right.Model.Vendor;

    private static PanelOutcome Decided(Verdict verdict, string rule, ConfidenceLevel? confidence) =>
        new(OutcomeOf(verdict.Decision), verdict.Decision, verdict.Resolution?.ChosenProposal, rule) { Confidence = confidence };
}
