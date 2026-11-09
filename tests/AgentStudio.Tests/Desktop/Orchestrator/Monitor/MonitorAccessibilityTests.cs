using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Controls.Info;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Monitor;

/// <summary>
/// F13-8 and F13-7 (docs/17 D-15 to D-17): the cards of the monitor are accessible (whole names, announced changes, bars with a
/// value for UI Automation), and a burst of events does not block the UI thread for more than 50 ms. Built offscreen, never shown.
/// </summary>
public sealed class MonitorAccessibilityTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    [Fact]
    public async Task The_card_of_an_agent_has_a_whole_name_announces_its_state_and_its_bars_have_a_value_for_ui_automation()
    {
        var inner = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, View("T1", "exec1", WorkTaskStatus.Running), View("T2", "exec2", WorkTaskStatus.Running)));
        var run = new AnsweringRun(inner);
        var time = new FakeTimeProvider(T0);
        var model = new RunMonitorViewModel(time, new InlineSynchronizationContext());
        model.Roster = [AgentDef("exec1", "Ejecutor 1"), AgentDef("exec2", "Ejecutor 2")];
        model.Attach(run);
        inner.Emit(
            Step("T1", 94_000),
            Move("T2", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "Espera tu respuesta"),
            new QuestionPendingEvent(T0, Pending("T2", "exec2")));
        await WaitUntilAsync(() => model.HasPendingQuestions);
        time.Advance(TimeSpan.FromSeconds(1));

        var (cards, bars, info) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new RunMonitorView { DataContext = model };
            var bitmap = OffscreenRender.Render(view, 1440, 520);
            OffscreenRender.SaveIfRequested(bitmap, "fase13-monitor-tarjetas.png");
            var found = Descendants<Border>(view).Where(b => b.DataContext is AgentCardViewModel card && AutomationProperties.GetName(b) == card.AccessibleName).ToList();
            var peerBars = Descendants<MetricBar>(view).Where(b => b.DataContext is AgentCardViewModel).ToList();
            return (found.Select(b => (AutomationProperties.GetName(b), AutomationProperties.GetLiveSetting(b))).ToList(), peerBars.Select(Describe).ToList(), Descendants<InfoButton>(view).Select(i => i.InfoKey).ToList());
        });

        Assert.Equal(2, cards.Count);
        Assert.StartsWith("Ejecutor 1 · Claude Code · sonnet · Trabajando en T1 · contexto 47 %", cards[0].Item1, StringComparison.Ordinal);
        Assert.Contains("informado por el CLI", cards[0].Item1, StringComparison.Ordinal);
        Assert.Equal(AutomationLiveSetting.Polite, cards[0].Item2);
        Assert.StartsWith("Ejecutor 2 · Claude Code · sonnet · Te pregunta algo: espera tu respuesta", cards[1].Item1, StringComparison.Ordinal);
        Assert.Equal(AutomationLiveSetting.Assertive, cards[1].Item2);

        // The context bar of the working agent has a value, a minimum and a maximum in UI Automation; the one without a figure has none: never read as zero.
        var withValue = bars.First(b => b.Name.StartsWith("Contexto: 47 %", StringComparison.Ordinal));
        Assert.True(withValue.HasRange);
        Assert.Equal((47d, 0d, 100d), (withValue.Value, withValue.Minimum, withValue.Maximum));
        Assert.Equal(AutomationControlType.ProgressBar, withValue.Type);
        var without = bars.First(b => b.Name.StartsWith("Consumo: sin intentos todavía", StringComparison.Ordinal));
        Assert.False(without.HasRange);

        Assert.Contains("Info_Monitor", info);
    }

    private static (string Name, bool HasRange, double Value, double Minimum, double Maximum, AutomationControlType Type) Describe(MetricBar bar)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(bar);
        var range = peer.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
        return (peer.GetName(), range is not null, range?.Value ?? double.NaN, range?.Minimum ?? double.NaN, range?.Maximum ?? double.NaN, peer.GetAutomationControlType());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The monitor did not read the events in time.");
    }

    [Fact]
    public async Task A_burst_of_events_over_forty_nodes_never_blocks_the_ui_thread_for_more_than_fifty_milliseconds_and_repaints_four_times_a_second_at_most()
    {
        const int Nodes = 40;
        var result = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var tasks = Enumerable.Range(1, Nodes).Select(i => View("T" + i, "exec" + i, WorkTaskStatus.Running)).ToArray();
            var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, tasks));
            var monitor = new RunMonitorViewModel(TimeProvider.System, new DispatcherSynchronizationContext(dispatcher));
            monitor.Roster = [.. Enumerable.Range(1, Nodes).Select(i => AgentDef("exec" + i, "Ejecutor " + i))];
            var view = new RunMonitorView { DataContext = monitor };
            monitor.Attach(run);
            var changes = new List<DateTimeOffset>();
            monitor.Changed += (_, _) => changes.Add(DateTimeOffset.UtcNow);

            // What the dispatcher runs is timed per operation, whoever queued it. The operations of data (Normal priority and above: each
            // event of the burst, the one notification of its repaint, the grouped flush of the live state and the bindings they touch) are
            // what the monitor is asked to keep under 50 ms. The layout passes of the screen (Render priority) are timed apart.
            var data = new ConcurrentBag<double>();
            var layouts = new ConcurrentBag<double>();
            var starts = new ConcurrentDictionary<DispatcherOperation, long>();
            dispatcher.Hooks.OperationStarted += (_, e) => starts[e.Operation] = Stopwatch.GetTimestamp();
            dispatcher.Hooks.OperationCompleted += (_, e) =>
            {
                if (starts.TryRemove(e.Operation, out var begun))
                {
                    (e.Operation.Priority >= DispatcherPriority.Normal ? data : layouts).Add(Stopwatch.GetElapsedTime(begun).TotalMilliseconds);
                }
            };

            // A warm-up, so the first use of each path (JIT, templates) is not counted as a block of the burst.
            run.Emit(Step("T1", 1_000));
            await Task.Delay(300);
            OffscreenRender.Render(view, 1440, 900);

            // The operation that rendered the warm-up ends here, and is not part of the measure.
            await Task.Delay(300);
            data.Clear();
            layouts.Clear();
            changes.Clear();

            var rng = new Random(13);
            var events = new List<OrchestrationEvent>();
            for (var round = 0; round < 25; round++)
            {
                for (var node = 1; node <= Nodes; node++)
                {
                    events.Add(Step("T" + node, rng.Next(1_000, 150_000)));
                }
            }

            var started = Stopwatch.GetTimestamp();
            run.Emit([.. events]);

            // The UI thread keeps pumping while the burst is read and applied.
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < 2.5)
            {
                await Task.Delay(20);
            }

            var dataMax = data.Max();
            var dataCount = data.Count;
            var renderMax = layouts.Count == 0 ? 0 : layouts.Max();
            var changed = changes.ToList();

            // The layout of the cards that the grouped state repaints, once it settled: a list that virtualizes builds only what is in view.
            var cards = Descendants<ItemsControl>(view).First(i => AutomationProperties.GetName(i) == "Agentes");
            var layout = Stopwatch.GetTimestamp();
            cards.InvalidateMeasure();
            cards.Measure(new Size(400, 440));
            var cardsMs = Stopwatch.GetElapsedTime(layout).TotalMilliseconds;
            var virtualizing = new[] { "Agentes", "Tareas de la ejecución" }.All(name => Descendants<ItemsControl>(view).First(i => AutomationProperties.GetName(i) == name) is { } list && VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling);
            return (DataMax: dataMax, RenderMax: renderMax, Cards: cardsMs, Virtualizing: virtualizing, Count: dataCount, Changes: changed, Events: events.Count, Agents: monitor.AgentCards.Count);
        });

        Assert.True(result.Count > 100, "The dispatcher ran too few operations for the measure to mean anything.");
        Assert.True(result.DataMax < 50, $"The UI thread was blocked for {result.DataMax:F1} ms by one operation of data (limit 50 ms) with {result.Events} events over {Nodes} nodes.");
        Assert.True(result.Cards < 50, $"The layout of the cards blocked the UI thread for {result.Cards:F1} ms (limit 50 ms) with {result.Events} events over {Nodes} nodes.");

        // A full layout pass of the whole monitor with the 40 tasks changing at once is not part of this limit (see docs/04 section 21.10):
        // it only must not be pathological.
        Assert.True(result.RenderMax < 1_000, $"A layout pass of the monitor took {result.RenderMax:F0} ms.");

        // 2.5 seconds of burst: at most four grouped changes a second, plus the one that was waiting.
        Assert.InRange(result.Changes.Count, 1, 12);
        for (var i = 1; i < result.Changes.Count; i++)
        {
            Assert.True(result.Changes[i] - result.Changes[i - 1] >= LiveProjection.MinimumInterval - TimeSpan.FromMilliseconds(30), "Two grouped changes came closer than 250 ms.");
        }

        Assert.Equal(Nodes, result.Agents);
        Assert.True(result.Virtualizing, "The lists of the agents and of the tasks virtualize, so a repaint costs what is in view.");
    }
}
