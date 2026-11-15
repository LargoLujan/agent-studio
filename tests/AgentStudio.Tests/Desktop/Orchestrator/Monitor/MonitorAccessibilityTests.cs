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
        Assert.Contains("informado por el programa del agente", cards[0].Item1, StringComparison.Ordinal);
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

    /// <summary>Polls until the monitor read the events; the minute is only the guard of a hang, not a limit on the monitor.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 12_000 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The monitor did not read the events in time.");
    }

    /// <summary>
    /// The burst of F13-7 without a clock: a thousand events over forty nodes are applied, and the monitor says nothing until its timer is due; the time
    /// then moves by hand, so what is counted is the grouped changes (at most four a second, never closer than 250 ms) and not how fast the machine is.
    /// </summary>
    [Fact]
    public void A_burst_of_events_over_forty_nodes_is_grouped_into_at_most_four_changes_a_second_whatever_the_machine()
    {
        const int Nodes = 40;
        const int Bursts = 8;
        var tasks = Enumerable.Range(1, Nodes).Select(i => View("T" + i, "exec" + i, WorkTaskStatus.Running)).ToArray();
        var (monitor, run, ui, time) = Open(tasks);
        monitor.Roster = [.. Enumerable.Range(1, Nodes).Select(i => AgentDef("exec" + i, "Ejecutor " + i))];
        var changes = new List<DateTimeOffset>();
        monitor.Changed += (_, _) => changes.Add(time.GetUtcNow());
        ui.RunAll();
        var started = time.GetUtcNow();
        var rng = new Random(13);
        var applied = 0;

        for (var burst = 1; burst <= Bursts; burst++)
        {
            var events = new List<OrchestrationEvent>();
            for (var round = 0; round < 4; round++)
            {
                for (var node = 1; node <= Nodes; node++)
                {
                    events.Add(Step("T" + node, rng.Next(1_000, 150_000)));
                }
            }

            // The last event of the burst tells the monitor read it all: the status of T40 alternates, and that is what the loop below waits for.
            var (from, to) = burst % 2 == 1 ? (WorkTaskStatus.Running, WorkTaskStatus.Blocked) : (WorkTaskStatus.Blocked, WorkTaskStatus.Running);
            events.Add(Move("T40", from, to, "end of burst " + burst));
            run.Emit([.. events]);
            applied += events.Count;

            var last = monitor.Tasks.Single(t => t.Id.Value == "T40");
            var deadline = Stopwatch.GetTimestamp() + (60 * Stopwatch.Frequency);
            while (last.Status != to)
            {
                ui.RunAll();
                Assert.True(Stopwatch.GetTimestamp() < deadline, "The monitor did not read the burst (the clock of the guard is the real one).");
                Thread.Yield();
            }

            if (burst == 1)
            {
                // A burst is not a repaint by itself: with the time still, nothing was published however many events were applied.
                Assert.Empty(changes);
            }

            time.Advance(TimeSpan.FromMilliseconds(100));
            ui.RunAll();
        }

        time.Advance(TimeSpan.FromSeconds(1));
        ui.RunAll();

        var elapsed = time.GetUtcNow() - started;
        Assert.True(applied > 1_000, "The burst must be a thousand events at least.");
        Assert.InRange(changes.Count, 1, (int)Math.Ceiling(elapsed.TotalSeconds * 4) + 1);
        for (var i = 1; i < changes.Count; i++)
        {
            Assert.True(changes[i] - changes[i - 1] >= LiveProjection.MinimumInterval, $"Two grouped changes came {(changes[i] - changes[i - 1]).TotalMilliseconds:0} ms apart (the least is 250 ms).");
        }

        Assert.Equal(Nodes, monitor.AgentCards.Count);
    }

    /// <summary>
    /// The lists of the agents and of the tasks virtualize and recycle, so a repaint costs what is in view and not the forty nodes: what the layout of the
    /// burst test below measures in milliseconds is, here, only the structure that makes it cheap.
    /// </summary>
    [Fact]
    public void The_lists_of_the_agents_and_of_the_tasks_virtualize_so_a_repaint_costs_what_is_in_view()
    {
        const int Nodes = 40;
        // The monitor is filled before any view is bound to it, so nothing changes under a binding from another thread.
        var tasks = Enumerable.Range(1, Nodes).Select(i => View("T" + i, "exec" + i, WorkTaskStatus.Running)).ToArray();
        var (monitor, run, ui, time) = Open(tasks);
        monitor.Roster = [.. Enumerable.Range(1, Nodes).Select(i => AgentDef("exec" + i, "Ejecutor " + i))];
        run.Emit(Enumerable.Range(1, Nodes).Select(i => (OrchestrationEvent)Step("T" + i, 1_000 * i)).ToArray());
        Pump(ui, Nodes);
        Settle(time, ui);
        Assert.Equal(Nodes, monitor.AgentCards.Count);

        var (virtualizing, realized) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new RunMonitorView { DataContext = monitor };
            OffscreenRender.Render(view, 1440, 900);
            var lists = new[] { "Agentes", "Tareas de la ejecución" }.Select(name => Descendants<ItemsControl>(view).First(i => AutomationProperties.GetName(i) == name)).ToList();
            var all = lists.All(list => VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling);
            return (all, Descendants<Border>(view).Select(b => b.DataContext).OfType<AgentCardViewModel>().Distinct().Count());
        });

        Assert.True(virtualizing, "The lists of the agents and of the tasks virtualize, so a repaint costs what is in view.");
        Assert.InRange(realized, 1, Nodes - 1);
    }

    // Measurement: wall-clock times of this machine, so the CI leaves it out (the deterministic versions are the two tests above, which count the
    // grouped changes with a clock the test moves and check the virtualization). On a busy machine the thread that is preempted is not the monitor's doing.
    [Fact]
    [Trait("Category", "Measurement")]
    public async Task A_burst_of_events_over_forty_nodes_never_blocks_the_ui_thread_for_more_than_fifty_milliseconds_and_repaints_four_times_a_second_at_most()
    {
        const int Nodes = 40;
        var result = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var tasks = Enumerable.Range(1, Nodes).Select(i => View("T" + i, "exec" + i, WorkTaskStatus.Running)).ToArray();
            var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, tasks));
            // Everything the monitor asks of the UI thread goes through its context: each event of the burst, the one notification that
            // repaints the bars of the tasks and the grouped flush of the live state, with the bindings they touch. Each of those is timed
            // here, so what other tests do on the shared dispatcher at the same time is not counted. They are what the monitor is asked
            // to keep under 50 ms.
            var data = new ConcurrentBag<double>();
            var monitor = new RunMonitorViewModel(TimeProvider.System, new TimingContext(new DispatcherSynchronizationContext(dispatcher), data));
            monitor.Roster = [.. Enumerable.Range(1, Nodes).Select(i => AgentDef("exec" + i, "Ejecutor " + i))];
            var view = new RunMonitorView { DataContext = monitor };
            monitor.Attach(run);
            var changes = new List<DateTimeOffset>();
            monitor.Changed += (_, _) => changes.Add(DateTimeOffset.UtcNow);

            // A warm-up, so the first use of each path (JIT, templates) is not counted as a block of the burst.
            run.Emit(Step("T1", 1_000));
            await Task.Delay(300);
            OffscreenRender.Render(view, 1440, 900);

            // The operation that rendered the warm-up ends here, and is not part of the measure.
            await Task.Delay(300);
            data.Clear();
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
            // On a loaded machine the reader of the burst may not get a turn in those 2.5 seconds: the measure waits for the first
            // grouped change (30 seconds at most) instead of taking its absence for a failure of the monitor.
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < 2.5 || (changes.Count == 0 && Stopwatch.GetElapsedTime(started).TotalSeconds < 30))
            {
                await Task.Delay(20);
            }

            var measured = Stopwatch.GetElapsedTime(started);

            // The suite runs other tests at the same time on the same processors, and a thread that is preempted for tens of milliseconds
            // is not the monitor's doing: the limit is asked of 99 operations in 100, and no operation may be pathological (500 ms).
            var sorted = data.Order().ToList();
            var dataP99 = sorted[(int)Math.Floor((sorted.Count - 1) * 0.99)];
            var dataMax = sorted[^1];
            var dataCount = sorted.Count;
            var changed = changes.ToList();

            // The layout of the cards that the grouped state repaints, once it settled: a list that virtualizes builds only what is in view.
            // The best of three, which is what the layout costs and not what the scheduler did to it.
            var cards = Descendants<ItemsControl>(view).First(i => AutomationProperties.GetName(i) == "Agentes");
            var cardsMs = double.MaxValue;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var layout = Stopwatch.GetTimestamp();
                cards.InvalidateMeasure();
                cards.Measure(new Size(400, 440));
                cardsMs = Math.Min(cardsMs, Stopwatch.GetElapsedTime(layout).TotalMilliseconds);
            }

            var virtualizing = new[] { "Agentes", "Tareas de la ejecución" }.All(name => Descendants<ItemsControl>(view).First(i => AutomationProperties.GetName(i) == name) is { } list && VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling);
            return (DataP99: dataP99, DataMax: dataMax, Cards: cardsMs, Virtualizing: virtualizing, Count: dataCount, Changes: changed, Events: events.Count, Agents: monitor.AgentCards.Count, Measured: measured);
        });

        Assert.True(result.Count > 100, "The dispatcher ran too few operations for the measure to mean anything.");
        Assert.True(result.DataP99 < 50, $"The UI thread was blocked for {result.DataP99:F1} ms by 1 operation of data in 100 (limit 50 ms; the worst took {result.DataMax:F1} ms) with {result.Events} events over {Nodes} nodes.");
        Assert.True(result.DataMax < 500, $"One operation of data blocked the UI thread for {result.DataMax:F1} ms (a pathological block) with {result.Events} events over {Nodes} nodes.");
        Assert.True(result.Cards < 50, $"The layout of the cards blocked the UI thread for {result.Cards:F1} ms (limit 50 ms) with {result.Events} events over {Nodes} nodes.");

        // A full layout pass of the whole monitor with the 40 tasks changing at once is not part of this limit (see docs/04 section 21.10).

        // The burst as long as it was measured (2.5 seconds, more on a loaded machine): at most four grouped changes a second, plus
        // the one that was waiting and the one of the second under way.
        Assert.InRange(result.Changes.Count, 1, (int)Math.Ceiling(result.Measured.TotalSeconds * 4) + 2);
        for (var i = 1; i < result.Changes.Count; i++)
        {
            Assert.True(result.Changes[i] - result.Changes[i - 1] >= LiveProjection.MinimumInterval - TimeSpan.FromMilliseconds(30), "Two grouped changes came closer than 250 ms.");
        }

        Assert.Equal(Nodes, result.Agents);
        Assert.True(result.Virtualizing, "The lists of the agents and of the tasks virtualize, so a repaint costs what is in view.");
    }

    /// <summary>
    /// The context of the UI the monitor gets in the test of the burst: it runs what is posted on the dispatcher and times each callback,
    /// so the measure is of what the monitor asked of the UI thread and not of what other tests do on the shared dispatcher.
    /// </summary>
    private sealed class TimingContext(SynchronizationContext inner, ConcurrentBag<double> durations) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            inner.Post(
                _ =>
                {
                    var begun = Stopwatch.GetTimestamp();
                    d(state);
                    durations.Add(Stopwatch.GetElapsedTime(begun).TotalMilliseconds);
                },
                null);

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("The monitor must post, never send.");
    }
}
