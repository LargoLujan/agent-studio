using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Debate.Judging.Debates;

/// <summary>What the judge sees of a participant (docs/15 D-15): its label and its role, never its agent, CLI, vendor or model.</summary>
public sealed record DebateSide(string Label, DebateRole Role);

/// <summary>
/// A contribution as the judge reads it (D-24): its label (<c>C1</c>…, from its index), the entry that published it, its
/// round, its author by label (<c>P1</c>…), its kind and the contribution it replies to, by label; its evidence and the
/// SHA-256 the log anchored; <see cref="Text"/>, the JSON the participant answered (redacted), read against that hash and
/// null when it cannot be read or does not match (a system check says why); the evidence it cites, the quotes the system
/// could not resolve in the commit, the secrets redacted, whether it was marked finished and when its turn answered.
/// </summary>
public sealed record DebateContributionMaterial(
    long Sequence,
    string Label,
    int Index,
    int Round,
    string Author,
    ContributionKind Kind,
    string? RepliesTo,
    EvidenceId Evidence,
    string Sha256,
    string? Text,
    IReadOnlyList<EvidenceId> Cites,
    IReadOnlyList<string> UnresolvedCitations,
    int Redactions,
    bool Finished,
    DateTimeOffset AnsweredAt);

/// <summary>
/// A round as the log closed it (D-20): the phase that followed and, when it went to judgment, why and which limit. The
/// tokens are left out on purpose: the source of each figure names the CLI that reported it.
/// </summary>
public sealed record DebateRoundMaterial(long Sequence, DateTimeOffset At, int Round, DebatePhase Next, DebateJudgmentReason? Judgment, DebateLimitReached Limit, long ElapsedSeconds);

/// <summary>
/// An entry of the protocol of a debate as the judge may see it (D-15): the opening (without vendors), a contribution, the
/// close of a round or the registration of an evidence, by sequence, with a reference the system writes (a label, a round,
/// an evidence id). Never the launches of the agents, nor the payloads, which name them.
/// </summary>
public sealed record DebateProtocolEntry(long Sequence, DateTimeOffset At, DecisionKind Kind, string Reference);

/// <summary>
/// One debate, blind (D-15 and D-24): what it is about, the literal question (read against the hash <c>DebateOpened</c>
/// anchored, or a note in its place), the participants by label and role, the proposals that can be chosen, the
/// contributions in order, the rounds, the limits and its evidence, whose titles and authors name participants only by
/// their labels. <see cref="Proposals"/> are the labels of the participants whose round-1 contribution is a proposal that
/// can be chosen: every participant in a free debate and an architecture review, the proponents in an adversarial one, and
/// none in a tribunal or a debate that reviews a run. <see cref="Quotes"/> are the lines of code the contributions quote,
/// captured by the system from the commit and read against their anchored hash, by evidence.
/// </summary>
public sealed record DebateMaterial(
    RunId Debate,
    DebateMode Mode,
    DebateSubjectKind Subject,
    string? SubjectReference,
    string? SnapshotCommit,
    string Question,
    EvidenceId QuestionEvidence,
    long OpenedSequence,
    DateTimeOffset OpenedAt,
    IReadOnlyList<DebateSide> Participants,
    IReadOnlyList<string> Proposals,
    IReadOnlyList<DebateContributionMaterial> Contributions,
    IReadOnlyList<DebateRoundMaterial> Rounds,
    DebateLimits Limits,
    int MaxAgentRuns,
    IReadOnlyList<EvidenceRecord> Evidence,
    IReadOnlyDictionary<EvidenceId, string> Quotes,
    IReadOnlyList<DebateProtocolEntry> Protocol)
{
    /// <summary>Why the debate went to judgment, from the last round the log closed into judgment; null if none did.</summary>
    public DebateRoundMaterial? Judgment => Rounds.LastOrDefault(r => r.Judgment is not null);
}

/// <summary>A run the debate is about, or the object of a challenged verdict: its whole dossier of Phase 3 (D-24), with its producers in sight (D-15).</summary>
public sealed record RunSubject(RunId Run, JudgeDossier Dossier);

/// <summary>
/// A whole-project audit that is the object of a challenged verdict (D-38): what its log says (the commit audited, the
/// producers and the authors declared) and the evidence it registered (the verification of HEAD).
/// </summary>
public sealed record AuditSubject(RunId Audit, string? HeadCommit, string? RequirementSetHash, IReadOnlyList<VendorId> Producers, IReadOnlyList<EvidenceRecord> Evidence);

/// <summary>What a challenged verdict was about (D-38).</summary>
public enum ChallengedObject
{
    /// <summary>The verdict is not in the store, so its object is unknown: the rules of Phase 3 apply.</summary>
    Unknown,
    Run,
    Debate,
    Audit,
}

/// <summary>
/// A revision debate (D-38): the challenged verdict as the store holds it (null when it is not there), what it was about
/// and that material, rebuilt from the stores, and the reasons of the person who challenged it, read against the SHA-256
/// <c>VerdictChallenged</c> anchored (null when they cannot be). The challenged verdict is what a judge said: a claim to
/// check, never proof. When the object is a debate, <see cref="Debate"/> is that debate, blind, and <see cref="DebateRun"/>
/// the run it was about, if any.
/// </summary>
public sealed record ChallengeSubject(
    string Verdict,
    StoredVerdict? Stored,
    ChallengedObject Object,
    string? ChallengedBy,
    EvidenceId? Reasons,
    string? ReasonsText,
    RunSubject? Run,
    DebateMaterial? Debate,
    RunSubject? DebateRun,
    AuditSubject? Audit);

/// <summary>
/// The material of the judgment of a debate (D-24): the debate, blind; what the validator checks (<see cref="Dossier"/>:
/// the requirements, the head of the log the verdict must echo, every evidence that can be cited, the vendors no judge may
/// run on, the commit under review when the subject is a run, the system checks and <see cref="JudgeDossier.Debate"/>); and
/// the subject: the run it is about, or the challenge it answers.
/// </summary>
public sealed record DebateDossier(DebateMaterial Debate, JudgeDossier Dossier, RunSubject? Run, ChallengeSubject? Challenge)
{
    /// <summary>The labels the judge may choose among and compare, in the order of the header (D-25); for a challenge of a debate, those of the challenged debate.</summary>
    public IReadOnlyList<string> Proposals => Dossier.Debate?.Proposals ?? [];
}

/// <summary>
/// A dossier of a debate and its package under the dossier root. <see cref="Header"/> is the immutable header the judges
/// come from, and <see cref="HeaderVerified"/> says it hashes to what <c>DebateOpened</c> anchored (N-05): a judge is never
/// launched on one that does not. <see cref="Judging"/> and <see cref="Participants"/> are the arrangement and the
/// participants of the header, as the domain checks them. <see cref="Snapshot"/> is the copy of <see cref="CodeCommit"/>
/// the judges work in (inside the package), or null when there is no commit or it could not be copied.
/// </summary>
public sealed record DebateDossierPackage(
    DebateDossier Dossier,
    DossierFiles Files,
    DebateRecord Header,
    bool HeaderVerified,
    bool DecisionLogIntact,
    JudgingArrangement Judging,
    IReadOnlyList<DebateParticipant> Participants,
    string? CodeCommit,
    string? Snapshot)
{
    /// <summary>The file of the package with the dossier in the order the judge at <paramref name="seat"/> reads it: the second judge of a panel reads the proposals reversed (D-14).</summary>
    public string DossierFile(int seat) => seat % 2 == 1 ? DebateDossierBuilder.ReversedDossierFile : DebateDossierBuilder.DossierFile;
}
