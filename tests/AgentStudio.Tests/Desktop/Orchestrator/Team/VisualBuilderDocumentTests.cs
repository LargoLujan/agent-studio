using System.Windows;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>
/// The visual builder inside the «Orquestador» document (docs/17 D-07, D-08, D-14 and D-37, F13-1, F13-2 and F13-7): the Team
/// layer follows the template, editing it withdraws the approval and says so, the grouped live state reaches only the nodes it
/// names, and changing the verification profile goes through the fixed barrier. A document created without the services of the
/// builder is the one of before the phase.
/// </summary>
public sealed class VisualBuilderDocumentTests
{
    private const string Root = @"C:\proyectos\calc";

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public CanvasLayout Saved { get; private set; } = CanvasLayout.Empty;

        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => Saved;

        public void Save(string projectRoot, CanvasLayout layout) => Saved = layout;
    }

    /// <summary>The template of the project in memory: what is saved is what is read next.</summary>
    private sealed class MutableRoster : IAgentRoster
    {
        public List<AgentDefinition> Agents { get; } = [.. PlanFixtures.Roster()];

        public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentDefinition>>([.. Agents]);

        public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
        {
            var index = Agents.FindIndex(a => a.Id == agent.Id);
            if (index >= 0)
            {
                Agents[index] = agent;
            }
            else
            {
                Agents.Add(agent);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>The live projection that the monitor implements, as a double that raises the grouped changes the test wants.</summary>
    private sealed class FakeLive : IRunLiveProjection
    {
        public RunLiveState? Current { get; private set; }

        public event EventHandler<RunLiveChangedEventArgs>? Changed;

        public void Raise(RunLiveState state, string[] changed, params (TaskId From, TaskId To)[] pulses)
        {
            Current = state;
            Changed?.Invoke(this, new RunLiveChangedEventArgs(state, changed, pulses));
        }
    }

    private static OrchestrationBackend Backend(MutableRoster roster, FakeProfileSource? profile = null) =>
        new(new FakeOrchestrator(), new FakeWorkspace(), new FakeRequirementStore(PlanFixtures.Requirements()), roster, profile ?? new FakeProfileSource(Backends.Profile));

    private static async Task<OrchestratorDocumentViewModel> OpenAsync(
        OrchestrationBackend backend,
        BuilderServices? services,
        bool reduceMotion = false,
        ReinforcedConfirmation? confirmation = null)
    {
        var document = new OrchestratorDocumentViewModel(
            Root,
            new OrchestrationSession(backend),
            new MemoryLayoutStore(),
            new FakeTimeProvider(T0),
            new InlineSynchronizationContext(),
            confirmation,
            drafts: null,
            builder: services,
            reduceMotion: () => reduceMotion);
        await document.OpenAsync();
        return document;
    }

    [Fact]
    public async Task A_document_without_the_services_of_the_builder_is_the_one_of_before_the_phase()
    {
        var document = await OpenAsync(Backend(new MutableRoster()), services: null);

        Assert.Null(document.Builder);
        Assert.Empty(document.Canvas.Team.Agents);
        Assert.True(document.Canvas.IsPlanLayer);
    }

    [Fact]
    public async Task With_the_builder_the_team_layer_shows_the_template_while_the_person_prepares()
    {
        var document = await OpenAsync(Backend(new MutableRoster()), BuilderServices.None);

        Assert.NotNull(document.Builder);
        Assert.True(document.Canvas.IsTeamLayer);
        Assert.Equal(PlanFixtures.Roster().Count, document.Canvas.Team.Agents.Count);
        Assert.True(document.Builder!.Editor.CanEdit);
        Assert.False(document.Builder.Editor.CanRemoveAgents);
        Assert.Equal(document.Canvas.Team.Agents.Count, document.Canvas.Team.Nodes.OfType<AgentNodeViewModel>().Count());
    }

    [Fact]
    public async Task A_proposal_brings_the_plan_layer_and_a_change_of_the_team_retires_the_approval_and_says_so()
    {
        var roster = new MutableRoster();
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        document.LoadProposal(new PlanProposal(new RunId("20261109-abc123"), PlanFixtures.Plan(), [], false, AgentRunStatus.Succeeded, null, null));
        Assert.True(document.Canvas.IsPlanLayer);
        document.ValidateCommand.Execute(null);
        await document.ApproveCommand.ExecuteAsync(null);
        Assert.True(document.IsApproved);
        var before = RosterHash.Of(document.Context!.Roster);
        var editor = document.Builder!.Editor;
        Assert.True(editor.IsPlanApproved);
        Assert.Equal("El plan está aprobado: si cambias el equipo, tendrás que aprobarlo otra vez.", editor.ConsequenceText);

        var executor = document.Context.Roster.Single(a => a.Id.Value == "ejecutor");
        Assert.True(await editor.ApplyAsync(executor with { Name = "Ejecutor principal" }, "Cambiado."));

        Assert.False(document.IsApproved);
        Assert.NotEqual(before, RosterHash.Of(document.Context.Roster));
        Assert.Equal("Ejecutor principal", document.Canvas.Team.Agents.Single(a => a.Id.Value == "ejecutor").Name);
        Assert.Contains("vuelve a aprobar el plan", document.Canvas.NoticeText, StringComparison.Ordinal);
        Assert.Contains("retira la aprobación", editor.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agentes_of_the_side_bar_opens_the_document_on_the_team_layer_and_does_nothing_without_the_builder()
    {
        var document = await OpenAsync(Backend(new MutableRoster()), BuilderServices.None);
        document.Canvas.ShowPlanCommand.Execute(null);

        document.ShowTeamLayer();
        Assert.True(document.Canvas.IsTeamLayer);

        var legacy = await OpenAsync(Backend(new MutableRoster()), services: null);
        legacy.ShowTeamLayer();
        Assert.True(legacy.Canvas.IsPlanLayer);
    }

    [Fact]
    public async Task The_team_layer_is_read_only_while_the_tab_follows_a_run()
    {
        var document = await OpenAsync(Backend(new MutableRoster()), BuilderServices.None);

        Assert.False(document.Canvas.Team.IsReadOnly);
        document.Roster.IsLocked = true;
        document.Canvas.Team.IsReadOnly = document.Roster.IsLocked;

        Assert.False(document.Builder!.Editor.CanEdit);
        Assert.Equal("El equipo no se puede cambiar mientras se sigue una ejecución o la pantalla trabaja.", document.Builder.Editor.AddExecutorHint);
    }

    [Fact]
    public async Task The_grouped_live_state_reaches_only_the_nodes_it_names_and_the_pulses_run_unless_motion_is_reduced()
    {
        foreach (var reduced in new[] { false, true })
        {
            var live = new FakeLive();
            var document = await OpenAsync(Backend(new MutableRoster()), new BuilderServices(Live: live), reduceMotion: reduced);
            document.Canvas.LoadTasks(PlanFixtures.ValidTasks(), CanvasLayout.Empty);
            var team = document.Canvas.Team;
            var executor = team.Agents.Single(a => a.Id.Value == "ejecutor");
            var executor2 = team.Agents.Single(a => a.Id.Value == "ejecutor2");

            live.Raise(
                State(1, 12, false, Live(executor.Id, AgentLiveStatus.Working, new TaskId("T1")), Live(executor2.Id, AgentLiveStatus.Working, new TaskId("T2"))),
                ["ejecutor", "T1"],
                (new TaskId("T1"), new TaskId("T3")));

            Assert.Equal("Trabajando en T1", executor.StatusText);
            Assert.Equal(AgentLiveStatus.Idle, executor2.Status);
            Assert.Equal(!reduced, executor.IsWriting && team.Connections.Any(c => c.IsAnimated));
            Assert.Equal(!reduced, document.Canvas.Connections.Single(c => c.Source.Node.Id.Value == "T1" && c.Target.Node.Id.Value == "T3").IsPulsing);

            // The next change clears the pulse: it is a single short one.
            live.Raise(State(2, 12, false, Live(executor.Id, AgentLiveStatus.Idle)), ["ejecutor"]);
            Assert.All(document.Canvas.Connections, c => Assert.False(c.IsPulsing));
            Assert.Equal(0, team.AnimatedCount);
        }
    }

    [Fact]
    public async Task A_task_shows_its_attempt_and_why_it_is_blocked_no_longer_only_blocked()
    {
        var live = new FakeLive();
        var document = await OpenAsync(Backend(new MutableRoster()), new BuilderServices(Live: live));
        document.Canvas.LoadTasks(PlanFixtures.ValidTasks(), CanvasLayout.Empty);
        var task = new TaskLiveState(new TaskId("T6"), WorkTaskStatus.Blocked, new AgentId("ejecutor"), 2, "falta la cuota", null, false);
        var state = State(1, 12, false) with { Tasks = new Dictionary<TaskId, TaskLiveState> { [task.Task] = task } };

        live.Raise(state, ["T6"]);

        var node = document.Canvas.Nodes.Single(n => n.Id.Value == "T6");
        Assert.Equal("intento 2 · Bloqueada: falta la cuota", node.LiveText);
        Assert.Null(document.Canvas.Nodes.Single(n => n.Id.Value == "T1").LiveText);
    }

    [Fact]
    public async Task Changing_the_verification_profile_waits_for_windows_hello_and_never_without_it()
    {
        foreach (var (answer, saved) in new[] { (UserPresenceResult.Canceled, 0), (UserPresenceResult.Verified, 1) })
        {
            var source = new FakeProfileSource(null, Backends.Profile);
            var document = await OpenAsync(Backend(new MutableRoster(), source), BuilderServices.None, confirmation: new ReinforcedConfirmation(FakeUserPresenceVerifier.Answering(answer), () => false));
            Assert.True(document.UseProposedProfileCommand.CanExecute(null));

            await document.UseProposedProfileCommand.ExecuteAsync(null);

            Assert.Equal(saved, source.Saved.Count);
            if (saved == 0)
            {
                Assert.Equal("No se confirmó que eres tú: el perfil de verificación no cambia.", document.ErrorMessage);
            }
        }
    }

    [Fact]
    public async Task The_analysis_runs_for_the_document_and_a_clean_plan_can_still_be_approved()
    {
        var document = await OpenAsync(Backend(new MutableRoster()), BuilderServices.None);
        document.LoadProposal(new PlanProposal(new RunId("20261109-abc123"), PlanFixtures.Plan(), [], false, AgentRunStatus.Succeeded, null, null));

        await document.Builder!.Band.RefreshAsync();

        Assert.True(document.Builder.Band.IsCurrent);
        Assert.DoesNotContain(document.Builder.Band.Findings, f => f.Severity == AgentStudio.Domain.Orchestration.Analysis.FindingSeverity.Error);
        Assert.True(document.ApproveCommand.CanExecute(null));
    }
}
