using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.TestSupport;

/// <summary>
/// The clock of the tests of the wait before a remote operation (docs/21 D-25): a <see cref="FakeTimeProvider"/> that counts the timers asked of it, so a
/// test knows that the code under test is waiting (a timer exists) before it moves the time, and no test waits for the real clock.
/// </summary>
internal sealed class RemoteWaitClock : TimeProvider
{
    private readonly FakeTimeProvider _inner = new(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));
    private int _timers;

    /// <summary>How many timers the code under test asked for: each wait of a poll is one.</summary>
    public int Timers => Volatile.Read(ref _timers);

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _timers);
        return _inner.CreateTimer(callback, state, dueTime, period);
    }

    public void Advance(TimeSpan by) => _inner.Advance(by);

    /// <summary>Returns once the code under test is waiting on its <paramref name="count"/>th timer; spins on the counter, never on the clock.</summary>
    public async Task WaitForTimerAsync(int count)
    {
        for (var spin = 0; Timers < count; spin++)
        {
            Assert.True(spin < 5_000_000, $"The code under test never asked for timer {count}.");
            await Task.Yield();
        }
    }

    /// <summary>
    /// Moves the time one <paramref name="step"/> at a time until <paramref name="task"/> ends, as the code under test comes round to ask again. A step that finds
    /// no timer yet just moves the clock: the next one wakes it, so the order of the two threads does not matter.
    /// </summary>
    public Task AdvanceUntilAsync(Task task, TimeSpan step) => AdvanceUntilAsync(_inner, task, step);

    /// <summary>
    /// Moves the time one <paramref name="step"/> at a time only until <paramref name="taken"/> says the code under test got the project; from then on the clock stays
    /// where it is, so what runs after the wait does not depend on how fast this thread spins.
    /// </summary>
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, Task task, TimeSpan step, Func<bool> taken)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(taken);
        for (var spins = 0; !task.IsCompleted && !taken(); spins++)
        {
            Assert.True(spins < 2_000_000, "The code under test never got the project.");
            time.Advance(step);
            await Task.Yield();
        }
    }

    /// <summary>The same for a <see cref="FakeTimeProvider"/> that a world of tests already has (the engine harness, the debate world…).</summary>
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, Task task, TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(task);
        for (var spins = 0; !task.IsCompleted; spins++)
        {
            Assert.True(spins < 2_000_000, "The task did not end by moving the clock.");
            time.Advance(step);
            await Task.Yield();
        }
    }
}
