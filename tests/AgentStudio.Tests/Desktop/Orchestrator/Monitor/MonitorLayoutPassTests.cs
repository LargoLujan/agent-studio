using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator.Monitor;

/// <summary>
/// F13-7 (docs/17 D-16): the layout and the render of the whole monitor, with 40 tasks and 40 agents whose context changes at once,
/// built offscreen on the window-less host and never shown. It is the pass the UI thread runs after a burst of events (Render priority):
/// measure, arrange and draw of everything that changed. Each round changes every task, waits for the grouped repaint to settle and times
/// the pass; the limit is asked of the median of the rounds, and the slowest is reported, because the suite runs other tests on the same
/// processors.
/// </summary>
public sealed class MonitorLayoutPassTests
{
    /// <summary>What a pass may take, without load, for 40 tasks changing at once (F13-7).</summary>
    public const double LimitMilliseconds = 50;

    /// <summary>
    /// How much more a pass may take when other tests share the processors: the median is judged with this margin and the number is in the message.
    /// The limit of F13-7 itself is <see cref="LimitMilliseconds"/>; the margin is documented in docs/04 section 21.10.
    /// </summary>
    public const double LoadMargin = 2;

    // Measurement, outside the CI like the pass of the canvas (F13-7): in the whole suite the rounds went from 52 to 411 ms on
    // one machine, so as a gate it would only tell how busy the processors were.
    [Fact]
    [Trait("Category", "Measurement")]
    public async Task A_full_layout_and_render_pass_of_the_monitor_with_forty_tasks_changing_at_once_stays_within_the_limit()
    {
        const int Nodes = 40;
        var (median, slowest, rounds) = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var tasks = Enumerable.Range(1, Nodes).Select(i => View("T" + i, "exec" + i, WorkTaskStatus.Running)).ToArray();
            var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, tasks));
            var monitor = new RunMonitorViewModel(TimeProvider.System, new DispatcherSynchronizationContext(dispatcher));
            monitor.Roster = [.. Enumerable.Range(1, Nodes).Select(i => AgentDef("exec" + i, "Ejecutor " + i))];
            var view = new RunMonitorView { DataContext = monitor };
            monitor.Attach(run);
            var size = new Size(1440, 900);

            // A warm-up: the first layout builds the templates and loads the code.
            run.Emit(Step("T1", 1_000));
            await Task.Delay(300);
            Pass(view, size);
            await Task.Delay(300);

            var passes = new List<double>();
            var rng = new Random(13);
            for (var round = 0; round < 7; round++)
            {
                run.Emit([.. Enumerable.Range(1, Nodes).Select(n => (OrchestrationEvent)Step("T" + n, rng.Next(1_000, 150_000) + (round * 1_000)))]);

                // The events are applied and the grouped change is published (250 ms): the pass that follows is what the screen does with it.
                await Task.Delay(500);
                passes.Add(Pass(view, size));
            }

            var ordered = passes.Order().ToList();
            return (ordered[ordered.Count / 2], ordered[^1], string.Join(", ", passes.Select(p => p.ToString("F0"))));
        });

        Assert.True(
            median <= LimitMilliseconds * LoadMargin,
            $"A pass of layout and render of the monitor with {Nodes} tasks changing at once took {median:F0} ms (median; limit {LimitMilliseconds} ms with a margin of x{LoadMargin} for load; slowest {slowest:F0} ms; rounds {rounds}).");
    }

    /// <summary>Measure, arrange, lay out and draw the view as the UI thread does after a change; the time of the whole pass.</summary>
    private static double Pass(FrameworkElement view, Size size)
    {
        var started = Stopwatch.GetTimestamp();
        view.InvalidateMeasure();
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
