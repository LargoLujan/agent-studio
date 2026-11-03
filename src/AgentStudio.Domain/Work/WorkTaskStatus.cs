namespace AgentStudio.Domain.Work;

public enum WorkTaskStatus
{
    Pending,
    Running,
    Blocked,
    AwaitingApproval,
    Completed,
    Failed,
    Cancelled,

    /// <summary>
    /// Terminal. The task never ran because what it waited for can no longer happen: the branch that led to it was not
    /// taken, or the tasks it waited for were skipped. Only <see cref="TaskGraph.PropagateSkips"/> sets it.
    /// </summary>
    Skipped,
}

/// <summary>How a task combines its prerequisite edges (see <see cref="TaskGraph"/>).</summary>
public enum JoinPolicy
{
    /// <summary>Waits for every prerequisite edge to be satisfied; one dead edge makes the task skipped.</summary>
    AllCompleted,

    /// <summary>
    /// Merge point. Waits until no edge is pending and none is broken, and runs when at least one edge is satisfied;
    /// it is skipped only when every edge is dead.
    /// </summary>
    AnyCompleted,
}

/// <summary>The node types of the visual orchestrator (master prompt, section 6).</summary>
public enum WorkTaskKind
{
    AgentWork,
    Condition,
    ToolRun,
    Review,
    Debate,
    HumanApproval,
    Integration,
}

/// <summary>Allowed state transitions. Anything not listed here is rejected.</summary>
public static class WorkTaskTransitions
{
    private static readonly Dictionary<WorkTaskStatus, WorkTaskStatus[]> Allowed = new()
    {
        [WorkTaskStatus.Pending] =
        [
            WorkTaskStatus.Running, WorkTaskStatus.Blocked, WorkTaskStatus.AwaitingApproval, WorkTaskStatus.Cancelled,
            WorkTaskStatus.Skipped,
        ],
        [WorkTaskStatus.Running] =
        [
            WorkTaskStatus.Completed, WorkTaskStatus.Failed, WorkTaskStatus.Cancelled,
            WorkTaskStatus.Blocked, WorkTaskStatus.AwaitingApproval,
        ],
        [WorkTaskStatus.Blocked] = [WorkTaskStatus.Pending, WorkTaskStatus.Cancelled, WorkTaskStatus.Skipped],
        [WorkTaskStatus.AwaitingApproval] = [WorkTaskStatus.Running, WorkTaskStatus.Completed, WorkTaskStatus.Failed, WorkTaskStatus.Cancelled],
        [WorkTaskStatus.Failed] = [WorkTaskStatus.Pending],
        [WorkTaskStatus.Completed] = [],
        [WorkTaskStatus.Cancelled] = [],
        [WorkTaskStatus.Skipped] = [],
    };

    public static bool IsAllowed(WorkTaskStatus from, WorkTaskStatus to) => Allowed[from].Contains(to);

    public static bool IsTerminal(WorkTaskStatus status) =>
        status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Skipped;

    public static IReadOnlyList<WorkTaskStatus> NextStates(WorkTaskStatus from) => Allowed[from];
}
