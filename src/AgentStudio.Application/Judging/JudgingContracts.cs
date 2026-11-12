using AgentStudio.Application.Coordination;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Judging;

// Phase 3 brought forward the part of these contracts that reviews an orchestration run (docs/13 section 3.5); Phase 5 adds
// the audit of the whole project, the auditor of the orchestrator and the challenge of a verdict (docs/15 section 5.4).

/// <summary>An independent judge. Implementations run on a provider chosen by the user, read-only.</summary>
public interface IJudge
{
    JudgeIdentity Identity { get; }

    Task<Verdict> EvaluateAsync(JudgeDossier dossier, CancellationToken cancellationToken);

    /// <summary>
    /// The correction round: the judge sees its previous verdict and the rules it broke (<see cref="VerdictValidator"/>)
    /// and answers again. There is at most one.
    /// </summary>
    Task<Verdict> ReviseAsync(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, CancellationToken cancellationToken);
}

public enum AuditScope
{
    /// <summary>The "Auditar proyecto completo" button: requirements, code, tests, docs and orchestrator decisions.</summary>
    WholeProject,
    CurrentPlan,
    OrchestratorDecisions,

    /// <summary>The judgment of a debate (docs/15 D-23). Appended: <c>verdicts.scope</c> stores the name.</summary>
    Debate,
}

/// <summary>What to build a dossier about: a whole project or, with <see cref="RunId"/>, one orchestration run.</summary>
public sealed record DossierRequest(string ProjectId, AuditScope Scope, string? RunId = null);

/// <summary>
/// Builds the dossier straight from Agent Studio's stores (requirements, decision log, evidence, tasks). The
/// orchestrator has no access to this component, so it cannot choose what the judge sees. Callers pass identifiers,
/// never material.
/// </summary>
public interface IJudgeDossierBuilder
{
    Task<JudgeDossier> BuildAsync(DossierRequest request, CancellationToken cancellationToken);
}

// ---- Phase 5: auditing the whole project, auditing the orchestrator and challenging a verdict (docs/15 section 5.4) ----

/// <summary>
/// «Auditar proyecto completo» (D-28): the frozen requirements to audit against (by hash), the judge of the template and the
/// vendors of the authors the user declares, because the log does not know who wrote what Agent Studio did not.
/// </summary>
public sealed record ProjectAuditRequest(ProjectContext Project, string RequirementSetHash, AgentId Judge, IReadOnlyList<VendorId> DeclaredAuthors, AuditMilestoneScope? Milestone = null);

/// <summary>
/// What closing a milestone narrows an audit to (docs/16 D-49): the milestone, and the runs (the linked ones with their follow-ups)
/// and debates the dossier of the judge lists. Null on a request is the whole project. The rule of the producers is not narrowed:
/// the code at HEAD is the project's, so the vendors of every run of the log are still excluded from judging.
/// </summary>
public sealed record AuditMilestoneScope(string Milestone, string Name, string Objective, IReadOnlyList<string> Runs, IReadOnlyList<string> Debates);

/// <summary>
/// What the preparation of an audit shows; nothing is written (D-06 and D-43). <see cref="Judge"/> is null when nobody may
/// judge (rule P with every vendor a producer, or a log that does not verify), and <see cref="Problems"/> says why; the
/// audit then reports what the system checks without a judge. <see cref="SkippedSteps"/> are the steps of the profile
/// that do not run (<c>isolation: none</c>, D-27). <see cref="HeaderHash"/> is the hash of what is shown
/// (<c>DebateHeaderHash.Proposal</c>) that the approval (<c>audit.run</c>) binds.
/// </summary>
public sealed record ProjectAuditProposal(
    ProjectAuditRequest Request,
    string? HeadCommit,
    bool UncommittedChanges,
    AgentDefinition? Judge,
    IReadOnlyList<VendorId> ProducerVendors,
    VerificationProfile? Verification,
    IReadOnlyList<string> SkippedSteps,
    MetricValue<long> EstimatedTokens,
    IReadOnlyList<string> Problems,
    string HeaderHash,
    string ApprovalText,
    IReadOnlyList<UserText>? ProblemTexts = null)
{
    /// <summary>Whether a judge will be launched: there is one and nothing stops it.</summary>
    public bool CanJudge => Judge is not null && Problems.Count == 0;
}

/// <summary>
/// A problem the system lists without a judge (D-31): its stable code, where it is (a run, a debate, a memory entry) and what it is,
/// in Spanish. <paramref name="Message"/> is what the dossier of the judge and the log carry, so it never changes; <paramref name="Text"/>
/// says the same as a coded text that the desktop paints in the language of the person (docs/17 D-47), or null when a producer gives
/// only the message.
/// </summary>
public sealed record PendingProblem(string Code, string Source, string Message, UserText? Text = null);

/// <summary>
/// The conformity report of an audit (D-31). It replaces the record of Phase 0, which nothing implemented.
/// <see cref="Verdict"/> is the system's escalation (never stored as a judge's) when no judge ran, and then
/// <see cref="VerdictId"/> is null. <see cref="Verification"/> is the evidence of the verification of HEAD.
/// </summary>
public sealed record AuditReport(
    RunId Audit,
    AuditScope Scope,
    string? HeadCommit,
    bool UncommittedChanges,
    Verdict Verdict,
    string? VerdictId,
    IReadOnlyList<VerdictViolation> ValidationIssues,
    bool Corrected,
    bool DecisionLogIntact,
    ReviewDisposition Disposition,
    IReadOnlyList<EvidenceRecord> Verification,
    IReadOnlyList<PendingProblem> Pending,
    DateTimeOffset GeneratedAt,
    UserText? EscalationText = null)
{
    /// <summary>The verdict is usable only if a judge gave it, it passed validation and the log it refers to is intact.</summary>
    public bool IsTrustworthy => ValidationIssues.Count == 0 && DecisionLogIntact && VerdictId is not null;
}

/// <summary>Use case behind the "Auditar proyecto completo" button (D-28 to D-31).</summary>
public interface IProjectAuditService
{
    /// <summary>The judge (rule P with the producers of the log and the declared authors), the profile and the estimate. Writes nothing.</summary>
    Task<ProjectAuditProposal> PrepareAsync(ProjectAuditRequest request, CancellationToken ct);

    /// <summary>
    /// Asks the approval (<c>IApprovalGate</c>, action <c>audit.run</c>), verifies the log (broken: no judge, the report says
    /// so), records <c>ProjectAuditStarted</c>, verifies HEAD in isolation, builds the index dossier, runs the judge with one
    /// correction and records <c>VerdictIssued</c> and <c>ProjectAuditFinished</c>.
    /// </summary>
    Task<AuditReport> AuditAsync(ProjectAuditProposal proposal, IProgress<AgentEvent>? progress, CancellationToken ct);
}

/// <summary>
/// What the auditor of the orchestrator found in a run (D-33): the stable code (<c>orchestrator.assignment</c>…), how
/// serious, the tasks and the entries of the log involved, and the message in Spanish.
/// </summary>
public sealed record OrchestratorFinding(string Code, Severity Severity, IReadOnlyList<TaskId> Tasks, IReadOnlyList<long> Entries, string Message);

/// <summary>
/// The audit of one run (D-34): the verification of the log (when it does not verify nothing is written and this is the
/// report), the findings, the sequences of the entries recorded now (none for a finding already noted) and whether it
/// asked to replan.
/// </summary>
public sealed record OrchestratorAuditReport(RunId Run, DecisionLogVerification Log, IReadOnlyList<OrchestratorFinding> Findings, IReadOnlyList<long> Recorded, bool ReplanRequested);

/// <summary>The deterministic auditor of the orchestrator (D-33 to D-35): free and idempotent; it only appends alerts and requests to replan.</summary>
public interface IOrchestratorAuditService
{
    Task<OrchestratorAuditReport> AuditRunAsync(ProjectContext project, RunId run, CancellationToken ct);
}

/// <summary>
/// «Impugnar» (D-38): the stored verdict, the user's reasons (stored as a <c>HumanNote</c>) and the revision debate to open,
/// adversarial, whose requirements are those of the challenged verdict.
/// </summary>
public sealed record VerdictChallengeRequest(ProjectContext Project, string Verdict, string Reasons, DebateRequest Revision);

/// <summary>Where the challenge of a verdict stands (D-40), as the delivery and the card read it.</summary>
public enum ChallengeStatus
{
    /// <summary>The verdict was never challenged.</summary>
    None,

    /// <summary>Challenged, and its revision is not closed: the delivery is blocked.</summary>
    Pending,

    /// <summary>The revision confirmed what the verdict said.</summary>
    Confirmed,

    /// <summary>The revision withdrew an approval: the delivery is blocked.</summary>
    Revoked,

    /// <summary>The revision turned towards an approval: it only counts once the user accepts it (<c>verdict.revision.accept</c>).</summary>
    AwaitingAcceptance,

    /// <summary>The user accepted the revision that turned towards an approval.</summary>
    Accepted,

    /// <summary>The revision needed a person and the user decided («Decidir yo»).</summary>
    DecidedByUser,

    /// <summary>The revision debate was aborted without a judgment: it counts for nothing, the delivery stays blocked while it is the latest word, and the verdict can be challenged again.</summary>
    Aborted,
}

/// <summary>Challenges of verdicts (D-38 to D-40): a revision debate that inherits who may judge and changes the judge.</summary>
public interface IVerdictChallenges
{
    /// <summary>Inherits the excluded vendors of the challenged object and picks another judge (another agent when the template has one). Writes nothing.</summary>
    Task<DebateProposal> PrepareAsync(VerdictChallengeRequest request, CancellationToken ct);

    /// <summary>Records <c>VerdictChallenged</c> (the reasons as evidence), then opens the revision debate.</summary>
    Task<IDebateRun> ChallengeAsync(DebateProposal proposal, CancellationToken ct);

    Task<ChallengeStatus> StatusAsync(ProjectContext project, string verdict, CancellationToken ct);

    /// <summary>Accepts a revision that turned towards an approval (<c>IApprovalGate</c>, action <c>verdict.revision.accept</c>, bound to the verdict).</summary>
    Task AcceptRevisionAsync(ProjectContext project, string revisionVerdict, CancellationToken ct);
}

public enum ReviewDisposition
{
    Approved,
    ChangesRequested,
    Rejected,

    /// <summary>
    /// The verdict escalates to the user, or the log is not intact, or the verdict stayed invalid after the correction,
    /// or the judge's answer could not be read as a verdict.
    /// </summary>
    NeedsHuman,

    /// <summary>
    /// The judge's provider hit its usage limit before there was a verdict: nothing was decided, and the review is asked
    /// again once the limit resets (<see cref="ReviewOutcome.RetryAfter"/>, when the provider said).
    /// </summary>
    RateLimited,
}

/// <summary>Asks for the review of a run. Only identifiers travel: the engine never builds or sees the dossier.</summary>
public sealed record ReviewRequest(ProjectContext Project, RunId Run, TaskId ReviewTask, AgentId Judge, string WorkspacePath);

/// <summary>
/// The outcome of a review. An invalid verdict is stored exactly as the judge gave it, with its
/// <see cref="Violations"/>; <see cref="Corrected"/> says the judge was asked for one correction.
/// <see cref="RetryAfter"/> is when the usage limit of a <see cref="ReviewDisposition.RateLimited"/> review resets, when
/// the provider said; it is null otherwise.
/// </summary>
public sealed record ReviewOutcome(
    string VerdictId,
    Verdict Verdict,
    IReadOnlyList<VerdictViolation> Violations,
    bool Corrected,
    bool DecisionLogIntact,
    ReviewDisposition Disposition,
    DateTimeOffset? RetryAfter = null);

/// <summary>
/// Use case of the Debate module: records <c>ReviewRequested</c> (and checks the log), builds the dossier from the
/// stores, runs the judge read-only, validates the verdict without changing it, allows one correction and persists
/// every verdict append-only. The judge's vendor must differ from the vendors of everyone who produced the work.
/// </summary>
public interface IIndependentReview
{
    Task<ReviewOutcome> ReviewRunAsync(ReviewRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken);
}
