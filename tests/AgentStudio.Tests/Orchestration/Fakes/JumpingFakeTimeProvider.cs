using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>
/// A fake clock that moves on, once, in the very moment a timer of a given due time is being armed: after whoever arms it
/// read the clock to work out the due time, and before the timer exists. A test thread that advances the clock as soon as it
/// sees a run resting can land there, and so can a machine that falls asleep; this clock lands there every time.
/// </summary>
internal sealed class JumpingFakeTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private Jump? _pending;
    private int _jumped;

    /// <summary>Whether the clock made its jump.</summary>
    public bool Jumped => Volatile.Read(ref _jumped) == 1;

    /// <summary>The next timer armed with <paramref name="dueTime"/> is armed after the clock moved on by <paramref name="by"/>.</summary>
    public void JumpWhenArming(TimeSpan dueTime, TimeSpan by) => Volatile.Write(ref _pending, new Jump(dueTime, by));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (Volatile.Read(ref _pending) is { } jump && jump.DueTime == dueTime && Interlocked.CompareExchange(ref _pending, null, jump) == jump)
        {
            Advance(jump.By);
            Volatile.Write(ref _jumped, 1);
        }

        return base.CreateTimer(callback, state, dueTime, period);
    }

    private sealed record Jump(TimeSpan DueTime, TimeSpan By);
}
