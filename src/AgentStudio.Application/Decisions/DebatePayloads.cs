using AgentStudio.Application.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Decisions;

// The payloads of Phase 5 (docs/15 D-03 and section 5.2). Like every payload they are written once, in the one format of
// DecisionPayloadSerializer, and the log hashes their text: to change one, add a field and raise V. None carries text of an
// agent (D-02): the question, a contribution, a quote of code and the reasons of a challenge are evidence, and a payload
// carries their id and SHA-256. Their run id is the debate (deb-yyyyMMdd-xxxxxx) or the audit (aud-yyyyMMdd-xxxxxx),
// except where noted, so RunLog.EntriesOf selects them as it selects those of a run.

/// <summary>A participant of a debate as <c>DebateOpened</c> records it: the agent, its role, where it runs and its label for the judge (<c>P1</c>…).</summary>
public sealed record DebateParticipantEntry(AgentId Agent, DebateRole Role, ProviderId Provider, VendorId Vendor, string? Model, string Label);

/// <summary>A judge of a debate or an audit: the agent and where it runs.</summary>
public sealed record DebateJudgeEntry(AgentId Agent, ProviderId Provider, VendorId Vendor, string? Model);

/// <summary>
/// A debate opens, after the user approved it and before its first turn (D-03 and D-06). It anchors the canonical hash of
/// the immutable header stored in <c>debate_sessions</c> (<paramref name="HeaderHash"/>, <c>DebateHeaderHash.Of</c>) and
/// the hash of the proposal the approval was bound to (<paramref name="ApprovedProposalHash"/>, <c>DebateHeaderHash.Proposal</c>),
/// so the log ties that approval to the debate that opened. The question is evidence (<paramref name="Question"/> and its
/// SHA-256). <paramref name="Challenges"/> is the verdict a revision debate challenges.
/// </summary>
public sealed record DebateOpenedPayload(
    DebateMode Mode,
    string HeaderHash,
    string ApprovedProposalHash,
    string RequirementSetHash,
    EvidenceId Question,
    string QuestionSha256,
    DebateSubjectKind Subject,
    string? SubjectRef,
    string? SnapshotCommit,
    IReadOnlyList<DebateParticipantEntry> Participants,
    IReadOnlyList<DebateJudgeEntry> Judges,
    DebateJudgingKind Judging,
    IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> WaivedParticipantVendors,
    int MaxRounds,
    long MaxDurationSeconds,
    long? MaxTotalTokens,
    int MaxAgentRuns,
    string? Challenges) : DecisionPayload;

/// <summary>
/// A contribution is published when its round closes (D-17), in the order of the header: its index (0-based, the one
/// <c>RepliesTo</c> points at), round, author, kind and the contribution it replies to; its evidence and SHA-256 (the
/// redacted text, D-10); the evidence it cites and the quotes that could not be resolved in the commit; how many secrets were
/// redacted; whether the participant marked it as finished; and the turn and attempt that produced it.
/// <paramref name="AnsweredAt"/> is when that turn answered, not when it was published (D-20).
/// </summary>
public sealed record DebateContributionRecordedPayload(
    int Index,
    int Round,
    AgentId Author,
    ContributionKind Kind,
    int? RepliesTo,
    EvidenceId Evidence,
    string Sha256,
    IReadOnlyList<EvidenceId> Cites,
    IReadOnlyList<string> UnresolvedCitations,
    int Redactions,
    bool Finished,
    string Turn,
    int Attempt,
    DateTimeOffset AnsweredAt) : DecisionPayload;

/// <summary>
/// A round closes (D-20): the phase that follows, why it went to judgment when it did and the limit reached, the tokens the
/// participants used so far with their origin (<c>DebateBudget.Used</c>), whether an estimate is among them, the seconds
/// since <c>DebateOpened</c> and the reason in Spanish (it is written by the system, never by an agent).
/// </summary>
public sealed record DebateRoundClosedPayload(
    int Round,
    DebatePhase Next,
    DebateJudgmentReason? Judgment,
    DebateLimitReached Limit,
    MetricSnapshot TokensUsed,
    bool TokensIncludeEstimates,
    long ElapsedSeconds,
    string Reason) : DecisionPayload;

/// <summary>
/// A debate ends (D-26): its outcome, the ids of its verdicts, the rule applied (<c>JudgingPanel.Single</c>, <c>Agree</c>,
/// <c>Disagree</c>, <c>Invalid</c> or <c>User</c>, and <c>aborted</c> for an aborted debate), the proposal chosen and the reason.
/// </summary>
public sealed record DebateClosedPayload(DebateOutcome Outcome, IReadOnlyList<string> Verdicts, string Rule, string? ChosenProposal, string Reason) : DecisionPayload
{
    /// <summary>The rule of a debate that was aborted, not judged.</summary>
    public const string AbortedRule = "aborted";
}

/// <summary>
/// A person challenges a stored verdict (D-38): the verdict, who challenges it, the reasons (a <c>HumanNote</c>, by id and
/// SHA-256) and the revision debate it opens. Its run id is that of the challenged verdict: the run, debate or audit.
/// </summary>
public sealed record VerdictChallengedPayload(string Verdict, string ChallengedBy, EvidenceId Reasons, string ReasonsSha256, string RevisionDebate) : DecisionPayload;

/// <summary>
/// The auditor of the orchestrator, or the judge of an orchestrator review, asks to replan a run (D-34 to D-36): the codes
/// of the findings, the tasks involved, the message and the verdict behind it, if any. Its run id is the run. Replanning is
/// proposing a follow-up run (ADR-0013): nothing is changed in the run itself.
/// </summary>
public sealed record ReplanRequestedPayload(IReadOnlyList<string> Findings, IReadOnlyList<TaskId> Tasks, string Message, string? Verdict) : DecisionPayload;

/// <summary>
/// An audit of the whole project starts (D-28 and D-29): the canonical hash of its header in <c>project_audits</c>, the scope,
/// the commit audited and whether there were uncommitted changes (which do not enter), the requirements, the hash of the
/// verification profile, the judge, the vendors that produced code according to the log and the authors the user declared.
/// </summary>
public sealed record ProjectAuditStartedPayload(
    string HeaderHash,
    AuditScope Scope,
    string? HeadCommit,
    bool UncommittedChanges,
    string RequirementSetHash,
    string VerificationProfileHash,
    DebateJudgeEntry Judge,
    IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> DeclaredAuthors) : DecisionPayload;

/// <summary>An audit of the whole project ends (D-31): its verdicts, the disposition, whether the report can be trusted and a summary written by the system.</summary>
public sealed record ProjectAuditFinishedPayload(IReadOnlyList<string> Verdicts, ReviewDisposition Disposition, bool Trustworthy, string Summary) : DecisionPayload;
