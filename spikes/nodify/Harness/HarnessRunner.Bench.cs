using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nodify;
using NodifySpike.ViewModels;

namespace NodifySpike.Harness;

internal sealed partial class HarnessRunner
{
    private static readonly TimeSpan MeasureFor = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 200 nodes and 300 wires: first render, the domain check at that size, then pan, zoom and node drag with the frame
    /// counter. The same pan runs first on the 12-node graph so the numbers can be read against what the machine does when
    /// the graph is not the bottleneck. The viewport is always driven from a UI-thread loop, never from inside a Rendering
    /// handler (see <see cref="ExperimentAsync"/>).
    /// </summary>
    private async Task StressAsync()
    {
        var frames = new List<FrameStats>();
        Keyboard.ClearFocus(); // no focus ring around the editor in the pictures
        await SampleLoadAsync("benchmark start");

        // 0. Baseline: the small graph, same movement.
        _vm.Clear();
        SampleGraphs.LoadSmall(_vm);
        await SettleAsync(300);
        _editor.FitToScreen();
        await SettleAsync(400);
        frames.Add(await PanAsync("baseline, 12 nodes: pan at fit zoom"));

        // 1. A plan at the validator's limit (MaxTasks = 20), then the first render of the stress graph and a warm reload.
        _vm.Clear();
        _vm.ViewportZoom = 1;
        _vm.ViewportLocation = new Point(0, 0);
        await SettleAsync(400);
        var planSize = await MeasureLoadAsync(() => SampleGraphs.LoadStress(_vm, 20, 25, 7), 25);
        Log($"plan-size graph (20 nodes, 25 wires): {planSize}");
        _vm.Clear();
        await SettleAsync(400);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = WorkingSetMegabytes();
        var first = await LoadStressMeasuredAsync();
        var reloads = new List<RenderTimes>();
        for (var i = 0; i < 4; i++)
        {
            await SettleAsync(300);
            _vm.Clear();
            await SettleAsync(400);
            reloads.Add(await LoadStressMeasuredAsync());
        }

        await SettleAsync(500);
        Log($"first load: {first}; warm reloads: {string.Join("; ", reloads)}");

        var containers = VisualTree.Descendants<ItemContainer>(_editor).Count();
        var wires = VisualTree.Descendants<Nodify.Connection>(_editor).Count();
        var reloadMs = reloads.Select(r => r.FullyDrawnMs).Order().ToList();
        Check("bench", "200 nodes and 300 wires are rendered", containers == 200 && wires == 300, $"{containers} containers, {wires} wires; first load {first.FirstFrameMs:0} ms to first frame, {first.FullyDrawnMs:0} ms fully drawn; 4 warm reloads fully drawn in {reloadMs[0]:0}-{reloadMs[^1]:0} ms");

        // 2. The domain check at this size: rebuild the graph from 200 tasks and 300 dependencies, and probe a cycle.
        var rebuild = Time(() => PlanGraph.Build(_vm.Nodes, _vm.Connections, DateTimeOffset.UtcNow), 20);
        var wire = _vm.Connections[_vm.Connections.Count / 2];
        var probe = () => PlanGraph.CheckCycle(_vm.Nodes, _vm.Connections, wire.Target.Node.Outputs[0], wire.Source.Node.Inputs[0], DateTimeOffset.UtcNow);
        var cycleProbe = Time(() => probe(), 20);
        var verdict = probe();
        Check("bench", "the cycle probe still refuses a back edge on the 200/300 graph", verdict.Verdict == ConnectionVerdict.Cycle, $"{verdict.Message}; rebuild {rebuild:0.00} ms, probe {cycleProbe:0.00} ms (mean of 20)");

        // 3. Screenshot at fit zoom.
        _editor.FitToScreen();
        await SettleAsync(700);
        var fitZoom = _vm.ViewportZoom;
        Shot("fase3-spike-nodify-200.png");
        var after = WorkingSetMegabytes();

        // 4. Scripted pan and zoom with everything in view, and with a part of the graph in view.
        frames.Add(await PanAsync("200 nodes: scripted pan at fit zoom (everything in view)"));
        frames.Add(await ZoomAsync("200 nodes: scripted zoom sweep (x1.8 each way)"));
        _vm.ViewportZoom = 1;
        _vm.ViewportLocation = new Point(600, 300);
        await SettleAsync(400);
        frames.Add(await PanAsync("200 nodes: scripted pan at 100% (a fifth of the graph in view)"));

        // 5. The same through the input pipeline: right-button drag, wheel and dragging a node (its wires follow). Skipped (and
        // reported as such) when the run could not take the keyboard focus safely: pressing on an element makes WPF focus it.
        if (!_keyboardFocusUnavailable)
        {
            _editor.FitToScreen();
            await SettleAsync(500);
            frames.Add(await RealPanAsync("200 nodes: right-button drag pan at fit zoom (mouse messages)"));
            frames.Add(await RealWheelAsync("200 nodes: mouse wheel zoom (wheel messages)"));
            frames.Add(await RealNodeDragAsync("200 nodes: left-drag of a node at fit zoom, its wires follow (mouse messages)"));
        }

        // 6. Headroom: Nodify's own rendering optimisation (caches the containers when zoomed out) is off below 700 nodes.
        var previous = NodifyEditor.OptimizeRenderingMinimumContainers;
        NodifyEditor.OptimizeRenderingMinimumContainers = 100;
        _editor.FitToScreen();
        await SettleAsync(500);
        frames.Add(await PanAsync("200 nodes: scripted pan at fit zoom with Nodify's rendering optimisation forced on (threshold 100)"));
        NodifyEditor.OptimizeRenderingMinimumContainers = previous;

        foreach (var stats in frames)
        {
            Log($"frames: {stats}");
        }

        await SampleLoadAsync("benchmark end");

        _report.Benchmark = new BenchmarkReport(200, 300, containers, wires, planSize, first, reloads, Math.Round(rebuild, 2), Math.Round(cycleProbe, 2), Math.Round(before, 1), Math.Round(after, 1), Math.Round(fitZoom, 3), frames);
        // The gate is "interactive" (30 fps), not "at the display's 60 Hz": the machine is shared and the numbers move with its load.
        var panFit = frames.First(f => f.Scenario.StartsWith("200 nodes: scripted pan at fit", StringComparison.Ordinal) && !f.Scenario.Contains("forced", StringComparison.Ordinal));
        Check("bench", "pan with all 200 nodes in view stays interactive (at least 30 frames per second)", panFit.AverageFps >= 30, $"{panFit.AverageFps:0.0} fps, median {panFit.MedianFrameMs} ms, p95 {panFit.P95FrameMs} ms, {panFit.FramesOver33Ms} frames over 33 ms");
        var realPan = frames.FirstOrDefault(f => f.Scenario.Contains("right-button drag pan", StringComparison.Ordinal));
        if (realPan is not null)
        {
            Check("bench", "a right-button drag pan with 200 nodes stays interactive (at least 30 frames per second)", realPan.AverageFps >= 30, $"{realPan.AverageFps:0.0} fps, p95 {realPan.P95FrameMs} ms");
        }
    }

    private Task<RenderTimes> LoadStressMeasuredAsync() => MeasureLoadAsync(() => SampleGraphs.LoadStress(_vm, 200, 300, 42), 300);

    private static double WorkingSetMegabytes() => Process.GetCurrentProcess().WorkingSet64 / 1048576d;

    private static double Time(Action action, int runs)
    {
        action();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++)
        {
            action();
        }

        return clock.Elapsed.TotalMilliseconds / runs;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await NextFrameAsync();
        }

        return condition();
    }

    /// <summary>Moves the viewport along a Lissajous loop, three times in three seconds.</summary>
    private async Task<FrameStats> PanAsync(string scenario)
    {
        var origin = _vm.ViewportLocation;
        var amplitude = new Vector(_vm.ViewportSize.Width * 0.2, _vm.ViewportSize.Height * 0.2);
        var stats = await DriveAsync(scenario, progress =>
        {
            var angle = progress * 2 * Math.PI * 3;
            _vm.ViewportLocation = new Point(origin.X + (amplitude.X * Math.Sin(angle)), origin.Y + (amplitude.Y * Math.Sin(angle * 2)));
        });
        _vm.ViewportLocation = origin;
        await SettleAsync(150);
        return stats;
    }

    /// <summary>Zooms in and out around the centre of the view, as the mouse wheel does (ZoomAtPosition).</summary>
    private async Task<FrameStats> ZoomAsync(string scenario)
    {
        var startZoom = _vm.ViewportZoom;
        var startLocation = _vm.ViewportLocation;
        var stats = await DriveAsync(scenario, progress =>
        {
            var target = startZoom * Math.Pow(1.8, Math.Sin(progress * 2 * Math.PI * 2));
            var centre = _vm.ViewportLocation + (new Vector(_vm.ViewportSize.Width, _vm.ViewportSize.Height) / 2);
            _editor.ZoomAtPosition(target / _vm.ViewportZoom, centre);
        });
        _vm.ViewportZoom = startZoom;
        _vm.ViewportLocation = startLocation;
        await SettleAsync(150);
        return stats;
    }

    /// <summary>Holds the given mouse button and circles the pointer for three seconds while frames are counted.</summary>
    private async Task<FrameStats> RealDragAsync(string scenario, MouseButton button, (int X, int Y) start, int radius)
    {
        // Start measuring only once the press has been taken (something captured the mouse); a lost press is repeated.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await MoveToAsync(start, 150);
            _input.ButtonDown(button, start.X, start.Y);
            await Task.Delay(60);
            if (Mouse.Captured is not null)
            {
                break;
            }

            _input.ButtonUp(button, start.X, start.Y);
            Log($"{scenario}: the press was lost (attempt {attempt} of 3), trying again");
        }

        var measuring = FrameMeter.MeasureAsync(scenario, MeasureFor);
        var clock = Stopwatch.StartNew();
        (int X, int Y) last = start;
        while (!measuring.IsCompleted)
        {
            var angle = clock.Elapsed.TotalSeconds * 2 * Math.PI;
            last = (start.X + (int)(radius * (Math.Cos(angle) - 1)), start.Y + (int)(radius * Math.Sin(angle)));
            _input.Move(last.X, last.Y);
            await Task.Delay(8);
        }

        _input.ButtonUp(button, last.X, last.Y);
        var stats = await measuring;
        await SettleAsync(200);
        return stats;
    }

    private Task<FrameStats> RealPanAsync(string scenario)
    {
        var centre = EditorPoint(_editor.ActualWidth / 2, _editor.ActualHeight / 2);
        var radius = (int)(Math.Min(_editor.ActualWidth, _editor.ActualHeight) * 0.18 * VisualTreeHelper.GetDpi(_window).DpiScaleX);
        return RealDragAsync(scenario, MouseButton.Right, centre, radius);
    }

    private Task<FrameStats> RealNodeDragAsync(string scenario)
    {
        var node = _vm.Nodes.OrderByDescending(n => _vm.Connections.Count(c => c.Source.Node == n || c.Target.Node == n)).Skip(5).First();
        return RealDragAsync(scenario, MouseButton.Left, NodeHeaderPoint(node), 60);
    }

    /// <summary>One wheel notch every 50 ms for three seconds, alternating in and out, over the middle of the canvas.</summary>
    private async Task<FrameStats> RealWheelAsync(string scenario)
    {
        var centre = EditorPoint(_editor.ActualWidth / 2, _editor.ActualHeight / 2);
        await MoveToAsync(centre, 150);
        var measuring = FrameMeter.MeasureAsync(scenario, MeasureFor);
        var notch = 0;
        while (!measuring.IsCompleted)
        {
            _input.Wheel(notch / 10 % 2 == 0 ? 120 : -120, centre.X, centre.Y);
            notch++;
            await Task.Delay(50);
        }

        var stats = await measuring;
        _editor.FitToScreen();
        await SettleAsync(300);
        return stats;
    }
}
