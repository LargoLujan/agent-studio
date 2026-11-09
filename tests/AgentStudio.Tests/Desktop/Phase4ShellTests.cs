using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Skills;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Desktop.Memory;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using AgentStudio.Tests.TestSupport;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The shell with the Phase 4 screens (docs/14 section 9, «Integración»): «Skills» opens the catalog of the open project,
/// «Consumo» and «Memoria» are one tab each for the session and follow the open project, the user's context thresholds
/// reach every orchestrator, the end of a run lets «Memoria» export again, and the end of a run of Codex (whatever started
/// it) reads the Codex quota, only with the opt-in (docs/18 P37). Nothing is shown and no CLI is launched: the ports are
/// fakes, and the app-server of Codex is a double.
/// </summary>
public sealed class Phase4ShellTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2));

    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(Now);

    public void Dispose() => _temp.Dispose();

    private sealed record Shell(
        MainViewModel Main,
        FakeUsageReport Report,
        FakeQuotaReader Codex,
        FakeProjectMemory Memory,
        FakeRunActivity Activity,
        UsageDashboardViewModel Usage,
        MemoryViewModel MemoryScreen,
        FakeMemoryDerivation Derivation,
        FakeMemoryExporter Exporter,
        AgentStudio.Application.Providers.AgentRunEnds RunEnds);

    private Shell Create(RecordingSettingsStore? settings = null)
    {
        var store = settings ?? new RecordingSettingsStore();
        var report = new FakeUsageReport();
        var codex = new FakeQuotaReader();
        var runEnds = new AgentStudio.Application.Providers.AgentRunEnds();
        var usage = new UsageDashboardViewModel(
            report,
            new FakeUsageExporter(),
            new FakeSaveFilePicker(null),
            store,
            _clock,
            new QuotaViewModel(new FakeQuotaObservations(), codex, _clock, runEnds),
            new ObservabilitySettingsViewModel(store));
        var memory = new FakeProjectMemory();
        var activity = new FakeRunActivity();
        var exporter = new FakeMemoryExporter();
        var derivation = new FakeMemoryDerivation(new MemoryDerivationResult([], [], []));
        var memoryScreen = new MemoryViewModel(
            new FakeProjectWorkspace(),
            memory,
            exporter,
            activity,
            new DailySummaryViewModel(new FakeDailySummaryService(), _clock),
            _clock);
        var main = new MainViewModel(
            new InMemorySettingsStore(),
            new FakeFolderPicker(),
            new FakeUnsavedChangesPrompt(),
            new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), _clock),
            new ExplorerViewModel(),
            new JournalViewModel(_clock),
            new QuickOpenViewModel(),
            new InlineOrchestrators(_clock),
            new SkillsDocumentFactory(SkillsBackend.None, _clock),
            usage,
            memoryScreen,
            new ProjectMemoryUpkeep(new FakeProjectWorkspace(), derivation, exporter));
        return new Shell(main, report, codex, memory, activity, usage, memoryScreen, derivation, exporter, runEnds);
    }

    private static Task RunActivityAsync(MainViewModel main, string id)
    {
        var activity = main.Activities.Single(a => a.Id == id);
        return ((IAsyncRelayCommand)activity.Command).ExecuteAsync(activity);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), message);
    }

    [Fact]
    public void The_activity_bar_offers_skills_usage_and_memory_as_tabs()
    {
        var shell = Create();

        Assert.Equal(
            [
                "Explorador", "Orquestador", Texts.Get("Shell_Activity_Agents"), "Skills", Texts.Get("Shell_Activity_Tools"), Texts.Get("Shell_Activity_Tests"),
                "Consumo", "Memoria", "Terminal", "Auto Doctor", "Historial · bitácora",
            ],
            shell.Main.Activities.Select(a => a.Title));
        foreach (var id in new[] { "skills", "usage", "memory" })
        {
            var activity = shell.Main.Activities.Single(a => a.Id == id);
            Assert.True(activity.IsEnabled);
            Assert.Null(activity.PanelId);
            Assert.Equal(activity.Title, activity.ToolTip);
        }
    }

    [Fact]
    public async Task With_a_project_the_welcome_tab_offers_usage_and_memory_too()
    {
        var shell = Create();
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));

        shell.Main.ShowWelcomeCommand.Execute(null);

        var welcome = Assert.IsType<WelcomeDocumentViewModel>(shell.Main.ActiveContent);
        Assert.Equal(["Orquestador", "Ir al fichero", "Consumo", "Memoria", "Terminal"], welcome.ProjectActions.Select(a => a.Title));
        Assert.Same(shell.Main.OpenUsageCommand, welcome.ProjectActions[2].Command);
        Assert.Same(shell.Main.OpenMemoryCommand, welcome.ProjectActions[3].Command);
    }

    [Fact]
    public void Without_the_usage_and_memory_screens_their_activities_are_not_offered()
    {
        var main = ShellFactory.CreateMain();

        Assert.DoesNotContain(main.Activities, a => a.Id is "usage" or "memory");
        Assert.True(main.Activities.Single(a => a.Id == "skills").IsEnabled);
    }

    [Fact]
    public async Task Skills_opens_the_tab_of_the_open_project_and_a_project_switch_closes_it()
    {
        var shell = Create();
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        var first = shell.Main.ProjectPath;

        await RunActivityAsync(shell.Main, "skills");
        await RunActivityAsync(shell.Main, "skills");

        var skills = Assert.Single(shell.Main.Documents.OfType<SkillsDocumentViewModel>());
        Assert.Equal(first, skills.ProjectRoot);
        Assert.Same(skills, shell.Main.ActiveContent);

        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("web"));

        Assert.Empty(shell.Main.Documents.OfType<SkillsDocumentViewModel>());
        await RunActivityAsync(shell.Main, "skills");
        var second = Assert.Single(shell.Main.Documents.OfType<SkillsDocumentViewModel>());
        Assert.NotSame(skills, second);
        Assert.Equal(shell.Main.ProjectPath, second.ProjectRoot);
    }

    [Fact]
    public async Task Usage_asks_for_the_open_project_and_follows_a_switch_while_its_tab_is_open()
    {
        var shell = Create();
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        Assert.Empty(shell.Report.Queries);

        await RunActivityAsync(shell.Main, "usage");
        await RunActivityAsync(shell.Main, "usage");

        Assert.Same(shell.Usage, Assert.Single(shell.Main.Documents.OfType<UsageDashboardViewModel>()));
        Assert.Equal([shell.Main.ProjectPath!], Assert.Single(shell.Report.Queries).Roots);

        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("web"));

        Assert.Equal(2, shell.Report.Queries.Count);
        Assert.Equal([shell.Main.ProjectPath!], shell.Report.Queries[^1].Roots);
    }

    [Fact]
    public async Task A_closed_usage_tab_asks_again_only_when_it_is_reopened_for_another_project()
    {
        var shell = Create();
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await RunActivityAsync(shell.Main, "usage");
        await shell.Main.CloseDocumentAsync(shell.Usage);

        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("web"));

        Assert.Single(shell.Report.Queries);
        await RunActivityAsync(shell.Main, "usage");
        Assert.Equal(2, shell.Report.Queries.Count);
        Assert.Equal([shell.Main.ProjectPath!], shell.Report.Queries[^1].Roots);
    }

    [Fact]
    public async Task Memory_shows_the_open_project_and_follows_a_switch_while_its_tab_is_open()
    {
        var shell = Create();
        var calc = _temp.CreateDirectory("calc");
        await shell.Main.OpenProjectAsync(calc);

        await RunActivityAsync(shell.Main, "memory");

        Assert.Same(shell.MemoryScreen, Assert.Single(shell.Main.Documents.OfType<MemoryViewModel>()));
        Assert.Equal(shell.Main.ProjectPath, Assert.Single(shell.Memory.Recalls).Project.RootPath);

        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("web"));

        Assert.Equal(shell.Main.ProjectPath, shell.Memory.Recalls[^1].Project.RootPath);
    }

    [Fact]
    public async Task The_saved_context_thresholds_reach_every_orchestrator()
    {
        var store = new RecordingSettingsStore(AppSettings.Default.WithContextThresholds(60, 80, 90));
        var shell = Create(store);
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await RunActivityAsync(shell.Main, "orchestrator");
        var calc = Assert.Single(shell.Main.Documents.OfType<OrchestratorDocumentViewModel>());

        Assert.Equal((0.60, 0.80, 0.90), Thresholds(calc));

        // Saved by the «Guardar» of «Ajustes», whose «Contexto y cuota» is the one «Consumo» judges its report with.
        shell.Usage.Preferences.WarningText = "50";
        shell.Usage.Preferences.CompressText = "75";
        shell.Usage.Preferences.CriticalText = "92";
        new AgentStudio.Desktop.Settings.SettingsDocumentViewModel(store, shell.Usage.Preferences).SaveCommand.Execute(null);

        Assert.Equal((0.50, 0.75, 0.92), Thresholds(calc));
    }

    [Fact]
    public async Task The_end_of_a_run_lets_memory_export_again_and_reads_no_quota_when_no_codex_ran()
    {
        // P37: the quota of Codex follows the end of a run of Codex, not the end of any orchestration.
        var shell = Create(new RecordingSettingsStore(AppSettings.Default with { CodexQuotaReadEnabled = true }));
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await RunActivityAsync(shell.Main, "memory");
        await RunActivityAsync(shell.Main, "orchestrator");
        var monitor = Assert.Single(shell.Main.Documents.OfType<OrchestratorDocumentViewModel>()).Monitor;
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));

        var exportAsked = 0;
        shell.MemoryScreen.PropertyChanged += (_, e) => exportAsked += e.PropertyName == nameof(MemoryViewModel.ExportBlocked) ? 1 : 0;

        shell.Activity.Busy = true;
        monitor.Attach(run);

        Assert.True(monitor.IsActive);
        Assert.Equal(1, exportAsked);
        Assert.NotNull(shell.MemoryScreen.ExportBlocked);
        Assert.Equal(0, shell.Codex.Reads);

        shell.Activity.Busy = false;
        run.Finish(new RunResult(run.Id, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Todo hecho"));
        await monitor.Listening;

        Assert.False(monitor.IsActive);
        Assert.True(exportAsked >= 2, "The memory screen was not told that the run ended.");
        Assert.Null(shell.MemoryScreen.ExportBlocked);
        Assert.Equal(0, shell.Codex.Reads);
    }

    [Fact]
    public async Task The_end_of_a_codex_run_reads_the_Codex_quota_once_with_the_opt_in_and_never_twice_within_five_minutes()
    {
        var shell = Create(new RecordingSettingsStore(AppSettings.Default with { CodexQuotaReadEnabled = true }));

        // A run of Claude asks nothing; a run of Codex, whatever started it, reads the quota with the app-server (a double here).
        shell.RunEnds.Notify(new AgentStudio.Application.Providers.AgentRunEnded(AgentStudio.Domain.Providers.ProviderId.ClaudeCode, AgentStudio.Application.Providers.AgentRunStatus.Succeeded));
        await shell.Usage.Quota.CodexReadAfterRun;
        Assert.Equal(0, shell.Codex.Reads);

        shell.RunEnds.Notify(new AgentStudio.Application.Providers.AgentRunEnded(AgentStudio.Domain.Providers.ProviderId.CodexCli, AgentStudio.Application.Providers.AgentRunStatus.Succeeded));
        await shell.Usage.Quota.CodexReadAfterRun;
        Assert.Equal(1, shell.Codex.Reads);

        _clock.Advance(TimeSpan.FromMinutes(2));
        shell.RunEnds.Notify(new AgentStudio.Application.Providers.AgentRunEnded(AgentStudio.Domain.Providers.ProviderId.CodexCli, null));
        await shell.Usage.Quota.CodexReadAfterRun;
        Assert.Equal(1, shell.Codex.Reads);

        _clock.Advance(TimeSpan.FromMinutes(4));
        shell.RunEnds.Notify(new AgentStudio.Application.Providers.AgentRunEnded(AgentStudio.Domain.Providers.ProviderId.CodexCli, AgentStudio.Application.Providers.AgentRunStatus.Failed));
        await shell.Usage.Quota.CodexReadAfterRun;
        Assert.Equal(2, shell.Codex.Reads);
    }

    [Fact]
    public async Task Without_the_opt_in_the_end_of_a_codex_run_launches_nothing()
    {
        var shell = Create();

        shell.RunEnds.Notify(new AgentStudio.Application.Providers.AgentRunEnded(AgentStudio.Domain.Providers.ProviderId.CodexCli, AgentStudio.Application.Providers.AgentRunStatus.Succeeded));
        await shell.Usage.Quota.CodexReadAfterRun;

        Assert.Equal(0, shell.Codex.Reads);
    }

    [Fact]
    public async Task The_end_of_a_run_derives_and_exports_its_memory_and_the_open_memory_tab_shows_it()
    {
        var shell = Create();
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await RunActivityAsync(shell.Main, "memory");
        await RunActivityAsync(shell.Main, "orchestrator");
        var monitor = Assert.Single(shell.Main.Documents.OfType<OrchestratorDocumentViewModel>()).Monitor;
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        var recallsBefore = shell.Memory.Recalls.Count;

        // Opening a folder without storage derived nothing.
        Assert.Empty(shell.Derivation.Derived);

        monitor.Attach(run);
        run.Finish(new RunResult(run.Id, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Todo hecho"));
        await monitor.Listening;

        Assert.Equal(shell.Main.ProjectPath, Assert.Single(shell.Derivation.Derived).RootPath);
        Assert.Equal(shell.Main.ProjectPath, Assert.Single(shell.Exporter.Exports).RootPath);
        // «Memoria» refreshes through its RefreshQueue, which can finish just after the run is reported as ended.
        await WaitUntilAsync(() => shell.Memory.Recalls.Count > recallsBefore, "The memory tab was not refreshed after the run.");
        Assert.StartsWith("Memoria exportada", shell.Main.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_opt_in_the_end_of_a_run_reads_no_quota()
    {
        var shell = Create();
        await shell.Main.OpenProjectAsync(_temp.CreateDirectory("calc"));
        await RunActivityAsync(shell.Main, "orchestrator");
        var monitor = Assert.Single(shell.Main.Documents.OfType<OrchestratorDocumentViewModel>()).Monitor;
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));

        monitor.Attach(run);
        run.Finish(new RunResult(run.Id, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Todo hecho"));
        await monitor.Listening;

        Assert.False(monitor.IsActive);
        Assert.Equal(0, shell.Codex.Reads);
    }

    private static (double, double, double) Thresholds(OrchestratorDocumentViewModel document)
    {
        var thresholds = document.Monitor.ContextThresholds;
        return (thresholds.Warning, thresholds.Compress, thresholds.Critical);
    }

    /// <summary>«Orquestador» documents without an engine whose monitor applies what it reads at once, on the test's thread.</summary>
    private sealed class InlineOrchestrators(TimeProvider time) : IOrchestratorDocumentFactory
    {
        public OrchestratorDocumentViewModel Create(string projectRoot) =>
            new(projectRoot, new OrchestrationSession(OrchestrationBackend.None), new FileCanvasLayoutStore(new ProjectStorageFiles()), time, new InlineSynchronizationContext());
    }
}
