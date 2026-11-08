using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>What the workers and the user tell the loop. Only the loop changes the graph.</summary>
internal abstract record Signal;

/// <summary>The provider's <c>StartAsync</c> returned: the task may go from Pending to Running now, not before.</summary>
internal sealed record WorkerLaunched(TaskId Task, int Attempt, WorktreeInfo? Worktree) : Signal;

internal sealed record WorkerEnded(TaskId Task, int Attempt, AttemptEnd End) : Signal;

internal enum CommandKind
{
    Pause,
    ResumeDispatch,
    RetryBlocked,
    Cancel,
}

internal sealed record UserCommand(CommandKind Kind, TaskId? Task, string? Reason, TaskCompletionSource Done) : Signal;

/// <summary>
/// The power of the machine changed (docs/16 D-40): the handler of the power events only posts it, and the loop, the one
/// reader, tells its clock how long the machine slept and looks again at once.
/// </summary>
internal sealed record PowerChanged(AgentStudio.Application.Execution.PowerEvent Event) : Signal;

internal enum EndKind
{
    Completed,
    Failed,
    AwaitingHuman,
    RateLimited,

    /// <summary>The worker could not get the task going (CLI missing, worktree or skills failed); the task never ran.</summary>
    LaunchFailed,

    /// <summary>An unexpected error after the task started (a tool or service failed).</summary>
    Broken,

    RepositoryChanged,

    /// <summary>
    /// The hidden ref of a task the integration builds on no longer points at the commit the system verified for it:
    /// something outside the system moved it. Nothing is merged, and nothing more is dispatched.
    /// </summary>
    SourceMoved,

    /// <summary>
    /// A row of the evidence the integrator captured is gone, or no longer matches the record it handed back, when the
    /// engine reads it back moments later in the same process: it was rewritten behind the store's triggers. The
    /// integration is not accepted, nothing is retried and nothing more is dispatched (ADR-0010).
    /// </summary>
    IntegrationEvidenceTampered,
    Cancelled,

    /// <summary>The review escalated to a person: the task waits for an approval.</summary>
    ApprovalNeeded,
    Approved,
    Denied,

    /// <summary>
    /// Phase 13 (docs/17 D-28): the agent ended blocked with a valid question (<see cref="AttemptEnd.Question"/>). The loop
    /// records it within the caps and the task waits for the answer, or blocks it with «demasiadas preguntas» beyond them.
    /// </summary>
    AsksQuestion,
}

/// <summary>How one attempt of a task ended, with what the loop needs to record it.</summary>
internal sealed record AttemptEnd(EndKind Kind, string Reason)
{
    public IReadOnlyList<EvidenceId> Evidence { get; init; } = [];

    public TaskResult? Result { get; init; }

    public string? SessionId { get; init; }

    /// <summary>The commit the task's worktree is at after the attempt (the base of the next attempt).</summary>
    public string? Head { get; init; }

    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>The class of the end of the attempt, as its adapter recognised it (docs/16 D-41): the wait of a limit follows it.</summary>
    public AttemptEndClass? EndClass { get; init; }

    /// <summary>The tests the system ran in the attempt, for the handoff of a later one.</summary>
    public AgentStudio.Domain.Evidence.TestRunSummary? Tests { get; init; }

    /// <summary>The summary of the agent's report, its word (for the handoff of a later attempt).</summary>
    public string? AgentSummary { get; init; }

    /// <summary>What the agent's report left open, its word (for the handoff of a later attempt).</summary>
    public IReadOnlyList<string> AgentOpenIssues { get; init; } = [];

    /// <summary>The next attempt of the task goes in a new session with a handoff, and why (docs/16 D-42).</summary>
    public HandoffReason? Renew { get; init; }

    public IReadOnlyList<string> Differences { get; init; } = [];

    /// <summary>For <see cref="EndKind.SourceMoved"/>: the tasks whose refs moved.</summary>
    public IReadOnlyList<TaskId> MovedSources { get; init; } = [];

    public string? VerdictId { get; init; }

    public string? IntegrationCommit { get; init; }

    /// <summary>The attempt recorded <c>AgentRunStarted</c> (it counts against <c>MaxAgentRuns</c>).</summary>
    public bool AgentRunRecorded { get; init; }

    /// <summary>
    /// The check before going on after a wait stopped the attempt before anything was launched (F8-3): it spent no launch,
    /// a review's included.
    /// </summary>
    public bool Unlaunched { get; init; }

    public WorktreeInfo? Worktree { get; init; }

    /// <summary>For <see cref="EndKind.AsksQuestion"/>: the question of the report, as the rules validated it (not redacted yet).</summary>
    public Domain.Questions.AgentQuestion? Question { get; init; }

    /// <summary>For <see cref="EndKind.AsksQuestion"/>: what the system estimated for each option (docs/17 D-32); empty when it estimated nothing.</summary>
    public IReadOnlyList<Application.Decisions.OptionEstimateSnapshot> QuestionEstimates { get; init; } = [];
}

internal enum StopCause
{
    None,
    Stalled,
    TimedOut,
    User,
}

/// <summary>A task handed to a worker. The worker owns its cancellation; the loop says why it stops it.</summary>
internal sealed class InFlight(TaskId task, int attempt, WorkTaskKind kind, ProviderId? provider, bool countsForParallel) : IDisposable
{
    private int _cause;

    public TaskId Task { get; } = task;

    public int Attempt { get; } = attempt;

    public WorkTaskKind Kind { get; } = kind;

    public ProviderId? Provider { get; } = provider;

    public bool CountsForParallel { get; } = countsForParallel;

    public CancellationTokenSource Stop { get; } = new();

    public bool Launched { get; set; }

    public Task? Work { get; set; }

    public StopCause Cause => (StopCause)Volatile.Read(ref _cause);

    /// <summary>Stops the work for <paramref name="cause"/>; the first cause wins.</summary>
    public void Cancel(StopCause cause)
    {
        if (Interlocked.CompareExchange(ref _cause, (int)cause, (int)StopCause.None) == (int)StopCause.None)
        {
            try
            {
                Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The work already ended.
            }
        }
    }

    public void Dispose() => Stop.Dispose();
}
