using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Work;

namespace AgentStudio.Application.Orchestration;

// Phase 3 contract (docs/13 section 3.3): a running orchestration, with the same shape as IAgentRun.

/// <summary>
/// A run in progress. <see cref="Events"/> has a single consumer and is buffered; disposing the run kills the processes
/// of the agents it started. Pause stops dispatching new tasks; it does not stop the ones running.
/// </summary>
public interface IOrchestrationRun : IAsyncDisposable
{
    RunId Id { get; }

    IAsyncEnumerable<OrchestrationEvent> Events { get; }

    Task<RunResult> Completion { get; }

    RunSnapshot Snapshot();

    /// <summary>
    /// Pauses the dispatch, and completes once the pause is in the log (<c>RunDispatchChanged</c>). It fails
    /// (<see cref="InvalidOperationException"/>) when the run ended before the pause was recorded: a pause the log does not
    /// show would let the run resume by itself after a close (docs/16 D-39).
    /// </summary>
    Task PauseAsync();

    Task ResumeDispatchAsync();

    /// <summary>Puts a blocked task back to pending so it is dispatched again (this is the user's decision, and it may spend quota).</summary>
    Task RetryBlockedAsync(TaskId task);

    Task CancelAsync(string reason);

    /// <summary>
    /// Phase 13 (docs/17 D-29 and D-30): answers the question the agent of <see cref="QuestionAnswer.Task"/> asked. The engine
    /// records <c>QuestionAnswered</c> (the person's own text as evidence first), and a new attempt, which counts in
    /// <see cref="RunOptions.MaxAgentRuns"/>, receives the question and the answer as data. Answering is not approving: it asks
    /// no Windows Hello and grants nothing. The default body says the run takes no answers: WP4 implements it in the engine,
    /// and a run inspected from its log never takes one. A question of the orchestrator before it plans (its task is null)
    /// is not answered here: there is no run yet, and its answers go to the next planning (<see cref="PlanningRequest.Answers"/>).
    /// </summary>
    /// <exception cref="NotSupportedException">This run takes no answers.</exception>
    /// <exception cref="InvalidOperationException">The task is not waiting for the answer to that question, or the answer has no task.</exception>
    Task AnswerAsync(QuestionAnswer answer) =>
        throw new NotSupportedException("This run takes no answers to the questions of its agents.");
}

/// <summary>Everything the UI needs to follow a run. The state itself is read from <see cref="IOrchestrationRun.Snapshot"/>.</summary>
public abstract record OrchestrationEvent(DateTimeOffset At);

public sealed record TaskStatusChangedEvent(DateTimeOffset At, TaskId Task, WorkTaskStatus From, WorkTaskStatus To, string Reason) : OrchestrationEvent(At);

/// <summary>The task is ready but its provider has no free slot ("en cola del proveedor"); it stays pending.</summary>
public sealed record TaskQueuedEvent(DateTimeOffset At, TaskId Task, ProviderId Provider) : OrchestrationEvent(At);

/// <summary>An event of the agent working on a task; every one also counts as a heartbeat for the abandonment monitor.</summary>
public sealed record AgentActivityEvent(DateTimeOffset At, TaskId Task, AgentEvent Event) : OrchestrationEvent(At);

public sealed record EvidenceCapturedEvent(DateTimeOffset At, TaskId? Task, EvidenceRecord Evidence) : OrchestrationEvent(At);

public sealed record GraphHealthEvent(DateTimeOffset At, GraphHealthReport Report) : OrchestrationEvent(At);

/// <summary>A task is waiting for the user to approve or deny an action.</summary>
public sealed record ApprovalPendingEvent(DateTimeOffset At, TaskId Task, ApprovalRequest Request) : OrchestrationEvent(At);

/// <summary>
/// Phase 13 (docs/17 D-29 and D-31): an agent asked the person a question, and its task waits for the answer
/// (<c>AwaitingAnswer</c>). The monitor shows its card and the window opens if the app is in view; otherwise the tray says
/// «Un agente te pregunta algo», with no data of the project. The tasks that do not depend on it go on.
/// </summary>
public sealed record QuestionPendingEvent(DateTimeOffset At, PendingQuestion Question) : OrchestrationEvent(At);

/// <summary>Phase 13 (docs/17 D-29): the question was answered (by the person, or by the consent of the run), and its task goes on.</summary>
public sealed record QuestionAnsweredEvent(DateTimeOffset At, string Question, TaskId? Task, QuestionAnswerMethod Method) : OrchestrationEvent(At);

public sealed record RunPhaseChangedEvent(DateTimeOffset At, RunPhase Phase, string? Detail) : OrchestrationEvent(At);

public sealed record RunFinishedEvent(DateTimeOffset At, RunResult Result) : OrchestrationEvent(At);

/// <summary>
/// The approved integration of the run was delivered to <paramref name="TargetRef"/> (the branch the person had checked out)
/// at <paramref name="Commit"/>: what the log of an inspected run says of its deliveries (<see cref="IRunInspector"/>).
/// </summary>
public sealed record RunDeliveredEvent(DateTimeOffset At, string TargetRef, string? Commit) : OrchestrationEvent(At);
