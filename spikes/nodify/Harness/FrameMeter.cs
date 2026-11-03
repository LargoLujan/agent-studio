using System.Windows.Media;

namespace NodifySpike.Harness;

/// <summary>
/// Counts rendered frames with <see cref="CompositionTarget.Rendering"/>: WPF raises it once per frame it is about to
/// render, with the frame's composition time, so the gaps between calls are the real frame intervals (a gap above
/// 16.7 ms at 60 Hz is a missed frame). While measuring, <c>onFrame</c> moves the viewport, which is what pan and zoom do.
/// </summary>
internal static class FrameMeter
{
    public static async Task<FrameStats> MeasureAsync(string scenario, TimeSpan duration, Action<double>? onFrame = null)
    {
        var intervals = new List<double>();
        var finished = new TaskCompletionSource();
        var first = TimeSpan.MinValue;
        var last = TimeSpan.MinValue;

        void OnRendering(object? sender, EventArgs e)
        {
            var time = ((RenderingEventArgs)e).RenderingTime;
            if (time == last)
            {
                return; // WPF can raise the event twice for one frame
            }

            if (first == TimeSpan.MinValue)
            {
                first = time;
            }
            else
            {
                intervals.Add((time - last).TotalMilliseconds);
            }

            last = time;
            var progress = (time - first).TotalMilliseconds / duration.TotalMilliseconds;
            if (progress >= 1)
            {
                CompositionTarget.Rendering -= OnRendering;
                finished.TrySetResult();
                return;
            }

            onFrame?.Invoke(progress);
        }

        CompositionTarget.Rendering += OnRendering;
        if (await Task.WhenAny(finished.Task, Task.Delay(duration + TimeSpan.FromSeconds(3))) != finished.Task)
        {
            // No frames: WPF does not render a window it considers invisible. Report what was seen instead of hanging.
            CompositionTarget.Rendering -= OnRendering;
        }

        return Summarize(scenario, intervals);
    }

    public static FrameStats Summarize(string scenario, IReadOnlyList<double> intervals)
    {
        if (intervals.Count == 0)
        {
            return new FrameStats(scenario, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var sorted = intervals.Order().ToArray();
        var seconds = intervals.Sum() / 1000d;
        return new FrameStats(
            scenario,
            intervals.Count,
            Math.Round(seconds, 2),
            Math.Round(intervals.Count / seconds, 1),
            Math.Round(intervals.Average(), 2),
            Math.Round(sorted[sorted.Length / 2], 2),
            Math.Round(sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * 0.95) - 1)], 2),
            Math.Round(sorted[^1], 2),
            intervals.Count(i => i > 20),
            intervals.Count(i => i > 33.4));
    }
}
