using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Judging;

public enum VerdictDecision
{
    Approved,
    ChangesRequested,
    Rejected,
    EscalateToHuman,

    /// <summary>Legitimate disagreement that the judge could not settle with the available evidence.</summary>
    Unresolved,
}

public enum AssessmentStatus
{
    Satisfied,
    NotSatisfied,
    NotVerifiable,
}

public sealed record RequirementAssessment(
    RequirementId Requirement,
    AssessmentStatus Status,
    IReadOnlyList<EvidenceId> Evidence,
    string Rationale);

/// <summary>What the judge must look for (master prompt, section 8).</summary>
public enum FindingCategory
{
    IgnoredRequirement,
    OmittedTask,
    FabricatedResult,
    TestsNotRun,
    ContradictoryDecision,
    OutOfScopeChange,
    IntegrationError,
    SecurityIssue,
    PrematureCompletion,
    InstructionViolation,
    Other,
}

public enum Severity
{
    Low,
    Medium,
    High,
    Critical,
}

public sealed record Finding(
    FindingCategory Category,
    Severity Severity,
    string Description,
    IReadOnlyList<EvidenceId> Evidence,
    IReadOnlyList<long> DecisionRefs);

public enum ConfidenceLevel
{
    Low,
    Medium,
    High,
}

public sealed record Confidence(ConfidenceLevel Level, string Justification);

public sealed record JudgeIdentity(AgentId Agent, ModelBinding Model);

/// <summary>What the judge of a debate says of one proposal (docs/15 D-25), by its label (<c>P1</c>, <c>P2</c>…).</summary>
public sealed record ProposalComparison(string Proposal, string Strengths, string Weaknesses);

/// <summary>
/// The structured resolution of the judge of a debate (docs/15 D-25): the proposal it chose, by its label, or none
/// (<paramref name="ChosenProposal"/> null, the sentinel <c>"ninguna"</c> of the schema), and the strengths and weaknesses of
/// each proposal. <see cref="VerdictValidator"/> checks that both name only proposals of the debate.
/// </summary>
public sealed record DebateResolution(string? ChosenProposal, IReadOnlyList<ProposalComparison> Comparison);

/// <summary>
/// Structured resolution of the independent judge (master prompt, section 7). It quotes the hashes of the frozen
/// requirements and of the decision log head it examined, so the verdict is tied to exactly that material.
/// </summary>
public sealed record Verdict(
    VerdictDecision Decision,
    string Summary,
    IReadOnlyList<RequirementAssessment> Assessments,
    IReadOnlyList<EvidenceId> EvidenceExamined,
    IReadOnlyList<string> PendingObjections,
    IReadOnlyList<string> Risks,
    IReadOnlyList<Finding> Findings,
    Confidence Confidence,
    IReadOnlyList<string> CorrectiveActions,
    bool RequiresHumanIntervention,
    JudgeIdentity Judge,
    string RequirementSetHash,
    string DecisionLogHeadHash,
    DateTimeOffset IssuedAt)
{
    /// <summary>
    /// The resolution of a debate (docs/15 D-25): the proposal chosen and the comparison of the proposals. Null outside the
    /// debates, so the verdict of a run review and every verdict stored before Phase 5 are read and validated as they were.
    /// </summary>
    public DebateResolution? Resolution { get; init; }
}

/// <summary>
/// What the dossier of a debate adds to the judge's material (docs/15 D-25): its mode, the labels of its proposals
/// (<c>P1</c>, <c>P2</c>…) and whether it is a debate that chooses among the contributions (<see cref="Choice"/>) or one
/// that reviews work produced by others. The dossier builder sets <see cref="Choice"/> from the debate, and for a challenge
/// from the object of the verdict it challenges; the validator reads it, never derives it.
/// </summary>
public sealed record DebateDossierContext(DebateMode Mode, IReadOnlyList<string> Proposals, bool Choice);

/// <summary>
/// Everything the judge receives. It is assembled by Agent Studio from its own stores, never by the orchestrator,
/// so the orchestrator cannot filter what the judge sees (master prompt, part 4, decision 2).
/// </summary>
public sealed record JudgeDossier(
    string OriginalRequest,
    FrozenRequirementSet Requirements,
    string OrchestratorPlan,
    IReadOnlyList<DecisionEntry> Decisions,
    string DecisionLogHeadHash,
    IReadOnlyList<EvidenceRecord> Evidence,
    IReadOnlyList<WorkTaskSnapshot> Tasks,
    IReadOnlySet<VendorId> ExcludedVendors)
{
    /// <summary>
    /// The commit under review, when the system knows it: the result of the run's last clean integration, as the
    /// decision log records it. Then only test runs that verified this exact commit
    /// (<see cref="EvidenceRecord.SubjectCommit"/>) count for approval: tests of another commit prove nothing about it.
    /// </summary>
    public string? CandidateCommit { get; init; }

    /// <summary>
    /// What the system found wrong with the material itself: evidence the log never registered, that disagrees with its
    /// entry, that changed after capture or cannot be read, a run header that disagrees with the log, a log that cannot
    /// be replayed. Any of them makes approval impossible.
    /// </summary>
    public IReadOnlyList<string> IntegrityProblems { get; init; } = [];

    /// <summary>
    /// The debate this dossier is about (docs/15 D-25); null for the review of a run and for a whole-project audit, whose
    /// rules are exactly those of Phase 3.
    /// </summary>
    public DebateDossierContext? Debate { get; init; }
}
