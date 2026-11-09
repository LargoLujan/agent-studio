using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>The immutable header of a run as the engine uses it: what was approved and frozen when it started.</summary>
internal sealed class RunContext
{
    public static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultTaskTimeout = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan DefaultMaxQuotaWait = TimeSpan.FromMinutes(15);

    /// <summary>The cooldown of a provider that reported a rate limit without saying when it resets.</summary>
    public static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(5);

    public RunContext(
        ProjectContext project,
        RunId run,
        PlanDefinition plan,
        FrozenRequirementSet requirements,
        VerificationProfile profile,
        IReadOnlyList<AgentDefinition> roster,
        RunOptions options,
        string baseCommit,
        DateTimeOffset createdAt)
    {
        Project = project;
        Run = run;
        Plan = plan;
        Requirements = requirements;
        Profile = profile;
        Options = options;
        BaseCommit = baseCommit;
        CreatedAt = createdAt;
        Specs = plan.Tasks.ToDictionary(t => t.Id);
        Agents = roster.DistinctBy(a => a.Id).ToDictionary(a => a.Id);
        var order = plan.BuildGraph(createdAt).TopologicalOrder();
        TopologicalIndex = order.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
    }

    public ProjectContext Project { get; }

    public RunId Run { get; }

    public PlanDefinition Plan { get; }

    public FrozenRequirementSet Requirements { get; }

    public VerificationProfile Profile { get; }

    public RunOptions Options { get; }

    public string BaseCommit { get; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyDictionary<TaskId, TaskSpec> Specs { get; }

    public IReadOnlyDictionary<AgentId, AgentDefinition> Agents { get; }

    public IReadOnlyDictionary<TaskId, int> TopologicalIndex { get; }

    public TimeSpan StallThreshold => Options.StallThreshold ?? DefaultStallThreshold;

    public TimeSpan Heartbeat => Options.HeartbeatInterval ?? DefaultHeartbeat;

    public TimeSpan MaxQuotaWait => Options.MaxAutoWaitForQuota ?? DefaultMaxQuotaWait;

    /// <summary>
    /// What a task waits after the reset its provider announced, with the consent of the header (D-36); never negative, even
    /// for a stored header written before the waits were checked.
    /// </summary>
    public TimeSpan QuotaResetMargin => Options.ResolvedQuotaResetMargin();

    /// <summary>
    /// The longest wait without a reset time, with the consent of the header (D-37); a minute at least, even for a stored
    /// header written before the waits were checked.
    /// </summary>
    public TimeSpan MaxEstimatedQuotaWait => Options.ResolvedMaxEstimatedQuotaWait();

    public TimeSpan TimeoutOf(TaskSpec spec) => spec.Timeout ?? Options.DefaultTaskTimeout ?? DefaultTaskTimeout;

    public IEnumerable<TaskId> PrerequisitesOf(TaskId task) => Specs[task].DependsOn.Select(d => d.Prerequisite);

    /// <summary>
    /// The verification steps of a task, from the frozen profile: the ones the task lists, in its order, each preceded by
    /// the steps it requires that are not there yet, in the order of the profile (<see cref="VerificationProfile.StepsToRun(IEnumerable{string})"/>):
    /// a task that names only <c>test</c> also gets the <c>build</c> its test needs on a fresh copy of the commit.
    /// </summary>
    /// <exception cref="InvalidOperationException">A step the task names, or one it requires, is not in the frozen profile.</exception>
    public IReadOnlyList<VerificationStep> StepsOf(TaskSpec spec) => Profile.StepsToRun(spec.VerificationSteps);

    /// <summary>The steps a tool run of <paramref name="spec"/> runs: its tool, preceded by the steps the tool requires.</summary>
    /// <exception cref="InvalidOperationException">The tool, or a step it requires, is not in the frozen profile.</exception>
    public IReadOnlyList<VerificationStep> ToolStepsOf(TaskSpec spec) =>
        Profile.StepsToRun([spec.ToolId ?? throw new InvalidOperationException($"Tool task '{spec.Id}' names no tool.")]);

    public bool IsAncestor(TaskId ancestor, TaskId of)
    {
        var stack = new Stack<TaskId>(PrerequisitesOf(of));
        var seen = new HashSet<TaskId>();
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == ancestor)
            {
                return true;
            }

            if (seen.Add(current))
            {
                foreach (var next in PrerequisitesOf(current))
                {
                    stack.Push(next);
                }
            }
        }

        return false;
    }

    public IEnumerable<TaskId> AncestorsInOrder(TaskId of)
    {
        var found = new HashSet<TaskId>();
        var stack = new Stack<TaskId>(PrerequisitesOf(of));
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (found.Add(current))
            {
                foreach (var next in PrerequisitesOf(current))
                {
                    stack.Push(next);
                }
            }
        }

        return found.OrderBy(id => TopologicalIndex[id]).ThenBy(id => id.Value, StringComparer.Ordinal);
    }

    public static bool ProducesCode(TaskSpec spec) =>
        spec.Kind == WorkTaskKind.Integration || (spec.Kind == WorkTaskKind.AgentWork && spec.Workspace == WorkspaceMode.Write);

    public static bool NeedsCodeBase(TaskSpec spec) =>
        spec.Kind is WorkTaskKind.Review or WorkTaskKind.ToolRun || (spec.Kind == WorkTaskKind.AgentWork && spec.Workspace != WorkspaceMode.None);
}

/// <summary>A worktree of a task as recorded (<c>WorktreeCreated</c>); leases are short-lived and always re-obtained through <c>EnsureAsync</c>.</summary>
internal sealed record WorktreeInfo(string Name, string Path, string RefName, string BaseCommit);

/// <summary>A ref of a completed task and the commit the system verified for it, which the ref must still point at.</summary>
internal sealed record VerifiedRef(TaskId Task, string RefName, string Commit);

/// <summary>The result of a completed task: the facts downstream tasks and conditions use.</summary>
internal sealed record TaskResult(
    string? Commit,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyDictionary<string, string> Outputs,
    TestRunSummary? Tests,
    EvidenceId? Report,
    string? ReportSha256);

/// <summary>Why the last attempt failed, given to the next attempt as data.</summary>
internal sealed record FailureNote(int Attempt, string Reason, IReadOnlyList<EvidenceId> Evidence);

/// <summary>The loop's own memory of the run, beyond the graph. It can be rebuilt from the log and the attempt records.</summary>
internal sealed class RunLedger
{
    public Dictionary<TaskId, int> Attempts { get; } = [];

    /// <summary>The attempts that recorded <c>AgentRunStarted</c> (the budget counts these and the reviews).</summary>
    public int AgentRunsUsed { get; set; }

    public Dictionary<TaskId, string> Sessions { get; } = [];

    public Dictionary<TaskId, WorktreeInfo> Worktrees { get; } = [];

    /// <summary>The commit the worktree of a task is at between attempts (the base of its next attempt).</summary>
    public Dictionary<TaskId, string> Heads { get; } = [];

    public Dictionary<TaskId, TaskResult> Results { get; } = [];

    public Dictionary<TaskId, BlockReason> Blocks { get; } = [];

    public Dictionary<TaskId, DateTimeOffset> BlockedAt { get; } = [];

    public Dictionary<TaskId, FailureNote> Failures { get; } = [];

    public Dictionary<Domain.Providers.ProviderId, DateTimeOffset> Cooldowns { get; } = [];

    /// <summary>How many waits without a reset time each task has had in a row: the next one is the next step (D-37).</summary>
    public Dictionary<TaskId, int> EstimatedWaits { get; } = [];

    /// <summary>The tasks whose next launch follows a wait for quota: the access of their provider is checked first (D-38).</summary>
    public HashSet<TaskId> CheckAccess { get; } = [];

    /// <summary>
    /// How many checks in a row found the limit of each task still on and launched nothing (F8-3): a launch of the task
    /// starts the count again, and at <c>MaxAgentRuns</c> the task no longer waits by itself (ADR-0034, decision 9).
    /// </summary>
    public Dictionary<TaskId, int> StoppedChecks { get; } = [];

    /// <summary>The tasks whose next attempt goes in a new session with a handoff, and why (D-42 and D-43).</summary>
    public Dictionary<TaskId, HandoffReason> Renew { get; } = [];

    /// <summary>What the last attempts of each task left for a handoff: its last tests and its agent's last report.</summary>
    public Dictionary<TaskId, AttemptNotes> Notes { get; } = [];

    public HashSet<string> Alerts { get; } = new(StringComparer.Ordinal);

    /// <summary>Phase 13 (docs/17 D-29): the questions of the executors, pending and answered, and how many were asked.</summary>
    public Questions.QuestionBook Questions { get; } = new();

    /// <summary>The user's repository changed during a step of this run: nothing more is dispatched.</summary>
    public bool RepositoryChanged { get; set; }

    /// <summary>The ref of a verified task moved behind the system's back before its integration: nothing more is dispatched.</summary>
    public bool SourceMoved { get; set; }

    /// <summary>A row of an integration's evidence changed in the store between its capture and its read: nothing more is dispatched.</summary>
    public bool IntegrationEvidenceTampered { get; set; }

    public string? IntegrationCommit { get; set; }

    public string? VerdictId { get; set; }
}
