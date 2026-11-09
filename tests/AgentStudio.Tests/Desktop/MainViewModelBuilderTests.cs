using System.IO;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Setup;
using AgentStudio.Desktop.Tools;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Desktop.Orchestrator.Monitor;
using AgentStudio.Tests.Domain.Orchestration;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// What the shell shows of the visual builder at the end of Phase 13 (docs/17 section 9.3, «Integración (el lead)»): «Agentes»,
/// «Herramientas» and «Pruebas» in the activity bar, «Configurar proyecto» (the welcome tab and the band of preparation), the
/// counter of the agents in the top bar, the panels of the bottom zone following the monitor of the project, and the notice of
/// the tray for the questions. The engine and the windows are doubles: nothing is shown and no agent is launched.
/// </summary>
public sealed class MainViewModelBuilderTests
{
    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    private sealed class RecordingHost : IQuestionWindowHost
    {
        public bool IsAppInView { get; set; } = true;

        public List<string> Shown { get; } = [];

        public void Show(QuestionViewModel question, bool activate = true) => Shown.Add(question.Id);

        public void Close(QuestionViewModel question)
        {
        }

        public void Withdraw()
        {
        }

        public void Restore()
        {
        }
    }

    /// <summary>A shell over a fake engine, with the services of the builder and a real configurator over fakes of its ports.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(bool withSetup = true, RecordingHost? host = null)
        {
            Engine = new FakeOrchestrator();
            Roster = new InMemoryRoster(SetupTeams.Default());
            Monitors = new MonitorServices(hostFactory: host is null ? null : () => host);
            var backend = Backends.Complete(Engine) with { Roster = Roster };
            var factory = new OrchestratorDocumentFactory(
                new OrchestrationSession(backend),
                new MemoryLayoutStore(),
                new FakeTimeProvider(T0),
                builder: BuilderServices.None,
                monitors: Monitors);
            var setup = withSetup ? new SetupViewModelFactory(SetupService()) : null;
            Main = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
                new ExplorerViewModel(),
                new JournalViewModel(TimeProvider.System),
                new QuickOpenViewModel(),
                orchestrators: factory,
                monitors: Monitors,
                setup: setup,
                setupSuggestions: new SetupSuggestions(Folder.Combine("suggestions")));
        }

        public TempDirectory Folder { get; } = new();

        public void Dispose() => Folder.Dispose();

        public FakeOrchestrator Engine { get; }

        public InMemoryRoster Roster { get; }

        public MonitorServices Monitors { get; }

        public MainViewModel Main { get; }

        private ProjectSetupService SetupService() => new(
            new FakeRepositoryReader(SetupFakes.Facts(["App.slnx", "src/App/App.csproj"])),
            Roster,
            new FakeProfiles(),
            new FakeOptionsStore(),
            new RecordingRecorder(),
            new FakeBrief(),
            new FakeSkillCatalog(FakeSkillCatalog.Skill("dotnet-testing", ["dotnet", "testing"])),
            new FakeActivations(),
            requirements: null,
            models: new BuiltInModelCatalog(),
            learning: null,
            gate: new FakeGate(true));

        public async Task<string> OpenProjectAsync()
        {
            var root = Folder.CreateDirectory("proyecto");
            await Main.OpenProjectAsync(root);
            return root;
        }
    }

    private static Task RunAsync(MainViewModel main, string activityId)
    {
        var activity = main.Activities.Single(a => a.Id == activityId);
        return activity.Command is IAsyncRelayCommand async ? async.ExecuteAsync(activity) : Task.Run(() => activity.Command.Execute(activity));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }

    // ---- «Agentes», «Pruebas» and «Herramientas» ----

    [Fact]
    public async Task Agentes_opens_the_orchestrator_of_the_project_on_the_layer_team()
    {
        using var rig = new Rig();
        await rig.OpenProjectAsync();

        await RunAsync(rig.Main, "agents");

        var document = Assert.IsType<OrchestratorDocumentViewModel>(rig.Main.ActiveContent);
        Assert.True(document.Canvas.IsTeamLayer);

        // The tab already exists and the person went to the plan: «Agentes» takes them back to the team, in the same tab.
        document.Canvas.Layer = CanvasLayer.Plan;
        await RunAsync(rig.Main, "agents");

        Assert.Same(document, rig.Main.ActiveContent);
        Assert.True(document.Canvas.IsTeamLayer);
        Assert.Single(rig.Main.Documents.OfType<OrchestratorDocumentViewModel>());
    }

    [Fact]
    public async Task Agentes_without_a_project_says_so_and_opens_nothing()
    {
        using var rig = new Rig();

        await RunAsync(rig.Main, "agents");

        Assert.DoesNotContain(rig.Main.Documents, d => d is OrchestratorDocumentViewModel);
        Assert.NotNull(rig.Main.StatusMessage);
    }

    [Fact]
    public void Pruebas_shows_or_hides_the_panel_of_tests_and_the_three_panels_exist_with_their_own_ids()
    {
        using var rig = new Rig();
        var requested = new List<string>();
        rig.Main.PanelToggleRequested += (_, id) => requested.Add(id);

        var tests = rig.Main.Activities.Single(a => a.Id == "tests");
        tests.Command.Execute(tests);

        Assert.Equal([PanelIds.Tests], requested);
        Assert.Equal(PanelIds.Tests, tests.PanelId);
        Assert.Equal(Texts.Get("Shell_Activity_Tests"), tests.Title);
        Assert.Equal(["panel:problems", "panel:output", "panel:tests"], [PanelIds.Problems, PanelIds.Output, PanelIds.Tests]);
        Assert.NotNull(rig.Main.Problems);
        Assert.NotNull(rig.Main.Output);
        Assert.NotNull(rig.Main.Tests);

        // The tests of HEAD have no service yet (docs/04 section 21.10): the button is not shown instead of one that does nothing.
        Assert.False(rig.Main.Tests.CanRunHead);
    }

    [Fact]
    public async Task Herramientas_opens_one_tab_with_the_tools_of_the_template_of_the_project()
    {
        using var rig = new Rig();
        await rig.OpenProjectAsync();

        await RunAsync(rig.Main, "tools");

        var document = Assert.IsType<ToolsDocumentViewModel>(rig.Main.ActiveContent);
        Assert.True(document.Tools.HasAgents);
        Assert.NotEmpty(document.Tools.Groups);
        Assert.Equal(Texts.Get("Tools_Title"), document.Title);

        await RunAsync(rig.Main, "tools");

        Assert.Single(rig.Main.Documents.OfType<ToolsDocumentViewModel>());
    }

    [Fact]
    public async Task Giving_a_tool_to_an_agent_leads_to_the_inspector_of_the_layer_team_and_says_so()
    {
        using var rig = new Rig();
        await rig.OpenProjectAsync();
        await RunAsync(rig.Main, "tools");
        var tools = Assert.IsType<ToolsDocumentViewModel>(rig.Main.ActiveContent).Tools;
        var row = tools.Groups.SelectMany(g => g.Rows).First(r => r.CanBeGiven);
        await RunAsync(rig.Main, "agents");
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(rig.Main.ActiveContent);
        orchestrator.Canvas.Layer = CanvasLayer.Plan;

        tools.GiveToAgentCommand.Execute(row);
        await WaitForAsync(() => rig.Main.StatusMessage is not null);

        Assert.True(orchestrator.Canvas.IsTeamLayer);
        Assert.Same(orchestrator, rig.Main.ActiveContent);
        Assert.NotNull(orchestrator.Canvas.Team.SelectedAgent);
        Assert.Equal(Texts.Format("Shell_Tools_Give", row.Name), rig.Main.StatusMessage);
    }

    // ---- «Configurar proyecto» ----

    [Fact]
    public async Task The_welcome_tab_of_a_project_offers_configurar_proyecto_and_opening_it_reads_the_project()
    {
        using var rig = new Rig();
        await rig.OpenProjectAsync();
        rig.Main.ShowWelcomeCommand.Execute(null);
        var welcome = Assert.IsType<WelcomeDocumentViewModel>(rig.Main.ActiveContent);
        var action = Assert.Single(welcome.ProjectActions, a => a.Title == Texts.Get("Shell_Setup_Action"));

        await ((IAsyncRelayCommand)action.Command).ExecuteAsync(null);

        var document = Assert.IsType<SetupDocumentViewModel>(rig.Main.ActiveContent);
        Assert.True(document.Setup.HasProject);
        Assert.Equal(["Orquestador", "Ejecutor", "Juez"], document.Setup.TeamRows.Select(r => r.Name));
        Assert.Equal(Texts.Format("Shell_Setup_Title", "proyecto"), document.Title);

        // One tab for the project: asking again shows it, it does not make another.
        await ((IAsyncRelayCommand)action.Command).ExecuteAsync(null);
        Assert.Single(rig.Main.Documents.OfType<SetupDocumentViewModel>());
    }

    [Fact]
    public async Task The_band_of_preparation_of_the_orchestrator_opens_the_configurator_of_its_project()
    {
        using var rig = new Rig();
        await rig.OpenProjectAsync();
        await RunAsync(rig.Main, "orchestrator");
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(rig.Main.ActiveContent);

        orchestrator.ConfigureProjectCommand.Execute(null);
        await WaitForAsync(() => rig.Main.Documents.OfType<SetupDocumentViewModel>().Any());

        var document = rig.Main.Documents.OfType<SetupDocumentViewModel>().Single();
        Assert.Same(document, rig.Main.ActiveContent);
        Assert.Equal(orchestrator.ProjectRoot, document.ProjectRoot);
    }

    [Fact]
    public async Task Without_the_configurator_composed_the_welcome_tab_does_not_offer_it_and_the_command_does_nothing()
    {
        using var rig = new Rig(withSetup: false);
        await rig.OpenProjectAsync();
        rig.Main.ShowWelcomeCommand.Execute(null);
        var welcome = Assert.IsType<WelcomeDocumentViewModel>(rig.Main.ActiveContent);

        await rig.Main.OpenSetupCommand.ExecuteAsync(null);

        Assert.DoesNotContain(welcome.ProjectActions, a => a.Title == Texts.Get("Shell_Setup_Action"));
        Assert.DoesNotContain(rig.Main.Documents, d => d is SetupDocumentViewModel);
    }

    [Fact]
    public async Task The_configurator_is_suggested_the_first_time_a_project_is_opened_and_never_again_nor_by_itself()
    {
        using var rig = new Rig();
        rig.Main.ShowWelcomeCommand.Execute(null);
        var welcome = Assert.IsType<WelcomeDocumentViewModel>(rig.Main.ActiveContent);
        Assert.False(welcome.HasSetupSuggestion);

        var root = await rig.OpenProjectAsync();

        Assert.Equal(Texts.Get("Shell_Setup_Suggestion"), welcome.SetupSuggestion);
        Assert.DoesNotContain(rig.Main.Documents, d => d is SetupDocumentViewModel);
        Assert.True(new SetupSuggestions(rig.Folder.Combine("suggestions")).WasSuggested(root));

        // Another project is suggested its own; coming back to this one, it is not suggested a second time, and nothing opens by itself.
        var other = rig.Folder.CreateDirectory("otro");
        await rig.Main.OpenProjectAsync(other);
        Assert.True(welcome.HasSetupSuggestion);
        await rig.Main.OpenProjectAsync(root);
        Assert.False(welcome.HasSetupSuggestion);
        Assert.DoesNotContain(rig.Main.Documents, d => d is SetupDocumentViewModel);
    }

    [Fact]
    public async Task What_the_configurator_applies_to_the_template_reaches_the_tab_of_the_orchestrator()
    {
        using var rig = new Rig();
        await rig.OpenProjectAsync();
        await RunAsync(rig.Main, "orchestrator");
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(rig.Main.ActiveContent);
        var before = orchestrator.Context!.Roster.Select(a => a.Model).ToList();
        await rig.Main.OpenSetupCommand.ExecuteAsync(null);
        var setup = Assert.IsType<SetupDocumentViewModel>(rig.Main.ActiveContent).Setup;
        setup.Steps[(int)SetupStep.Verification].IsIncluded = false;
        setup.UseAllRecommendedCommand.Execute(null);

        await setup.ApplyCommand.ExecuteAsync(null);
        await WaitForAsync(() => !orchestrator.Context!.Roster.Select(a => a.Model).SequenceEqual(before));

        var stored = await rig.Roster.ListAsync("p-1", TestContext.Current.CancellationToken);
        Assert.Equal(stored.Select(a => a.Model), orchestrator.Context!.Roster.Select(a => a.Model));
    }

    // ---- the counter of the agents and the panels follow the monitor of the project ----

    [Fact]
    public async Task The_counter_of_the_top_bar_follows_the_agents_of_the_run_and_empties_without_one()
    {
        using var rig = new Rig();
        await rig.OpenProjectAsync();
        await RunAsync(rig.Main, "orchestrator");
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(rig.Main.ActiveContent);
        Assert.Equal(string.Empty, rig.Main.AgentCounter);
        Assert.False(rig.Main.HasAgentCounter);

        var run = new FakeRun(BackgroundSamples.Snapshot("20261109-aaaaaa", RunPhase.Running, 1, BackgroundSamples.TaskOf("T1", WorkTaskStatus.Running, "ejecutor"), BackgroundSamples.TaskOf("T2", WorkTaskStatus.Pending)));
        orchestrator.Monitor.Attach(run, new ProjectContext("p-1", orchestrator.ProjectRoot));

        Assert.True(rig.Main.HasAgentCounter);
        Assert.Equal(AgentLiveTexts.Counter(orchestrator.Monitor.Current!), rig.Main.AgentCounter);
        Assert.StartsWith(Texts.Format("Monitor_AgentCard_Counter", string.Empty)[..8], rig.Main.AgentCounter, StringComparison.Ordinal);
        Assert.Contains(Texts.Format("Monitor_Counter_Working", 1), rig.Main.AgentCounter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panels_of_the_bottom_zone_follow_the_monitor_of_the_open_project_and_never_the_run_of_another()
    {
        using var rig = new Rig();
        var first = Directory.CreateDirectory(rig.Folder.Combine("a")).FullName;
        var second = Directory.CreateDirectory(rig.Folder.Combine("b")).FullName;
        await rig.Main.OpenProjectAsync(first);
        await RunAsync(rig.Main, "orchestrator");
        var orchestrator = Assert.IsType<OrchestratorDocumentViewModel>(rig.Main.ActiveContent);
        var failed = BackgroundSamples.TaskOf("T1", WorkTaskStatus.Failed, "ejecutor");
        orchestrator.Monitor.Attach(new FakeRun(BackgroundSamples.Snapshot("20261109-aaaaaa", RunPhase.Running, 1, failed)), new ProjectContext("p-1", first));
        rig.Main.Problems.Refresh();
        Assert.NotEmpty(rig.Main.Problems.Rows);

        await rig.Main.OpenProjectAsync(second);

        // The project opened has no run: the panels do not keep the problems of the one left.
        Assert.Empty(rig.Main.Problems.Rows);
        Assert.Equal(string.Empty, rig.Main.AgentCounter);
    }

    // ---- the tray (docs/17 D-41) ----

    [Fact]
    public void A_question_out_of_view_shows_its_notice_in_the_tray_with_no_data_of_the_project()
    {
        var host = new RecordingHost { IsAppInView = false };
        var monitors = new MonitorServices(hostFactory: () => host);
        var icon = new FakeTrayIcon();
        using var tray = new TrayController(
            icon,
            new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan),
            new ManagedExit(new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan), new FakeExitPrompt(), new FakeRunExitRecorder(), new FakeShellWindow(), new FakeAppShutdown()),
            new FakePanelPresenter(),
            TrayShellActions.None,
            new FakeFolderOpener(),
            @"C:\logs",
            new FakeTimeProvider(T0));
        tray.Start();
        using var follow = tray.FollowAttention(monitors);
        var (monitor, run, ui, _) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        using var release = monitors.Equip(monitor);

        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        var notification = Assert.Single(icon.Notifications);
        Assert.Equal(TrayController.AttentionTitle, notification.Title);
        Assert.Equal(Texts.Get("Monitor_Question_Notice"), notification.Text);
        Assert.DoesNotContain("T1", notification.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("exec1", notification.Text, StringComparison.Ordinal);
        Assert.Empty(host.Shown);
    }

    [Fact]
    public void A_question_with_the_app_in_view_opens_its_window_and_the_tray_says_nothing()
    {
        var host = new RecordingHost();
        var monitors = new MonitorServices(hostFactory: () => host);
        var icon = new FakeTrayIcon();
        var work = new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan);
        using var tray = new TrayController(icon, work, new ManagedExit(work, new FakeExitPrompt(), new FakeRunExitRecorder(), new FakeShellWindow(), new FakeAppShutdown()), new FakePanelPresenter(), TrayShellActions.None, new FakeFolderOpener(), @"C:\logs", new FakeTimeProvider(T0));
        tray.Start();
        using var follow = tray.FollowAttention(monitors);
        var (monitor, run, ui, _) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        using var release = monitors.Equip(monitor);

        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        Assert.Equal(["T1-q1"], host.Shown);
        Assert.Empty(icon.Notifications);
    }

    [Fact]
    public void The_tray_follows_the_attention_only_until_the_subscription_is_disposed()
    {
        var host = new RecordingHost { IsAppInView = false };
        var monitors = new MonitorServices(hostFactory: () => host);
        var icon = new FakeTrayIcon();
        var work = new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan);
        using var tray = new TrayController(icon, work, new ManagedExit(work, new FakeExitPrompt(), new FakeRunExitRecorder(), new FakeShellWindow(), new FakeAppShutdown()), new FakePanelPresenter(), TrayShellActions.None, new FakeFolderOpener(), @"C:\logs", new FakeTimeProvider(T0));
        tray.Start();
        var follow = tray.FollowAttention(monitors);
        var (monitor, run, ui, _) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        using var release = monitors.Equip(monitor);
        follow.Dispose();

        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        Assert.Empty(icon.Notifications);
    }
}
