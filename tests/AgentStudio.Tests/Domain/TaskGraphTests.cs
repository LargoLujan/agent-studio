using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class TaskGraphTests
{
    private static TaskGraph Graph(params string[] ids)
    {
        var graph = new TaskGraph();
        foreach (var id in ids)
        {
            graph.Add(NewTask(id));
        }

        return graph;
    }

    private static void Dep(TaskGraph graph, string dependent, string prerequisite) => graph.AddDependency(Tid(dependent), Tid(prerequisite));

    private static void Start(TaskGraph graph, string id, DateTimeOffset at) => graph[Tid(id)].TransitionTo(WorkTaskStatus.Running, at, "start");

    private static void Finish(TaskGraph graph, string id)
    {
        if (graph[Tid(id)].Status == WorkTaskStatus.Pending)
        {
            Start(graph, id, T0);
        }

        graph[Tid(id)].Complete([Eid("e")], T0, "done");
    }

    private static void Fail(TaskGraph graph, string id)
    {
        Start(graph, id, T0);
        graph[Tid(id)].TransitionTo(WorkTaskStatus.Failed, T0, "boom");
    }

    private static void Cancel(TaskGraph graph, string id) => graph[Tid(id)].TransitionTo(WorkTaskStatus.Cancelled, T0, "cancel");

    // ---- dependencies and cycles ----

    [Fact]
    public void A_task_cannot_depend_on_itself()
    {
        var graph = Graph("a");

        var ex = Assert.Throws<DependencyCycleException>(() => Dep(graph, "a", "a"));

        Assert.Equal([Tid("a"), Tid("a")], ex.Cycle);
        Assert.Empty(graph.PrerequisitesOf(Tid("a")));
    }

    [Fact]
    public void A_dependency_that_closes_a_cycle_is_rejected_with_a_real_closed_path()
    {
        var graph = Graph("a", "b", "c");
        Dep(graph, "a", "b"); // a needs b
        Dep(graph, "b", "c"); // b needs c

        var ex = Assert.Throws<DependencyCycleException>(() => Dep(graph, "c", "a")); // c needs a would close the loop

        Assert.Equal(ex.Cycle[0], ex.Cycle[^1]);
        Assert.Equal(Tid("c"), ex.Cycle[0]);
        Assert.Equal(Tid("a"), ex.Cycle[1]); // first edge is the rejected one
        for (var i = 1; i < ex.Cycle.Count - 1; i++)
        {
            Assert.Contains(ex.Cycle[i + 1], graph.PrerequisitesOf(ex.Cycle[i]));
        }

        Assert.Empty(graph.PrerequisitesOf(Tid("c")));
    }

    [Fact]
    public void Adding_the_same_dependency_twice_is_harmless()
    {
        var graph = Graph("a", "b");
        Dep(graph, "a", "b");
        Dep(graph, "a", "b");

        Assert.Single(graph.PrerequisitesOf(Tid("a")));
    }

    [Fact]
    public void Duplicate_task_ids_and_unknown_tasks_are_rejected()
    {
        var graph = Graph("a");

        Violation(() => graph.Add(NewTask("a")));
        Assert.Throws<KeyNotFoundException>(() => Dep(graph, "a", "ghost"));
        Assert.Throws<KeyNotFoundException>(() => Dep(graph, "ghost", "a"));
    }

    // ---- ready tasks ----

    [Fact]
    public void ReadyTasks_are_pending_tasks_whose_prerequisites_are_all_completed()
    {
        var graph = Graph("a", "b", "c", "d");
        Dep(graph, "c", "a");
        Dep(graph, "c", "b");
        Dep(graph, "d", "c");

        Assert.Equal(["a", "b"], graph.ReadyTasks().Select(t => t.Id.Value));

        Finish(graph, "a");
        Assert.Equal(["b"], graph.ReadyTasks().Select(t => t.Id.Value)); // c still waits for b

        Finish(graph, "b");
        Assert.Equal(["c"], graph.ReadyTasks().Select(t => t.Id.Value));
    }

    [Fact]
    public void A_non_pending_task_is_never_ready()
    {
        var graph = Graph("a", "b");
        Start(graph, "a", T0);
        Cancel(graph, "b");

        Assert.Empty(graph.ReadyTasks());
    }

    [Fact]
    public void A_task_whose_prerequisite_is_failed_cancelled_or_running_is_not_ready()
    {
        var graph = Graph("p1", "p2", "p3", "x1", "x2", "x3");
        Dep(graph, "x1", "p1");
        Dep(graph, "x2", "p2");
        Dep(graph, "x3", "p3");
        Fail(graph, "p1");
        Cancel(graph, "p2");
        Start(graph, "p3", T0);

        Assert.DoesNotContain(graph.ReadyTasks(), t => t.Id.Value.StartsWith('x'));
    }

    // ---- topological order ----

    [Fact]
    public void TopologicalOrder_respects_every_edge_and_is_deterministic()
    {
        var graph = Graph("a", "b", "c", "d", "e");
        Dep(graph, "a", "e");
        Dep(graph, "b", "a");
        Dep(graph, "c", "a");
        Dep(graph, "d", "b");
        Dep(graph, "d", "c");

        var first = graph.TopologicalOrder();
        var second = graph.TopologicalOrder();

        Assert.Equal(first, second);
        Assert.Equal(5, first.Count);
        AssertRespectsEdges(graph, first);
    }

    [Fact]
    public void TopologicalOrder_of_an_empty_graph_is_empty()
    {
        Assert.Empty(new TaskGraph().TopologicalOrder());
    }

    [Fact]
    public void Random_graphs_never_get_a_cycle_and_TopologicalOrder_respects_all_accepted_edges()
    {
        var random = new Random(424242);
        var rejected = 0;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var size = random.Next(2, 13);
            var ids = Enumerable.Range(0, size).Select(i => $"t{i}").ToArray();
            var graph = Graph(ids);
            var accepted = new List<(TaskId Dependent, TaskId Prerequisite)>();

            for (var attempt = 0; attempt < size * 3; attempt++)
            {
                var dependent = Tid(ids[random.Next(size)]);
                var prerequisite = Tid(ids[random.Next(size)]);
                try
                {
                    graph.AddDependency(dependent, prerequisite);
                    accepted.Add((dependent, prerequisite));
                }
                catch (DependencyCycleException ex)
                {
                    rejected++;
                    Assert.Equal(ex.Cycle[0], ex.Cycle[^1]);
                    Assert.Equal(dependent, ex.Cycle[0]);
                    Assert.Equal(prerequisite, ex.Cycle[1]);
                    for (var i = 1; i < ex.Cycle.Count - 1; i++)
                    {
                        Assert.Contains((ex.Cycle[i], ex.Cycle[i + 1]), accepted);
                    }
                }
            }

            var order = graph.TopologicalOrder();
            Assert.Equal(size, order.Count);
            Assert.Equal(size, order.Distinct().Count());
            foreach (var (dependent, prerequisite) in accepted)
            {
                Assert.True(order.ToList().IndexOf(prerequisite) < order.ToList().IndexOf(dependent), $"iteration {iteration}: {prerequisite} must precede {dependent}");
            }

            Assert.Equal(order, graph.TopologicalOrder());
        }

        Assert.True(rejected > 0, "The generator never produced a cycle, so the property would be vacuous.");
    }

    private static void AssertRespectsEdges(TaskGraph graph, IReadOnlyList<TaskId> order)
    {
        foreach (var task in graph.Tasks)
        {
            foreach (var prerequisite in graph.PrerequisitesOf(task.Id))
            {
                Assert.True(order.ToList().IndexOf(prerequisite) < order.ToList().IndexOf(task.Id));
            }
        }
    }

    // ---- health analysis ----

    [Fact]
    public void Analyze_reports_running_tasks_without_activity_beyond_the_threshold_as_stalled()
    {
        var graph = Graph("quiet", "busy", "borderline", "waiting");
        Start(graph, "quiet", T0);
        Start(graph, "busy", T0);
        Start(graph, "borderline", T0);
        graph[Tid("busy")].RecordActivity(T0.AddMinutes(9));
        var now = T0.AddMinutes(10);

        var report = graph.Analyze(now, TimeSpan.FromMinutes(10));
        Assert.Empty(report.StalledTasks); // exactly at the threshold is not beyond it

        var later = graph.Analyze(now.AddSeconds(1), TimeSpan.FromMinutes(10));
        Assert.Equal([Tid("quiet"), Tid("borderline")], later.StalledTasks);
    }

    [Fact]
    public void Analyze_marks_unfinished_dependants_of_a_cancelled_task_as_unreachable_transitively()
    {
        var graph = Graph("root", "mid", "leaf", "independent");
        Dep(graph, "mid", "root");
        Dep(graph, "leaf", "mid");
        Cancel(graph, "root");

        var report = graph.Analyze(T0, TimeSpan.FromHours(1));

        Assert.Equal([Tid("mid"), Tid("leaf")], report.UnreachableTasks);
        Assert.DoesNotContain(Tid("independent"), report.UnreachableTasks);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public void Analyze_marks_dependants_of_a_failed_task_without_retries_left_as_unreachable_and_flags_it_as_exhausted()
    {
        var graph = new TaskGraph();
        graph.Add(NewTask("dead", maxRetries: 0));
        graph.Add(NewTask("after"));
        Dep(graph, "after", "dead");
        Fail(graph, "dead");

        var report = graph.Analyze(T0, TimeSpan.FromHours(1));

        Assert.Equal([Tid("after")], report.UnreachableTasks);
        Assert.Equal([Tid("dead")], report.RetryExhaustedTasks);
    }

    [Fact]
    public void Analyze_does_not_call_dependants_of_a_retriable_failure_unreachable()
    {
        var graph = Graph("flaky", "after");
        Dep(graph, "after", "flaky");
        Fail(graph, "flaky");

        var report = graph.Analyze(T0, TimeSpan.FromHours(1));

        Assert.Empty(report.UnreachableTasks);
        Assert.Empty(report.RetryExhaustedTasks);
        Assert.False(report.IsDeadlocked); // the failed task can still be retried
    }

    [Fact]
    public void Analyze_marks_a_retriable_failure_that_depends_on_a_cancelled_task_as_unreachable()
    {
        var graph = Graph("gone", "retry");
        Dep(graph, "retry", "gone");
        Fail(graph, "retry");
        Cancel(graph, "gone");

        var report = graph.Analyze(T0, TimeSpan.FromHours(1));

        Assert.Equal([Tid("retry")], report.UnreachableTasks);
        Assert.True(report.IsDeadlocked); // retrying it can never make it runnable
    }

    [Fact]
    public void Analyze_ignores_finished_dependants_of_a_cancelled_task()
    {
        var graph = Graph("a", "b");
        Cancel(graph, "a");
        Cancel(graph, "b");

        Assert.Empty(graph.Analyze(T0, TimeSpan.FromHours(1)).UnreachableTasks);
    }

    [Fact]
    public void A_healthy_graph_in_progress_reports_nothing()
    {
        var graph = Graph("a", "b");
        Dep(graph, "b", "a");

        var report = graph.Analyze(T0, TimeSpan.FromHours(1));

        Assert.True(report.IsHealthy);
        Assert.False(report.IsDeadlocked);
    }

    [Fact]
    public void An_empty_or_fully_finished_graph_is_not_deadlocked()
    {
        Assert.False(new TaskGraph().Analyze(T0, TimeSpan.FromHours(1)).IsDeadlocked);

        var graph = Graph("a", "b");
        Finish(graph, "a");
        Cancel(graph, "b");
        Assert.False(graph.Analyze(T0, TimeSpan.FromHours(1)).IsDeadlocked);
    }

    [Fact]
    public void Deadlock_is_unfinished_work_with_nothing_able_to_advance()
    {
        var graph = new TaskGraph();
        graph.Add(NewTask("dead", maxRetries: 0));
        graph.Add(NewTask("after"));
        Dep(graph, "after", "dead");
        Fail(graph, "dead");

        Assert.True(graph.Analyze(T0, TimeSpan.FromHours(1)).IsDeadlocked);
    }

    [Fact]
    public void Blocked_work_alone_is_a_deadlock()
    {
        var graph = Graph("a");
        graph[Tid("a")].TransitionTo(WorkTaskStatus.Blocked, T0, "waiting");

        Assert.True(graph.Analyze(T0, TimeSpan.FromHours(1)).IsDeadlocked);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.AwaitingApproval)]
    [InlineData(WorkTaskStatus.Completed)]
    [InlineData(WorkTaskStatus.Failed)]
    [InlineData(WorkTaskStatus.Cancelled)]
    public void A_prerequisite_cannot_be_added_to_a_dependent_task_that_already_started(WorkTaskStatus status)
    {
        var graph = Graph("a", "b");
        var dependent = graph[Tid("b")];
        switch (status)
        {
            case WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval:
                dependent.TransitionTo(status, T0, "go");
                break;
            case WorkTaskStatus.Completed:
                Finish(graph, "b");
                break;
            case WorkTaskStatus.Failed:
                Fail(graph, "b");
                break;
            default:
                Cancel(graph, "b");
                break;
        }

        Assert.Equal("graph.dependency-on-started-task", Violation(() => Dep(graph, "b", "a")).Rule);
        Assert.Empty(graph.PrerequisitesOf(Tid("b")));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Pending)]
    [InlineData(WorkTaskStatus.Blocked)]
    public void A_prerequisite_can_be_added_to_a_pending_or_blocked_dependent(WorkTaskStatus status)
    {
        var graph = Graph("a", "b");
        if (status == WorkTaskStatus.Blocked)
        {
            graph[Tid("b")].TransitionTo(WorkTaskStatus.Blocked, T0, "wait");
        }

        Dep(graph, "b", "a");

        Assert.Equal([Tid("a")], graph.PrerequisitesOf(Tid("b")));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.AwaitingApproval)]
    public void Running_or_awaiting_approval_work_prevents_a_deadlock(WorkTaskStatus status)
    {
        var graph = Graph("a", "b");
        Dep(graph, "b", "a");
        graph[Tid("a")].TransitionTo(status, T0, "go");

        Assert.False(graph.Analyze(T0, TimeSpan.FromHours(1)).IsDeadlocked);
    }
}
