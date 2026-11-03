using AgentStudio.Domain.Work;

namespace NodifySpike.ViewModels;

public enum ConnectionVerdict
{
    Allowed,
    WrongDirection,
    NotAvailable,
    Duplicate,
    Cycle,
}

/// <summary>The outcome of asking whether two connectors may be joined; <see cref="Message"/> is what the user reads.</summary>
public sealed record ConnectionCheck(ConnectionVerdict Verdict, string? Message)
{
    public static ConnectionCheck Ok { get; } = new(ConnectionVerdict.Allowed, null);
}

/// <summary>
/// Bridge between the canvas and the domain graph. <see cref="TaskGraph"/> is append-only (no remove), so the canvas
/// never keeps one alive: every check rebuilds a fresh graph from the nodes and wires, which is also what
/// <c>PlanDefinition.BuildGraph</c> does (docs/13 section 3.1). Rebuilding makes cascade deletes trivial.
/// </summary>
public static class PlanGraph
{
    /// <summary>Builds the domain graph; a wire output -> input means "the input's node depends on the output's node".</summary>
    public static TaskGraph Build(IEnumerable<NodeViewModel> nodes, IEnumerable<ConnectionViewModel> connections, DateTimeOffset at)
    {
        var graph = new TaskGraph();
        foreach (var node in nodes)
        {
            graph.Add(new WorkTask(node.Id, node.Title, node.Kind, at));
        }

        foreach (var connection in connections)
        {
            graph.AddDependency(connection.Target.Node.Id, connection.Source.Node.Id);
        }

        return graph;
    }

    /// <summary>
    /// Asks the domain whether adding "<paramref name="input"/>'s node depends on <paramref name="output"/>'s node"
    /// closes a cycle. <see cref="TaskGraph.AddDependency"/> throws <see cref="DependencyCycleException"/> with the path
    /// and leaves the graph untouched, so the throwaway graph is the only thing the probe can break.
    /// </summary>
    public static ConnectionCheck CheckCycle(
        IEnumerable<NodeViewModel> nodes,
        IEnumerable<ConnectionViewModel> connections,
        ConnectorViewModel output,
        ConnectorViewModel input,
        DateTimeOffset at)
    {
        var graph = Build(nodes, connections, at);
        try
        {
            graph.AddDependency(input.Node.Id, output.Node.Id);
            return ConnectionCheck.Ok;
        }
        catch (DependencyCycleException ex)
        {
            return new ConnectionCheck(ConnectionVerdict.Cycle, $"Ciclo: {FormatCycle(ex)}");
        }
    }

    /// <summary>
    /// <see cref="DependencyCycleException.Cycle"/> follows "depends on" edges, <c>[dependent, prerequisite, ..., dependent]</c>.
    /// The canvas arrows point the other way, so the path is reversed to read along the wires: T1 → T2 → T3 → T1.
    /// </summary>
    public static string FormatCycle(DependencyCycleException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return string.Join(" → ", exception.Cycle.Reverse().Select(id => id.ToString()));
    }
}
