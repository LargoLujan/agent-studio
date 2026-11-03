using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Context;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Controls;

/// <summary>
/// The common bar (docs/18 P3): drawn offscreen in each of its states (exact, estimated with its pattern, every level,
/// empty with «no expuesto»), and read by UI Automation as a progress bar named with its whole line, with the RangeValue
/// pattern only while it has a value. No window is shown.
/// </summary>
public sealed class MetricBarTests
{
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Error = Color.FromRgb(0xFF, 0x5C, 0x6C);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);
    private static readonly ContextHealthThresholds Thresholds = new();

    private static MetricBarReading Exact(long used) =>
        MetricBars.Context("Contexto", "claude-sonnet-5-5", MetricValue<long>.Exact(used, "claude assistant.usage"), MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), Thresholds);

    private static MetricBarReading Estimated(long used) =>
        MetricBars.Context("Contexto", "claude-sonnet-5-5", MetricValue<long>.Exact(used, "claude assistant.usage"), ModelWindowTable.Current.WindowFor("claude-sonnet-5-5"), Thresholds);

    [Fact]
    public void Every_state_renders_offscreen_with_the_colour_of_its_level_and_an_empty_track_without_a_value()
    {
        var (drawn, success, warning, error, accent, fills) = WpfHost.Run(() =>
        {
            MetricBarReading[] readings =
            [
                Exact(300_000),
                Exact(750_000),
                Exact(900_000),
                Exact(980_000),
                Estimated(470_000),
                MetricBars.Budget("Presupuesto", 3, 12),
                MetricBars.Quota("Claude · 5 horas", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), "vista hace 12 min"),
                MetricBars.Empty("Contexto", "no expuesto", MetricOrigin.NotExposed),
                MetricBars.NoObservation("Contexto"),
            ];
            var panel = new StackPanel { Background = new SolidColorBrush(Surface) };
            foreach (var reading in readings)
            {
                panel.Children.Add(new MetricBar { Reading = reading, Margin = new Thickness(8, 4, 8, 4), LabelWidth = 120 });
            }

            panel.Children.Add(new MetricBar { Reading = MetricBars.Budget("Presupuesto", 3, 12), IsCompact = true, TrackWidth = 60, Margin = new Thickness(8, 4, 8, 4) });
            var bitmap = OffscreenRender.Render(panel, 640, 320);
            OffscreenRender.SaveIfRequested(bitmap, "tanda2-barra-comun.png");
            var bars = Descendants<MetricBar>(panel).ToList();
            return (
                OffscreenRender.CountUnlike(bitmap, Surface),
                OffscreenRender.Count(bitmap, Success),
                OffscreenRender.Count(bitmap, Warning),
                OffscreenRender.Count(bitmap, Error),
                OffscreenRender.Count(bitmap, Accent),
                bars.Select(b => ((FrameworkElement)b.FindName("Fill")).Visibility).ToList());
        });

        Assert.True(drawn > 20_000, $"Only {drawn} pixels differ from the surface: the bars were not drawn.");
        Assert.True(success > 100, $"Only {success} pixels have the colour of «sano».");
        Assert.True(warning > 100, $"Only {warning} pixels have the colour of «aviso» and «compresión recomendada».");
        Assert.True(error > 100, $"Only {error} pixels have the colour of «crítico».");
        Assert.True(accent > 100, $"Only {accent} pixels have the accent (the budget and the quota).");

        // The two bars without a value draw no fill at all: never a bar at zero.
        Assert.Equal(Visibility.Collapsed, fills[7]);
        Assert.Equal(Visibility.Collapsed, fills[8]);
        Assert.Equal(Visibility.Visible, fills[0]);
    }

    [Fact]
    public void An_estimate_carries_the_pattern_and_an_exact_value_does_not()
    {
        var (estimate, exact) = WpfHost.Run(() =>
        {
            var estimated = new MetricBar { Reading = Estimated(470_000) };
            var reported = new MetricBar { Reading = Exact(470_000) };
            return (((FrameworkElement)estimated.FindName("Hatch")).Visibility, ((FrameworkElement)reported.FindName("Hatch")).Visibility);
        });

        Assert.Equal(Visibility.Visible, estimate);
        Assert.Equal(Visibility.Collapsed, exact);
    }

    [Fact]
    public void Ui_automation_reads_a_progress_bar_named_with_its_whole_line_and_its_value_while_it_has_one()
    {
        var (type, name, status, help, value, maximum, readOnly, children) = WpfHost.Run(() =>
        {
            var bar = new MetricBar { Reading = Estimated(470_000) };
            var peer = UIElementAutomationPeer.CreatePeerForElement(bar);
            var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
            return (peer.GetAutomationControlType(), peer.GetName(), peer.GetItemStatus(), peer.GetHelpText(), range.Value, range.Maximum, range.IsReadOnly, peer.GetChildren());
        });

        Assert.Equal(AutomationControlType.ProgressBar, type);
        Assert.StartsWith("Contexto: ~47 % · estimado por Agent Studio · sano", name, StringComparison.Ordinal);
        Assert.Equal("estimado por Agent Studio", status);
        Assert.Contains("tabla base", help, StringComparison.Ordinal);
        Assert.Equal(47, value, 6);
        Assert.Equal(100, maximum);
        Assert.True(readOnly);
        Assert.Null(children);
    }

    [Fact]
    public void A_bar_without_a_value_has_no_range_value_and_is_never_read_as_zero()
    {
        var (pattern, name) = WpfHost.Run(() =>
        {
            var bar = new MetricBar { Reading = MetricBars.Empty("Contexto", "no expuesto", MetricOrigin.NotExposed) };
            var peer = UIElementAutomationPeer.CreatePeerForElement(bar);
            return (peer.GetPattern(PatternInterface.RangeValue), peer.GetName());
        });

        Assert.Null(pattern);
        Assert.Equal("Contexto: no expuesto", name);
    }

    /// <summary>
    /// V37, seen live: the bars a screen collapses because there is nothing to show (a task without an agent, a quota window
    /// without a bar) were read as «: sin dato». Hidden or collapsed, a bar is neither a control nor content; shown without a
    /// reading it is still read, as «sin dato».
    /// </summary>
    [Fact]
    public void A_hidden_bar_is_not_read_and_one_shown_without_a_reading_says_sin_dato()
    {
        var (hidden, hiddenWithReading, invisible, empty, named, shown) = WpfHost.Run(() =>
        {
            static (string Name, bool Control, bool Content) Read(MetricBar bar)
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(bar);
                return (peer.GetName(), peer.IsControlElement(), peer.IsContentElement());
            }

            var named = new MetricBar();
            AutomationProperties.SetName(named, "Contexto de T1");
            return (
                Read(new MetricBar { Visibility = Visibility.Collapsed }),
                Read(new MetricBar { Reading = Exact(10), Visibility = Visibility.Collapsed }),
                Read(new MetricBar { Visibility = Visibility.Hidden }),
                Read(new MetricBar()),
                Read(named),
                Read(new MetricBar { Reading = Exact(10_000) }));
        });

        Assert.Equal((string.Empty, false, false), hidden);
        Assert.False(hiddenWithReading.Control);
        Assert.False(hiddenWithReading.Content);
        Assert.Equal((string.Empty, false, false), invisible);
        Assert.Equal(("sin dato", true, true), empty);
        Assert.Equal(("Contexto de T1", true, true), named);
        Assert.True(shown.Control);
        Assert.StartsWith("Contexto: 1 %", shown.Name, StringComparison.Ordinal);
    }

    /// <summary>V37 in the monitor: a tool task has no context bar; its collapsed bar is not in the control view of the tasks.</summary>
    [Fact]
    public async Task In_the_monitor_the_collapsed_bar_of_a_task_without_an_agent_is_not_a_control()
    {
        var monitor = new AgentStudio.Desktop.Orchestrator.Monitor.RunMonitorViewModel(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(AgentStudio.Tests.Domain.TestData.T0), new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(
            AgentStudio.Application.Orchestration.RunPhase.Running,
            FakeRun.View("T1", AgentStudio.Domain.Work.WorkTaskKind.ToolRun, status: AgentStudio.Domain.Work.WorkTaskStatus.Running)));
        monitor.Attach(run);
        for (var i = 0; i < 400 && monitor.Tasks.Count == 0; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        var bars = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new AgentStudio.Desktop.Views.Orchestrator.RunMonitorView { DataContext = monitor };
            OffscreenRender.Render(view, 1100, 640);
            return Descendants<MetricBar>(view)
                .Where(b => b.Reading is null)
                .Select(b => UIElementAutomationPeer.CreatePeerForElement(b))
                .Select(p => (p.GetName(), p.IsControlElement()))
                .ToList();
        });

        Assert.NotEmpty(bars);
        Assert.All(bars, b => Assert.Equal((string.Empty, false), b));
    }

    [Fact]
    public void The_value_cannot_be_set_through_ui_automation()
    {
        WpfHost.Run(() =>
        {
            var bar = new MetricBar { Reading = MetricBars.Budget("Presupuesto", 3, 12) };
            var range = (IRangeValueProvider)UIElementAutomationPeer.CreatePeerForElement(bar).GetPattern(PatternInterface.RangeValue);
            Assert.Throws<ElementNotEnabledException>(() => range.SetValue(5));
            Assert.Equal(3, range.Value);
            Assert.Equal(12, range.Maximum);
        });
    }

    [Fact]
    public void A_name_given_by_the_screen_wins_over_the_line()
    {
        var name = WpfHost.Run(() =>
        {
            var bar = new MetricBar { Reading = Exact(10) };
            AutomationProperties.SetName(bar, "Contexto de T1");
            return UIElementAutomationPeer.CreatePeerForElement(bar).GetName();
        });

        Assert.Equal("Contexto de T1", name);
    }

    [Fact]
    public void The_compact_form_says_the_origin_in_one_word_and_keeps_the_whole_line_in_the_tooltip()
    {
        var (text, tooltip) = WpfHost.Run(() =>
        {
            var bar = new MetricBar { Reading = Estimated(470_000), IsCompact = true };
            var line = (TextBlock)bar.FindName("ValueLine");
            return (string.Concat(line.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)), bar.ToolTip as string);
        });

        Assert.Equal("~47 % estimado · sano", text);
        Assert.StartsWith("Contexto: ~47 % · estimado por Agent Studio", tooltip, StringComparison.Ordinal);
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
