using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views.Observability;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The retouches of tanda 2b (docs/18 §3, the walk of 2026-11-03), drawn without ever being shown (<see cref="OffscreenRender"/>):
/// the summary of a finished run once (V35), the reset of a quota once (V36), the status bar in one line at 1920 px during a
/// run (V38) and the context peaks of «Consumo» as bars (V39). No window is shown; synthetic data only.
/// </summary>
public sealed class Tanda2bRenderTests
{
    private static ContextObserved Step(long tokens, string model = "claude-haiku-4-5") =>
        new(T0, model, MetricValue<long>.Exact(tokens, "claude assistant.usage"), MetricValue<long>.Missing("llega con el resultado"));

    [Fact]
    public async Task V35_the_monitor_of_a_run_that_finished_well_says_its_summary_once()
    {
        const string Summary = "Terminó bien: 3 tareas completadas y 0 omitidas.";
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1"), FakeRun.View("T2"), FakeRun.View("T3")));
        monitor.Attach(run);
        var result = new RunResult(new("20261103-abc123"), RunPhase.Succeeded, FakeRun.Healthy, null, null, Summary);
        run.Emit(new RunPhaseChangedEvent(T0, RunPhase.Succeeded, Summary), new RunFinishedEvent(T0, result));
        run.Finish(result);
        await monitor.Listening;

        var shown = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new RunMonitorView { DataContext = monitor };
            OffscreenRender.Render(view, 1100, 640);
            return Descendants<TextBlock>(view).Count(t => TextOf(t) == Summary && Shown(t));
        });

        Assert.Equal(1, shown);
    }

    [Fact]
    public async Task V36_a_quota_window_whose_reset_passed_says_when_it_reset_once()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        var resetAt = new DateTimeOffset(2026, 10, 30, 19, 20, 0, TimeSpan.Zero);
        var observations = new FakeQuotaObservations
        {
            Latest = { new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.92, "claude rate_limit_event"), null, resetAt, resetAt.AddHours(-3), "rate_limit_event") },
        };

        var texts = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var quota = new QuotaViewModel(observations, null, clock);
            await quota.RefreshAsync();
            var view = new QuotaView { DataContext = quota };
            OffscreenRender.Render(view, 460, 620);
            return Descendants<TextBlock>(view).Where(Shown).Select(TextOf).ToList();
        });

        Assert.Single(texts, t => t.Contains("se reinició", StringComparison.Ordinal));
        Assert.Contains("se reinició el 30/10 a las 19:20, sin dato nuevo", texts);
    }

    /// <summary>The path of the project of the person, as the status bar shows it: the longest one seen live.</summary>
    private const string RealProject = @"C:\Personal Projects\Agent Studio";

    /// <summary>
    /// V38, seen live: during a run, on a screen 1920 px wide, the budget went down to a second line of the status bar. The
    /// four bars of a run (the context of its task, Claude's 5 hours and 7 days and the budget) fit in one line next to the
    /// path of the project of the person and the version, at the usual scales of such a screen (WPF lays out 1920 px at
    /// 100 %, 125 % and 150 % in 1920, 1536 and 1280 device-independent pixels), each still with its figure, its origin, its
    /// level and its age.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    public async Task V38_during_a_run_the_status_bar_fits_in_one_line_on_a_screen_1920_px_wide(int scale)
    {
        var shown = await StatusBarAsync(scale, worst: false);

        Assert.Equal(["Contexto · T1", "Claude 5 h", "Claude 7 días", "Presupuesto"], shown.Labels);
        Assert.True(shown.Lines == 1, shown.Why($"{scale} %"));
        Assert.Empty(shown.Cut);

        // Nothing the revision of WP-F asked is lost: the origin, the level and the age go next to each figure.
        Assert.Equal(["~1 % estimado · sano", "57 % exacto · hace 14 min", "18 % exacto · hace 14 min", "2 de 12 exacto"], shown.Texts);
    }

    /// <summary>
    /// V38 with the worst texts seen: the path of the person, a loaded run (the fullest of three tasks near compression), a
    /// window of the quota that reset with no new figure and a budget well spent. One line at 100 % and 125 %; at 150 % the
    /// four do not fit next to the path and the version by any reasonable means, and the bars go to a second line, whole and
    /// in order (docs/18 §3, the limit of V38). Nothing is ever cut.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    public async Task V38_with_the_worst_texts_seen_the_status_bar_fits_in_one_line_but_at_150_and_never_cuts_a_bar(int scale)
    {
        var shown = await StatusBarAsync(scale, worst: true);

        Assert.Equal(["Contexto · T2, la más llena de 3", "Claude 5 h", "Claude 7 días", "Presupuesto"], shown.Labels);
        // One line where it fits; at 150 % at most two. The exact count at the edge depends on the width of the version
        // text (its commit hash), so the limit is «at most», never «exactly».
        Assert.True(scale < 150 ? shown.Lines == 1 : shown.Lines <= 2, shown.Why($"{scale} %"));
        Assert.Empty(shown.Cut);
        Assert.Equal(["~87 % estimado · compresión recomendada", "se reinició · sin dato", "57 % exacto · hace 14 min", "9 de 12 exacto"], shown.Texts);
    }

    /// <summary>
    /// V38 in the windows the review measured, not maximized: 1859 px at 125 % (1487 device-independent pixels), where even
    /// the worst texts fit in one line, and 1850 px at 150 % (1233), where the four do not fit beside the path and the version
    /// and go whole to a second line (docs/18 §3, the limit of V38). The bars never cut.
    /// </summary>
    [Theory]
    [InlineData(1487, false, 1)]
    [InlineData(1487, true, 1)]
    [InlineData(1233, false, 2)]
    [InlineData(1233, true, 2)]
    public async Task V38_in_a_window_not_maximized_the_bars_never_cut_and_wrap_whole(int width, bool worst, int lines)
    {
        var shown = await StatusBarAsync(width, $"tanda2b-barra-de-estado-{width}{(worst ? "-peor" : string.Empty)}.png", worst);

        // At most the documented lines: the exact count at the edge depends on the width of the version text.
        Assert.True(shown.Lines <= lines, shown.Why($"{width} DIP"));
        Assert.Empty(shown.Cut);
    }

    /// <summary>
    /// The re-review of V38: the message of the status bar is cut at 180 px, and whoever uses only the keyboard cannot reach
    /// its tooltip. A screen reader reads it whole (its name is the whole text) and is told when it changes (polite).
    /// </summary>
    [Fact]
    public void V38_the_message_cut_at_180_px_is_read_whole_and_announced()
    {
        const string Message = "La ejecución 20261103-abc123 se reanudó sola, pero su pestaña estaba ocupada. La sigue la bandeja del sistema.";
        using var temp = new TempDirectory();
        var (width, live, name) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var clock = new FakeTimeProvider(T0);
            var vm = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), clock),
                new ExplorerViewModel(),
                new JournalViewModel(clock),
                new QuickOpenViewModel());
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            var root = (FrameworkElement)window.Content;
            vm.StatusMessage = Message;
            OffscreenRender.Render(root, 1280, 700);
            var text = (TextBlock)window.FindName("StatusMessageText");
            return (
                text.ActualWidth,
                System.Windows.Automation.AutomationProperties.GetLiveSetting(text),
                System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(text).GetName());
        });

        Assert.True(width <= 180.5, $"The message takes {width:0} px.");
        Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, live);
        Assert.Equal(Message, name);
    }

    /// <summary>The status bar of the main window during a run, drawn off screen at <paramref name="scale"/> % of a screen 1920 px wide.</summary>
    private static Task<StatusBarShown> StatusBarAsync(int scale, bool worst) =>
        StatusBarAsync(1920d * 100 / scale, $"tanda2b-barra-de-estado-1920-al-{scale}{(worst ? "-peor" : string.Empty)}.png", worst);

    /// <summary>The status bar of the main window during a run, drawn off screen <paramref name="width"/> device-independent pixels wide.</summary>
    private static async Task<StatusBarShown> StatusBarAsync(double width, string picture, bool worst)
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(T0);
        var monitor = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        (string Task, long Tokens)[] tasks = worst ? [("T1", 100_000), ("T2", 870_000), ("T3", 300_000)] : [("T1", 2_000)];
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, [.. tasks.Select(t => FakeRun.View(t.Task, status: WorkTaskStatus.Running))]) with
        {
            AgentRunsUsed = worst ? 9 : 2,
            MaxAgentRuns = 12,
        });
        monitor.Attach(run);
        run.Emit([.. tasks.Select(t => (OrchestrationEvent)new AgentActivityEvent(T0, new(t.Task), worst ? Step(t.Tokens, "claude-sonnet-5-5") : Step(t.Tokens)))]);
        for (var i = 0; i < 400 && monitor.Tasks.Count(t => t.LastContext is not null) < tasks.Length; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        var observations = new FakeQuotaObservations
        {
            Latest =
            {
                worst
                    ? new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.92, "claude rate_limit_event"), null, T0.AddMinutes(-30), T0.AddHours(-3), "rate_limit_event")
                    : new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.57, "claude rate_limit_event"), null, T0.AddHours(2), T0.AddMinutes(-14), "rate_limit_event"),
                new QuotaObservation(ProviderId.ClaudeCode, "seven_day", MetricValue<double>.Exact(worst ? 0.57 : 0.18, "claude rate_limit_event"), null, T0.AddDays(3), T0.AddMinutes(-14), "rate_limit_event"),
            },
        };

        return await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var quota = new QuotaViewModel(observations, null, clock);
            await quota.RefreshAsync();
            var strip = new StatusStripViewModel(quota, clock, null);
            strip.Track(monitor);
            strip.Refresh();
            var vm = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), clock),
                new ExplorerViewModel(),
                new JournalViewModel(clock),
                new QuickOpenViewModel(),
                statusStrip: strip)
            {
                ProjectPath = RealProject,
            };
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            var root = (FrameworkElement)window.Content;
            var bitmap = OffscreenRender.Render(root, width, width * 1040 / 1920);
            OffscreenRender.SaveIfRequested(bitmap, picture);
            var strips = (ItemsControl)window.FindName("StatusStripBars");
            var bars = Descendants<MetricBar>(strips).Where(b => b.ActualWidth > 0).ToList();
            var rows = bars.Select(b => Math.Round(b.TranslatePoint(new Point(0, 0), root).Y)).Distinct().Count();
            var lines = bars.Select(b => (TextBlock)b.FindName("ValueLine")).ToList();

            // A bar is cut when it goes past the window or its words do not fit in it (an ellipsis is not «fits»): the words
            // measured without a limit are wider than what they were given.
            var cut = bars.Zip(lines)
                .Where(p => p.First.TranslatePoint(new Point(p.First.ActualWidth, 0), root).X > width + 0.5 || Natural(p.Second) > p.Second.ActualWidth + 0.5)
                .Select(p => p.First.Reading?.Label ?? string.Empty)
                .ToList();
            var path = (TextBlock)window.FindName("StatusMessageText");
            return new StatusBarShown(
                rows,
                bars.Select(b => b.Reading?.Label).ToList(),
                lines.Select(TextOf).ToList(),
                bars.Select(b => Math.Round(b.ActualWidth)).ToList(),
                cut,
                Math.Round(path.ActualWidth),
                $"{TextOf(path)}; version «{((TextBlock)window.FindName("VersionText")).Text}» {Math.Round(((FrameworkElement)window.FindName("VersionText")).ActualWidth)}; strip {Math.Round(strips.ActualWidth)} of {Math.Round(((FrameworkElement)strips.Parent).ActualWidth)}");
        });
    }

    /// <summary>The width a text block's words take without a limit, without its margins.</summary>
    private static double Natural(TextBlock text)
    {
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return text.DesiredSize.Width - text.Margin.Left - text.Margin.Right;
    }

    /// <summary>What the status bar showed: in how many lines, each bar's label, words and width, the bars cut, and the path beside them.</summary>
    private sealed record StatusBarShown(int Lines, List<string?> Labels, List<string> Texts, List<double> Widths, List<string> Cut, double PathWidth, string Path)
    {
        public string Why(string at) =>
            $"At {at} the bars take {Lines} lines; widths {string.Join(", ", Widths)} (path «{Path}» {PathWidth}); texts: {string.Join(" | ", Texts)}.";
    }

    [Fact]
    public async Task V39_the_context_peaks_of_consumo_are_bars()
    {
        var (peaks, groups, attempts) = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var clock = new AgentStudio.Tests.Memory.FakeClock(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2)));
            var store = new RecordingSettingsStore();
            var model = new UsageDashboardViewModel(
                new FakeUsageReport(() => UsageSamples.Day1),
                new FakeUsageExporter(),
                new FakeSaveFilePicker(null),
                store,
                clock,
                new QuotaViewModel(new FakeQuotaObservations(), null, clock),
                new ObservabilitySettingsViewModel(store));
            await model.SetProjectAsync(@"C:\proyectos\calc");
            var view = new UsageDashboardView { DataContext = model };
            var bitmap = OffscreenRender.Render(view, 1500, 1180);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2b-consumo-contexto-maximo.png");
            // The groups and the attempts; the rows of the axes of Phase 9 (their own list) have a bar too, and are counted apart.
            var axes = (System.Windows.Media.Visual)view.FindName("AxesList");
            var all = Descendants<MetricBar>(view).Where(b => b.ActualWidth > 0 && b.Reading?.Label == UsageDashboardViewModel.PeakLabel).ToList();
            var bars = all.Where(b => !b.IsDescendantOf(axes)).Select(b => b.Reading!.ValueText).ToList();
            Assert.Equal(model.Axes.Sum(a => a.Rows.Count), all.Count - bars.Count);
            return (bars, model.Groups.Count, model.Attempts.Count);
        });

        Assert.Equal(groups + attempts, peaks.Count);
        Assert.Equal(["30 %", "no expuesto", "~30 %", "sin dato"], peaks);
    }

    /// <summary>What a text block says: its text, or the text of its runs.</summary>
    private static string TextOf(TextBlock text) =>
        text.Text is { Length: > 0 } plain ? plain : string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));

    /// <summary>A text block on screen: visible itself and in every parent, with some width.</summary>
    private static bool Shown(TextBlock text)
    {
        for (DependencyObject? node = text; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return text.ActualWidth > 0 && TextOf(text).Length > 0;
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
}
