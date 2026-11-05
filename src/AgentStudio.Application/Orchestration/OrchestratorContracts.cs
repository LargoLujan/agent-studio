using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Application.Orchestration;

// Phase 3 contracts (docs/13 section 3.3). They replace the IOrchestrator that took a bare TaskGraph: a graph with no
// specifications and no run id cannot be resumed after a restart.

/// <summary>Folders the engine works in, all under <c>%LOCALAPPDATA%\AgentStudio</c>; the composition root decides them.</summary>
public sealed record OrchestrationPaths(string WorktreeRoot, string DossierRoot, string SkillSessionRoot);

/// <summary>Asks the orchestrator agent for a plan. The requirements are the ones the user froze; the agent may only suggest more.</summary>
public sealed record PlanningRequest(
    ProjectContext Project,
    string OriginalRequest,
    FrozenRequirementSet Requirements,
    IReadOnlyList<AgentDefinition> Roster,
    AgentId Orchestrator,
    VerificationProfile Verification,
    PlanLimits Limits);

/// <summary>
/// The result of planning. <see cref="Plan"/> is null when the agent produced nothing usable; <see cref="Violations"/>
/// lists what the validator rejected (empty for a plan that may run); <see cref="Corrected"/> says the agent was asked
/// for one correction. <see cref="RawPlanEvidence"/> is the agent's answer as it came. <see cref="Warnings"/> are the
/// validator's warnings about <see cref="Plan"/> (<see cref="PlanValidator.Warnings"/>: a plan that may run but whose work
/// could never be delivered, say), for the review to show next to its approval; null or empty when there are none.
/// </summary>
public sealed record PlanProposal(
    RunId Run,
    PlanDefinition? Plan,
    IReadOnlyList<PlanViolation> Violations,
    bool Corrected,
    AgentRunStatus Status,
    string? Error,
    EvidenceId? RawPlanEvidence,
    IReadOnlyList<PlanWarning>? Warnings = null)
{
    /// <summary>
    /// Phase 12 (docs/17 D-06, F12-9): why the orchestrator planned without the tool <c>search_project_history</c>, in
    /// Spanish (its CLI does not launch the local MCP servers, the project has no trusted history yet, its template gives it
    /// no memory...). It is not an error. Null when it had the tool, or when the composition has none to give. Whether it had
    /// it is also in the prompt captured as evidence, which mentions the tool only then.
    /// </summary>
    public string? HistoryToolNotice { get; init; }
}

/// <summary>
/// Limits and timings of a run, frozen in its header and anchored by <see cref="RunHeaderHash"/>. Null means the engine
/// default (stall 10 min, heartbeat 5 s, task timeout 20 min, quota wait 15 min).
/// </summary>
/// <param name="QuotaResetMargin">
/// Phase 8 (docs/16 D-36): how long a limited task waits after the reset its provider announced before it is tried again.
/// Null is <see cref="DefaultQuotaResetMargin"/>.
/// </param>
/// <param name="MaxEstimatedQuotaWait">
/// Phase 8 (D-37): the cap of each wait when the provider gave no reset; those waits grow (5, 10, 20, 40 and 60 minutes) and
/// show as «estimada». Null is <see cref="DefaultMaxEstimatedQuotaWait"/>.
/// </param>
/// <param name="AutoResumeConsent">
/// Phase 8 (D-39, decision 4 (a)): the user's consent, given when approving the plan, for this run to wait until the
/// announced reset whatever it takes (beyond <see cref="MaxAutoWaitForQuota"/>) and to resume by itself after a restart of
/// the app, unless the person paused it or left it when exiting. Never beyond <see cref="MaxAgentRuns"/>. False by default:
/// the run behaves as before Phase 8.
/// </param>
/// <remarks>
/// The options of Phase 8 are written into the header (its hash and its stored row) only when they are not the defaults, so
/// every header stored before them is read and hashed exactly as it was (ADR-0034).
/// </remarks>
public sealed record RunOptions(
    int MaxParallelTasks = 3,
    int MaxAgentRuns = 12,
    TimeSpan? StallThreshold = null,
    TimeSpan? HeartbeatInterval = null,
    TimeSpan? DefaultTaskTimeout = null,
    TimeSpan? MaxAutoWaitForQuota = null,
    TimeSpan? QuotaResetMargin = null,
    TimeSpan? MaxEstimatedQuotaWait = null,
    bool AutoResumeConsent = false)
{
    /// <summary>The margin after an announced reset when the header gives none (D-36): 3 minutes.</summary>
    public static readonly TimeSpan DefaultQuotaResetMargin = TimeSpan.FromMinutes(3);

    /// <summary>The cap of a wait without a reset when the header gives none (D-37): 60 minutes.</summary>
    public static readonly TimeSpan DefaultMaxEstimatedQuotaWait = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The shortest cap of the waits without a reset (D-37): one minute, the first of the short steps. A shorter one would end
    /// every such wait almost at once, and check or launch again in a burst.
    /// </summary>
    public static readonly TimeSpan MinEstimatedQuotaWait = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Refuses the waits of a new run that make no sense (ADR-0034): a negative <see cref="QuotaResetMargin"/>, or a
    /// <see cref="MaxEstimatedQuotaWait"/> under <see cref="MinEstimatedQuotaWait"/>. The engine asks it before a run starts;
    /// a stored header is never refused for them, it is read as it was (<see cref="ResolvedQuotaResetMargin"/>).
    /// </summary>
    /// <exception cref="ArgumentException">One of the waits makes no sense.</exception>
    public void EnsureValid()
    {
        if (QuotaResetMargin < TimeSpan.Zero)
        {
            throw new ArgumentException($"QuotaResetMargin must not be negative; it is {QuotaResetMargin}.", nameof(QuotaResetMargin));
        }

        if (MaxEstimatedQuotaWait < MinEstimatedQuotaWait)
        {
            throw new ArgumentException($"MaxEstimatedQuotaWait must be at least {MinEstimatedQuotaWait}; it is {MaxEstimatedQuotaWait}.", nameof(MaxEstimatedQuotaWait));
        }
    }

    /// <summary>
    /// The margin the engine waits after an announced reset: the one given, the default when none is, and none for a negative
    /// one of a stored header (written before the waits were checked).
    /// </summary>
    public TimeSpan ResolvedQuotaResetMargin() =>
        QuotaResetMargin is { } margin ? (margin > TimeSpan.Zero ? margin : TimeSpan.Zero) : DefaultQuotaResetMargin;

    /// <summary>
    /// The cap of each wait without a reset that the engine uses: the one given, the default when none is, and
    /// <see cref="MinEstimatedQuotaWait"/> for a shorter one of a stored header.
    /// </summary>
    public TimeSpan ResolvedMaxEstimatedQuotaWait() =>
        MaxEstimatedQuotaWait is { } cap ? (cap > MinEstimatedQuotaWait ? cap : MinEstimatedQuotaWait) : DefaultMaxEstimatedQuotaWait;
}

/// <summary>
/// Starts a run on an approved plan. <see cref="Run"/> is the id allocated by planning, or null for a plan written by
/// hand. <see cref="Follows"/> names the run this one continues (a follow-up run after a conflict or a rejection).
/// <see cref="ValidatedRoster"/> is the roster the plan was validated against and shown with when the user approved it:
/// the run starts only if the project's stored roster is exactly that one (<see cref="RosterHash"/>), and a request
/// without it is refused. <see cref="ApprovedWarning"/> is the warning about writers at medium integrity shown next to
/// «Aprobar plan» when the user approved it, empty when the review showed none: the run starts only if the warning the
/// engine reads when it starts is that one (ADR-0018, A-04). Null says no warning was shown, so the run starts only if
/// the engine reads none either: a plan with such writers is refused before anything is written (fail closed).
/// </summary>
public sealed record RunRequest(
    ProjectContext Project,
    RunId? Run,
    string OriginalRequest,
    FrozenRequirementSet Requirements,
    PlanDefinition Plan,
    ApprovalDecision PlanApproval,
    VerificationProfile Verification,
    RunOptions Options,
    RunId? Follows = null,
    IReadOnlyList<AgentDefinition>? ValidatedRoster = null,
    string? ApprovedWarning = null);

/// <summary>A run as listed: its state comes from replaying its log, and an unfinished run with no process behind it shows as <c>Interrupted</c>.</summary>
public sealed record RunSummary(RunId Id, DateTimeOffset CreatedAt, RunPhase Phase, string PlanHash, string Summary, RunId? Follows);

/// <summary>Plans and runs orchestrations. The orchestrator agent only plans; everything else is done by the system.</summary>
public interface IOrchestrator
{
    /// <summary>Allocates the run id, asks the orchestrator agent for a plan (one correction at most), validates it, and persists plan and <c>PlanCreated</c>.</summary>
    Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken);

    Task<IOrchestrationRun> StartAsync(RunRequest request, CancellationToken cancellationToken);

    /// <summary>Resumes a run after a restart by replaying its log. It runs only after the user asks, because resuming spends quota.</summary>
    Task<IOrchestrationRun> ResumeAsync(ProjectContext project, RunId run, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunSummary>> ListRunsAsync(ProjectContext project, CancellationToken cancellationToken);
}

public enum RunPhase
{
    Running,
    Paused,
    WaitingForQuota,
    AwaitingHuman,

    /// <summary>The app stopped while the run was unfinished. Not a deadlock: nothing is wrong with the graph.</summary>
    Interrupted,
    Succeeded,
    Failed,
    Blocked,
    Cancelled,
}

/// <summary>What the UI shows about one task. <see cref="BlockReason"/> is structured: Interrupted, ProviderUnavailable, RateLimitedUntil(t) or AwaitingHuman.</summary>
public sealed record TaskView(
    TaskId Id,
    string Title,
    WorkTaskKind Kind,
    WorkTaskStatus Status,
    AgentId? Agent,
    int Attempt,
    int RetryCount,
    string? WorktreePath,
    string? Ref,
    string? BlockReason,
    IReadOnlyList<EvidenceId> Evidence);

/// <summary>
/// The state of a run at one moment: its phase, its tasks, the health of its graph and the agent runs it used. Tanda 2
/// (docs/18 P3): <paramref name="MaxAgentRuns"/> is the budget of the run (<see cref="RunOptions.MaxAgentRuns"/>), so a screen
/// shows the runs used against it; null when whoever built the snapshot did not say it (a double, an older reader).
/// </summary>
public sealed record RunSnapshot(RunId Id, RunPhase Phase, IReadOnlyList<TaskView> Tasks, GraphHealthReport Health, int AgentRunsUsed, int? MaxAgentRuns = null);

public sealed record RunResult(RunId Id, RunPhase Phase, GraphHealthReport Health, string? IntegrationCommit, string? VerdictId, string Summary);
