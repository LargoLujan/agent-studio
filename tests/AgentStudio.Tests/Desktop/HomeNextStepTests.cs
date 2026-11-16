using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Application.Diagnostics;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Memory;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Memory;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// «Inicio» with the next step of the open project (docs/24 §3.2): one button chosen from what the shell knows without opening
/// anything it did not open already (whether Agent Studio wrote in the folder, what the orchestrator of the project has open,
/// whether the configurator was applied, whether a run goes on), never from what it cannot know. And the way to «Requisitos»
/// that «Auditar» asks the shell for. The engine is a double: nothing is planned or run.
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

    /// <summary>A project folder where Agent Studio already wrote: its database exists (empty: nothing here opens it).</summary>
    private string ProjectWithStorage(string name)
    {
        var root = _temp.CreateDirectory(name);
        _temp.WriteFile(Path.Combine(name, ".agentstudio", "agentstudio.db"), string.Empty);
        return root;
    }

    private static LogRecorder Applied()
    {
        var log = new LogRecorder();
        log.Entries.Add(LogRecorder.Entry(DecisionKind.ProjectSetupApplied));
        return log;
    }

    private static MainViewModel Shell(ProjectProgressReader? progress, OrchestrationBackend? backend = null, DebateScreens? debates = null) => new(
        new InMemorySettingsStore(),
        new FakeFolderPicker(),
        new FakeUnsavedChangesPrompt(),
        new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), TimeProvider.System),
        new ExplorerViewModel(),
        new JournalViewModel(TimeProvider.System),
        new QuickOpenViewModel(),
        orchestrators: new OrchestratorDocumentFactory(new OrchestrationSession(backend ?? Backends.Complete(new FakeOrchestrator())), new MemoryLayoutStore(), new FakeTimeProvider(T0)),
        debates: debates,
        progress: progress);

    /// <summary>Shows «Inicio» and waits for its step.</summary>
    private static async Task<NextStepViewModel> StepAsync(MainViewModel shell)
    {
        shell.ShowWelcomeCommand.Execute(null);
        await shell.NextStepRead;
        var welcome = Assert.IsType<WelcomeDocumentViewModel>(shell.ActiveContent);
        return Assert.IsType<NextStepViewModel>(welcome.NextStep);
    }

    /// <summary>Opens the orchestrator of the open project (as the person does), then shows «Inicio».</summary>
    private static async Task<NextStepViewModel> StepWithTheOrchestratorOpenAsync(MainViewModel shell)
    {
        await shell.OpenOrchestratorCommand.ExecuteAsync(null);
        return await StepAsync(shell);
    }

    // ---- what is read ----

    [Fact]
    public void A_folder_has_storage_only_once_agent_studio_wrote_its_database_there()
    {
        Assert.False(ProjectProgressReader.HasStorage(_temp.CreateDirectory("nuevo")));
        Assert.True(ProjectProgressReader.HasStorage(ProjectWithStorage("usado")));
    }

    [Fact]
    public async Task The_configurator_applied_is_the_entry_of_the_log_that_auto_doctor_reads()
    {
        var project = new ProjectContext("p-1", _temp.Path);

        Assert.True(await new ProjectProgressReader(Applied()).SetupAppliedAsync(project, TestContext.Current.CancellationToken));
        Assert.False(await new ProjectProgressReader(new LogRecorder()).SetupAppliedAsync(project, TestContext.Current.CancellationToken));
    }

    /// <summary>What cannot be read is «not known», never «no»: a log that does not verify, a log that fails, no log, a project not open.</summary>
    [Fact]
    public async Task What_cannot_be_read_is_not_known_never_no()
    {
        var project = new ProjectContext("p-1", _temp.Path);
        var broken = Applied();
        broken.Verification = new DecisionLogVerification(false, 3, "The hash of the entry 3 does not match.");
        var failing = new LogRecorder { LoadFailure = new IOException("locked") };

        Assert.Null(await new ProjectProgressReader(broken).SetupAppliedAsync(project, TestContext.Current.CancellationToken));
        Assert.Null(await new ProjectProgressReader(failing).SetupAppliedAsync(project, TestContext.Current.CancellationToken));
        Assert.Null(await new ProjectProgressReader(null).SetupAppliedAsync(project, TestContext.Current.CancellationToken));
        Assert.Null(await new ProjectProgressReader(Applied()).SetupAppliedAsync(new ProjectContext(string.Empty, _temp.Path), TestContext.Current.CancellationToken));
    }

    // ---- the step ----

    /// <summary>
    /// Review fix 3: «Inicio» never opens a project the app did not open yet. With storage and the orchestrator not opened, the step
    /// claims nothing, and the workspace was never asked to open it.
    /// </summary>
    [Fact]
    public async Task Before_the_orchestrator_opens_the_project_the_step_claims_nothing_and_opens_nothing()
    {
        var workspace = new FakeProjectWorkspace();
        var shell = Shell(new ProjectProgressReader(Applied()), Backends.Complete(new FakeOrchestrator()) with { Workspace = workspace });
        await shell.OpenProjectAsync(ProjectWithStorage("calc"));

        var step = await StepAsync(shell);

        Assert.Equal(NextStepKind.NewRequest, step.Kind);
        Assert.Empty(workspace.Opened);
    }

    /// <summary>Review fix 1: a project prepared by hand in the orchestrator (agents and a profile) is set up, whatever the log says.</summary>
    [Fact]
    public async Task A_project_prepared_by_hand_with_agents_and_a_profile_asks_for_its_first_request()
    {
        var shell = Shell(new ProjectProgressReader(new LogRecorder()), Backends.Complete(new FakeOrchestrator()) with { Requirements = new FakeRequirementStore() });
        await shell.OpenProjectAsync(ProjectWithStorage("calc"));

        var step = await StepWithTheOrchestratorOpenAsync(shell);

        Assert.Equal(NextStepKind.FirstRequest, step.Kind);
        Assert.Equal("Escribir tu primera petición", step.Action);
        Assert.Equal("El proyecto ya está configurado. Ahora cuenta qué quieres que hagan los agentes.", step.Reason);

        await ((IAsyncRelayCommand)step.Command).ExecuteAsync(null);

        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        Assert.True(orchestrator.IsPreparationExpanded);
        Assert.Equal(OrchestratorFocus.Request, orchestrator.PendingFocus);
    }

    /// <summary>Review fix 1: the configurator applied (its entry in the log) is set up even without a verification profile.</summary>
    [Fact]
    public async Task A_project_where_the_configurator_was_applied_asks_for_its_first_request_without_a_profile()
    {
        var backend = Backends.Complete(new FakeOrchestrator()) with { Requirements = new FakeRequirementStore(), Verification = null };
        var shell = Shell(new ProjectProgressReader(Applied()), backend);
        await shell.OpenProjectAsync(ProjectWithStorage("calc"));

        Assert.Equal(NextStepKind.FirstRequest, (await StepWithTheOrchestratorOpenAsync(shell)).Kind);
    }

    [Fact]
    public async Task A_project_set_up_with_frozen_requirements_asks_for_a_new_request()
    {
        var shell = Shell(new ProjectProgressReader(new LogRecorder()));
        await shell.OpenProjectAsync(ProjectWithStorage("calc"));

        var step = await StepWithTheOrchestratorOpenAsync(shell);

        Assert.Equal(NextStepKind.NewRequest, step.Kind);
        Assert.Equal("Escribir una petición nueva", step.Action);
        Assert.Same(shell.WriteRequestCommand, step.Command);
    }

    /// <summary>
    /// A run going on comes first (it can go on in a project never set up), and «Inicio» says what it is doing in plain words:
    /// working, or paused (review fix 5).
    /// </summary>
    [Fact]
    public async Task A_run_going_on_comes_first_and_says_whether_it_works_or_is_paused()
    {
        var shell = Shell(new ProjectProgressReader(new LogRecorder()));
        await shell.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await shell.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        Assert.Equal(NextStepKind.NewRequest, (await StepAsync(shell)).Kind);

        orchestrator.Monitor.Attach(new FakeRun(new RunSnapshot(new RunId("20261116-a1b2c3"), RunPhase.Running, [], FakeRun.Healthy, 0)), orchestrator.Context!.Project!);
        await shell.NextStepRead;
        var welcome = shell.Documents.OfType<WelcomeDocumentViewModel>().Single();
        var working = Assert.IsType<NextStepViewModel>(welcome.NextStep);
        Assert.Equal(NextStepKind.RunGoingOn, working.Kind);
        Assert.Equal("Ver la ejecución en marcha", working.Action);
        Assert.Equal("Los agentes están trabajando en este proyecto. Mira cómo va la ejecución.", working.Reason);
        Assert.Same(shell.OpenOrchestratorCommand, working.Command);

        var paused = Shell(new ProjectProgressReader(new LogRecorder()));
        await paused.OpenProjectAsync(_temp.CreateDirectory("pausado"));
        await paused.OpenOrchestratorCommand.ExecuteAsync(null);
        var other = Assert.IsType<OrchestratorDocumentViewModel>(paused.ActiveContent);
        other.Monitor.Attach(new FakeRun(new RunSnapshot(new RunId("20261116-d4e5f6"), RunPhase.Paused, [], FakeRun.Healthy, 0)), other.Context!.Project!);
        Assert.Equal("La ejecución de este proyecto está en pausa. Mira dónde se quedó y reanúdala cuando quieras.", (await StepAsync(paused)).Reason);
    }

    /// <summary>
    /// Review fix 4: a question of the run does not change its phase (other tasks go on), so «Inicio» follows the questions too: it
    /// says the run waits for your answer while one waits, and that the agents work once it is answered.
    /// </summary>
    [Fact]
    public async Task A_question_of_the_run_changes_what_inicio_says_of_it()
    {
        var shell = Shell(new ProjectProgressReader(new LogRecorder()));
        await shell.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await shell.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        await StepAsync(shell);
        var welcome = shell.Documents.OfType<WelcomeDocumentViewModel>().Single();
        orchestrator.Monitor.Attach(new FakeRun(new RunSnapshot(new RunId("20261116-a1b2c3"), RunPhase.Running, [], FakeRun.Healthy, 0)), orchestrator.Context!.Project!);
        await shell.NextStepRead;
        Assert.Equal(AgentStudio.Desktop.Localization.Texts.Get("Welcome_Next_Run"), welcome.NextStep?.Reason);

        var question = new AgentStudio.Desktop.Orchestrator.Questions.QuestionViewModel(
            new PendingQuestion("T1-q1", Tid("T1"), Aid("exec1"), 1, "¿Uso decimal o double?", Eid("EV-1"), [], T0), _ => Task.CompletedTask);
        orchestrator.Monitor.PendingQuestions.Add(question);
        await shell.NextStepRead;
        Assert.Equal(AgentStudio.Desktop.Localization.Texts.Get("Welcome_Next_RunWaiting"), welcome.NextStep?.Reason);

        orchestrator.Monitor.PendingQuestions.Remove(question);
        await shell.NextStepRead;
        Assert.Equal(AgentStudio.Desktop.Localization.Texts.Get("Welcome_Next_Run"), welcome.NextStep?.Reason);
    }

    [Fact]
    public async Task Changing_project_drops_the_step_of_the_project_left()
    {
        var shell = Shell(new ProjectProgressReader(new LogRecorder()), Backends.Complete(new FakeOrchestrator()) with { Requirements = new FakeRequirementStore() });
        await shell.OpenProjectAsync(ProjectWithStorage("uno"));
        Assert.Equal(NextStepKind.FirstRequest, (await StepWithTheOrchestratorOpenAsync(shell)).Kind);

        await shell.OpenProjectAsync(_temp.CreateDirectory("dos"));
        await shell.NextStepRead;

        // The second one is a folder where Agent Studio never wrote, and no configurator was composed: a new request.
        var welcome = shell.Documents.OfType<WelcomeDocumentViewModel>().Single();
        Assert.Equal(NextStepKind.NewRequest, welcome.NextStep?.Kind);
    }

    // ---- «Ir a los requisitos» and «Auditar» ----

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
        Assert.Equal("Esos requisitos son de otro proyecto. Ábrelo para verlos.", shell.StatusMessage);
    }

    /// <summary>
    /// Review fix 2: after «Ir a los requisitos» the person freezes them and clicks back on the tab of «Auditar»: it reads them again,
    /// so it no longer says they are missing nor sends the person back.
    /// </summary>
    [Fact]
    public async Task Coming_back_to_auditar_after_freezing_the_requirements_reads_them_again()
    {
        var store = new FakeRequirementStore();
        var backend = new DebateScreenBackend(
            Challenges: new FakeVerdictChallenges(),
            Audits: new FakeProjectAuditService(),
            Workspace: new FakeWorkspace(),
            Roster: new FakeRoster(() => DebateSamples.Roster),
            Requirements: store,
            Verdicts: new FakeVerdictStore());
        MainViewModel? shell = null;
        var screens = new DebateScreens(backend, new FakeClock(DateTimeOffset.UnixEpoch), () => shell);
        shell = Shell(null, debates: screens);
        await shell.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await shell.OpenAuditCommand.ExecuteAsync(null);
        var audit = Assert.IsType<ProjectAuditDocumentViewModel>(shell.ActiveContent);
        Assert.True(audit.ShowsGoToRequirements);

        await audit.GoToRequirementsCommand.ExecuteAsync(null);
        Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        await store.SaveAsync("p-1", DebateSamples.Requirements, TestContext.Current.CancellationToken);
        shell.ActiveContent = audit;
        await shell.AuditRequirementsRead;

        Assert.False(audit.ShowsGoToRequirements);
        Assert.DoesNotContain("No se puede auditar sin requisitos congelados.", audit.Problems);
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

    /// <summary>Layout fix (a): while a run goes on the band of the plan folds to one line, and unfolds by itself when it ends.</summary>
    [Fact]
    public async Task The_band_of_the_plan_folds_while_a_run_goes_on_and_can_be_unfolded()
    {
        var shell = Shell(null);
        await shell.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await shell.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(shell.ActiveContent);
        Assert.False(orchestrator.IsReviewFolded);
        Assert.False(orchestrator.CanFoldReview);

        orchestrator.Monitor.Attach(new FakeRun(new RunSnapshot(new RunId("20261116-a1b2c3"), RunPhase.Running, [], FakeRun.Healthy, 0)), orchestrator.Context!.Project!);

        Assert.True(orchestrator.IsReviewFolded);
        Assert.True(orchestrator.CanFoldReview);
        orchestrator.ToggleReviewCommand.Execute(null);
        Assert.True(orchestrator.ShowsReview);
    }
}
