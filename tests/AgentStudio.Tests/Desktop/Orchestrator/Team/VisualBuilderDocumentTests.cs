using System.Reflection;
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
using AgentStudio.Domain.Orchestration.Analysis;
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

        /// <summary>What reached the store, in order: <c>save:id</c> and <c>delete:id</c>.</summary>
        public List<string> Operations { get; } = [];

        public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentDefinition>>([.. Agents]);

        public Task DeleteAsync(string projectId, AgentId agent, CancellationToken cancellationToken)
        {
            Operations.Add("delete:" + agent.Value);
            Agents.RemoveAll(a => a.Id == agent);
            return Task.CompletedTask;
        }

        public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
        {
            Operations.Add("save:" + agent.Id.Value);
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
        Assert.True(document.Builder.Editor.CanRemoveAgents);
        Assert.Equal(document.Canvas.Team.Agents.Count, document.Canvas.Team.Nodes.OfType<AgentNodeViewModel>().Count());
    }

    [Fact]
    public async Task Without_the_store_of_the_template_the_team_layer_neither_saves_nor_offers_to_take_agents_out()
    {
        var document = await OpenAsync(new OrchestrationBackend(new FakeOrchestrator(), new FakeWorkspace(), new FakeRequirementStore(PlanFixtures.Requirements())), BuilderServices.None);

        Assert.False(document.Builder!.Editor.CanEdit);
        Assert.False(document.Builder.Editor.CanRemoveAgents);
    }

    [Fact]
    public async Task Taking_the_selected_agent_out_reaches_the_store_and_the_document_reads_the_template_again()
    {
        var roster = new MutableRoster();
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        var editor = document.Builder!.Editor;
        var before = RosterHash.Of(document.Context!.Roster);
        document.Canvas.Team.Select(document.Canvas.Team.Agents.Single(a => a.Id.Value == "ejecutor2"));

        editor.RemoveSelectedCommand.Execute(null);
        await editor.ConfirmRemoveCommand.ExecuteAsync(null);

        Assert.Equal(["delete:ejecutor2"], roster.Operations);
        Assert.DoesNotContain(roster.Agents, a => a.Id.Value == "ejecutor2");
        Assert.DoesNotContain(document.Context.Roster, a => a.Id.Value == "ejecutor2");
        Assert.DoesNotContain(document.Canvas.Team.Agents, a => a.Id.Value == "ejecutor2");
        Assert.NotEqual(before, RosterHash.Of(document.Context.Roster));
        Assert.StartsWith("Se quitó ejecutor2 del equipo.", editor.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_orchestrator_cannot_be_taken_out_of_the_template_of_the_document()
    {
        var roster = new MutableRoster();
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        var editor = document.Builder!.Editor;
        document.Canvas.Team.Select(document.Canvas.Team.Agents.Single(a => a.Id.Value == "orquestador"));

        // Refused before the question: there is no yes to give.
        editor.RemoveSelectedCommand.Execute(null);
        Assert.Null(editor.PendingRemoval);
        Assert.False(editor.ConfirmRemoveCommand.CanExecute(null));
        Assert.Contains("Hace falta el orquestador", editor.ErrorMessage, StringComparison.Ordinal);
        await editor.ConfirmRemoveCommand.ExecuteAsync(null);

        Assert.Empty(roster.Operations);
        Assert.Contains(roster.Agents, a => a.Id.Value == "orquestador");
        Assert.Contains("Hace falta el orquestador", editor.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// What P48 was about: a change of the draft whose new state lacks agents the template has (the undo of «Añadir un juez», say)
    /// reaches the store as one set. The draft host works out which agents go from the two states, and nothing is saved or removed
    /// until the single confirmation of the set (a change that grants power) has been given. The host is the private one of the
    /// document, reached through the band that holds it.
    /// </summary>
    [Fact]
    public async Task The_draft_host_takes_out_of_the_store_the_agents_the_new_state_lacks_only_after_the_set_is_confirmed()
    {
        foreach (var (answer, written) in new[] { (UserPresenceResult.Canceled, false), (UserPresenceResult.Verified, true) })
        {
            var roster = new MutableRoster();
            roster.Agents.Add(roster.Agents.Single(a => a.Id.Value == "juezA") with { Id = new AgentId("juez-extra"), Name = "juez extra" });
            var document = await OpenAsync(Backend(roster), BuilderServices.None, confirmation: new ReinforcedConfirmation(FakeUserPresenceVerifier.Answering(answer), () => false));
            var host = DraftHostOf(document);
            var before = host.ReadState();
            var executor = before.Roster.Single(a => a.Id.Value == "ejecutor");
            var after = before with
            {
                Roster = [.. before.Roster.Where(a => a.Id.Value != "juez-extra").Select(a => a == executor ? executor with { AllowedTools = [.. executor.AllowedTools, "Bash"] } : a)],
            };

            var done = await host.ApplyAsync(before, after, null);

            Assert.Equal(written, done);
            Assert.Equal(written ? ["save:ejecutor", "delete:juez-extra"] : [], roster.Operations);
            Assert.Equal(!written, roster.Agents.Any(a => a.Id.Value == "juez-extra"));
            Assert.Equal(written, roster.Agents.Single(a => a.Id.Value == "ejecutor").AllowedTools.Contains("Bash"));
        }
    }

    [Fact]
    public async Task The_draft_host_takes_agents_out_without_asking_when_nothing_grants_power_and_refuses_to_take_the_orchestrator()
    {
        var roster = new MutableRoster();
        roster.Agents.Add(roster.Agents.Single(a => a.Id.Value == "juezA") with { Id = new AgentId("juez-extra"), Name = "juez extra" });
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        var host = DraftHostOf(document);
        var before = host.ReadState();

        Assert.True(await host.ApplyAsync(before, before with { Roster = [.. before.Roster.Where(a => a.Id.Value != "juez-extra")] }, null));
        Assert.Equal(["delete:juez-extra"], roster.Operations);

        var current = host.ReadState();
        Assert.False(await host.ApplyAsync(current, current with { Roster = [.. current.Roster.Where(a => a.Role != AgentRole.Orchestrator)] }, null));
        Assert.Equal(["delete:juez-extra"], roster.Operations);
        Assert.Contains("Hace falta el orquestador", document.Builder!.Editor.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The draft host of the document is a private nested class that only the band holds, so it is reached through the private field
    /// of the band. If that field is renamed or its type changes, the test says what to update instead of failing on a null.
    /// </summary>
    private static IDraftHost DraftHostOf(OrchestratorDocumentViewModel document)
    {
        var field = typeof(AnalysisBandViewModel).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(
            field is not null && typeof(IDraftHost).IsAssignableFrom(field.FieldType),
            "AnalysisBandViewModel no longer has a private field '_host' of type IDraftHost: these tests reach the draft host of the document through it. Update DraftHostOf with the new way to reach it.");
        return (IDraftHost)field!.GetValue(document.Builder!.Band)!;
    }

    [Fact]
    public async Task A_proposal_brings_the_plan_layer_and_a_change_of_the_team_retires_the_approval_and_says_so()
    {
        var roster = new MutableRoster();
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        document.LoadProposal(new PlanProposal(new RunId("20261109-abc123"), PlanFixtures.Plan(), [], false, AgentRunStatus.Succeeded, null, null));
        Assert.True(document.Canvas.IsPlanLayer);
        document.ValidateCommand.Execute(null);
        await document.Builder!.Band.RefreshAsync();
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

    /// <summary>P55: Ctrl+Z of a fix that changed the template reaches the store, because the reading the document does after the save keeps the history.</summary>
    [Fact]
    public async Task Undoing_a_fix_that_changed_the_template_gives_the_store_back_its_previous_state_and_redo_applies_it_again()
    {
        var roster = new MutableRoster();
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        var band = document.Builder!.Band;
        var original = roster.Agents.Single(a => a.Id.Value == "ejecutor");
        var hashBefore = RosterHash.Of(roster.Agents);

        await band.ApplyFixAsync(new ChangeModelFix(original.Id, "modelo-nuevo"));
        Assert.Equal("modelo-nuevo", roster.Agents.Single(a => a.Id.Value == "ejecutor").Model.Model);
        Assert.True(band.CanUndo);

        await band.UndoCommand.ExecuteAsync(null);
        Assert.Equal(original, roster.Agents.Single(a => a.Id.Value == "ejecutor"));
        Assert.Equal(hashBefore, RosterHash.Of(roster.Agents));
        Assert.Equal(hashBefore, RosterHash.Of(document.Context!.Roster));
        Assert.True(band.CanRedo);

        await band.RedoCommand.ExecuteAsync(null);
        Assert.Equal("modelo-nuevo", roster.Agents.Single(a => a.Id.Value == "ejecutor").Model.Model);
    }

    [Fact]
    public async Task Applying_a_state_with_the_same_agents_read_again_changes_none_of_them()
    {
        var roster = new MutableRoster();
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        var host = DraftHostOf(document);
        var before = host.ReadState();
        var copies = before.Roster.Select(a => a with
        {
            Objectives = [.. a.Objectives],
            AllowedTools = [.. a.AllowedTools],
            PathGrants = [.. a.PathGrants],
        }).ToList();

        Assert.True(await host.ApplyAsync(before, before with { Roster = copies }, null));

        Assert.Empty(roster.Operations);
    }

    [Fact]
    public async Task A_save_that_leaves_the_template_as_it_was_approved_keeps_the_approval_and_a_different_one_withdraws_it()
    {
        var roster = new MutableRoster();
        var document = await OpenAsync(Backend(roster), BuilderServices.None);
        document.LoadProposal(new PlanProposal(new RunId("20261109-abc123"), PlanFixtures.Plan(), [], false, AgentRunStatus.Succeeded, null, null));
        document.ValidateCommand.Execute(null);
        await document.Builder!.Band.RefreshAsync();
        await document.ApproveCommand.ExecuteAsync(null);
        Assert.True(document.IsApproved);
        var editor = document.Builder!.Editor;
        var executor = document.Context!.Roster.Single(a => a.Id.Value == "ejecutor");

        Assert.True(await editor.ApplyAsync(executor with { Objectives = [.. executor.Objectives] }, "Igual."));
        Assert.True(document.IsApproved);
        Assert.Contains("ejecutor", roster.Operations.Select(o => o.Replace("save:", string.Empty, StringComparison.Ordinal)));

        Assert.True(await editor.ApplyAsync(executor with { Name = "Otro nombre" }, "Distinto."));
        Assert.False(document.IsApproved);
    }

    private static IReadOnlyList<TaskSpec> TasksWithoutIntegration() =>
    [
        PlanFixtures.ValidTasks()[0],
        PlanFixtures.Spec("T2", WorkTaskKind.Review) with
        {
            Agent = new AgentId("juez"),
            Workspace = WorkspaceMode.ReadOnly,
            DependsOn = [PlanFixtures.Dep("T1")],
            MaxRetries = 0,
        },
    ];

    [Fact]
    public async Task Approving_fails_closed_while_the_analysis_is_pending_and_the_button_follows_the_band()
    {
        var document = await OpenAsync(Backend(new MutableRoster()), BuilderServices.None);
        document.Canvas.LoadTasks(PlanFixtures.ValidTasks(), CanvasLayout.Empty);
        document.RequestText = "Añadir Calc.Multiply";
        var raised = 0;
        document.ApproveCommand.CanExecuteChanged += (_, _) => raised++;

        // An edit just came: the analysis has not answered, so the plan is not approved even if the button was enabled before.
        Assert.Equal(AnalysisState.Pending, document.Builder!.Band.State);
        Assert.False(document.ApproveCommand.CanExecute(null));
        await document.ApproveCommand.ExecuteAsync(null);
        Assert.False(document.IsApproved);
        Assert.Contains("todavía no ha terminado", document.ErrorMessage, StringComparison.Ordinal);

        // The band ends, clean: the button is told, and the plan can be approved.
        await document.Builder.Band.RefreshAsync();
        Assert.True(raised > 0);
        Assert.True(document.ApproveCommand.CanExecute(null));
        await document.ApproveCommand.ExecuteAsync(null);
        Assert.True(document.IsApproved);
    }

    [Fact]
    public async Task The_errors_of_the_analysis_disable_approving_when_the_band_ends_and_approving_refuses_them()
    {
        var document = await OpenAsync(Backend(new MutableRoster()), BuilderServices.None);
        document.Canvas.LoadTasks(TasksWithoutIntegration(), CanvasLayout.Empty);
        document.RequestText = "Añadir Calc.Multiply";
        Assert.Equal(ValidationState.Valid, document.ValidationState);
        var raised = 0;
        document.ApproveCommand.CanExecuteChanged += (_, _) => raised++;

        await document.Builder!.Band.RefreshAsync();

        Assert.True(document.Builder.Band.BlocksApproval);
        Assert.True(raised > 0);
        Assert.False(document.ApproveCommand.CanExecute(null));
        await document.ApproveCommand.ExecuteAsync(null);
        Assert.False(document.IsApproved);
        Assert.Contains("tiene errores", document.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("tiene errores", document.ExecuteBlockers, StringComparison.Ordinal);
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
            var document = await OpenAsync(Backend(new MutableRoster()), new BuilderServices(Live: live, WindowsAnimates: () => true), reduceMotion: reduced);
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
    public async Task Turning_on_reduced_motion_stops_the_animations_that_are_running_at_once()
    {
        var live = new FakeLive();
        var reduce = false;
        var document = new OrchestratorDocumentViewModel(
            Root, new OrchestrationSession(Backend(new MutableRoster())), new MemoryLayoutStore(), new FakeTimeProvider(T0), new InlineSynchronizationContext(),
            builder: new BuilderServices(Live: live, WindowsAnimates: () => true), reduceMotion: () => reduce);
        await document.OpenAsync();
        document.Canvas.LoadTasks(PlanFixtures.ValidTasks(), CanvasLayout.Empty);
        var executor = document.Canvas.Team.Agents.Single(a => a.Id.Value == "ejecutor");
        live.Raise(State(1, 12, true, Live(executor.Id, AgentLiveStatus.Working, new TaskId("T1"))), ["ejecutor"], (new TaskId("T1"), new TaskId("T3")));
        Assert.True(document.Canvas.Team.AnimatedCount > 0);
        Assert.Contains(document.Canvas.Connections, c => c.IsPulsing);

        // The setting is saved: nothing waits for the next event of the run.
        reduce = true;
        MotionPolicy.NotifySettingChanged();

        Assert.Equal(0, document.Canvas.Team.AnimatedCount);
        Assert.DoesNotContain(document.Canvas.Connections, c => c.IsPulsing);
        Assert.Equal("Trabajando en T1", executor.StatusText);
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
