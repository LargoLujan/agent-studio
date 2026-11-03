using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Work;

public sealed record TaskStatusChange(WorkTaskStatus From, WorkTaskStatus To, DateTimeOffset At, string Reason);

public sealed record WorkTaskSnapshot(
    TaskId Id,
    string Title,
    WorkTaskKind Kind,
    WorkTaskStatus Status,
    AgentId? AssignedTo,
    int RetryCount,
    IReadOnlyList<EvidenceId> CompletionEvidence);

/// <summary>
/// A unit of work in the orchestration graph. Every status change goes through <see cref="TransitionTo"/> so the
/// history is complete and illegal transitions are impossible.
/// </summary>
public sealed class WorkTask
{
    private readonly List<TaskStatusChange> _history = [];
    private readonly List<EvidenceId> _completionEvidence = [];

    public WorkTask(
        TaskId id,
        string title,
        WorkTaskKind kind,
        DateTimeOffset createdAt,
        AgentId? assignedTo = null,
        int maxRetries = 2,
        bool? requiresEvidence = null,
        JoinPolicy join = JoinPolicy.AllCompleted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);

        Id = id;
        Title = title;
        Kind = kind;
        AssignedTo = assignedTo;
        MaxRetries = maxRetries;
        Join = join;

        // Work that produces results must prove them; conditions and approvals are decisions, not results.
        RequiresEvidence = requiresEvidence ?? kind is WorkTaskKind.AgentWork or WorkTaskKind.ToolRun or WorkTaskKind.Integration;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
    }

    public TaskId Id { get; }

    public string Title { get; }

    public WorkTaskKind Kind { get; }

    public AgentId? AssignedTo { get; private set; }

    public int MaxRetries { get; }

    /// <summary>How the task combines its prerequisite edges once it is part of a <see cref="TaskGraph"/>.</summary>
    public JoinPolicy Join { get; }

    public int RetryCount { get; private set; }

    public bool RequiresEvidence { get; }

    public WorkTaskStatus Status { get; private set; } = WorkTaskStatus.Pending;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public IReadOnlyList<TaskStatusChange> History => _history;

    public IReadOnlyList<EvidenceId> CompletionEvidence => _completionEvidence;

    public bool RetriesExhausted => RetryCount >= MaxRetries;

    public void Assign(AgentId agent)
    {
        if (Status is not (WorkTaskStatus.Pending or WorkTaskStatus.Blocked))
        {
            throw new DomainRuleViolationException("task.assign-only-when-not-started", $"Task '{Id}' cannot be reassigned while {Status}.");
        }

        AssignedTo = agent;
    }

    /// <summary>
    /// Moves the task to <paramref name="next"/> if the transition is legal. This entity does not know the task
    /// graph: that a task only starts once its dependencies are completed is enforced by the orchestration engine
    /// (Phase 3), not here. Completing, retrying and skipping have their own entry points
    /// (<see cref="Complete"/>, <see cref="Retry"/> and <see cref="TaskGraph.PropagateSkips"/>).
    /// </summary>
    public void TransitionTo(WorkTaskStatus next, DateTimeOffset at, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (next == WorkTaskStatus.Completed)
        {
            throw new DomainRuleViolationException("task.complete-via-complete", "Use Complete(...) so completion evidence is recorded.");
        }

        if (Status == WorkTaskStatus.Failed && next == WorkTaskStatus.Pending)
        {
            throw new DomainRuleViolationException("task.retry-via-retry", "Use Retry(...) so retries are counted.");
        }

        if (next == WorkTaskStatus.Skipped)
        {
            throw new DomainRuleViolationException("task.skip-via-graph", "Only the task graph can skip a task (TaskGraph.PropagateSkips).");
        }

        Move(next, at, reason);
    }

    /// <summary>
    /// Marks the task completed. Fails if the task must prove its result and no evidence is given. Only the presence
    /// of evidence ids is checked: that they exist in the evidence store, and that the dependencies are completed, is
    /// enforced by the orchestration engine (Phase 3), not by the entity.
    /// </summary>
    public void Complete(IEnumerable<EvidenceId> evidence, DateTimeOffset at, string reason)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var items = evidence.Distinct().ToList();

        if (RequiresEvidence && items.Count == 0)
        {
            throw new DomainRuleViolationException(
                "task.completion-requires-evidence",
                $"Task '{Id}' cannot be completed without evidence.");
        }

        Move(WorkTaskStatus.Completed, at, reason);
        _completionEvidence.AddRange(items);
    }

    /// <summary>Puts a failed task back to pending. Refuses once the retry budget is spent.</summary>
    public void Retry(DateTimeOffset at, string reason)
    {
        if (Status != WorkTaskStatus.Failed)
        {
            throw new DomainRuleViolationException("task.retry-only-failed", $"Only failed tasks can be retried; '{Id}' is {Status}.");
        }

        if (RetriesExhausted)
        {
            throw new DomainRuleViolationException("task.retry-limit", $"Task '{Id}' already used its {MaxRetries} retries.");
        }

        RetryCount++;
        Move(WorkTaskStatus.Pending, at, reason);
    }

    /// <summary>Called by <see cref="TaskGraph"/> when what the task waits for can no longer happen.</summary>
    internal void Skip(DateTimeOffset at, string reason) => Move(WorkTaskStatus.Skipped, at, reason);

    /// <summary>Heartbeat from the running agent; used to detect abandoned tasks.</summary>
    public void RecordActivity(DateTimeOffset at)
    {
        if (Status != WorkTaskStatus.Running)
        {
            throw new DomainRuleViolationException("task.activity-only-running", $"Task '{Id}' is not running.");
        }

        if (at > LastActivityAt)
        {
            LastActivityAt = at;
        }
    }

    public WorkTaskSnapshot Snapshot() => new(Id, Title, Kind, Status, AssignedTo, RetryCount, [.. _completionEvidence]);

    private void Move(WorkTaskStatus next, DateTimeOffset at, string reason)
    {
        if (!WorkTaskTransitions.IsAllowed(Status, next))
        {
            throw new DomainRuleViolationException("task.illegal-transition", $"Task '{Id}' cannot go from {Status} to {next}.");
        }

        _history.Add(new TaskStatusChange(Status, next, at, reason));
        Status = next;
        if (at > LastActivityAt)
        {
            LastActivityAt = at;
        }
    }
}
