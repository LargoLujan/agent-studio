using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using AgentStudio.Tests.TestSupport;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The shell with the Phase 5 screens (docs/15 section 9, «Integración»): «Debates» and «Auditar proyecto completo» open the
/// documents of the open project and a project switch takes them out of the dock; the shell is the navigation of the
/// review (it shows a challenge and opens the orchestrator with the corrective tasks), and every orchestrator's monitor gets
/// the review of its runs and the follow-up of a replan. Nothing is shown and no CLI is launched: the screens have no ports.
/// </summary>
public sealed class Phase5ShellTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(2));

    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(Now);

    public void Dispose() => _temp.Dispose();

    private (MainViewModel Main, DebateScreens Screens) Create(RunReviewServices? review = null)
    {
        MainViewModel? main = null;
        var screens = new DebateScreens(DebateScreenBackend.None, _clock, () => main);
        main = new MainViewModel(
            new InMemorySettingsStore(),
            new FakeFolderPicker(),
            new FakeUnsavedChangesPrompt(),
            new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), _clock),
            new ExplorerViewModel(),
            new JournalViewModel(_clock),
            new QuickOpenViewModel(),
            new InlineOrchestrators(_clock),
            debates: screens,
            review: review ?? new RunReviewServices());
        return (main, screens);
    }

    private static Task RunActivityAsync(MainViewModel main, string id)
    {
        var activity = main.Activities.Single(a => a.Id == id);
        return ((IAsyncRelayCommand)activity.Command).ExecuteAsync(activity);
    }

    [Fact]
    public void Debates_is_offered_as_a_tab_only_when_its_screens_were_composed()
    {
        var (main, _) = Create();

        var debates = main.Activities.Single(a => a.Id == "debates");
        Assert.True(debates.IsEnabled);
        Assert.Null(debates.PanelId);
        Assert.Equal("Debates (Ctrl+Mayús+D)", debates.ToolTip);
        Assert.DoesNotContain(ShellFactory.CreateMain().Activities, a => a.Id == "debates");
    }

    [Fact]
    public async Task With_a_project_the_welcome_tab_offers_the_debates_and_the_audit_with_their_shortcuts()
    {
        var (main, _) = Create();
        await main.OpenProjectAsync(_temp.CreateDirectory("calc"));

        main.ShowWelcomeCommand.Execute(null);

        var welcome = Assert.IsType<WelcomeDocumentViewModel>(main.ActiveContent);
        var debates = Assert.Single(welcome.ProjectActions, a => a.Title == "Debates");
        var audit = Assert.Single(welcome.ProjectActions, a => a.Title == "Auditar proyecto completo");
        Assert.Equal(("Ctrl+Mayús+D", "Ctrl+Mayús+A"), (debates.Shortcut, audit.Shortcut));
        Assert.Same(main.OpenDebatesCommand, debates.Command);
        Assert.Same(main.OpenAuditCommand, audit.Command);
    }

    [Fact]
    public async Task Without_a_project_neither_debates_nor_the_audit_open_and_the_shell_says_why()
    {
        var (main, _) = Create();

        await RunActivityAsync(main, "debates");
        Assert.Equal("Abre una carpeta de proyecto para debatir.", main.StatusMessage);

        await main.OpenAuditCommand.ExecuteAsync(null);
        Assert.Equal("Abre una carpeta de proyecto para auditarlo.", main.StatusMessage);
        Assert.DoesNotContain(main.Documents, d => d is DebatesDocumentViewModel or ProjectAuditDocumentViewModel);
    }

    [Fact]
    public async Task Debates_and_the_audit_open_the_documents_of_the_open_project_and_a_switch_takes_them_out_of_the_dock()
    {
        var (main, screens) = Create();
        await main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        var calc = main.ProjectPath!;

        await RunActivityAsync(main, "debates");
        await main.OpenAuditCommand.ExecuteAsync(null);

        var debates = Assert.Single(main.Documents.OfType<DebatesDocumentViewModel>());
        var audit = Assert.Single(main.Documents.OfType<ProjectAuditDocumentViewModel>());
        Assert.Equal(calc, debates.ProjectRoot);
        Assert.Same(screens.Debates(calc), debates);
        Assert.Same(audit, main.ActiveContent);

        await main.OpenProjectAsync(_temp.CreateDirectory("web"));

        Assert.DoesNotContain(main.Documents, d => d is DebatesDocumentViewModel or ProjectAuditDocumentViewModel);

        // Coming back shows the same documents: a debate going on is never lost with its tab.
        await main.OpenProjectAsync(calc);
        await RunActivityAsync(main, "debates");
        Assert.Same(debates, Assert.Single(main.Documents.OfType<DebatesDocumentViewModel>()));
    }

    [Fact]
    public async Task The_shell_is_the_navigation_of_the_review_and_the_corrective_tasks_go_to_the_orchestrator_of_the_project()
    {
        var (main, screens) = Create();
        await main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        var calc = main.ProjectPath!;
        Assert.True(screens.CanNavigate);

        var document = screens.Debates(calc);
        main.Show(document);
        Assert.Same(document, main.ActiveContent);

        await main.CreateCorrectiveTasksAsync(calc, "Corregir la escritura concurrente sin política.");

        var orchestrator = Assert.Single(main.Documents.OfType<OrchestratorDocumentViewModel>());
        Assert.Same(orchestrator, main.ActiveContent);
        Assert.Equal("Corregir la escritura concurrente sin política.", orchestrator.RequestText);
    }

    [Fact]
    public async Task Corrective_tasks_of_another_project_are_not_created_in_the_open_one()
    {
        var (main, _) = Create();
        await main.OpenProjectAsync(_temp.CreateDirectory("calc"));

        await main.CreateCorrectiveTasksAsync(_temp.CreateDirectory("web"), "Corregir algo.");

        Assert.Empty(main.Documents.OfType<OrchestratorDocumentViewModel>());
        Assert.Equal("Las tareas correctivas son de otro proyecto. Ábrelo para crearlas.", main.StatusMessage);
    }

    [Fact]
    public async Task Every_orchestrator_monitor_gets_the_review_of_its_runs_and_the_follow_up_of_a_replan()
    {
        var (main, _) = Create();
        await main.OpenProjectAsync(_temp.CreateDirectory("calc"));

        await RunActivityAsync(main, "orchestrator");

        var monitor = Assert.Single(main.Documents.OfType<OrchestratorDocumentViewModel>()).Monitor;
        Assert.NotNull(monitor.Review);
        Assert.NotNull(monitor.Review!.Challenge);
        Assert.NotNull(monitor.Review.CorrectiveTasks);
        Assert.NotNull(monitor.PrepareFollowUp);
    }

    [Fact]
    public async Task Preparing_the_follow_up_of_a_replan_empties_the_canvas_and_the_next_run_follows_the_audited_one()
    {
        var (main, _) = Create();
        await main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await RunActivityAsync(main, "orchestrator");
        var document = Assert.Single(main.Documents.OfType<OrchestratorDocumentViewModel>());
        var audited = new RunId("20261020-a1b2c3");

        await document.Monitor.PrepareFollowUp!(audited, "Replanificar: falta la revisión de T2.");

        Assert.True(document.Canvas.IsEmpty);
        Assert.False(document.Canvas.IsReadOnly);
        Assert.Equal("Replanificar: falta la revisión de T2.", document.RequestText);
        Assert.Equal(audited, document.FollowUpOf);
    }

    /// <summary>«Orquestador» documents without an engine whose monitor applies what it reads at once, on the test's thread.</summary>
    private sealed class InlineOrchestrators(TimeProvider time) : IOrchestratorDocumentFactory
    {
        public OrchestratorDocumentViewModel Create(string projectRoot) =>
            new(projectRoot, new OrchestrationSession(OrchestrationBackend.None), new FileCanvasLayoutStore(new ProjectStorageFiles()), time, new InlineSynchronizationContext());
    }
}
