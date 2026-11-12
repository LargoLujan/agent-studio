using System.IO;
using System.Windows;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The canvas of F3-6 (docs/13 section 8, ViewModels): creating, wiring, branching, deleting, the domain's cycle check
/// and the plan behind the drawing. The wire gestures are driven the way Nodify drives them: it writes the pending
/// connection's source and preview target and calls ConnectionCompletedCommand with a <c>(source, target)</c> tuple.
/// </summary>
public sealed class CanvasViewModelTests
{
    private static readonly WorkTaskKind[] AvailableKinds =
    [
        WorkTaskKind.AgentWork, WorkTaskKind.Condition, WorkTaskKind.ToolRun, WorkTaskKind.Review,
        WorkTaskKind.HumanApproval, WorkTaskKind.Integration,
    ];

    private static CanvasViewModel NewCanvas() => new(new FakeTimeProvider(T0));

    private static (object, object?) Ends(ConnectorViewModel from, ConnectorViewModel to) => (from, to);

    private static NodeViewModel Node(CanvasViewModel canvas, string id, WorkTaskKind kind = WorkTaskKind.AgentWork) =>
        canvas.Add(Tid(id), kind, $"Tarea {id}", new Point(0, 0));

    private static void Wire(CanvasViewModel canvas, NodeViewModel from, NodeViewModel to, int output = 0) =>
        canvas.ConnectCommand.Execute(Ends(from.Outputs[output], to.Inputs[0]));

    private static TaskSpec SpecOf(CanvasViewModel canvas, string id) => canvas.BuildTasks().Single(t => t.Id == Tid(id));

    [Fact]
    public void The_palette_offers_every_kind_and_shows_Debate_disabled_until_Phase_5()
    {
        var canvas = NewCanvas();

        Assert.Equal(
            ["Trabajo de agente", "Condición", "Herramienta", "Revisión", "Debate · aplazado", "Aprobación humana", "Integración"],
            canvas.Palette.Select(p => p.Label));
        Assert.All(AvailableKinds, kind => Assert.True(canvas.AddNodeCommand.CanExecute(kind)));
        Assert.False(canvas.AddNodeCommand.CanExecute(WorkTaskKind.Debate));
        Assert.Contains("«Debates»", canvas.Palette.Single(p => p.Kind == WorkTaskKind.Debate).Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_available_kind_creates_a_node_with_a_unique_id_and_its_connectors()
    {
        var canvas = NewCanvas();

        foreach (var kind in AvailableKinds)
        {
            canvas.AddNodeCommand.Execute(kind);
        }

        Assert.Equal(["T1", "T2", "T3", "T4", "T5", "T6"], canvas.Nodes.Select(n => n.Id.Value));
        Assert.Equal(AvailableKinds, canvas.Nodes.Select(n => n.Kind));
        Assert.All(canvas.Nodes, n => Assert.Single(n.Inputs));
        var condition = canvas.Nodes.Single(n => n.Kind == WorkTaskKind.Condition);
        Assert.Equal(["Sí", "No"], condition.Outputs.Select(o => o.Label));
        Assert.Equal([ConditionBranches.Yes, ConditionBranches.No], condition.Outputs.Select(o => o.Branch));
        Assert.All(canvas.Nodes.Where(n => n.Kind != WorkTaskKind.Condition), n => Assert.Null(Assert.Single(n.Outputs).Branch));

        // The last one created is selected, so the properties panel shows it straight away.
        Assert.Same(canvas.Nodes[^1], canvas.SelectedNode);
    }

    [Fact]
    public void A_debate_node_is_shown_as_Phase_5_and_cannot_be_wired()
    {
        var canvas = NewCanvas();
        var t1 = Node(canvas, "T1");
        var debate = Node(canvas, "D1", WorkTaskKind.Debate);

        Assert.True(debate.IsDisabled);
        Assert.Equal("Aplazado", debate.StatusLabel);
        Assert.False(canvas.ConnectCommand.CanExecute(Ends(t1.Outputs[0], debate.Inputs[0])));
        Assert.Equal("Debate: los nodos de debate del lienzo están aplazados; abre el debate desde «Debates».", canvas.Check(t1.Outputs[0], debate.Inputs[0]).Message);
    }

    [Fact]
    public void Dragging_an_output_onto_an_input_adds_a_wire_that_becomes_a_dependency()
    {
        var canvas = NewCanvas();
        var t1 = Node(canvas, "T1");
        var t2 = Node(canvas, "T2", WorkTaskKind.ToolRun);

        canvas.PendingConnection.Source = t1.Outputs[0];
        canvas.PendingConnection.PreviewTarget = t2.Inputs[0];
        Assert.Equal("Conectar con T2", canvas.PendingConnection.PreviewText);
        Assert.False(canvas.PendingConnection.IsPreviewError);

        canvas.ConnectCommand.Execute(Ends(t1.Outputs[0], t2.Inputs[0]));

        var wire = Assert.Single(canvas.Connections);
        Assert.Same(t1.Outputs[0], wire.Source);
        Assert.True(t1.Outputs[0].IsConnected);
        Assert.True(t2.Inputs[0].IsConnected);
        Assert.Equal([new DependencySpec(Tid("T1"))], SpecOf(canvas, "T2").DependsOn);
        Assert.Empty(SpecOf(canvas, "T1").DependsOn);
    }

    [Fact]
    public void A_wire_dragged_from_an_input_onto_an_output_points_the_same_way()
    {
        var canvas = NewCanvas();
        var t1 = Node(canvas, "T1");
        var t2 = Node(canvas, "T2");

        canvas.ConnectCommand.Execute(Ends(t2.Inputs[0], t1.Outputs[0]));

        Assert.Equal([new DependencySpec(Tid("T1"))], SpecOf(canvas, "T2").DependsOn);
    }

    [Fact]
    public void A_wire_that_closes_a_cycle_is_refused_with_the_path_along_the_wires()
    {
        var canvas = NewCanvas();
        var (t1, t2, t3) = (Node(canvas, "T1"), Node(canvas, "T2"), Node(canvas, "T3"));
        Wire(canvas, t1, t2);
        Wire(canvas, t2, t3);

        // While dragging: the wire turns red and the path is shown next to the cursor.
        canvas.PendingConnection.Source = t3.Outputs[0];
        canvas.PendingConnection.PreviewTarget = t1.Inputs[0];
        Assert.True(canvas.PendingConnection.IsPreviewError);
        Assert.Equal("Ciclo: T1 → T2 → T3 → T1", canvas.PendingConnection.PreviewText);

        // On drop: refused, explained, and nothing changes.
        Assert.True(canvas.ConnectCommand.CanExecute(Ends(t3.Outputs[0], t1.Inputs[0])));
        canvas.ConnectCommand.Execute(Ends(t3.Outputs[0], t1.Inputs[0]));

        Assert.Equal(2, canvas.Connections.Count);
        Assert.Equal(NoticeKind.Error, canvas.NoticeKind);
        Assert.Equal("Conexión rechazada. Ciclo: T1 → T2 → T3 → T1", canvas.NoticeText);
        Assert.False(t1.Inputs[0].IsConnected);
    }

    [Fact]
    public void A_node_wired_to_itself_is_a_cycle_too()
    {
        var canvas = NewCanvas();
        var t1 = Node(canvas, "T1");

        Assert.Equal("Ciclo: T1 → T1", canvas.Check(t1.Outputs[0], t1.Inputs[0]).Message);
    }

    [Fact]
    public void Each_branch_of_a_condition_becomes_a_dependency_on_that_branch()
    {
        var canvas = NewCanvas();
        var condition = Node(canvas, "T1", WorkTaskKind.Condition);
        var onYes = Node(canvas, "T2");
        var onNo = Node(canvas, "T3");

        Wire(canvas, condition, onYes, output: 0);
        Wire(canvas, condition, onNo, output: 1);

        Assert.Equal([ConditionBranches.Yes, ConditionBranches.No], canvas.Connections.Select(c => c.Branch));
        Assert.Equal([new DependencySpec(Tid("T1"), ConditionBranches.Yes)], SpecOf(canvas, "T2").DependsOn);
        Assert.Equal([new DependencySpec(Tid("T1"), ConditionBranches.No)], SpecOf(canvas, "T3").DependsOn);
    }

    [Fact]
    public void Both_branches_of_a_condition_cannot_lead_to_the_same_task()
    {
        var canvas = NewCanvas();
        var condition = Node(canvas, "T1", WorkTaskKind.Condition);
        var target = Node(canvas, "T2");
        Wire(canvas, condition, target, output: 0);

        canvas.ConnectCommand.Execute(Ends(condition.Outputs[1], target.Inputs[0]));

        Assert.Single(canvas.Connections);
        Assert.Equal(
            "Conexión rechazada. T2 ya espera a T1 por la otra rama. Una tarea solo puede esperar una rama de cada condición.",
            canvas.NoticeText);
    }

    [Fact]
    public void The_same_wire_twice_is_refused()
    {
        var canvas = NewCanvas();
        var (t1, t2) = (Node(canvas, "T1"), Node(canvas, "T2"));
        Wire(canvas, t1, t2);

        Assert.False(canvas.ConnectCommand.CanExecute(Ends(t1.Outputs[0], t2.Inputs[0])));
        Assert.Equal("Esa conexión ya existe.", canvas.Check(t1.Outputs[0], t2.Inputs[0]).Message);
    }

    [Fact]
    public void Deleting_a_node_removes_every_wire_attached_to_it()
    {
        var canvas = NewCanvas();
        var (t1, t2, t3) = (Node(canvas, "T1"), Node(canvas, "T2"), Node(canvas, "T3"));
        Wire(canvas, t1, t2);
        Wire(canvas, t2, t3);
        Wire(canvas, t1, t3);
        canvas.Select(t2);

        canvas.DeleteSelectionCommand.Execute(null);

        Assert.Equal(["T1", "T3"], canvas.Nodes.Select(n => n.Id.Value));
        var left = Assert.Single(canvas.Connections);
        Assert.Same(t1, left.Source.Node);
        Assert.Same(t3, left.Target.Node);
        Assert.True(t1.Outputs[0].IsConnected);
        Assert.True(t3.Inputs[0].IsConnected);
        Assert.False(t2.Inputs[0].IsConnected);
        Assert.False(t2.Outputs[0].IsConnected);
        Assert.Equal("Selección eliminada. Nodos: 1. Conexiones: 2.", canvas.NoticeText);
        Assert.Equal([new DependencySpec(Tid("T1"))], SpecOf(canvas, "T3").DependsOn);
    }

    [Fact]
    public void Deleting_a_selected_wire_keeps_both_nodes()
    {
        var canvas = NewCanvas();
        var (t1, t2) = (Node(canvas, "T1"), Node(canvas, "T2"));
        Wire(canvas, t1, t2);
        canvas.SelectedConnections.Add(canvas.Connections[0]);

        canvas.DeleteSelectionCommand.Execute(null);

        Assert.Empty(canvas.Connections);
        Assert.Equal(2, canvas.Nodes.Count);
        Assert.False(t1.Outputs[0].IsConnected);
    }

    [Fact]
    public void Selecting_exactly_one_node_shows_it_in_the_properties_panel()
    {
        var canvas = NewCanvas();
        var (t1, t2) = (Node(canvas, "T1"), Node(canvas, "T2"));

        canvas.SelectedNodes.Add(t1);
        Assert.Same(t1, canvas.SelectedNode);

        canvas.SelectedNodes.Add(t2);
        Assert.Null(canvas.SelectedNode);

        canvas.SelectedNodes.Remove(t1);
        Assert.Same(t2, canvas.SelectedNode);
    }

    [Fact]
    public void Editing_a_field_of_a_node_changes_the_plan_but_moving_it_does_not()
    {
        var canvas = NewCanvas();
        var t1 = Node(canvas, "T1");
        var changes = 0;
        canvas.PlanChanged += (_, _) => changes++;

        t1.Location = new Point(40, 40);
        Assert.Equal(0, changes);

        t1.Objective = "Añadir Calc.Multiply";
        t1.WriteScope = ["src/Calc/"];
        Assert.Equal(2, changes);
        Assert.Equal("Añadir Calc.Multiply", SpecOf(canvas, "T1").Objective);
        Assert.Equal(["src/Calc/"], SpecOf(canvas, "T1").WriteScope);
    }

    [Fact]
    public void A_read_only_canvas_refuses_new_nodes_wires_and_deletions()
    {
        var canvas = NewCanvas();
        var (t1, t2) = (Node(canvas, "T1"), Node(canvas, "T2"));
        canvas.Select(t1);

        canvas.IsReadOnly = true;

        Assert.False(canvas.AddNodeCommand.CanExecute(WorkTaskKind.AgentWork));
        Assert.False(canvas.DeleteSelectionCommand.CanExecute(null));
        Assert.False(canvas.ConnectCommand.CanExecute(Ends(t1.Outputs[0], t2.Inputs[0])));
        Assert.Equal("El plan se está ejecutando: no se puede cambiar.", canvas.Check(t1.Outputs[0], t2.Inputs[0]).Message);
    }

    [Fact]
    public void Problems_are_shown_next_to_their_nodes_and_cleared_when_gone()
    {
        var canvas = NewCanvas();
        var (t1, t2) = (Node(canvas, "T1"), Node(canvas, "T2"));

        canvas.ShowProblems(new Dictionary<TaskId, IReadOnlyList<string>> { [Tid("T1")] = ["La tarea T1 no tiene objetivo."] });

        Assert.Equal(["La tarea T1 no tiene objetivo."], t1.Problems);
        Assert.True(t1.HasProblems);
        Assert.Contains("1 problema", t1.AutomationName, StringComparison.Ordinal);
        Assert.Empty(t2.Problems);

        canvas.ShowProblems(new Dictionary<TaskId, IReadOnlyList<string>>());

        Assert.Empty(t1.Problems);
        Assert.False(t1.HasProblems);
    }

    [Fact]
    public void Every_status_has_its_own_label_and_statuses_reset_to_pending()
    {
        var canvas = NewCanvas();
        var nodes = Enum.GetValues<WorkTaskStatus>().Select((_, i) => Node(canvas, $"T{i + 1}")).ToList();

        foreach (var (status, i) in Enum.GetValues<WorkTaskStatus>().Select((s, i) => (s, i)))
        {
            canvas.SetStatus(Tid($"T{i + 1}"), status);
        }

        Assert.Equal(Enum.GetValues<WorkTaskStatus>(), nodes.Select(n => n.Status));
        Assert.Equal(
            ["Pendiente", "En curso", "Bloqueada", "Espera tu aprobación", "Completada", "Fallida", "Cancelada", "Omitida"],
            nodes.Select(n => n.StatusLabel));
        Assert.Equal(8, canvas.StatusLegend.Count);

        canvas.ResetStatuses();

        Assert.All(nodes, n => Assert.Equal(WorkTaskStatus.Pending, n.Status));
    }

    [Fact]
    public void A_loaded_plan_draws_its_dependencies_and_builds_back_the_same_plan()
    {
        var plan = PlanFixtures.Plan();
        var canvas = NewCanvas();

        canvas.LoadTasks(plan.Tasks, CanvasLayout.Empty);

        Assert.Equal(["T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8"], canvas.Nodes.Select(n => n.Id.Value));
        Assert.Equal(
            ["T1>T3", "T2>T3", "T3>T4", "T4:no>T6", "T4:yes>T5", "T5>T7", "T6>T7", "T7>T8"],
            canvas.Connections
                .Select(c => $"{c.Source.Node.Id}{(c.Branch is null ? string.Empty : ":" + c.Branch)}>{c.Target.Node.Id}")
                .Order(StringComparer.Ordinal));
        var rebuilt = PlanDefinition.Create(plan.Summary, canvas.BuildTasks(), plan.Risks, plan.Source, plan.Author);
        Assert.Equal(plan.ContentHash, rebuilt.ContentHash);

        // Layered: every task sits right of the tasks it waits for.
        var x = canvas.Nodes.ToDictionary(n => n.Id.Value, n => n.Location.X);
        Assert.True(x["T1"] < x["T3"] && x["T3"] < x["T4"] && x["T4"] < x["T5"] && x["T5"] < x["T7"] && x["T7"] < x["T8"]);
        Assert.Equal(x["T1"], x["T2"]);
    }

    [Fact]
    public void Dependencies_the_canvas_cannot_draw_stay_in_the_plan()
    {
        var tasks = PlanFixtures.Replace("T3", t => t with { DependsOn = [PlanFixtures.Dep("T1"), PlanFixtures.Dep("T2"), PlanFixtures.Dep("T99")] });
        var canvas = NewCanvas();

        canvas.LoadTasks(tasks, CanvasLayout.Empty);

        var t3 = canvas.Nodes.Single(n => n.Id == Tid("T3"));
        Assert.Equal([PlanFixtures.Dep("T99")], t3.ExtraDependencies);
        Assert.Equal(
            [PlanFixtures.Dep("T1"), PlanFixtures.Dep("T2"), PlanFixtures.Dep("T99")],
            SpecOf(canvas, "T3").DependsOn.OrderBy(d => d.Prerequisite.Value, StringComparer.Ordinal));
    }

    [Fact]
    public void Positions_round_trip_through_the_project_layout_file()
    {
        using var temp = new TempDirectory();
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());
        var plan = PlanFixtures.Plan();
        var first = NewCanvas();
        first.LayoutStore = store;
        first.ProjectRoot = temp.Path;
        first.LoadTasks(plan.Tasks, store.Load(temp.Path));

        first.Nodes.Single(n => n.Id == Tid("T1")).Location = new Point(512, 48);
        first.SaveLayoutCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(temp.Path, ".agentstudio", "canvas-layout.json")));
        var second = NewCanvas();
        second.LoadTasks(plan.Tasks, store.Load(temp.Path));
        Assert.Equal(new Point(512, 48), second.Nodes.Single(n => n.Id == Tid("T1")).Location);
        Assert.Equal(
            first.Nodes.Select(n => (n.Id, n.Location)),
            second.Nodes.Select(n => (n.Id, n.Location)));
    }

    [Fact]
    public void A_damaged_or_newer_layout_file_is_read_as_empty()
    {
        using var temp = new TempDirectory();
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());
        var path = store.GetLayoutPath(temp.Path);

        temp.WriteFile(".agentstudio/canvas-layout.json", "{ not json");
        Assert.Empty(store.Load(temp.Path).Positions);

        File.WriteAllText(path, """{ "version": 2, "nodes": { "T1": { "x": 1, "y": 2 } } }""");
        Assert.Empty(store.Load(temp.Path).Positions);

        File.WriteAllText(path, """{ "version": 1, "nodes": { "T1": { "x": 1, "y": 2 } } }""");
        Assert.Equal(new Point(1, 2), store.Load(temp.Path).Positions["T1"]);
    }
}
