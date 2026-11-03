using System.Globalization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Domain.Debate;

/// <summary>Debate modes required by the master prompt (section 7).</summary>
public enum DebateMode
{
    Free,
    Adversarial,
    ArchitectureReview,
    ImplementationAudit,
    OrchestratorReview,
    Tribunal,
}

public enum DebateRole
{
    Proponent,
    Critic,
    Panelist,
}

public sealed record DebateParticipant(AgentId Agent, DebateRole Role, ModelBinding Model);

/// <summary>Limits that keep a debate finite. The first one reached sends the debate to judgment.</summary>
public sealed record DebateLimits(int MaxRounds, TimeSpan MaxDuration, long? MaxTotalTokens = null)
{
    public static readonly DebateLimits Default = new(MaxRounds: 4, MaxDuration: TimeSpan.FromMinutes(30), MaxTotalTokens: 400_000);
}

public enum DebatePhase
{
    /// <summary>Round 1: each participant submits an independent proposal without seeing the others.</summary>
    Proposals,

    /// <summary>Rounds 2..N: objections, rebuttals and evidence.</summary>
    Deliberation,
    AwaitingJudgment,
    Closed,
}

public enum DebateOutcome
{
    Open,
    Resolved,
    Unresolved,
    EscalatedToUser,
    Aborted,
}

public enum ContributionKind
{
    Proposal,
    Assessment,
    Objection,
    Rebuttal,
    Evidence,
}

public enum DebateLimitReached
{
    None,
    Rounds,
    Duration,
    Tokens,
}

/// <summary>Why a debate went to judgment (docs/15 D-20); <c>DebateRoundClosed</c> records it.</summary>
public enum DebateJudgmentReason
{
    /// <summary>A person asked for judgment now («Pedir juicio ahora»).</summary>
    Requested,

    /// <summary>Every participant marked its contribution as finished in the same round: the positions converged.</summary>
    Converged,

    /// <summary>A limit was reached (<see cref="DebateSession.LimitReached"/> says which), also one a projection of the tokens decided before a round.</summary>
    Limit,

    /// <summary>A turn failed twice and a person asked for judgment with what there is (D-22).</summary>
    TurnMissing,

    /// <summary>A tribunal does not deliberate: after its independent assessments the judge synthesises.</summary>
    Tribunal,
}

/// <summary>
/// One contribution. In Phase 5 the text of an agent never leaves the evidence store (docs/15 D-02): the coordinator
/// puts in <see cref="Content"/> the reference to the contribution's evidence, and <see cref="At"/> is when its turn
/// answered, not when it was published at the end of its round (D-20).
/// </summary>
public sealed record DebateContribution(
    int Round,
    AgentId Author,
    ContributionKind Kind,
    string Content,
    IReadOnlyList<EvidenceId> Evidence,
    DateTimeOffset At,
    int? RepliesTo = null);

/// <summary>
/// One debate in the Debate Arena. It follows the protocol of the master prompt: concrete question, acceptance
/// criteria, frozen requirements, independent proposals, objections and rebuttals with evidence, and a structured
/// verdict from a judge who does not take part and (by default) runs on a different vendor.
/// </summary>
/// <remarks>
/// Phase 5 (docs/15 §5.1) gives a debate a subject and a judging arrangement (<see cref="JudgingArrangement"/>, checked
/// with <see cref="DebateJudgingRules"/>), records why it went to judgment (<see cref="JudgmentReason"/>) and closes it with
/// the outcome of <see cref="JudgingPanel.Combine"/> and its valid verdicts, comparing every judge by agent, provider and
/// vendor, never by model (D-26). The state is never stored: <see cref="DebateStateReplayer"/> rebuilds it from the log
/// through this same API.
/// </remarks>
public sealed class DebateSession
{
    private readonly List<DebateContribution> _contributions = [];
    private IReadOnlyList<Verdict> _verdicts = [];

    private DebateSession(
        DebateId id,
        DebateMode mode,
        DebateSubjectKind subject,
        string question,
        IReadOnlyList<string> acceptanceCriteria,
        FrozenRequirementSet requirements,
        IReadOnlyList<DebateParticipant> participants,
        JudgingArrangement judging,
        DebateLimits limits,
        DateTimeOffset startedAt)
    {
        Id = id;
        Mode = mode;
        Subject = subject;
        Question = question;
        AcceptanceCriteria = acceptanceCriteria;
        Requirements = requirements;
        Participants = participants;
        Judging = judging;
        Limits = limits;
        StartedAt = startedAt;
    }

    public DebateId Id { get; }

    public DebateMode Mode { get; }

    /// <summary>What the debate is about. A debate opened with the Phase 0 overload is about nothing (<see cref="DebateSubjectKind.None"/>).</summary>
    public DebateSubjectKind Subject { get; }

    public string Question { get; }

    public IReadOnlyList<string> AcceptanceCriteria { get; }

    public FrozenRequirementSet Requirements { get; }

    public IReadOnlyList<DebateParticipant> Participants { get; }

    /// <summary>Who judges the debate and with which waivers (docs/15 D-12 to D-14).</summary>
    public JudgingArrangement Judging { get; }

    /// <summary>The (first) judge of <see cref="Judging"/>: the one judge of the debate unless it is a cross-vendor panel.</summary>
    public JudgeIdentity Judge => Judging.Judges[0];

    public DebateLimits Limits { get; }

    public DateTimeOffset StartedAt { get; }

    public DebatePhase Phase { get; private set; } = DebatePhase.Proposals;

    public DebateOutcome Outcome { get; private set; } = DebateOutcome.Open;

    public int CurrentRound { get; private set; } = 1;

    public DebateLimitReached LimitReached { get; private set; } = DebateLimitReached.None;

    /// <summary>Why the debate went to judgment; null while it has not.</summary>
    public DebateJudgmentReason? JudgmentReason { get; private set; }

    /// <summary>What closed the debate: the combined judgment, or the decision of a person; null while it is not closed (or was aborted).</summary>
    public PanelOutcome? Panel { get; private set; }

    /// <summary>The valid verdicts the debate was closed with: one, one per judge of a panel, or none when a person decided without them.</summary>
    public IReadOnlyList<Verdict> Verdicts => _verdicts;

    /// <summary>The (first) verdict the debate was closed with, or null.</summary>
    public Verdict? Verdict => _verdicts.Count > 0 ? _verdicts[0] : null;

    public string? CloseReason { get; private set; }

    public IReadOnlyList<DebateContribution> Contributions => _contributions;

    /// <summary>
    /// Opens a debate with one judge (the Phase 0 contract). With <paramref name="requireIndependentJudge"/> the judge must
    /// run on a vendor no participant uses (<see cref="DebateJudgingKind.IndependentJudge"/>, no waiver); without it, every
    /// vendor of the participants is waived, so the judge may share one.
    /// </summary>
    public static DebateSession Open(
        DebateId id,
        DebateMode mode,
        string question,
        IReadOnlyList<string> acceptanceCriteria,
        FrozenRequirementSet requirements,
        IReadOnlyList<DebateParticipant> participants,
        JudgeIdentity judge,
        DebateLimits limits,
        DateTimeOffset startedAt,
        bool requireIndependentJudge = true)
    {
        ArgumentNullException.ThrowIfNull(acceptanceCriteria);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(limits);

        CheckDebate(question, acceptanceCriteria, limits, participants);

        if (participants.Any(p => p.Agent == judge.Agent))
        {
            throw Violation("debate.judge-not-participant", "The judge cannot also be a participant.");
        }

        CheckRoles(mode, participants);

        if (requireIndependentJudge && participants.Any(p => p.Model.Vendor == judge.Model.Vendor))
        {
            throw Violation("debate.judge-independence", $"The judge vendor '{judge.Model.Vendor}' is also used by a participant.");
        }

        var waived = requireIndependentJudge ? new HashSet<VendorId>() : participants.Select(p => p.Model.Vendor).ToHashSet();
        var judging = new JudgingArrangement(DebateJudgingKind.IndependentJudge, [judge], new HashSet<VendorId>(), waived);
        return new DebateSession(id, mode, DebateSubjectKind.None, question, [.. acceptanceCriteria], requirements, [.. participants], judging, limits, startedAt);
    }

    /// <summary>
    /// Opens a debate of Phase 5 (docs/15 §5.1) about <paramref name="subject"/>, judged as <paramref name="judging"/> says.
    /// Its acceptance criteria are those of the frozen requirements (<c>R1/c1: description</c>). The mode must debate that
    /// subject (D-07: an implementation audit and an orchestrator review are about a run; a challenge is adversarial or a
    /// tribunal; the others are about nothing or a commit), and the arrangement must be one
    /// <see cref="DebateJudgingRules.Violations"/> allows.
    /// </summary>
    public static DebateSession Open(
        DebateId id,
        DebateMode mode,
        DebateSubjectKind subject,
        string question,
        FrozenRequirementSet requirements,
        IReadOnlyList<DebateParticipant> participants,
        JudgingArrangement judging,
        DebateLimits limits,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(judging);
        ArgumentNullException.ThrowIfNull(limits);

        var criteria = requirements.Requirements
            .SelectMany(r => r.Criteria.Select(c => string.Create(CultureInfo.InvariantCulture, $"{r.Id}/{c.Id}: {c.Description}")))
            .ToList();
        CheckDebate(question, criteria, limits, participants);
        CheckRoles(mode, participants);

        if (!Debates(mode, subject))
        {
            throw Violation("debate.subject-mode", $"A {mode} debate cannot be about a {subject} subject.");
        }

        var broken = DebateJudgingRules.Violations(judging, mode, subject, participants);
        if (broken.Count > 0)
        {
            throw Violation(broken[0], $"The judging arrangement ({judging.Kind}) is not allowed for this debate: {string.Join(", ", broken)}.");
        }

        // A copy, so a set the caller still holds cannot change who may judge the debate afterwards.
        var copy = judging with
        {
            Judges = [.. judging.Judges],
            ProducerVendors = new HashSet<VendorId>(judging.ProducerVendors ?? new HashSet<VendorId>()),
            WaivedParticipantVendors = new HashSet<VendorId>(judging.WaivedParticipantVendors ?? new HashSet<VendorId>()),
        };
        return new DebateSession(id, mode, subject, question, criteria, requirements, [.. participants], copy, limits, startedAt);
    }

    public void Submit(DebateContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentException.ThrowIfNullOrWhiteSpace(contribution.Content);

        if (Participants.All(p => p.Agent != contribution.Author))
        {
            throw Violation("debate.author-not-participant", $"'{contribution.Author}' is not a participant.");
        }

        if (contribution.Round != CurrentRound)
        {
            throw Violation("debate.wrong-round", $"Contribution for round {contribution.Round} but the debate is in round {CurrentRound}.");
        }

        if (contribution.At > StartedAt + Limits.MaxDuration)
        {
            throw Violation("debate.time-limit", "The contribution is dated after the debate's time limit.");
        }

        if (contribution.RepliesTo is { } target && (target < 0 || target >= _contributions.Count))
        {
            throw Violation("debate.reply-target", "A reply must point to an existing contribution.");
        }

        switch (Phase)
        {
            case DebatePhase.Proposals:
                var expected = Mode == DebateMode.Tribunal ? ContributionKind.Assessment : ContributionKind.Proposal;
                if (contribution.Kind != expected || contribution.RepliesTo is not null)
                {
                    throw Violation("debate.independent-proposals", $"Round 1 only accepts independent {expected} contributions.");
                }

                if (_contributions.Any(c => c.Round == 1 && c.Author == contribution.Author))
                {
                    throw Violation("debate.one-proposal", $"'{contribution.Author}' already submitted in round 1.");
                }

                break;
            case DebatePhase.Deliberation:
                if (contribution.Kind is ContributionKind.Proposal or ContributionKind.Assessment)
                {
                    throw Violation("debate.deliberation-kinds", "Deliberation accepts objections, rebuttals and evidence.");
                }

                break;
            default:
                throw Violation("debate.closed-for-contributions", $"The debate no longer accepts contributions ({Phase}).");
        }

        _contributions.Add(contribution);
    }

    /// <summary>
    /// Moves to the next round, or to judgment when a limit is reached. Tribunals skip deliberation: panelists assess
    /// independently and the judge synthesises. Going to judgment records why (<see cref="DebateJudgmentReason.Limit"/> or
    /// <see cref="DebateJudgmentReason.Tribunal"/>).
    /// </summary>
    public void AdvanceRound(DateTimeOffset now, long tokensUsed)
    {
        EnsureOpenForDebate();

        if (Phase == DebatePhase.Proposals && MissesRoundOne())
        {
            throw Violation("debate.missing-proposals", "Every participant must submit in round 1 before deliberation starts.");
        }

        var limit = CheckLimits(now, tokensUsed, CurrentRound + 1);
        if (limit != DebateLimitReached.None || Mode == DebateMode.Tribunal)
        {
            LimitReached = limit;
            JudgmentReason = limit != DebateLimitReached.None ? DebateJudgmentReason.Limit : DebateJudgmentReason.Tribunal;
            Phase = DebatePhase.AwaitingJudgment;
            return;
        }

        CurrentRound++;
        Phase = DebatePhase.Deliberation;
    }

    /// <summary>Asks for judgment before the limits are reached (for example, because the positions converged).</summary>
    public void RequestJudgment() => RequestJudgment(DebateJudgmentReason.Requested);

    /// <summary>
    /// Sends the debate to judgment now, for <paramref name="reason"/> (docs/15 D-20 and D-22): also the cut a projection
    /// of the tokens decides before a round, which <see cref="AdvanceRound"/> alone would not see, and the judgment with a
    /// turn missing a person asks for. Only <see cref="DebateJudgmentReason.Limit"/> names a limit, and it must. A
    /// convergence and the synthesis of a tribunal need every contribution of round 1; the other reasons judge what there is.
    /// The round does not advance.
    /// </summary>
    public void RequestJudgment(DebateJudgmentReason reason, DebateLimitReached limit = DebateLimitReached.None)
    {
        EnsureOpenForDebate();

        if (_contributions.Count == 0)
        {
            throw Violation("debate.nothing-to-judge", "A debate without any contribution cannot be sent to judgment.");
        }

        if ((reason == DebateJudgmentReason.Limit) != (limit != DebateLimitReached.None))
        {
            throw Violation("debate.judgment-limit", $"A judgment for a limit names the limit, and no other reason does ({reason}, {limit}).");
        }

        if (reason == DebateJudgmentReason.Tribunal && Mode != DebateMode.Tribunal)
        {
            throw Violation("debate.judgment-tribunal", $"Only a tribunal goes to judgment to be synthesised; this debate is {Mode}.");
        }

        if (reason is DebateJudgmentReason.Converged or DebateJudgmentReason.Tribunal && Phase == DebatePhase.Proposals && MissesRoundOne())
        {
            throw Violation("debate.missing-proposals", $"A {reason} judgment needs every participant's contribution of round 1.");
        }

        LimitReached = limit;
        JudgmentReason = reason;
        Phase = DebatePhase.AwaitingJudgment;
    }

    /// <summary>Which limit (if any) is exceeded at <paramref name="now"/>, for the given round number.</summary>
    public DebateLimitReached CheckLimits(DateTimeOffset now, long tokensUsed, int round)
    {
        if (round > Limits.MaxRounds)
        {
            return DebateLimitReached.Rounds;
        }

        if (now - StartedAt > Limits.MaxDuration)
        {
            return DebateLimitReached.Duration;
        }

        return tokensUsed > Limits.MaxTotalTokens ? DebateLimitReached.Tokens : DebateLimitReached.None;
    }

    /// <summary>Closes the debate with the verdict of its one judge, as Phase 0 did: <see cref="JudgingPanel.Combine"/> with that verdict, valid.</summary>
    public void Close(Verdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        Close(JudgingPanel.Combine(Judging, [(verdict, true)]), [verdict]);
    }

    /// <summary>
    /// Closes the debate (docs/15 D-26) with what <see cref="JudgingPanel.Combine"/> decided, or with the decision of a person
    /// (<see cref="JudgingPanel.DecidedByUser"/>), and the valid verdicts behind it: one, one per judge of a panel, or none.
    /// Every verdict must come from a judge of the arrangement (by agent, provider and vendor, never by model), each judge
    /// at most once, all about the requirements of the debate and all echoing the same head of the log. A decision of one
    /// judge or of a panel needs its verdicts, and they must say that decision; a disagreement or an invalid set closes it
    /// only as needing a person.
    /// </summary>
    public void Close(PanelOutcome outcome, IReadOnlyList<Verdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(verdicts);

        if (Phase != DebatePhase.AwaitingJudgment)
        {
            throw Violation("debate.close-needs-judgment-phase", "A debate can only be closed after it is sent to judgment.");
        }

        if (outcome.Outcome is DebateOutcome.Open or DebateOutcome.Aborted)
        {
            throw Violation("debate.close-outcome", $"A debate is not closed {outcome.Outcome}: it is aborted with Abort.");
        }

        if (!JudgingPanel.Rules.Contains(outcome.Rule, StringComparer.Ordinal))
        {
            throw Violation("debate.close-rule", $"'{outcome.Rule}' is not a rule a debate is closed with ({string.Join(", ", JudgingPanel.Rules)}).");
        }

        foreach (var verdict in verdicts)
        {
            ArgumentNullException.ThrowIfNull(verdict, nameof(verdicts));
            if (!Judging.Judges.Any(j => JudgingPanel.SameJudge(j, verdict.Judge)))
            {
                throw Violation("debate.verdict-from-other-judge", "The verdict was not issued by this debate's judge.");
            }

            if (verdict.RequirementSetHash != Requirements.ContentHash)
            {
                throw Violation("debate.verdict-requirements", "The verdict refers to a different requirement set.");
            }
        }

        var judges = verdicts.Select(v => Judging.Judges.First(j => JudgingPanel.SameJudge(j, v.Judge))).ToList();
        if (judges.Distinct().Count() != judges.Count)
        {
            throw Violation("debate.verdict-duplicate-judge", "A judge gave more than one of the verdicts the debate is closed with.");
        }

        if (verdicts.Select(v => v.DecisionLogHeadHash).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw Violation("debate.verdict-heads", "The verdicts echo different heads of the decision log: they are not about the same material.");
        }

        var supported = outcome.Rule switch
        {
            JudgingPanel.Single => verdicts.Count == 1 && Decides(outcome, verdicts),
            JudgingPanel.Agree => verdicts.Count == Judging.Judges.Count && verdicts.Count > 1 && Decides(outcome, verdicts),
            JudgingPanel.Disagree or JudgingPanel.Invalid => outcome.NeedsHuman && outcome.Decision is null,
            _ => true, // a person decided, with the verdicts there are
        };
        if (!supported)
        {
            throw Violation("debate.close-unsupported", $"The verdicts do not support closing the debate {outcome.Outcome} by the rule '{outcome.Rule}'.");
        }

        _verdicts = [.. verdicts];
        Panel = outcome;
        Phase = DebatePhase.Closed;
        Outcome = outcome.Outcome;
        CloseReason = verdicts.Count == 1 ? verdicts[0].Summary : outcome.Rule;
    }

    public void Abort(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Phase == DebatePhase.Closed)
        {
            throw Violation("debate.already-closed", "The debate is already closed.");
        }

        Phase = DebatePhase.Closed;
        Outcome = DebateOutcome.Aborted;
        CloseReason = reason;
    }

    /// <summary>The checks every debate passes, in the order of Phase 0.</summary>
    private static void CheckDebate(string question, IReadOnlyList<string> acceptanceCriteria, DebateLimits limits, IReadOnlyList<DebateParticipant> participants)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw Violation("debate.question-required", "A debate needs a concrete question.");
        }

        if (acceptanceCriteria.Count == 0 || acceptanceCriteria.Any(string.IsNullOrWhiteSpace))
        {
            throw Violation("debate.criteria-required", "A debate needs explicit acceptance criteria.");
        }

        if (limits.MaxRounds < 1 || limits.MaxDuration <= TimeSpan.Zero || limits.MaxTotalTokens is <= 0)
        {
            throw Violation("debate.limits-must-be-positive", "Debate limits must be positive.");
        }

        if (participants.Select(p => p.Agent).Distinct().Count() != participants.Count)
        {
            throw Violation("debate.duplicate-participant", "An agent can take part only once.");
        }

        if (participants.Count < 2)
        {
            throw Violation("debate.two-participants", "A debate needs at least two participants besides the judge.");
        }
    }

    /// <summary>The roles each mode needs (docs/15 D-07).</summary>
    private static void CheckRoles(DebateMode mode, IReadOnlyList<DebateParticipant> participants)
    {
        switch (mode)
        {
            case DebateMode.Adversarial or DebateMode.ImplementationAudit
                when !participants.Any(p => p.Role == DebateRole.Proponent) || !participants.Any(p => p.Role == DebateRole.Critic):
                throw Violation("debate.adversarial-roles", $"{mode} needs at least one proponent and one critic.");
            case DebateMode.Tribunal when participants.Any(p => p.Role != DebateRole.Panelist):
                throw Violation("debate.tribunal-roles", "A tribunal is made only of panelists.");
            case DebateMode.OrchestratorReview when participants.Any(p => p.Role == DebateRole.Proponent):
                throw Violation("debate.reviewer-roles", "An orchestrator review is made of reviewers (critics or panelists).");
        }
    }

    /// <summary>Which subjects each mode debates (docs/15 D-07 and D-38).</summary>
    private static bool Debates(DebateMode mode, DebateSubjectKind subject) => mode switch
    {
        DebateMode.Free or DebateMode.ArchitectureReview => subject is DebateSubjectKind.None or DebateSubjectKind.Commit,
        DebateMode.Adversarial => subject is DebateSubjectKind.None or DebateSubjectKind.Commit or DebateSubjectKind.Verdict,
        DebateMode.ImplementationAudit or DebateMode.OrchestratorReview => subject == DebateSubjectKind.Run,
        DebateMode.Tribunal => Enum.IsDefined(subject),
        _ => false,
    };

    private static bool Decides(PanelOutcome outcome, IReadOnlyList<Verdict> verdicts) =>
        outcome.Decision is { } decision
        && outcome.Outcome == JudgingPanel.OutcomeOf(decision)
        && verdicts.All(v => v.Decision == decision);

    private bool MissesRoundOne() => Participants.Any(p => _contributions.All(c => c.Round != 1 || c.Author != p.Agent));

    private void EnsureOpenForDebate()
    {
        if (Phase is DebatePhase.AwaitingJudgment or DebatePhase.Closed)
        {
            throw Violation("debate.not-open", $"The debate is in phase {Phase}.");
        }
    }

    private static DomainRuleViolationException Violation(string rule, string message) => new(rule, message);
}
