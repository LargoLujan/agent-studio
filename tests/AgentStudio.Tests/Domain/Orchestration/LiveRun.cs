using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

/// <summary>
/// A minimal stand-in for the engine: it mutates a graph through the public API and records the events the engine
/// would write to the decision log. The replayer must rebuild exactly this graph from those events.
/// </summary>
internal sealed class LiveRun
{
    private DateTimeOffset _now;

    public LiveRun(PlanDefinition plan, DateTimeOffset createdAt)
    {
        Plan = plan;
        CreatedAt = createdAt;
        _now = createdAt;
        Graph = plan.BuildGraph(createdAt);
    }

    public PlanDefinition Plan { get; }

    public DateTimeOffset CreatedAt { get; }

    public TaskGraph Graph { get; }

    public List<RunEvent> Events { get; } = [];

    /// <summary>The graph signature after each operation, with the number of events recorded so far.</summary>
    public List<(int EventCount, string Signature)> Snapshots { get; } = [];

    private DateTimeOffset Tick() => _now = _now.AddSeconds(1);

    private void Snapshot() => Snapshots.Add((Events.Count, Signature(Graph)));

    private void Move(string id, WorkTaskStatus to, string reason, Action<WorkTask, DateTimeOffset> apply, IReadOnlyList<EvidenceId>? evidence = null)
    {
        var at = Tick();
        var task = Graph[Tid(id)];
        var from = task.Status;
        apply(task, at);
        Events.Add(new TaskTransitioned(at, task.Id, from, to, reason, evidence ?? []));
        Snapshot();
    }

    public void Start(string id) =>
        Move(id, WorkTaskStatus.Running, "start", (t, at) => t.TransitionTo(WorkTaskStatus.Running, at, "start"));

    public void Complete(string id)
    {
        var evidence = new[] { Eid("ev-" + id) };
        Move(id, WorkTaskStatus.Completed, "done", (t, at) => t.Complete(evidence, at, "done"), evidence);
    }

    public void Decide(string id, string branch)
    {
        var at = Tick();
        Events.Add(new ConditionResolved(at, Tid(id), branch));
        var task = Graph[Tid(id)];
        var from = task.Status;
        Graph.CompleteCondition(task.Id, branch, at, $"chose {branch}");
        Events.Add(new TaskTransitioned(at, task.Id, from, WorkTaskStatus.Completed, $"chose {branch}", []));
        Snapshot();
    }

    public void Fail(string id) =>
        Move(id, WorkTaskStatus.Failed, "boom", (t, at) => t.TransitionTo(WorkTaskStatus.Failed, at, "boom"));

    public void Retry(string id) =>
        Move(id, WorkTaskStatus.Pending, "again", (t, at) => t.Retry(at, "again"));

    public void Block(string id) =>
        Move(id, WorkTaskStatus.Blocked, "interrupted", (t, at) => t.TransitionTo(WorkTaskStatus.Blocked, at, "interrupted"));

    public void Unblock(string id) =>
        Move(id, WorkTaskStatus.Pending, "resumed", (t, at) => t.TransitionTo(WorkTaskStatus.Pending, at, "resumed"));

    public void AwaitApproval(string id) =>
        Move(id, WorkTaskStatus.AwaitingApproval, "ask", (t, at) => t.TransitionTo(WorkTaskStatus.AwaitingApproval, at, "ask"));

    public void Cancel(string id) =>
        Move(id, WorkTaskStatus.Cancelled, "cancel", (t, at) => t.TransitionTo(WorkTaskStatus.Cancelled, at, "cancel"));

    /// <summary>Phase 13: the agent of a running task asks <paramref name="question"/>, and the task waits for the answer, as the engine records it.</summary>
    public void Ask(string id, string question)
    {
        var at = Tick();
        Events.Add(new QuestionPosed(at, Tid(id), question));
        var task = Graph[Tid(id)];
        task.TransitionTo(WorkTaskStatus.Blocked, at, "asks");
        Events.Add(new TaskTransitioned(at, task.Id, WorkTaskStatus.Running, WorkTaskStatus.Blocked, "asks", [], RunStateReplayer.AwaitingAnswerBlock));
        Snapshot();
    }

    /// <summary>Phase 13: the person answers <paramref name="question"/>, and its task goes back to pending.</summary>
    public void Answer(string id, string question)
    {
        var at = Tick();
        Events.Add(new QuestionResolved(at, Tid(id), question));
        Move(id, WorkTaskStatus.Pending, "answered", (t, at2) => t.TransitionTo(WorkTaskStatus.Pending, at2, "answered"));
    }

    /// <summary>Skips what is doomed and records one event per skipped task, all stamped with the same instant.</summary>
    public IReadOnlyList<TaskId> Propagate()
    {
        var at = Tick();
        var skipped = Graph.PropagateSkips(at);
        foreach (var id in skipped)
        {
            var change = Graph[id].History[^1];
            Events.Add(new TaskTransitioned(at, id, change.From, WorkTaskStatus.Skipped, change.Reason, []));
        }

        Snapshot();
        return skipped;
    }

    public RunReplay Replay(IEnumerable<RunEvent>? events = null, string? expectedPlanHash = null) =>
        RunStateReplayer.Replay(Plan, CreatedAt, events ?? Events, expectedPlanHash);

    /// <summary>Everything that identifies the state of a graph except heartbeats: status, retries, evidence, branch and history.</summary>
    public static string Signature(TaskGraph graph) => string.Join(
        "\n",
        graph.Tasks.Select(t => string.Join(
            "|",
            t.Id,
            t.Status,
            t.RetryCount,
            string.Join(",", t.CompletionEvidence),
            graph.TakenBranch(t.Id) ?? "-",
            string.Join(";", t.History.Select(h => $"{h.From}>{h.To}@{h.At:O}:{h.Reason}")))));
}
