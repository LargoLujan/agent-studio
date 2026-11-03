using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

public enum ConnectionVerdict
{
    Allowed,
    WrongDirection,
    NotAvailable,
    Duplicate,
    Cycle,
    BranchConflict,
    PlanAlreadyBroken,
    Rejected,
    ReadOnly,
}

/// <summary>The outcome of asking whether two connectors may be joined; <see cref="Message"/> is what the user reads.</summary>
public sealed record ConnectionCheck(ConnectionVerdict Verdict, string? Message)
{
    public static ConnectionCheck Ok { get; } = new(ConnectionVerdict.Allowed, null);

    public bool IsAllowed => Verdict == ConnectionVerdict.Allowed;
}

/// <summary>
/// Bridge between the canvas and the domain graph (ADR-0015, pattern 6). <see cref="TaskGraph"/> only grows, so the canvas
/// never keeps one: every check builds a fresh graph from the nodes and wires, which is what
/// <c>PlanDefinition.BuildGraph</c> does with the plan. The domain decides cycles and branch rules, so the canvas and the
/// plan validator cannot disagree.
/// </summary>
public static class PlanGraph
{
    private const string BranchConflictRule = "graph.dependency-branch-conflict";

    /// <summary>
    /// Builds the domain graph; a wire output → input means "the input's node depends on the output's node", waiting for
    /// the output's branch when it leaves a condition. Throws <see cref="DomainRuleViolationException"/> when the wires
    /// already break a rule (a loaded plan may).
    /// </summary>
    public static TaskGraph Build(IEnumerable<NodeViewModel> nodes, IEnumerable<ConnectionViewModel> connections, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);
        var graph = new TaskGraph();
        foreach (var node in nodes)
        {
            // Only the shape matters here: a title the user is still typing must not make the probe fail.
            var title = string.IsNullOrWhiteSpace(node.Title) ? node.Id.Value : node.Title;
            graph.Add(new WorkTask(node.Id, title, node.Kind, at, join: node.Join));
        }

        foreach (var connection in connections)
        {
            graph.AddDependency(connection.Target.Node.Id, connection.Source.Node.Id, connection.Branch);
        }

        return graph;
    }

    /// <summary>
    /// Asks the domain whether the wire <paramref name="output"/> → <paramref name="input"/> may be added. The probe
    /// graph is the only thing it can break: <see cref="TaskGraph.AddDependency"/> rejects before changing anything.
    /// </summary>
    public static ConnectionCheck CheckWire(
        IEnumerable<NodeViewModel> nodes,
        IEnumerable<ConnectionViewModel> connections,
        ConnectorViewModel output,
        ConnectorViewModel input,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        TaskGraph graph;
        try
        {
            graph = Build(nodes, connections, at);
        }
        catch (DependencyCycleException ex)
        {
            return new ConnectionCheck(ConnectionVerdict.PlanAlreadyBroken, $"El plan ya tiene un ciclo ({FormatCycle(ex)}): quítalo antes de añadir conexiones.");
        }
        catch (DomainRuleViolationException)
        {
            return new ConnectionCheck(ConnectionVerdict.PlanAlreadyBroken, "Las conexiones del plan ya incumplen una regla del grafo: corrígelas antes de añadir otra.");
        }

        try
        {
            graph.AddDependency(input.Node.Id, output.Node.Id, output.Branch);
            return ConnectionCheck.Ok;
        }
        catch (DependencyCycleException ex)
        {
            return new ConnectionCheck(ConnectionVerdict.Cycle, $"Ciclo: {FormatCycle(ex)}");
        }
        catch (DomainRuleViolationException ex) when (ex.Rule == BranchConflictRule)
        {
            return new ConnectionCheck(
                ConnectionVerdict.BranchConflict,
                $"{input.Node.Id} ya espera a {output.Node.Id} por la otra rama: una tarea solo puede esperar una rama de cada condición.");
        }
        catch (DomainRuleViolationException ex)
        {
            return new ConnectionCheck(ConnectionVerdict.Rejected, $"El grafo no admite esa conexión (regla {ex.Rule}).");
        }
    }

    /// <summary>
    /// <see cref="DependencyCycleException.Cycle"/> follows "depends on" edges, <c>[dependent, prerequisite, …, dependent]</c>.
    /// The wires point the other way, so the path is reversed to read along them: T1 → T2 → T3 → T1.
    /// </summary>
    public static string FormatCycle(DependencyCycleException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return string.Join(" → ", exception.Cycle.Reverse().Select(id => id.ToString()));
    }
}
