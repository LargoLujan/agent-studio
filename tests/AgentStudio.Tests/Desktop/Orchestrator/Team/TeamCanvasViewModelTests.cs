using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>
/// F13-1 and F13-2 (docs/17 D-07 to D-11): the Team layer is the template seen as a graph. Its nodes are the agents and the
/// repository; «Reparte», «Revisa» and «Trabaja en» are drawn from it; and none of its cables is ever a dependency of a task.
/// </summary>
public sealed class TeamCanvasViewModelTests
{
    [Fact]
    public void It_draws_the_template_as_nodes_and_cables_and_hides_the_participants_of_the_debates()
    {
        var team = LoadedTeam();

        Assert.Equal(["orquestador", "ejecutor", "juez", "juez-claude", "juez-revision"], team.Agents.Select(a => a.Id.Value));
        Assert.Equal(6, team.Nodes.Count);
        Assert.IsType<RepositoryNodeViewModel>(team.Nodes[^1]);
        Assert.Single(team.Connections, c => c.Kind == TeamEdgeKind.Distributes);
        Assert.Equal(3, team.Connections.Count(c => c.Kind == TeamEdgeKind.Reviews));
        Assert.Single(team.Connections, c => c.Kind == TeamEdgeKind.WorksIn);
        Assert.Empty(team.Graph.Validate());

        team.ShowOnlyOrchestration = false;
        Assert.Contains(team.Agents, a => a.Id == DefaultAgentRoster.Architect);
        Assert.Contains(team.Agents, a => a.Id == DefaultAgentRoster.Critic);
    }

    [Fact]
    public void A_judge_of_the_vendor_of_a_producer_draws_its_cable_as_an_error_with_the_text()
    {
        var team = LoadedTeam();

        var same = team.Connections.Single(c => c.Kind == TeamEdgeKind.Reviews && c.Edge.ToAgent == DefaultAgentRoster.ClaudeJudge);
        var other = team.Connections.First(c => c.Kind == TeamEdgeKind.Reviews && c.Edge.ToAgent == DefaultAgentRoster.Judge);

        Assert.True(same.IsError);
        Assert.False(other.IsError);
        Assert.Equal("ejecutor y juez-claude son de la misma empresa: el juez tiene que ser de otra.", same.Text);
        Assert.Equal("revisa", other.Text);
    }

    [Fact]
    public void The_cables_of_the_team_never_enter_the_plan_and_BuildTasks_is_the_same_with_and_without_the_layer()
    {
        var canvas = new CanvasViewModel(new FakeTimeProvider(T0));
        var t1 = canvas.Add(new TaskId("T1"), WorkTaskKind.AgentWork, "Uno", new System.Windows.Point(0, 0));
        t1.Agent = DefaultAgentRoster.Executor;
        var t2 = canvas.Add(new TaskId("T2"), WorkTaskKind.Review, "Dos", new System.Windows.Point(300, 0));
        t2.Agent = DefaultAgentRoster.Judge;
        Assert.NotNull(canvas.Link(t1.Outputs[0], t2.Inputs[0]));
        var before = PlanDefinition.Create("plan", canvas.BuildTasks(), [], PlanSource.User, null).ContentHash;
        var tasksBefore = canvas.BuildTasks();

        canvas.Team.Load(Roster(), RepositoryReading.Of(Repository()), CanvasLayout.Empty);

        Assert.NotEmpty(canvas.Team.Connections);
        var after = PlanDefinition.Create("plan", canvas.BuildTasks(), [], PlanSource.User, null).ContentHash;
        Assert.Equal(before, after);
        Assert.Equal(tasksBefore.Count, canvas.BuildTasks().Count);
        Assert.Single(canvas.Connections);
        Assert.All(canvas.BuildTasks(), t => Assert.DoesNotContain(t.DependsOn, d => d.Prerequisite.Value is "orquestador" or "ejecutor" or "juez" or "repository"));
        Assert.Equal(["T1", "T2"], canvas.Nodes.Select(n => n.Id.Value));
    }

    [Fact]
    public void The_selection_is_linked_in_both_directions()
    {
        var canvas = new CanvasViewModel(new FakeTimeProvider(T0));
        var t1 = canvas.Add(new TaskId("T1"), WorkTaskKind.AgentWork, "Uno", new System.Windows.Point(0, 0));
        t1.Agent = DefaultAgentRoster.Executor;
        var t2 = canvas.Add(new TaskId("T2"), WorkTaskKind.Review, "Dos", new System.Windows.Point(300, 0));
        t2.Agent = DefaultAgentRoster.Judge;
        canvas.Team.Load(Roster(), RepositoryReading.Of(Repository()), CanvasLayout.Empty);

        // A click on the card of an agent: its tasks shine in the plan.
        var executor = canvas.Team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);
        canvas.Team.Select(executor);
        Assert.True(t1.IsHighlighted);
        Assert.False(t2.IsHighlighted);
        Assert.Same(executor, canvas.Team.SelectedAgent);

        // A click on a task: its agent shines in the team layer and in the strip.
        canvas.Select(t2);
        Assert.True(canvas.Team.Agents.Single(a => a.Id == DefaultAgentRoster.Judge).IsHighlighted);
        Assert.False(executor.IsHighlighted);
        Assert.Equal("ejecutor", t1.AgentLabel);
    }

    [Fact]
    public void Showing_the_layers_is_a_choice_and_the_plan_is_the_default_of_the_model()
    {
        var canvas = new CanvasViewModel(new FakeTimeProvider(T0));

        Assert.True(canvas.IsPlanLayer);
        canvas.ShowTeamCommand.Execute(null);
        Assert.True(canvas.IsTeamLayer);
        Assert.False(canvas.IsPlanLayer);
        canvas.ShowPlanCommand.Execute(null);
        Assert.Equal(CanvasLayer.Plan, canvas.Layer);
    }

    [Fact]
    public void The_limits_of_a_team_are_seen_while_it_is_assembled()
    {
        var executors = Enumerable.Range(1, 9).Select(n => Roster().Single(a => a.Id == DefaultAgentRoster.Executor) with { Id = new AgentId($"ejecutor-{n}"), Name = $"Ejecutor {n}" });
        var roster = Roster().Concat(executors).ToList();

        var team = LoadedTeam(roster);

        Assert.Contains("Hay más ejecutores de los que admite el equipo (8 como máximo).", team.Problems);
        Assert.Equal("Orquestador: 1 de 1 · ejecutores: 10 de 8 · jueces: 3", team.LimitsText);

        var withoutJudge = LoadedTeam([.. Roster().Where(a => a.Role != AgentRole.Judge)]);
        Assert.Contains(withoutJudge.Problems, p => p.StartsWith("Hace falta al menos un juez", StringComparison.Ordinal));

        var two = LoadedTeam([.. Roster(), Roster().Single(a => a.Role == AgentRole.Orchestrator) with { Id = new AgentId("orquestador-2") }]);
        Assert.Contains("Solo puede haber un orquestador.", two.Problems);
    }

    [Fact]
    public void A_cable_to_a_folder_is_checked_like_a_drag_and_names_its_refusal()
    {
        var team = LoadedTeam();
        var executor = team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);
        var judge = team.Agents.Single(a => a.Id == DefaultAgentRoster.Judge);
        var folder = team.Repository!.Folders.Single(f => f.Folder == "src/");
        var requests = new List<FolderCableRequest>();
        team.FolderCableRequested += (_, r) => requests.Add(r);

        Assert.Equal("Solo un agente que escribe trabaja en carpetas.", team.RequestConnect(judge, folder));
        Assert.Null(team.RequestConnect(executor, folder));
        Assert.Equal("«Trabaja en» va de un agente a una carpeta del repositorio.", team.RequestConnect(executor, executor.Input));
        team.IsReadOnly = true;
        Assert.Equal("El equipo no se puede cambiar mientras se sigue una ejecución o la pantalla trabaja.", team.RequestConnect(executor, folder));

        Assert.Equal([new FolderCableRequest(DefaultAgentRoster.Executor, "src/", true)], requests);
        Assert.All(team.TargetsOf(executor), t => Assert.False(t.IsAllowed));
    }

    [Fact]
    public void Connect_with_lists_the_valid_places_for_the_keyboard()
    {
        var team = LoadedTeam();
        var executor = team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);

        var targets = team.TargetsOf(executor);

        Assert.Equal(["docs/", "src/", "tests/", "todo el proyecto"], targets.Select(t => t.Label).Order(StringComparer.Ordinal));
        Assert.Contains(targets, t => t.Connector.IsWholeProject && t.IsConnected);
        Assert.Contains(targets, t => t.Label == "src/" && !t.IsConnected && t.IsAllowed);
        Assert.DoesNotContain(team.TargetsOf(team.Agents.Single(a => a.Id == DefaultAgentRoster.Judge)), t => t.IsAllowed && !t.IsConnected);
    }

    [Fact]
    public void The_folders_of_an_agent_are_drawn_as_its_cables_and_are_main_folders_of_the_repository()
    {
        var executor = Roster().Single(a => a.Id == DefaultAgentRoster.Executor) with
        {
            PathGrants = [new PathGrant("src", FileAccessLevel.ReadWrite), new PathGrant("tests/", FileAccessLevel.ReadWrite), new PathGrant("tools/", FileAccessLevel.ReadWrite)],
        };
        var roster = Roster().Select(a => a.Id == executor.Id ? executor : a).ToList();

        var team = LoadedTeam(roster);

        Assert.Equal(["src/", "tests/", "tools/"], team.Connections.Where(c => c.Kind == TeamEdgeKind.WorksIn).Select(c => c.Edge.ToFolder).Order(StringComparer.Ordinal));
        Assert.Contains(team.Repository!.Folders, f => f.Folder == "tools/");
        Assert.Empty(team.Graph.Validate());
    }

    [Fact]
    public void The_positions_come_from_the_layout_and_the_new_nodes_get_a_place_of_their_own()
    {
        var layout = new CanvasLayout(Points()) { TeamPositions = Points(("ejecutor", 700, 500), ("repository", 10, 20)) };
        var team = new TeamCanvasViewModel(Text());

        team.Load(Roster(), RepositoryReading.Of(Repository()), layout);

        Assert.Equal(new System.Windows.Point(700, 500), team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor).Location);
        Assert.Equal(new System.Windows.Point(10, 20), team.Repository!.Location);
        Assert.Equal(team.Agents.Count, team.Agents.Select(a => a.Location).Distinct().Count());
        var captured = team.CaptureLayout().TeamPositions;
        Assert.Equal(new System.Windows.Point(700, 500), captured["ejecutor"]);
        Assert.Contains("orquestador", captured.Keys);
    }

    [Fact]
    public void A_repository_that_could_not_be_read_says_so_on_its_node()
    {
        var team = new TeamCanvasViewModel(Text());
        team.Load(Roster(), RepositoryReading.Failed(RepositoryProblems.NotComposed), CanvasLayout.Empty);

        Assert.Null(team.Repository!.Node);
        Assert.Equal("No se pudo leer el repositorio: falta la capa de git.", team.Repository.ProblemText);
        Assert.Empty(team.Repository.Folders);
        Assert.All(team.Connections.Where(c => c.Kind == TeamEdgeKind.WorksIn), c => Assert.Null(c.Edge.ToFolder));
    }
}
