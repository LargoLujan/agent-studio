using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Preparation;

/// <summary>
/// «Memoria del proyecto» of each agent of the template (docs/14 section 9, WP5; D-23): a switch per agent between
/// <see cref="MemoryPolicy.None"/> and <see cref="MemoryPolicy.ReadSelected"/>, saved with <see cref="IAgentRoster.SaveAsync"/>.
/// The judge has no switch: it never receives memory. Changing the template withdraws the approval of a plan that waits
/// to run, and nothing changes while a run is followed.
/// </summary>
public sealed class RosterEditorViewModelTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly ProjectContext Project = new("p-1", Root);

    /// <summary>A template saved before Phase 4: nobody receives memory (the switches start off).</summary>
    private static IReadOnlyList<AgentDefinition> WithoutMemory() => [.. DefaultAgentRoster.Create().Select(a => a with { Memory = MemoryPolicy.None })];

    private sealed class Saves
    {
        public List<(ProjectContext Project, AgentDefinition Agent)> Agents { get; } = [];

        public int Reloads { get; set; }

        public Exception? Failure { get; set; }

        public RosterEditorViewModel Editor() => new(
            (project, agent) =>
            {
                if (Failure is not null)
                {
                    throw Failure;
                }

                Agents.Add((project, agent));
                return Task.CompletedTask;
            },
            () =>
            {
                Reloads++;
                return Task.CompletedTask;
            });
    }

    [Fact]
    public void Every_agent_of_the_template_has_a_switch_but_the_judge()
    {
        var editor = new Saves().Editor();

        editor.Load(Project, DefaultAgentRoster.Create());

        // The template of a new project gives the trusted memory to the orchestrator and the executor (docs/14 section
        // 12, decision 5); the judge never receives it.
        Assert.Equal(["orquestador", "ejecutor", "juez", "arquitecto", "critico", "juez-claude", "juez-revision"], editor.Agents.Select(a => a.Id.Value));
        Assert.Equal([true, true, false, true, true, false, false], editor.Agents.Select(a => a.CanReceiveMemory));
        Assert.Equal([true, true, false, false, false, false, false], editor.Agents.Select(a => a.ReceivesMemory));
        Assert.Equal("Recibe la memoria del proyecto (solo la de confianza).", editor.Agents[0].MemoryText);
        Assert.Equal("Nunca recibe memoria: su veredicto solo se apoya en el expediente.", editor.Agents[2].MemoryText);
        Assert.False(editor.Agents[2].CanEdit);
    }

    [Fact]
    public async Task Turning_a_switch_on_saves_that_agent_with_ReadSelected_and_nothing_else()
    {
        var saves = new Saves();
        var editor = saves.Editor();
        var template = WithoutMemory();
        editor.Load(Project, template);

        editor.Agents[1].ReceivesMemory = true;
        await editor.Saving;

        var (project, agent) = Assert.Single(saves.Agents);
        Assert.Equal(Project, project);
        Assert.Equal(template[1] with { Memory = MemoryPolicy.ReadSelected }, agent);
        Assert.Equal(1, saves.Reloads);
        Assert.Equal("«Ejecutor» recibe ahora la memoria del proyecto: solo la de confianza y relevante para su tarea.", editor.Notice);
        Assert.Equal("Recibe la memoria del proyecto (solo la de confianza).", editor.Agents[1].MemoryText);
    }

    [Fact]
    public async Task Turning_it_off_saves_None()
    {
        var saves = new Saves();
        var editor = saves.Editor();
        editor.Load(Project, [.. WithoutMemory().Select(a => a.Role == AgentRole.Orchestrator ? a with { Memory = MemoryPolicy.ReadSelected } : a)]);
        Assert.True(editor.Agents[0].ReceivesMemory);

        editor.Agents[0].ReceivesMemory = false;
        await editor.Saving;

        Assert.Equal(MemoryPolicy.None, Assert.Single(saves.Agents).Agent.Memory);
        Assert.Equal("«Orquestador» ya no recibe memoria del proyecto.", editor.Notice);
    }

    [Fact]
    public async Task A_save_that_fails_puts_the_switch_back_and_says_why()
    {
        var saves = new Saves { Failure = new IOException("La base de datos está bloqueada.") };
        var editor = saves.Editor();
        editor.Load(Project, WithoutMemory());

        editor.Agents[1].ReceivesMemory = true;
        await editor.Saving;

        Assert.False(editor.Agents[1].ReceivesMemory);
        Assert.Equal("No se pudo guardar la plantilla: La base de datos está bloqueada.", editor.ErrorMessage);
        Assert.Equal(0, saves.Reloads);
    }

    [Fact]
    public async Task While_locked_a_switch_saves_nothing_and_goes_back()
    {
        var saves = new Saves();
        var editor = saves.Editor();
        editor.Load(Project, WithoutMemory());

        editor.IsLocked = true;
        Assert.False(editor.Agents[1].CanEdit);
        editor.Agents[1].ReceivesMemory = true;
        await editor.Saving;

        Assert.Empty(saves.Agents);
        Assert.False(editor.Agents[1].ReceivesMemory);
    }

    // ---- G1: the model of each agent ----------------------------------------------------------------------------------

    [Fact]
    public void Every_agent_offers_the_default_of_its_cli_the_names_it_takes_and_another_one()
    {
        var editor = new Saves().Editor();

        editor.Load(Project, DefaultAgentRoster.Create());

        var orchestrator = editor.Agents.Single(a => a.Id == DefaultAgentRoster.Orchestrator);
        Assert.Equal([AgentModels.DefaultLabel, "opus", "sonnet", "haiku", AgentModels.OtherLabel], orchestrator.ModelOptions.Select(o => o.Label));
        Assert.Equal(AgentModels.DefaultLabel, orchestrator.SelectedModel.Label);
        Assert.Equal("Usa el modelo predeterminado del programa.", orchestrator.ModelText);
        var revision = editor.Agents.Single(a => a.Id == DefaultAgentRoster.RevisionJudge);
        Assert.Equal([AgentModels.DefaultLabel, DefaultAgentRoster.RevisionJudgeModel, AgentModels.OtherLabel], revision.ModelOptions.Select(o => o.Label));
        Assert.Equal(DefaultAgentRoster.RevisionJudgeModel, revision.SelectedModel.Model);

        // A judge has no memory switch, but its model can be chosen.
        var judge = editor.Agents.Single(a => a.Id == DefaultAgentRoster.Judge);
        Assert.False(judge.CanEdit);
        Assert.True(judge.CanChangeModel);
    }

    [Fact]
    public async Task Choosing_a_model_saves_that_agent_with_it_and_the_default_of_the_cli_saves_none()
    {
        var saves = new Saves();
        var editor = saves.Editor();
        var template = DefaultAgentRoster.Create();
        editor.Load(Project, template);

        editor.Agents[1].SelectedModel = editor.Agents[1].ModelOptions.Single(o => o.Model == "sonnet");
        await editor.Saving;

        var (project, agent) = Assert.Single(saves.Agents);
        Assert.Equal(Project, project);
        Assert.Equal(template[1] with { Model = template[1].Model with { Model = "sonnet" } }, agent);
        Assert.Equal("«Ejecutor» usará el modelo sonnet desde la próxima ejecución o planificación.", editor.Notice);
        Assert.Equal(1, saves.Reloads);

        // Read again as the tab does, and back to the default of the CLI.
        editor.Load(Project, [.. template.Select(a => a.Id == agent.Id ? agent : a)]);
        Assert.Equal("sonnet", editor.Agents[1].SelectedModel.Model);
        editor.Agents[1].SelectedModel = editor.Agents[1].ModelOptions[0];
        await editor.Saving;
        Assert.Null(saves.Agents[^1].Agent.Model.Model);
    }

    [Fact]
    public async Task Another_model_is_typed_checked_with_the_rule_of_the_cli_and_saved()
    {
        var saves = new Saves();
        var editor = saves.Editor();
        editor.Load(Project, DefaultAgentRoster.Create());
        var executor = editor.Agents[1];

        executor.SelectedModel = executor.ModelOptions[^1];
        Assert.True(executor.IsTypingModel);
        Assert.Empty(saves.Agents);

        executor.TypedModel = "--dangerously-skip-permissions";
        Assert.NotNull(executor.TypedModelProblem);
        Assert.False(executor.UseTypedModelCommand.CanExecute(null));

        executor.TypedModel = "claude-sonnet-5-5";
        Assert.Null(executor.TypedModelProblem);
        Assert.True(executor.UseTypedModelCommand.CanExecute(null));
        executor.UseTypedModelCommand.Execute(null);
        await editor.Saving;

        Assert.False(executor.IsTypingModel);
        Assert.Equal("claude-sonnet-5-5", Assert.Single(saves.Agents).Agent.Model.Model);
    }

    [Fact]
    public async Task A_model_that_cannot_be_saved_or_a_locked_template_puts_the_choice_back()
    {
        var saves = new Saves { Failure = new IOException("La base de datos está bloqueada.") };
        var editor = saves.Editor();
        editor.Load(Project, DefaultAgentRoster.Create());

        editor.Agents[0].SelectedModel = editor.Agents[0].ModelOptions.Single(o => o.Model == "haiku");
        await editor.Saving;

        Assert.Equal(AgentModels.DefaultLabel, editor.Agents[0].SelectedModel.Label);
        Assert.Equal("No se pudo guardar la plantilla: La base de datos está bloqueada.", editor.ErrorMessage);

        saves.Failure = null;
        editor.IsLocked = true;
        Assert.False(editor.Agents[0].CanChangeModel);
        editor.Agents[0].SelectedModel = editor.Agents[0].ModelOptions.Single(o => o.Model == "haiku");
        await editor.Saving;

        Assert.Empty(saves.Agents);
        Assert.Equal(AgentModels.DefaultLabel, editor.Agents[0].SelectedModel.Label);
    }

    [Fact]
    public void Without_the_roster_store_or_a_project_no_switch_can_change()
    {
        var withoutStore = new RosterEditorViewModel(null, () => Task.CompletedTask);
        withoutStore.Load(Project, DefaultAgentRoster.Create());
        var withoutProject = new Saves().Editor();
        withoutProject.Load(null, DefaultAgentRoster.Create());

        Assert.All(withoutStore.Agents, a => Assert.False(a.CanEdit));
        Assert.All(withoutProject.Agents, a => Assert.False(a.CanEdit));
    }

    // ---- In the orchestrator tab ------------------------------------------------------------------------------------

    /// <summary>The template of the project, saved in place by id as the SQLite roster does (and refusing what the domain refuses).</summary>
    private sealed class TemplateRoster : IAgentRoster
    {
        private readonly List<AgentDefinition> _agents = [.. WithoutMemory()];

        public List<AgentDefinition> Saved { get; } = [];

        public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentDefinition>>([.. _agents]);

        public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
        {
            agent.EnsureValid();
            Saved.Add(agent);
            var index = _agents.FindIndex(a => a.Id == agent.Id);
            if (index < 0)
            {
                _agents.Add(agent);
            }
            else
            {
                _agents[index] = agent;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string projectId, AgentId agent, CancellationToken cancellationToken)
        {
            _agents.RemoveAll(a => a.Id == agent);
            return Task.CompletedTask;
        }
    }

    private sealed class NoLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    private static async Task<(OrchestratorDocumentViewModel Document, TemplateRoster Roster, FakeOrchestrator Engine)> OpenWithAnApprovedPlanAsync()
    {
        var roster = new TemplateRoster();
        var engine = new FakeOrchestrator { Start = _ => new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running))) };
        var backend = Backends.Complete(engine) with { Roster = roster, WriteConfinement = new FixedWriteConfinement(ProviderId.ClaudeCode) };
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(backend), new NoLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(
            [
                PlanFixtures.Spec("T1") with { Agent = DefaultAgentRoster.Executor, Workspace = WorkspaceMode.Write, WriteScope = ["src/Calc/"], Covers = [Rid("R1")], VerificationSteps = ["build", "test"] },
                PlanFixtures.Spec("R1", WorkTaskKind.Review) with { Agent = DefaultAgentRoster.Judge, Workspace = WorkspaceMode.ReadOnly, DependsOn = [PlanFixtures.Dep("T1")], MaxRetries = 0 },
            ],
            CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        Assert.True(document.IsApproved);
        return (document, roster, engine);
    }

    [Fact]
    public async Task In_the_orchestrator_tab_a_switch_saves_the_template_and_withdraws_the_approval_of_the_plan_that_waits()
    {
        var (document, roster, _) = await OpenWithAnApprovedPlanAsync();

        document.Roster.Agents.Single(a => a.Id == DefaultAgentRoster.Executor).ReceivesMemory = true;
        await document.Roster.Saving;

        Assert.Equal(MemoryPolicy.ReadSelected, Assert.Single(roster.Saved).Memory);
        Assert.Null(document.PlanDecision);
        Assert.False(document.IsApproved);
        Assert.Equal("Plan sin aprobar.", document.ApprovalText);
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.Equal(MemoryPolicy.ReadSelected, document.Context!.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Memory);
        Assert.True(document.Roster.Agents.Single(a => a.Id == DefaultAgentRoster.Executor).ReceivesMemory);

        // Approved again, the run asks for the template as it is now.
        document.ApproveCommand.Execute(null);
        Assert.True(document.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task In_the_orchestrator_tab_a_model_saves_the_template_withdraws_the_approval_and_shows_in_the_line_of_the_agent()
    {
        var (document, roster, _) = await OpenWithAnApprovedPlanAsync();
        var executor = document.Roster.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);

        executor.SelectedModel = executor.ModelOptions.Single(o => o.Model == "haiku");
        await document.Roster.Saving;

        Assert.Equal("haiku", Assert.Single(roster.Saved).Model.Model);
        Assert.Null(document.PlanDecision);
        Assert.False(document.IsApproved);
        Assert.Contains(document.RosterLines, l => l.StartsWith("ejecutor · Ejecutor", StringComparison.Ordinal) && l.Contains("modelo haiku", StringComparison.Ordinal));
        Assert.Equal("haiku", document.Roster.Agents.Single(a => a.Id == DefaultAgentRoster.Executor).SelectedModel.Model);
    }

    [Fact]
    public async Task In_the_orchestrator_tab_the_judge_has_no_switch()
    {
        var (document, _, _) = await OpenWithAnApprovedPlanAsync();

        var judge = document.Roster.Agents.Single(a => a.Id == DefaultAgentRoster.Judge);

        Assert.False(judge.CanReceiveMemory);
        Assert.False(judge.CanEdit);
    }

    [Fact]
    public async Task In_the_orchestrator_tab_nothing_can_be_switched_while_a_run_is_followed()
    {
        var (document, roster, _) = await OpenWithAnApprovedPlanAsync();
        Assert.True(document.Roster.Agents[0].CanEdit);

        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.True(document.Monitor.IsAttached);
        Assert.True(document.Roster.IsLocked);
        Assert.All(document.Roster.Agents, a => Assert.False(a.CanEdit));
        document.Roster.Agents[0].ReceivesMemory = true;
        await document.Roster.Saving;
        Assert.Empty(roster.Saved);
    }
}
