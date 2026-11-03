using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views.Background;
using AgentStudio.Desktop.Views.Debate;
using AgentStudio.Desktop.Views.Observability;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The bars of tanda 2 (docs/18 P3) drawn without ever being shown (<see cref="OffscreenRender"/>), in every site: the status
/// bar of the main window, the monitor, the panel of the tray, the quota of «Consumo», the planning of the orchestrator and
/// the debate room. No window is shown, no HWND, no tray icon; synthetic data only. With <c>AGENTSTUDIO_RENDER_EVIDENCE</c>
/// set, the pictures are saved there.
/// </summary>
public sealed class LiveBarsRenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);
    private static readonly Color Strong = Color.FromRgb(0x5F, 0x73, 0x96);

    private static ContextObserved Step(long tokens, string model = "claude-sonnet-5-5") =>
        new(T0, model, MetricValue<long>.Exact(tokens, "claude assistant.usage"), MetricValue<long>.Missing("llega con el resultado"));

    private static FakeQuotaObservations Observations() => new()
    {
        Latest =
        {
            new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), null, T0.AddHours(2), T0.AddMinutes(-12), "rate_limit_event"),
            new QuotaObservation(ProviderId.ClaudeCode, "seven_day", MetricValue<double>.Exact(0.18, "claude rate_limit_event"), null, T0.AddDays(3), T0.AddMinutes(-12), "rate_limit_event"),
        },
    };

    private static QuotaReadResult CodexRead { get; } = new(QuotaReadStatus.Read, [
        new QuotaObservation(ProviderId.CodexCli, "codex/primary", MetricValue<double>.Exact(0.41, "codex account/rateLimits/read"), TimeSpan.FromHours(5), T0.AddHours(3), T0, "app-server") { Label = "Codex" },
        new QuotaObservation(ProviderId.CodexCli, "codex/secondary", MetricValue<double>.Exact(0.08, "codex account/rateLimits/read"), TimeSpan.FromDays(7), T0.AddDays(5), T0, "app-server") { Label = "Codex" },
    ])
    {
        PlanType = "plus",
    };

    /// <summary>A run in course: T1 Claude at ~47 % (base table), T2 a review on Codex, T3 Claude without a step yet, T4 pending.</summary>
    private static async Task<RunMonitorViewModel> RunInCourseAsync(TimeProvider clock)
    {
        var monitor = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(
            RunPhase.Running,
            FakeRun.View("T1"),
            FakeRun.View("T2", WorkTaskKind.Review),
            FakeRun.View("T3"),
            FakeRun.View("T4")) with { AgentRunsUsed = 4, MaxAgentRuns = 12 });
        monitor.Attach(run);
        OrchestrationEvent[] events =
        [
            new TaskQueuedEvent(T0, new("T1"), ProviderId.ClaudeCode),
            new TaskStatusChangedEvent(T0, new("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada"),
            new AgentActivityEvent(T0, new("T1"), Step(470_000)),
            new TaskQueuedEvent(T0, new("T2"), ProviderId.CodexCli),
            new TaskStatusChangedEvent(T0, new("T2"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada"),
            new TaskQueuedEvent(T0, new("T3"), ProviderId.ClaudeCode),
            new TaskStatusChangedEvent(T0, new("T3"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada"),
        ];
        run.Emit(events);
        for (var i = 0; i < 400 && (monitor.Tasks.Count(t => t.Status == WorkTaskStatus.Running) < 3 || monitor.Tasks.FirstOrDefault()?.LastContext is null); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return monitor;
    }

    [Fact]
    public async Task The_status_bar_of_the_main_window_renders_offscreen_with_the_context_the_quota_and_the_budget()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(T0);
        var monitor = await RunInCourseAsync(clock);

        var (drawn, bars, accent, strong, labels) = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var quota = new QuotaViewModel(Observations(), new FakeQuotaReader(() => CodexRead), clock) { CodexOptedIn = true };
            await quota.RefreshAsync();
            await quota.ReadCodexCommand.ExecuteAsync(null);
            var strip = new StatusStripViewModel(quota, clock, null);
            strip.Track(monitor);

            // The clock of the test stands still: the build the strip spaced by a quarter of a second is asked now.
            strip.Refresh();
            var vm = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), clock),
                new ExplorerViewModel(),
                new JournalViewModel(clock),
                new QuickOpenViewModel(),
                statusStrip: strip);
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            var root = (FrameworkElement)window.Content;
            var bitmap = OffscreenRender.Render(root, 1280, 800);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2-barra-de-estado.png");
            var strips = (ItemsControl)window.FindName("StatusStripBars");
            var shown = Descendants<MetricBar>(strips).Where(b => b.ActualWidth > 0).ToList();

            // Six bars do not fit in one line at 1280: they wrap to a second one, and none is cut.
            Assert.True(strips.ActualHeight > 30, $"The bars take {strips.ActualHeight:F0} px: they did not wrap.");
            Assert.All(shown, b => Assert.True(b.TranslatePoint(new Point(b.ActualWidth, 0), root).X <= 1280, $"«{b.Reading?.Label}» is cut."));
            return (
                OffscreenRender.CountUnlike(bitmap, Background),
                shown.Count,
                OffscreenRender.Count(bitmap, Accent),
                OffscreenRender.Count(bitmap, Strong),
                shown.Select(b => b.Reading?.Label).ToList());
        });

        Assert.True(drawn > 100_000, $"Only {drawn} pixels differ from the background: the window was not drawn.");
        Assert.Equal(["Contexto · T1, la más llena de 3", "Claude 5 h", "Claude 7 días", "Codex 5 h", "Codex 7 días", "Presupuesto"], labels);
        Assert.Equal(6, bars);
        Assert.True(accent > 150, $"Only {accent} pixels have the accent: the quota and the budget bars were not drawn.");
        Assert.True(strong > 150, $"Only {strong} pixels have the outline of the tracks.");
    }

    [Fact]
    public async Task The_monitor_renders_offscreen_with_a_context_bar_for_every_task_with_an_agent_and_the_budget()
    {
        var clock = new FakeTimeProvider(T0);
        var monitor = await RunInCourseAsync(clock);

        var (drawn, success, bars, texts) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new RunMonitorView { DataContext = monitor };
            var bitmap = OffscreenRender.Render(view, 1100, 640);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2-monitor.png");
            var all = Descendants<MetricBar>(view).Where(b => b.ActualWidth > 0).ToList();
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Success), all.Count, all.Select(b => b.Reading?.ValueText).ToList());
        });

        Assert.True(drawn > 30_000, $"Only {drawn} pixels differ from the surface: the monitor was not drawn.");
        Assert.True(success > 100, $"Only {success} pixels have the colour of «sano»: the context of T1 was not drawn.");
        Assert.Equal(5, bars);
        Assert.Equal(["4 de 12", "~47 %", "no expuesto", "sin observaciones todavía", "sin empezar"], texts);
    }

    [Fact]
    public async Task The_panel_of_the_tray_renders_offscreen_with_bars_of_context_budget_and_quota()
    {
        var clock = new FakeTimeProvider(T0);
        var monitor = await RunInCourseAsync(clock);
        var panel = await WpfHost.RunAsync(async () =>
        {
            var work = new BackgroundWorkRegistry(clock, null, SynchronizationContext.Current, Timeout.InfiniteTimeSpan);
            work.TrackRun(AgentStudio.Tests.Desktop.Background.BackgroundSamples.RootA, monitor, () => AgentStudio.Tests.Desktop.Background.BackgroundSamples.ProjectA);
            var quota = new QuotaViewModel(Observations(), null, clock);
            var model = new StatusPanelViewModel(work, clock, new StatusPanelSources(Quota: quota), openApp: () => { });
            await model.RefreshAsync();
            return model;
        });

        var (drawn, bars) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new StatusPanelView { DataContext = panel };
            var bitmap = OffscreenRender.Render(view, 540, 760);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2-panel-bandeja.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), Descendants<MetricBar>(view).Count(b => b.ActualWidth > 0));
        });

        Assert.True(drawn > 20_000, $"Only {drawn} pixels differ from the panel: it was not drawn.");

        // Three tasks in course, the budget and the two windows of Claude.
        Assert.Equal(6, bars);
    }

    [Fact]
    public async Task The_quota_of_consumo_renders_offscreen_with_a_bar_in_every_window()
    {
        var clock = new FakeTimeProvider(T0);
        var bars = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var quota = new QuotaViewModel(Observations(), new FakeQuotaReader(() => CodexRead), clock) { CodexOptedIn = true };
            await quota.RefreshAsync();
            await quota.ReadCodexCommand.ExecuteAsync(null);
            var view = new QuotaView { DataContext = quota };
            var bitmap = OffscreenRender.Render(view, 460, 620);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2-consumo-cuota.png");
            return Descendants<MetricBar>(view).Where(b => b.ActualWidth > 0).Select(b => b.Reading?.ValueText).ToList();
        });

        Assert.Equal(["47 %", "18 %", "41 %", "8 %"], bars);
    }

    [Fact]
    public async Task The_planning_renders_offscreen_with_the_context_of_the_orchestrator_as_a_bar()
    {
        var reading = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var engine = new FakeOrchestrator
            {
                Plan = (_, progress, _) =>
                {
                    progress?.Report(new ContextObserved(T0, "claude-opus-5-5", MetricValue<long>.Exact(312_000, "claude assistant.usage"), MetricValue<long>.Missing("llega con el resultado")));
                    progress?.Report(new ToolInvoked(T0, "Read", "src/Calc/Calc.cs"));
                    return Task.FromResult(new PlanProposal(new RunId("20261021-c4a1f2"), PlanFixtures.Plan(), [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null));
                },
            };
            var model = new OrchestratorDocumentViewModel(@"C:\proyectos\calc", new OrchestrationSession(Backends.Complete(engine)), new NoCanvasLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
            await model.OpenAsync();
            model.IsPreparationExpanded = false;
            model.RequestText = "Añadir Calc.Multiply(a, b) con sus pruebas";
            await model.RequestPlanCommand.ExecuteAsync(null);
            var view = new OrchestratorView { DataContext = model };
            var bitmap = OffscreenRender.Render(view, 1500, 980);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2-planificacion.png");
            return Descendants<MetricBar>(view).Single(b => b.Reading?.Label == "Contexto del orquestador" && b.ActualWidth > 0).Reading;
        });

        Assert.Equal("~31 %", reading!.ValueText);
        Assert.Equal("estimado por Agent Studio", reading.Origin);
    }

    [Fact]
    public void The_debate_room_renders_offscreen_with_its_tokens_and_runs_as_bars()
    {
        var (drawn, texts) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = new DebateRoomViewModel(new AgentStudio.Tests.Memory.FakeClock(DebateSamples.Now), new InlineSynchronizationContext());
            var context = new DebateRoomContext("¿Procesos aislados por agente o un único proceso con hilos?", DebateSamples.Requirements, DebateJudgingKind.CrossVendorPanel, [VendorId.Anthropic, VendorId.OpenAI], [DebateSamples.ClaudeJudge, DebateSamples.CodexJudge]);
            model.Attach(new FakeDebateRun(DebateSamples.Snapshot(turnsInFlight: 2, contributions: DebateSamples.RoundOne, tokens: DebateSamples.Tokens(184_000))), DebateSamples.Project, context);
            var view = new DebateRoomView { DataContext = model };
            var bitmap = OffscreenRender.Render(view, 1100, 760);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2-sala-debate.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), Descendants<MetricBar>(view).Where(b => b.ActualWidth > 0).Select(b => b.Reading?.ValueText).ToList());
        });

        Assert.True(drawn > 20_000, $"Only {drawn} pixels differ from the surface: the room was not drawn.");
        Assert.Equal(["184.000 de 400.000 tokens", "3 de 10"], texts);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private sealed class NoCanvasLayout : AgentStudio.Desktop.Orchestrator.Canvas.ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public AgentStudio.Desktop.Orchestrator.Canvas.CanvasLayout Load(string projectRoot) => AgentStudio.Desktop.Orchestrator.Canvas.CanvasLayout.Empty;

        public void Save(string projectRoot, AgentStudio.Desktop.Orchestrator.Canvas.CanvasLayout layout)
        {
        }
    }
}
