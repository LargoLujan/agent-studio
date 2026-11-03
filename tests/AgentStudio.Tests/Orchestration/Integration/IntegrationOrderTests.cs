using AgentStudio.Application.Git;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Tests.Orchestration.Integration;

public sealed class IntegrationOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static TaskGraph Graph(params (string Id, string[] DependsOn)[] tasks)
    {
        var graph = new TaskGraph();
        foreach (var (id, _) in tasks)
        {
            graph.Add(new WorkTask(new TaskId(id), "Tarea " + id, WorkTaskKind.AgentWork, T0));
        }

        foreach (var (id, dependsOn) in tasks)
        {
            foreach (var prerequisite in dependsOn)
            {
                graph.AddDependency(new TaskId(id), new TaskId(prerequisite));
            }
        }

        return graph;
    }

    private static IntegrationSource Source(string id) => new(new TaskId(id), "refs/agentstudio/20261016-a1b2c3/" + id);

    private static readonly (string, string[])[] Plan =
    [
        ("A", []), ("B", []), ("C", ["A"]), ("D", ["C"]), ("E", ["B"]),
    ];

    [Fact]
    public void Sources_are_ordered_by_their_depth_in_the_graph_and_then_by_task_id()
    {
        var ordered = IntegrationOrder.Sort([Source("D"), Source("E"), Source("C"), Source("B"), Source("A")], Graph(Plan));

        Assert.Equal(["A", "B", "C", "E", "D"], ordered.Select(s => s.Task.Value));
    }

    [Fact]
    public void The_order_does_not_depend_on_the_order_the_plan_listed_its_tasks_in()
    {
        var forwards = IntegrationOrder.Sort([Source("E"), Source("D"), Source("A")], Graph(Plan));
        var backwards = IntegrationOrder.Sort([Source("A"), Source("D"), Source("E")], Graph([.. Enumerable.Reverse(Plan)]));

        Assert.Equal(["A", "E", "D"], forwards.Select(s => s.Task.Value));
        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void Task_ids_at_the_same_depth_compare_ordinally()
    {
        var ordered = IntegrationOrder.Sort([Source("T2"), Source("t1"), Source("T10")], Graph(("T2", []), ("t1", []), ("T10", [])));

        Assert.Equal(["T10", "T2", "t1"], ordered.Select(s => s.Task.Value));
    }

    [Fact]
    public void A_task_outside_the_graph_or_given_twice_is_refused()
    {
        var graph = Graph(Plan);

        Assert.Throws<ArgumentException>(() => IntegrationOrder.Sort([Source("A"), Source("Z")], graph));
        Assert.Throws<ArgumentException>(() => IntegrationOrder.Sort([Source("A"), Source("A")], graph));
    }
}
