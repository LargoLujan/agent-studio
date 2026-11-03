using AgentStudio.Application.Git;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Integration;

/// <summary>
/// The deterministic merge order of an integration (docs/13 section 5): topological index, then task id. The
/// topological index of a task is its depth in the graph, 0 for a task without prerequisites and otherwise one more
/// than its deepest prerequisite, so the order depends only on the graph and the ids, never on the order the plan
/// listed its tasks in. Two runs of the same plan therefore merge the same way, and so does a replay.
/// <see cref="IIntegrator"/> merges in the order it is given; the engine sorts the sources with <see cref="Sort"/>.
/// </summary>
public static class IntegrationOrder
{
    /// <exception cref="ArgumentException">A source's task is not in the graph, or a task is given twice.</exception>
    public static IReadOnlyList<IntegrationSource> Sort(IEnumerable<IntegrationSource> sources, TaskGraph graph)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(graph);

        var list = sources.ToList();
        var repeated = list.GroupBy(s => s.Task).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
        {
            throw new ArgumentException($"Task '{repeated.Key}' is given more than once.", nameof(sources));
        }

        var depths = new Dictionary<TaskId, int>();
        foreach (var source in list)
        {
            if (!graph.Tasks.Any(t => t.Id == source.Task))
            {
                throw new ArgumentException($"Task '{source.Task}' is not in the graph.", nameof(sources));
            }
        }

        return list
            .OrderBy(s => Depth(s.Task, graph, depths))
            .ThenBy(s => s.Task.Value, StringComparer.Ordinal)
            .ToArray();
    }

    // The graph is acyclic by construction (TaskGraph rejects cycles), so the recursion ends.
    private static int Depth(TaskId task, TaskGraph graph, Dictionary<TaskId, int> depths)
    {
        if (depths.TryGetValue(task, out var known))
        {
            return known;
        }

        var prerequisites = graph.PrerequisitesOf(task);
        var depth = prerequisites.Count == 0 ? 0 : prerequisites.Max(p => Depth(p, graph, depths)) + 1;
        depths[task] = depth;
        return depth;
    }
}
