using System.Diagnostics;
using AgentStudio.Infrastructure.Clocks;

namespace AgentStudio.Tests.Infrastructure.Clocks;

/// <summary>
/// The clock of the app (F8-5, docs/16 section 2.14): the wall clock and the timers are the system's, and the timestamp is
/// QueryUnbiasedInterruptTime, which stops while the machine sleeps. No test can make the machine sleep: these check the
/// unit, the order and the pace of the timestamp while it is awake, and that the rest is the system's.
/// </summary>
public sealed class AwakeTimeProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Its_timestamp_counts_hundreds_of_nanoseconds() =>
        Assert.Equal(TimeSpan.TicksPerSecond, new AwakeTimeProvider().TimestampFrequency);

    [Fact]
    public void Its_timestamp_never_goes_back()
    {
        var clock = new AwakeTimeProvider();
        var last = clock.GetTimestamp();
        for (var i = 0; i < 100_000; i++)
        {
            var now = clock.GetTimestamp();
            Assert.True(now >= last, $"The timestamp went back from {last} to {now}.");
            last = now;
        }
    }

    /// <summary>
    /// While the machine is awake the timestamp keeps the pace of QueryPerformanceCounter (<see cref="Stopwatch"/>): the
    /// unbiased count only moves with the clock tick of the system (15.6 ms by default), so the two intervals differ by a
    /// tick or two, never by a factor (a wrong unit would show as one).
    /// </summary>
    [Fact]
    public async Task Its_timestamp_keeps_the_pace_of_the_awake_time()
    {
        var clock = new AwakeTimeProvider();
        var watch = Stopwatch.StartNew();
        var start = clock.GetTimestamp();
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        var elapsed = clock.GetElapsedTime(start);
        watch.Stop();

        Assert.InRange(elapsed - watch.Elapsed, TimeSpan.FromMilliseconds(-50), TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Its_wall_clock_is_the_systems()
    {
        var clock = new AwakeTimeProvider();
        var before = DateTimeOffset.UtcNow;
        var now = clock.GetUtcNow();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(now, before, after);
        Assert.Equal(TimeSpan.Zero, now.Offset);
    }

    [Fact]
    public async Task Its_timers_are_the_systems()
    {
        var clock = new AwakeTimeProvider();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = clock.CreateTimer(_ => fired.TrySetResult(), null, TimeSpan.FromMilliseconds(20), Timeout.InfiniteTimeSpan);
        var watch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromMilliseconds(100), clock, Ct);

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(90), $"The delay ended after {watch.Elapsed.TotalMilliseconds} ms.");
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }
}
