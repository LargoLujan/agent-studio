using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Application.Persistence;

// Phase 3-5 contracts. The SQLite schema behind them is in AgentStudio.Infrastructure (Persistence/Schema).

/// <summary>Append-only storage of the decision log. There is deliberately no update or delete operation.</summary>
public interface IDecisionLogStore
{
    Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken);
}

/// <param name="RunId">The orchestration run, task and attempt the evidence belongs to, if any.</param>
/// <param name="MediaType">The media type of the content (for example <c>text/plain</c> or <c>text/x-diff</c>).</param>
/// <param name="SubjectCommit">
/// The commit the evidence verified: the one checked out where the build or the test run ran (a full git object id).
/// Null when the evidence is not about one commit. Only test runs of the commit under review count for approval.
/// </param>
public sealed record EvidenceDraft(
    EvidenceKind Kind,
    EvidenceOrigin Origin,
    string Title,
    string ProducedBy,
    TestRunSummary? TestRun = null,
    IReadOnlyList<RequirementId>? Covers = null,
    string? RunId = null,
    string? TaskId = null,
    int? Attempt = null,
    string? MediaType = null,
    string? SubjectCommit = null);

/// <summary>
/// Content-addressed, append-only evidence store. The store computes the hash itself from the bytes it receives:
/// callers cannot supply a hash, so evidence cannot be registered for content that does not exist.
/// </summary>
public interface IEvidenceStore
{
    Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken);

    Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>The evidence captured for one orchestration run, in capture order.</summary>
    Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken);
}

public interface IRequirementStore
{
    Task SaveAsync(string projectId, FrozenRequirementSet requirements, CancellationToken cancellationToken);

    Task<FrozenRequirementSet?> FindAsync(string projectId, string contentHash, CancellationToken cancellationToken);

    Task<FrozenRequirementSet?> LatestAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>Immutable plans, addressed by the <see cref="PlanDefinition.ContentHash"/> of their content.</summary>
public interface IPlanStore
{
    /// <summary>Saves the plan and returns its hash. Saving a plan that is already stored writes nothing.</summary>
    Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken cancellationToken);

    /// <summary>The plan with that hash, or null. The hash is recomputed from the stored content; a plan that no longer matches is an error.</summary>
    Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken cancellationToken);
}

/// <summary>
/// The immutable header of an orchestration run. Its state is not stored here: it comes from replaying the decision log.
/// </summary>
public sealed record RunRecord(
    RunId Id,
    string ProjectId,
    DateTimeOffset CreatedAt,
    string PlanHash,
    string RequirementSetHash,
    EvidenceId OriginalRequest,
    string BaseCommit,
    string? BaseRef,
    VerificationProfile Verification,
    IReadOnlyList<AgentDefinition> Roster,
    RunOptions Options,
    RunId? Follows);

public interface IRunRegistry
{
    Task CreateAsync(RunRecord run, CancellationToken cancellationToken);

    Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// A verdict as persisted. An invalid one is kept exactly as the judge gave it, with its <see cref="Issues"/>;
/// <see cref="Supersedes"/> points at the verdict a correction replaced, which stays in the store.
/// </summary>
public sealed record StoredVerdict(
    string Id,
    Verdict Verdict,
    string? RunId,
    string? TaskId,
    AuditScope Scope,
    bool Valid,
    IReadOnlyList<VerdictViolation> Issues,
    string? Supersedes);

public interface IVerdictStore
{
    Task<string> AppendAsync(string projectId, StoredVerdict verdict, CancellationToken cancellationToken);

    /// <summary>The verdicts of the project, or only those of one run when <paramref name="runId"/> is given, in the order they were issued.</summary>
    Task<IReadOnlyList<StoredVerdict>> ListAsync(string projectId, string? runId, CancellationToken cancellationToken);
}

/// <summary>The agent template of a project: who can take part in its runs.</summary>
public interface IAgentRoster
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken);

    Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken);
}

// ---- Phase 5 (docs/15 section 5.5 and section 6): the immutable headers of debates and whole-project audits ----

/// <summary>A participant of a debate with its whole definition, as the header freezes it: the agent, its role in the debate and its label for the judge.</summary>
public sealed record DebateParticipantDefinition(AgentDefinition Agent, DebateRole Role, string Label);

/// <summary>
/// The immutable header of a debate (D-01), in <c>debate_sessions</c>. Its state is not stored here: it comes from replaying
/// the decision log. <c>DebateOpened</c> anchors its canonical hash (<c>DebateHeaderHash.Of</c>), and resuming or launching a
/// judge requires the row to give that hash (N-05). The question is evidence, captured before the row with the debate's id
/// as its run. <paramref name="Challenges"/> is the verdict a revision debate challenges.
/// </summary>
public sealed record DebateRecord(
    RunId Id,
    string ProjectId,
    DateTimeOffset CreatedAt,
    DebateMode Mode,
    string RequirementSetHash,
    EvidenceId Question,
    DebateSubject Subject,
    string? SnapshotCommit,
    IReadOnlyList<DebateParticipantDefinition> Participants,
    IReadOnlyList<AgentDefinition> Judges,
    DebateJudgingKind Judging,
    IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> WaivedParticipantVendors,
    DebateLimits Limits,
    int MaxAgentRuns,
    string? Challenges);

/// <summary>The headers of the debates of a project. There is deliberately no update or delete operation.</summary>
public interface IDebateRegistry
{
    /// <summary>Inserts the header. The same row twice writes nothing; another row with the same id is refused.</summary>
    Task CreateAsync(DebateRecord debate, CancellationToken cancellationToken);

    Task<DebateRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken);

    /// <summary>The headers of the project, in the order they were created.</summary>
    Task<IReadOnlyList<DebateRecord>> ListAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// The immutable header of an audit of the whole project (D-28), in <c>project_audits</c>: the commit audited and whether
/// there were uncommitted changes, the requirements, the request (evidence), the trusted verification profile frozen for
/// it, the judge, the vendors that produced code according to the log and the authors the user declared.
/// <c>ProjectAuditStarted</c> anchors its canonical hash.
/// </summary>
public sealed record ProjectAuditRecord(
    RunId Id,
    string ProjectId,
    DateTimeOffset CreatedAt,
    string? HeadCommit,
    bool UncommittedChanges,
    string RequirementSetHash,
    EvidenceId Request,
    VerificationProfile Verification,
    AgentDefinition Judge,
    IReadOnlyList<VendorId> ProducerVendors,
    IReadOnlyList<VendorId> DeclaredAuthors);

/// <summary>The headers of the audits of a project, with the shape of <see cref="IDebateRegistry"/>.</summary>
public interface IProjectAuditRegistry
{
    /// <summary>Inserts the header. The same row twice writes nothing; another row with the same id is refused.</summary>
    Task CreateAsync(ProjectAuditRecord audit, CancellationToken cancellationToken);

    Task<ProjectAuditRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken);

    /// <summary>The headers of the project, in the order they were created.</summary>
    Task<IReadOnlyList<ProjectAuditRecord>> ListAsync(string projectId, CancellationToken cancellationToken);
}
