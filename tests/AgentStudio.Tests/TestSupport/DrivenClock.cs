using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.TestSupport;

/// <summary>
/// A clock a test drives by hand: a <see cref="FakeTimeProvider"/> in a fixed local zone that counts the timers asked of it, so a test knows that the code
/// under test is waiting (its timer exists) before it moves the time, and no test waits for the real clock. The count goes up once the timer is armed, so a test
/// that saw it can move the time and be sure to wake that wait.
/// </summary>
internal sealed class DrivenClock : TimeProvider
{
    /// <summary>How long a poll of the real clock waits between two looks at a flag: only the guard of a hung test depends on it.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _inner;
    private int _timers;

    public DrivenClock(DateTimeOffset startUtc, TimeSpan localOffset)
    {
        _inner = new FakeTimeProvider(startUtc);
        _inner.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test-driven", localOffset, "test", "test"));
    }

    /// <summary>How many timers the code under test has armed so far.</summary>
    public int Timers => Volatile.Read(ref _timers);

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _inner.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timers);
        return timer;
    }

    public void Advance(TimeSpan by) => _inner.Advance(by);

    /// <summary>
    /// Waits (the real clock only guards a hang) until the code under test arms a timer after the <paramref name="seen"/> ones, or until
    /// <paramref name="done"/> holds. Returns whether a timer was armed.
    /// </summary>
    public async Task<bool> WaitForTimerAfterAsync(int seen, Func<bool>? done = null, TimeSpan? guard = null)
    {
        var until = Stopwatch.GetTimestamp() + (long)((guard ?? TimeSpan.FromSeconds(60)).TotalSeconds * Stopwatch.Frequency);
        while (Timers <= seen)
        {
            if (done?.Invoke() == true)
            {
                return false;
            }

            Assert.True(Stopwatch.GetTimestamp() < until, "The code under test never armed the timer the test waits for.");
            await Task.Delay(Poll, TestContext.Current.CancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Moves the time by <paramref name="step"/> each time the code under test arms a timer, until <paramref name="done"/> holds: every wait it comes to is
    /// woken exactly once, and the time moves no more than its waits need. <paramref name="seen"/> is <see cref="Timers"/> as read <b>before</b> the stimulus
    /// that makes the code wait (a change, a release): read after it, a wait armed in between would be taken for an old one and never woken.
    /// </summary>
    public async Task AdvanceWhileAsync(Func<bool> done, TimeSpan step, int seen, TimeSpan? guard = null)
    {
        while (!done())
        {
            if (!await WaitForTimerAfterAsync(seen, done, guard))
            {
                return;
            }

            seen = Timers;
            Advance(step);
        }
    }
}
