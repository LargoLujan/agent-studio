using System.Windows;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;

namespace NodifySpike.ViewModels;

/// <summary>Graphs the harness loads: a small readable one (all 7 kinds, all 8 statuses) and the 200/300 stress graph.</summary>
public static class SampleGraphs
{
    /// <summary>
    /// Twelve nodes that show every kind and every status at once, laid out like a run in progress:
    /// T1 fans out to three lanes; T3 is a Condition whose "Sí" branch ran and whose "No" branch was skipped.
    /// </summary>
    public static void LoadSmall(CanvasViewModel canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Clear();

        var t1 = Add(canvas, "T1", WorkTaskKind.AgentWork, "Implementar Calc.Multiply", 0, 160, NodeStatus.Completed);
        var t2 = Add(canvas, "T2", WorkTaskKind.ToolRun, "Compilar y probar", 300, 40, NodeStatus.Completed);
        var t3 = Add(canvas, "T3", WorkTaskKind.Condition, "¿Pruebas en verde?", 600, 40, NodeStatus.Completed);
        var t4 = Add(canvas, "T4", WorkTaskKind.Integration, "Integrar ramas", 900, 0, NodeStatus.Running);
        var t5 = Add(canvas, "T5", WorkTaskKind.AgentWork, "Corregir fallos", 900, 170, NodeStatus.Skipped);
        var t6 = Add(canvas, "T6", WorkTaskKind.Review, "Revisión independiente", 1200, 0, NodeStatus.Pending);
        var t7 = Add(canvas, "T7", WorkTaskKind.HumanApproval, "Aprobar entrega", 1500, 0, NodeStatus.Pending);
        var t8 = Add(canvas, "T8", WorkTaskKind.AgentWork, "Actualizar documentación", 300, 290, NodeStatus.Failed);
        var t9 = Add(canvas, "T9", WorkTaskKind.ToolRun, "Validar enlaces", 600, 320, NodeStatus.Blocked);
        var t10 = Add(canvas, "T10", WorkTaskKind.AgentWork, "Migrar ejemplos", 300, 470, NodeStatus.Cancelled);
        var t11 = Add(canvas, "T11", WorkTaskKind.HumanApproval, "Aprobar borrado", 900, 330, NodeStatus.AwaitingApproval);
        _ = Add(canvas, "T12", WorkTaskKind.Debate, "Debate de diseño", 600, 480, NodeStatus.Pending);

        Wire(canvas, t1, t2);
        Wire(canvas, t2, t3);
        Wire(canvas, t3, t4, Branches.Yes);
        Wire(canvas, t3, t5, Branches.No);
        Wire(canvas, t4, t6);
        Wire(canvas, t5, t6);
        Wire(canvas, t6, t7);
        Wire(canvas, t1, t8);
        Wire(canvas, t8, t9);
        Wire(canvas, t9, t11);
        Wire(canvas, t1, t10);
    }

    /// <summary>
    /// A layered DAG of <paramref name="nodeCount"/> nodes and <paramref name="connectionCount"/> wires: every wire goes from
    /// a column to a later one, so it is acyclic by construction and needs no per-wire probe. Seeded, hence repeatable.
    /// </summary>
    public static void LoadStress(CanvasViewModel canvas, int nodeCount = 200, int connectionCount = 300, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        const int rows = 13;
        var columns = (int)Math.Ceiling(nodeCount / (double)rows);
        if (connectionCount < nodeCount - rows)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionCount), "Every node after the first column needs one parent.");
        }

        canvas.Clear();
        var random = new Random(seed);
        WorkTaskKind[] kinds =
        [
            WorkTaskKind.AgentWork, WorkTaskKind.ToolRun, WorkTaskKind.Condition,
            WorkTaskKind.Review, WorkTaskKind.HumanApproval, WorkTaskKind.Integration,
        ];

        var grid = Enumerable.Range(0, columns).Select(_ => new List<NodeViewModel>()).ToArray();
        for (var i = 0; i < nodeCount; i++)
        {
            var column = i / rows;
            var row = i % rows;
            var status = StressStatus(columns > 1 ? column / (double)(columns - 1) : 0d, random);
            var node = canvas.Add(new TaskId($"N{i + 1}"), kinds[random.Next(kinds.Length)], $"Tarea {i + 1}", new Point(column * 300, row * 150), status);
            grid[column].Add(node);
        }

        var seen = new HashSet<(ConnectorViewModel, ConnectorViewModel)>();

        void Link(NodeViewModel source, NodeViewModel target)
        {
            var output = source.Outputs[random.Next(source.Outputs.Count)];
            var input = target.Inputs[0];
            if (seen.Add((output, input)))
            {
                canvas.LinkUnchecked(output, input);
            }
        }

        for (var column = 1; column < columns; column++)
        {
            foreach (var node in grid[column])
            {
                Link(grid[column - 1][random.Next(grid[column - 1].Count)], node);
            }
        }

        while (canvas.Connections.Count < connectionCount)
        {
            var column = random.Next(1, columns);
            var sourceColumn = random.Next(Math.Max(0, column - 2), column);
            Link(grid[sourceColumn][random.Next(grid[sourceColumn].Count)], grid[column][random.Next(grid[column].Count)]);
        }
    }

    // A run in progress: the first columns are done, the middle ones are active, the rest is waiting.
    private static NodeStatus StressStatus(double progress, Random random)
    {
        var roll = random.NextDouble();
        if (progress < 0.3)
        {
            return roll switch
            {
                < 0.86 => NodeStatus.Completed,
                < 0.91 => NodeStatus.Failed,
                < 0.96 => NodeStatus.Skipped,
                _ => NodeStatus.Cancelled,
            };
        }

        if (progress < 0.5)
        {
            return roll switch
            {
                < 0.4 => NodeStatus.Running,
                < 0.6 => NodeStatus.Blocked,
                < 0.8 => NodeStatus.AwaitingApproval,
                _ => NodeStatus.Completed,
            };
        }

        return NodeStatus.Pending;
    }

    private static NodeViewModel Add(CanvasViewModel canvas, string id, WorkTaskKind kind, string title, double x, double y, NodeStatus status) =>
        canvas.Add(new TaskId(id), kind, title, new Point(x, y), status);

    private static void Wire(CanvasViewModel canvas, NodeViewModel from, NodeViewModel to, string? branch = null)
    {
        var output = branch is null ? from.Outputs[0] : from.Outputs.First(o => o.Branch == branch);
        _ = canvas.Link(output, to.Inputs[0]) ?? throw new InvalidOperationException($"The sample wire {from.Id} -> {to.Id} was refused.");
    }
}
