using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Application.Diagnostics;
using AgentStudio.Tests.Desktop.Memory;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// «Inicio» with the next step of the open project (docs/24 §3.2): one button chosen from what the shell can read (whether the
/// configurator was applied, whether there are frozen requirements, whether a run goes on), never from what it cannot. And the
/// way to «Requisitos» that «Auditar» asks the shell for. The engine is a double: nothing is planned or run.
/// </summary>
public sealed class HomeNextStepTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    private sealed class FailingWorkspace : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) =>
            Task.FromException<ProjectContext>(new IOException("locked"));
    }

    /// <summary>A project folder where Agent Studio already wrote: its database exists (empty: the reader never opens it).</summary>
    private string ProjectWithStorage(string name)
    {
        var root = _temp.CreateDirectory(name);
        _temp.WriteFile(Path.Combine(name, ".agentstudio", "agentstudio.db"), string.Empty);
        return root;
    }

    private static MainViewModel Shell(ProjectProgressReader? progress, OrchestrationBackend? backend = null) => new(
        new InMemorySettingsStore(),
        new FakeFolderPicker(),
        new FakeUnsavedChangesPrompt(),
        new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), TimeProvider.System),
        new ExplorerViewModel(),
        new JournalViewModel(TimeProvider.System),
        new QuickOpenViewModel(),
        orchestrators: new OrchestratorDocumentFactory(new OrchestrationSession(backend ?? Backends.Complete(new FakeOrchestrator())), new MemoryLayoutStore(), new FakeTimeProvider(T0)),
        progress: progress);

    private static async Task<NextStepViewModel> StepAsync(MainViewModel shell)
    {
        shell.ShowWelcomeCommand.Execute(null);
        await shell.NextStepRead;
        var welcome = Assert.IsType<WelcomeDocumentViewModel>(shell.ActiveContent);
        return Assert.IsType<NextStepViewModel>(welcome.NextStep);
    }

    // ---- what is read ----

    [Fact]
    public async Task A_folder_where_agent_studio_never_wrote_is_a_project_never_set_up_and_is_not_opened()
    {
        var workspace = new FakeProjectWorkspace();
        var reader = new ProjectProgressReader(workspace, new LogRecorder(), new FakeRequirementStore());

        var progress = await reader.ReadAsync(_temp.CreateDirectory("nuevo"), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectProgress.Untouched, progress);
        Assert.Empty(workspace.Opened);
    }

    [Fact]
    public async Task The_configurator_applied_is_the_entry_of_the_log_that_auto_doctor_reads_and_the_requirements_come_from_their_store()
    {
        var log = new LogRecorder();
        log.Entries.Add(LogRecorder.Entry(DecisionKind.ProjectSetupApplied));
        var reader = new ProjectProgressReader(new FakeProjectWorkspace(), log, new FakeRequirementStore(PlanFixtures.Requirements()));

        Assert.Equal(new ProjectProgress(true, true), await reader.ReadAsync(ProjectWithStorage("listo"), TestContext.Current.CancellationToken));

        var empty = new ProjectProgressReader(new FakeProjectWorkspace(), new LogRecorder(), new FakeRequirementStore());
        Assert.Equal(new ProjectProgress(false, false), await empty.ReadAsync(ProjectWithStorage("vacio"), TestContext.Current.CancellationToken));
    }

    /// <summary>What cannot be read is «not known», never «no»: a log that does not verify, a store that fails, a project that does not open.</summary>
    [Fact]
    public async Task What_cannot_be_read_is_not_known_never_no()
    {
        var root = ProjectWithStorage("roto");
        var broken = new LogRecorder { Verification = new DecisionLogVerification(false, 3, "The hash of the entry 3 does not match.") };
        broken.Entries.Add(LogRecorder.Entry(DecisionKind.ProjectSetupApplied));
        var failing = new LogRecorder { LoadFailure = new IOException("locked") };

        Assert.Null((await new ProjectProgressReader(new FakeProjectWorkspace(), broken, new FakeRequirementStore()).ReadAsync(root, TestContext.Current.CancellationToken)).SetupApplied);
        Assert.Null((await new ProjectProgressReader(new FakeProjectWorkspace(), failing, new FakeRequirementStore()).ReadAsync(root, TestContext.Current.CancellationToken)).SetupApplied);
        Assert.Equal(ProjectProgress.Unknown, await new ProjectProgressReader(new FailingWorkspace(), broken, new FakeRequirementStore()).ReadAsync(root, TestContext.Current.CancellationToken));
        Assert.Equal(ProjectProgress.Unknown, await new ProjectProgressReader(null, null, null).ReadAsync(root, TestContext.Current.CancellationToken));
    }

    // ---- the step ----

    [Fact]
    public async Task A_project_set_up_without_frozen_requirements_asks_for_its_first_request_and_the_button_leaves_the_keyboard_in_the_box()
    {
        var log = new LogRecorder();
        log.Entries.Add(LogRecorder.Entry(DecisionKind.ProjectSetupApplied));
        var shell = Shell(new ProjectProgressReader(new FakeProjectWorkspace(), log, new FakeRequirementStore()));
        await shell.OpenProjectAsync(ProjectWithStorage("calc"));

        var step = await StepAsync(shell);

        Assert.Equal(NextStepKind.FirstRequest, step.Kind);
        Assert.Equal("Escribir tu primera petición", step.Action);
        Assert.Equal("El proyecto ya está configurado. Ahora cuenta qué quieres que hagan los agentes.", step.Reason);

        await ((IAsyncRelayCommand)step.Command).ExecuteAsync(null);

        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        Assert.True(orchestrator.IsPreparationExpanded);
        Assert.Equal(OrchestratorFocus.Request, orchestrator.PendingFocus);
    }

    [Fact]
    public async Task A_project_set_up_with_frozen_requirements_asks_for_a_new_request()
    {
        var log = new LogRecorder();
        log.Entries.Add(LogRecorder.Entry(DecisionKind.ProjectSetupApplied));
        var shell = Shell(new ProjectProgressReader(new FakeProjectWorkspace(), log, new FakeRequirementStore(PlanFixtures.Requirements())));
        await shell.OpenProjectAsync(ProjectWithStorage("calc"));

        var step = await StepAsync(shell);

        Assert.Equal(NextStepKind.NewRequest, step.Kind);
        Assert.Equal("Escribir una petición nueva", step.Action);
        Assert.Same(shell.WriteRequestCommand, step.Command);
    }

    /// <summary>A run going on comes first: it is the most certain thing, and it can go on in a project never set up.</summary>
    [Fact]
    public async Task A_run_going_on_comes_first_and_the_step_follows_it_when_it_starts_and_ends()
    {
        var root = _temp.CreateDirectory("calc");
        var shell = Shell(new ProjectProgressReader(new FakeProjectWorkspace(), new LogRecorder(), new FakeRequirementStore()));
        await shell.OpenProjectAsync(root);
        await shell.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        Assert.Equal(NextStepKind.NewRequest, (await StepAsync(shell)).Kind);

        var run = new FakeRun(new RunSnapshot(new RunId("20261116-a1b2c3"), RunPhase.Running, [], FakeRun.Healthy, 0));
        orchestrator.Monitor.Attach(run, orchestrator.Context!.Project!);
        await shell.NextStepRead;

        var welcome = shell.Documents.OfType<WelcomeDocumentViewModel>().Single();
        var step = Assert.IsType<NextStepViewModel>(welcome.NextStep);
        Assert.Equal(NextStepKind.RunGoingOn, step.Kind);
        Assert.Equal("Ver la ejecución en marcha", step.Action);
        Assert.Same(shell.OpenOrchestratorCommand, step.Command);
    }

    [Fact]
    public async Task Changing_project_drops_the_step_of_the_project_left_and_closing_it_leaves_none()
    {
        var log = new LogRecorder();
        log.Entries.Add(LogRecorder.Entry(DecisionKind.ProjectSetupApplied));
        var shell = Shell(new ProjectProgressReader(new FakeProjectWorkspace(), log, new FakeRequirementStore()));
        await shell.OpenProjectAsync(ProjectWithStorage("uno"));
        Assert.Equal(NextStepKind.FirstRequest, (await StepAsync(shell)).Kind);

        await shell.OpenProjectAsync(_temp.CreateDirectory("dos"));
        await shell.NextStepRead;

        // The second one is a folder where Agent Studio never wrote, and no configurator was composed: a new request.
        var welcome = shell.Documents.OfType<WelcomeDocumentViewModel>().Single();
        Assert.Equal(NextStepKind.NewRequest, welcome.NextStep?.Kind);
    }

    // ---- «Ir a los requisitos» ----

    [Fact]
    public async Task Ir_a_los_requisitos_opens_the_orchestrator_of_the_project_on_its_requirements()
    {
        var root = _temp.CreateDirectory("calc");
        var shell = Shell(null);
        await shell.OpenProjectAsync(root);

        await shell.OpenRequirementsAsync(root);

        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        Assert.True(orchestrator.IsPreparationExpanded);
        Assert.Equal(OrchestratorFocus.Requirements, orchestrator.TakePendingFocus());
        Assert.Null(orchestrator.PendingFocus);
    }

    [Fact]
    public async Task Ir_a_los_requisitos_of_another_project_opens_nothing_and_says_why()
    {
        var shell = Shell(null);
        await shell.OpenProjectAsync(_temp.CreateDirectory("calc"));

        await shell.OpenRequirementsAsync(_temp.CreateDirectory("otro"));

        Assert.DoesNotContain(shell.Documents, d => d is OrchestratorDocumentViewModel);
        Assert.NotNull(shell.StatusMessage);
    }

    /// <summary>The status band of the orchestrator offers the way to «Requisitos» while the project has none frozen.</summary>
    [Fact]
    public async Task The_orchestrator_offers_the_way_to_its_requirements_only_while_none_are_frozen()
    {
        var without = Backends.Complete(new FakeOrchestrator()) with { Requirements = new FakeRequirementStore() };
        var shell = Shell(null, without);
        await shell.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await shell.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        Assert.True(orchestrator.ShowsGoToRequirements);

        orchestrator.IsPreparationExpanded = false;
        orchestrator.ShowRequirementsCommand.Execute(null);
        Assert.True(orchestrator.IsPreparationExpanded);
        Assert.Equal(OrchestratorFocus.Requirements, orchestrator.PendingFocus);

        var complete = Shell(null);
        await complete.OpenProjectAsync(_temp.CreateDirectory("listo"));
        await complete.OpenOrchestratorCommand.ExecuteAsync(null);
        Assert.False(Assert.IsType<OrchestratorDocumentViewModel>(complete.ActiveContent).ShowsGoToRequirements);
    }
}
