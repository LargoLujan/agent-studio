using System.Diagnostics;
using System.Windows;
using NodifySpike.ViewModels;

namespace NodifySpike.Harness;

internal sealed partial class HarnessRunner
{
    /// <summary>
    /// Methodology check, run with <c>--only exp</c> (not part of the default run): moving the viewport from inside a
    /// <c>CompositionTarget.Rendering</c> handler makes WPF lay out and render the frame twice, so it reports ~20 fps with 200
    /// nodes even though the same canvas pans at 57 fps when driven like real input. The benchmark therefore drives the
    /// viewport from a UI-thread loop and only listens to Rendering.
    /// </summary>
    private async Task ExperimentAsync()
    {
        _vm.Clear();
        SampleGraphs.LoadStress(_vm, 200, 300, 42);
        await SettleAsync(800);
        _editor.FitToScreen();
        await SettleAsync(700);

        var all = new List<FrameStats>();
        var origin = _editor.ViewportLocation;
        var amplitude = new Vector(_editor.ViewportSize.Width * 0.2, _editor.ViewportSize.Height * 0.2);
        Point At(double progress)
        {
            var angle = progress * 2 * Math.PI * 3;
            return new Point(origin.X + (amplitude.X * Math.Sin(angle)), origin.Y + (amplitude.Y * Math.Sin(angle * 2)));
        }

        all.Add(await FrameMeter.MeasureAsync("E1: view model set inside the Rendering handler (wrong way to drive it)", MeasureFor, progress => _vm.ViewportLocation = At(progress)));
        _vm.ViewportLocation = origin;
        await SettleAsync(300);
        all.Add(await DriveAsync("E2: view model set from a UI-thread loop every ~8 ms (how the benchmark drives it)", progress => _vm.ViewportLocation = At(progress)));
        _vm.ViewportLocation = origin;
        await SettleAsync(300);
        all.Add(await FrameMeter.MeasureAsync("E3: idle, nothing moves (WPF renders only when something changes)", MeasureFor));
        all.Add(await RealPanAsync("E4: right-button drag through mouse messages"));

        foreach (var stats in all)
        {
            Log($"EXP {stats}");
        }

        _report.Benchmark = null;
        _report.Experiment = all;
    }

    /// <summary>Calls <paramref name="step"/> with 0..1 progress every ~8 ms for <see cref="MeasureFor"/> while frames are counted.</summary>
    private static async Task<FrameStats> DriveAsync(string scenario, Action<double> step)
    {
        var measuring = FrameMeter.MeasureAsync(scenario, MeasureFor);
        var clock = Stopwatch.StartNew();
        while (!measuring.IsCompleted)
        {
            step(Math.Min(1d, clock.Elapsed / MeasureFor));
            await Task.Delay(8);
        }

        return await measuring;
    }
}
