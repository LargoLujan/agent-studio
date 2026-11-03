using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Work;

/// <summary>
/// Problems found by <see cref="TaskGraph.Analyze"/>: abandoned work, work that can never start and a graph that
/// cannot make progress at all.
/// </summary>
public sealed record GraphHealthReport(
    IReadOnlyList<TaskId> StalledTasks,
    IReadOnlyList<TaskId> UnreachableTasks,
    IReadOnlyList<TaskId> RetryExhaustedTasks,
    bool IsDeadlocked)
{
    public bool IsHealthy => StalledTasks.Count == 0 && UnreachableTasks.Count == 0 && RetryExhaustedTasks.Count == 0 && !IsDeadlocked;
}

public sealed class DependencyCycleException : DomainRuleViolationException
{
    public DependencyCycleException(IReadOnlyList<TaskId> cycle)
        : base("graph.cycle", $"Dependency cycle: {string.Join(" -> ", cycle)}.")
    {
        Cycle = cycle;
    }

    public DependencyCycleException()
    {
        Cycle = [];
    }

    public DependencyCycleException(string message)
        : base(message)
    {
        Cycle = [];
    }

    public DependencyCycleException(string message, Exception innerException)
        : base(message, innerException)
    {
        Cycle = [];
    }

    /// <summary>The closed path that the rejected dependency would create (first and last items are equal).</summary>
    public IReadOnlyList<TaskId> Cycle { get; }
}

/// <summary>
/// One prerequisite of a task. <see cref="Branch"/> is set only when the prerequisite is a <see cref="WorkTaskKind.Condition"/>:
/// the edge is satisfied only if the condition chose that branch.
/// </summary>
public sealed record DependencyEdge(TaskId Prerequisite, string? Branch);

/// <summary>
/// Directed acyclic graph of tasks. "A depends on B" means B must be completed before A can start. Cycles are
/// rejected when the dependency is added, so the graph is always schedulable.
/// </summary>
/// <remarks>
/// <para>
/// Conditional work. An edge leaving a <see cref="WorkTaskKind.Condition"/> carries the branch it waits for; the
/// condition is decided with <see cref="CompleteCondition"/>. Seen from its dependent, every edge is in one state:
/// </para>
/// <list type="bullet">
/// <item><b>satisfied</b>: the prerequisite is completed (and, for a branch edge, the branch was taken);</item>
/// <item><b>dead</b>: the prerequisite was skipped, or it completed on another branch, so this edge can never be satisfied;</item>
/// <item><b>broken</b>: the prerequisite was cancelled, or failed without retries left;</item>
/// <item><b>pending</b>: anything else, the edge may still change.</item>
/// </list>
/// <para>
/// A task with <see cref="JoinPolicy.AllCompleted"/> is ready when every edge is satisfied and is skipped as soon as
/// one edge is dead. A task with <see cref="JoinPolicy.AnyCompleted"/> is ready when no edge is pending or broken and
/// at least one is satisfied, and is skipped when every edge is dead. A task without prerequisites is always ready
/// and never skipped. A graph without conditions has no dead edges, so it behaves exactly as it did before they existed.
/// </para>
/// </remarks>
public sealed class TaskGraph
{
    private enum EdgeState
    {
        Pending,
        Satisfied,
        Dead,
        Broken,
    }

    private readonly Dictionary<TaskId, WorkTask> _tasks = [];
    private readonly List<TaskId> _insertionOrder = [];
    private readonly Dictionary<TaskId, HashSet<TaskId>> _prerequisites = [];
    private readonly Dictionary<(TaskId Dependent, TaskId Prerequisite), string> _branches = [];
    private readonly Dictionary<TaskId, string> _takenBranches = [];

    public IReadOnlyCollection<WorkTask> Tasks => _insertionOrder.Select(id => _tasks[id]).ToList();

    public WorkTask this[TaskId id] => _tasks.TryGetValue(id, out var task)
        ? task
        : throw new KeyNotFoundException($"Unknown task '{id}'.");

    public void Add(WorkTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_tasks.TryAdd(task.Id, task))
        {
            throw new DomainRuleViolationException("graph.duplicate-task", $"Task '{task.Id}' already exists.");
        }

        _insertionOrder.Add(task.Id);
        _prerequisites[task.Id] = [];
    }

    public IReadOnlyCollection<TaskId> PrerequisitesOf(TaskId task) => _prerequisites.TryGetValue(task, out var set)
        ? set
        : throw new KeyNotFoundException($"Unknown task '{task}'.");

    /// <summary>The prerequisites of <paramref name="dependent"/> with the branch each one waits for, in the order they were added.</summary>
    public IReadOnlyList<DependencyEdge> EdgesOf(TaskId dependent)
    {
        EnsureKnown(dependent);
        return Edges(dependent).ToList();
    }

    /// <summary>
    /// Declares that <paramref name="dependent"/> needs <paramref name="prerequisite"/> to be completed first.
    /// <paramref name="branch"/> is mandatory when the prerequisite is a condition (the outcome the edge waits for) and
    /// must be null otherwise. Declaring the same edge again is harmless; declaring it with another branch is rejected.
    /// </summary>
    public void AddDependency(TaskId dependent, TaskId prerequisite, string? branch = null)
    {
        EnsureKnown(dependent);
        EnsureKnown(prerequisite);

        if (dependent == prerequisite)
        {
            throw new DependencyCycleException([dependent, dependent]);
        }

        if (_tasks[prerequisite].Kind == WorkTaskKind.Condition)
        {
            if (string.IsNullOrWhiteSpace(branch))
            {
                throw new DomainRuleViolationException(
                    "graph.branch-required",
                    $"'{prerequisite}' is a condition: the dependency of '{dependent}' on it must name the branch it waits for.");
            }
        }
        else if (branch is not null)
        {
            throw new DomainRuleViolationException(
                "graph.branch-on-non-condition",
                $"'{prerequisite}' is not a condition, so the dependency of '{dependent}' on it cannot name a branch ('{branch}').");
        }

        // A task that has already left the waiting states cannot acquire new prerequisites: it would be running
        // (or finished) in violation of the dependency being declared.
        if (_tasks[dependent].Status is not (WorkTaskStatus.Pending or WorkTaskStatus.Blocked))
        {
            throw new DomainRuleViolationException(
                "graph.dependency-on-started-task",
                $"Task '{dependent}' is {_tasks[dependent].Status}; prerequisites can only be added to pending or blocked tasks.");
        }

        // A cycle appears if the prerequisite already (transitively) depends on the dependent.
        var path = FindPath(prerequisite, dependent);
        if (path is not null)
        {
            throw new DependencyCycleException([dependent, .. path]);
        }

        if (_prerequisites[dependent].Contains(prerequisite))
        {
            if (!string.Equals(_branches.GetValueOrDefault((dependent, prerequisite)), branch, StringComparison.Ordinal))
            {
                throw new DomainRuleViolationException(
                    "graph.dependency-branch-conflict",
                    $"'{dependent}' already depends on '{prerequisite}' through another branch.");
            }

            return;
        }

        _prerequisites[dependent].Add(prerequisite);
        if (branch is not null)
        {
            _branches[(dependent, prerequisite)] = branch;
        }
    }

    /// <summary>
    /// Completes a running condition and records the branch it chose. This is the only way to complete a condition
    /// that has branch edges: a condition completed with <see cref="WorkTask.Complete"/> chose no branch, so every
    /// branch edge leaving it is dead.
    /// </summary>
    public void CompleteCondition(TaskId condition, string takenBranch, DateTimeOffset at, string reason)
    {
        EnsureKnown(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(takenBranch);
        var task = _tasks[condition];
        if (task.Kind != WorkTaskKind.Condition)
        {
            throw new DomainRuleViolationException("graph.not-a-condition", $"Task '{condition}' is a {task.Kind}, not a condition.");
        }

        task.Complete([], at, reason);
        _takenBranches[condition] = takenBranch;
    }

    /// <summary>The branch chosen by a decided condition, or null while it is undecided (or for any other task).</summary>
    public string? TakenBranch(TaskId condition)
    {
        EnsureKnown(condition);
        return _takenBranches.GetValueOrDefault(condition);
    }

    /// <summary>
    /// Skips every pending or blocked task whose edges can no longer be satisfied (see the remarks of the class), and
    /// returns them in topological order. Skips cascade: one pass in topological order reaches the fixpoint, because
    /// the verdict on a task depends only on its prerequisites. Calling it again returns nothing.
    /// </summary>
    public IReadOnlyList<TaskId> PropagateSkips(DateTimeOffset at)
    {
        var (skips, _) = SimulateSkips();
        foreach (var (id, reason) in skips)
        {
            _tasks[id].Skip(at, reason);
        }

        return skips.Select(s => s.Task).ToList();
    }

    /// <summary>Pending tasks whose edges are satisfied according to their join policy, in insertion order.</summary>
    public IReadOnlyList<WorkTask> ReadyTasks()
    {
        var status = CurrentStatuses();
        return _insertionOrder
            .Select(id => _tasks[id])
            .Where(t => t.Status == WorkTaskStatus.Pending && IsReady(t, status))
            .ToList();
    }

    /// <summary>Kahn's algorithm; ties are broken by insertion order so the result is deterministic.</summary>
    public IReadOnlyList<TaskId> TopologicalOrder()
    {
        var remaining = _prerequisites.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var dependents = _insertionOrder.ToDictionary(id => id, _ => new List<TaskId>());
        foreach (var id in _insertionOrder)
        {
            foreach (var prerequisite in _prerequisites[id])
            {
                dependents[prerequisite].Add(id);
            }
        }

        var order = new List<TaskId>(_tasks.Count);
        var emitted = new HashSet<TaskId>();
        while (order.Count < _tasks.Count)
        {
            var next = _insertionOrder.First(id => !emitted.Contains(id) && remaining[id] == 0);
            order.Add(next);
            emitted.Add(next);
            foreach (var dependent in dependents[next])
            {
                remaining[dependent]--;
            }
        }

        return order;
    }

    /// <summary>
    /// Detects abandoned tasks (running without heartbeat for longer than <paramref name="stallThreshold"/>), tasks
    /// that can never start because a prerequisite failed for good or was cancelled, tasks out of retries, and
    /// deadlock (unfinished work but nothing running, ready or waiting for a human). The analysis sees the graph as
    /// it will be once <see cref="PropagateSkips"/> has run: work that is about to be skipped is neither unreachable
    /// nor unfinished, and the tasks its skip unblocks count as ready. It never changes the graph.
    /// </summary>
    public GraphHealthReport Analyze(DateTimeOffset now, TimeSpan stallThreshold)
    {
        var tasks = _insertionOrder.Select(id => _tasks[id]).ToList();
        var (_, effective) = SimulateSkips();

        var stalled = tasks
            .Where(t => t.Status == WorkTaskStatus.Running && now - t.LastActivityAt > stallThreshold)
            .Select(t => t.Id)
            .ToList();

        var deadEnds = tasks
            .Where(t => t.Status == WorkTaskStatus.Cancelled || (t.Status == WorkTaskStatus.Failed && t.RetriesExhausted))
            .Select(t => t.Id)
            .ToHashSet();

        // A retriable failure that depends on a dead end is unreachable too: retrying it can never make it runnable.
        var unreachable = tasks
            .Where(t => !WorkTaskTransitions.IsTerminal(effective[t.Id]) && !deadEnds.Contains(t.Id) && DependsOnAny(t.Id, deadEnds, effective))
            .Select(t => t.Id)
            .ToList();

        var retryExhausted = tasks
            .Where(t => t.Status == WorkTaskStatus.Failed && t.RetriesExhausted)
            .Select(t => t.Id)
            .ToList();

        var unfinished = tasks.Where(t => !WorkTaskTransitions.IsTerminal(effective[t.Id])).ToList();
        var canProgress = unfinished.Any(t => t.Status is WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval)
            || tasks.Any(t => effective[t.Id] == WorkTaskStatus.Pending && IsReady(t, effective))
            || unfinished.Any(t => t.Status == WorkTaskStatus.Failed && !t.RetriesExhausted && !unreachable.Contains(t.Id));
        var deadlocked = unfinished.Count > 0 && !canProgress;

        return new GraphHealthReport(stalled, unreachable, retryExhausted, deadlocked);
    }

    private IEnumerable<DependencyEdge> Edges(TaskId dependent) =>
        _prerequisites[dependent].Select(p => new DependencyEdge(p, _branches.GetValueOrDefault((dependent, p))));

    private Dictionary<TaskId, WorkTaskStatus> CurrentStatuses() => _tasks.ToDictionary(kv => kv.Key, kv => kv.Value.Status);

    private EdgeState Classify(DependencyEdge edge, IReadOnlyDictionary<TaskId, WorkTaskStatus> status)
    {
        switch (status[edge.Prerequisite])
        {
            case WorkTaskStatus.Completed:
                return edge.Branch is null || string.Equals(edge.Branch, _takenBranches.GetValueOrDefault(edge.Prerequisite), StringComparison.Ordinal)
                    ? EdgeState.Satisfied
                    : EdgeState.Dead;
            case WorkTaskStatus.Skipped:
                return EdgeState.Dead;
            case WorkTaskStatus.Cancelled:
                return EdgeState.Broken;
            case WorkTaskStatus.Failed when _tasks[edge.Prerequisite].RetriesExhausted:
                return EdgeState.Broken;
            default:
                return EdgeState.Pending;
        }
    }

    private bool IsReady(WorkTask task, IReadOnlyDictionary<TaskId, WorkTaskStatus> status)
    {
        var states = Edges(task.Id).Select(e => Classify(e, status)).ToList();
        if (states.Count == 0)
        {
            return true;
        }

        return task.Join == JoinPolicy.AllCompleted
            ? states.All(s => s == EdgeState.Satisfied)
            : states.All(s => s is EdgeState.Satisfied or EdgeState.Dead) && states.Any(s => s == EdgeState.Satisfied);
    }

    private string? SkipReason(WorkTask task, IReadOnlyDictionary<TaskId, WorkTaskStatus> status)
    {
        var edges = Edges(task.Id).Select(e => (Edge: e, State: Classify(e, status))).ToList();
        if (edges.Count == 0)
        {
            return null;
        }

        if (task.Join == JoinPolicy.AnyCompleted)
        {
            return edges.All(e => e.State == EdgeState.Dead)
                ? "Skipped: every prerequisite was skipped or sits on a branch that was not taken."
                : null;
        }

        foreach (var (edge, state) in edges)
        {
            if (state != EdgeState.Dead)
            {
                continue;
            }

            if (status[edge.Prerequisite] == WorkTaskStatus.Skipped)
            {
                return $"Skipped: prerequisite '{edge.Prerequisite}' was skipped.";
            }

            var taken = _takenBranches.GetValueOrDefault(edge.Prerequisite);
            var outcome = taken is null ? "it chose no branch" : $"it chose '{taken}'";
            return $"Skipped: branch '{edge.Branch}' of condition '{edge.Prerequisite}' was not taken ({outcome}).";
        }

        return null;
    }

    /// <summary>
    /// The tasks <see cref="PropagateSkips"/> would skip (in topological order, with the reason) and the status of every
    /// task once they are skipped. Changes nothing.
    /// </summary>
    private (List<(TaskId Task, string Reason)> Skips, Dictionary<TaskId, WorkTaskStatus> Status) SimulateSkips()
    {
        var status = CurrentStatuses();
        var skips = new List<(TaskId, string)>();
        foreach (var id in TopologicalOrder())
        {
            if (status[id] is not (WorkTaskStatus.Pending or WorkTaskStatus.Blocked))
            {
                continue;
            }

            if (SkipReason(_tasks[id], status) is { } reason)
            {
                status[id] = WorkTaskStatus.Skipped;
                skips.Add((id, reason));
            }
        }

        return (skips, status);
    }

    /// <summary>
    /// True if a dead end is reachable through the prerequisites. A skipped task is a settled fact: whatever sits
    /// behind it no longer matters to the tasks that wait for it.
    /// </summary>
    private bool DependsOnAny(TaskId task, HashSet<TaskId> targets, IReadOnlyDictionary<TaskId, WorkTaskStatus> status)
    {
        var visited = new HashSet<TaskId>();
        var stack = new Stack<TaskId>(_prerequisites[task]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (status[current] == WorkTaskStatus.Skipped)
            {
                continue;
            }

            if (targets.Contains(current))
            {
                return true;
            }

            if (visited.Add(current))
            {
                foreach (var next in _prerequisites[current])
                {
                    stack.Push(next);
                }
            }
        }

        return false;
    }

    /// <summary>Path from <paramref name="from"/> to <paramref name="to"/> following prerequisite edges, or null.</summary>
    private List<TaskId>? FindPath(TaskId from, TaskId to)
    {
        var parents = new Dictionary<TaskId, TaskId>();
        var visited = new HashSet<TaskId> { from };
        var queue = new Queue<TaskId>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == to)
            {
                var path = new List<TaskId> { current };
                while (parents.TryGetValue(path[^1], out var parent))
                {
                    path.Add(parent);
                }

                path.Reverse();
                return path;
            }

            foreach (var next in _prerequisites[current])
            {
                if (visited.Add(next))
                {
                    parents[next] = current;
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    private void EnsureKnown(TaskId id)
    {
        if (!_tasks.ContainsKey(id))
        {
            throw new KeyNotFoundException($"Unknown task '{id}'.");
        }
    }
}
