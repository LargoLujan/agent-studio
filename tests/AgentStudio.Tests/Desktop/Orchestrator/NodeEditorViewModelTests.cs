using System.Windows;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Domain.Orchestration;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>The properties panel of a node (F3-6): agent, objective, instructions, scope, coverage, steps, retries, timeout and predicate.</summary>
public sealed class NodeEditorViewModelTests
{
    private static EditorOptions Options() =>
        new(PlanFixtures.Roster(), PlanFixtures.Requirements().Requirements, [("build", "Compilar"), ("test", "Probar"), ("lint", "Analizar")]);

    private static NodeViewModel NewNode(string id = "T1", WorkTaskKind kind = WorkTaskKind.AgentWork) =>
        new(Tid(id), kind, $"Tarea {id}", new Point(0, 0));

    private static NodeEditorViewModel Edit(NodeViewModel node, params NodeViewModel[] others) =>
        new(node, Options(), [node, .. others], isReadOnly: false);

    [Fact]
    public void Texts_agent_workspace_and_scope_edit_the_node()
    {
        var node = NewNode();
        var editor = Edit(node);

        editor.Title = "Implementar Calc.Multiply";
        editor.Objective = "Añadir Calc.Multiply(a, b)";
        editor.Instructions = "Sin cambiar la API existente.";
        editor.SelectedAgent = editor.AgentOptions.Single(o => o.Value == Aid("ejecutor"));
        editor.SelectedWorkspace = editor.WorkspaceOptions.Single(o => o.Value == WorkspaceMode.Write);
        editor.WriteScopeText = "src/Calc/\r\n  tests/Calc.Tests/  \r\n\r\n";

        Assert.Equal("Implementar Calc.Multiply", node.Title);
        Assert.Equal("Añadir Calc.Multiply(a, b)", node.Objective);
        Assert.Equal("Sin cambiar la API existente.", node.Instructions);
        Assert.Equal(Aid("ejecutor"), node.Agent);
        Assert.Equal(WorkspaceMode.Write, node.Workspace);
        Assert.Equal(["src/Calc/", "tests/Calc.Tests/"], node.WriteScope);
        Assert.Equal("ejecutor · Ejecutor · p-anthropic", editor.SelectedAgent.Label);
    }

    [Fact]
    public void Requirements_and_steps_are_check_lists_that_keep_what_the_project_does_not_have()
    {
        var node = NewNode();
        node.Covers = [Rid("R1"), Rid("R9")];
        node.VerificationSteps = ["test", "deploy"];
        var editor = Edit(node);

        Assert.Equal(["R1", "R2", "R9"], editor.CoverOptions.Select(o => o.Value));
        Assert.Equal([true, false, true], editor.CoverOptions.Select(o => o.IsChecked));
        Assert.Equal([true, true, false], editor.CoverOptions.Select(o => o.IsKnown));
        Assert.Equal(["build", "test", "lint", "deploy"], editor.StepOptions.Select(o => o.Value));
        Assert.Equal([false, true, false, true], editor.StepOptions.Select(o => o.IsChecked));

        editor.StepOptions[0].IsChecked = true;
        editor.CoverOptions[2].IsChecked = false;

        Assert.Equal(["build", "test", "deploy"], node.VerificationSteps);
        Assert.Equal([Rid("R1")], node.Covers);
    }

    [Fact]
    public void Retries_and_timeout_take_only_valid_numbers()
    {
        var node = NewNode();
        var editor = Edit(node);

        editor.MaxRetriesText = "tres";
        Assert.NotNull(editor.RetriesError);
        Assert.Equal(2, node.MaxRetries);

        editor.MaxRetriesText = "1";
        Assert.Null(editor.RetriesError);
        Assert.Equal(1, node.MaxRetries);

        editor.TimeoutMinutesText = "2,5";
        Assert.Equal(TimeSpan.FromMinutes(2.5), node.Timeout);

        editor.TimeoutMinutesText = "-4";
        Assert.NotNull(editor.TimeoutError);
        Assert.Equal(TimeSpan.FromMinutes(2.5), node.Timeout);

        editor.TimeoutMinutesText = string.Empty;
        Assert.Null(editor.TimeoutError);
        Assert.Null(node.Timeout);
    }

    [Fact]
    public void A_predicate_exists_once_its_kind_and_task_are_chosen()
    {
        var condition = NewNode("T2", WorkTaskKind.Condition);
        var editor = Edit(condition, NewNode("T1"));

        editor.SelectedPredicateKind = editor.PredicateKindOptions.Single(o => o.Value == PredicateKind.TestsPassed);
        Assert.Null(condition.Condition);

        editor.SelectedPredicateTask = editor.PredicateTaskOptions.Single(o => o.Value == Tid("T1"));
        Assert.Equal(new TestsPassed(Tid("T1")), condition.Condition);

        editor.SelectedPredicateKind = editor.PredicateKindOptions.Single(o => o.Value == PredicateKind.OutputEquals);
        Assert.Null(condition.Condition);

        editor.PredicateKey = "estado";
        editor.PredicateValue = "ok";
        Assert.Equal(new OutputEquals(Tid("T1"), "estado", "ok"), condition.Condition);
        Assert.True(editor.PredicateNeedsKeyAndValue);
        Assert.False(editor.PredicateNeedsPrefix);
    }

    [Fact]
    public void An_agent_or_a_predicate_task_the_project_does_not_have_is_kept_and_marked()
    {
        var condition = NewNode("T2", WorkTaskKind.Condition);
        condition.Agent = Aid("fantasma");
        condition.Condition = new TaskSucceeded(Tid("T77"));

        var editor = Edit(condition);

        Assert.Equal("fantasma (no está en la plantilla)", editor.SelectedAgent.Label);
        Assert.Equal("T77 (no está en el plan)", editor.SelectedPredicateTask?.Label);
        Assert.Equal(new TaskSucceeded(Tid("T77")), condition.Condition);
    }

    [Fact]
    public void A_null_pushed_by_a_combo_box_that_is_being_torn_down_changes_nothing()
    {
        var node = NewNode();
        node.Agent = Aid("ejecutor");
        node.ToolId = "lint";
        var editor = Edit(node);

        editor.SelectedAgent = null!;
        editor.SelectedTool = null!;

        Assert.Equal(Aid("ejecutor"), node.Agent);
        Assert.Equal("lint", node.ToolId);
    }
}
