namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// Finds the time the machine spent asleep (F8-5; docs/16 section 2.14, D-40), so that the silence of an attempt leaves
/// it out. The wall clock says when things happen and when an absolute wait ends (the reset of a provider goes on coming
/// closer while the machine sleeps); no attempt works while the machine sleeps, so its silence must not grow then. The
/// clock learns of a sleep in two ways: as the gap between how far the wall clock moved and how far an awake clock, one that
/// stops while the machine sleeps, moved since it last looked; and as the power events report it
/// (<see cref="Report"/>). Both see the same sleeps, so what it hands out is the larger of the two counts, never their sum.
/// The deadline of an attempt needs nothing of this: it is a timer of the <see cref="TimeProvider"/>, and the timer queue
/// of .NET 10 does not count sleep on Windows (it reads QueryUnbiasedInterruptTime and waits with WaitOne, which since
/// Windows 8 leaves out the time asleep).
/// </summary>
/// <remarks>
/// The awake clock is the timestamp of the <see cref="TimeProvider"/> unless another is given: the app's
/// <c>AwakeTimeProvider</c> is QueryUnbiasedInterruptTime. That of <see cref="TimeProvider.System"/> is
/// QueryPerformanceCounter, which counts the time asleep: with it no gap shows, and only the power events tell the sleep.
/// Only the loop uses the clock (the power events reach it as a signal of the loop): it is not thread-safe.
/// </remarks>
internal sealed class SleepAwareClock
{
    /// <summary>A gap up to this long is the two clocks drifting apart, not sleep.</summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _awake;
    private DateTimeOffset _wallSeen;
    private TimeSpan _awakeSeen;
    private TimeSpan _found;
    private TimeSpan _reported;
    private TimeSpan _handedOut;

    /// <param name="time">The wall clock, and the awake clock when <paramref name="awake"/> is null.</param>
    /// <param name="awake">How long the machine has been awake since some fixed origin; null takes it from the timestamp of <paramref name="time"/>.</param>
    public SleepAwareClock(TimeProvider time, Func<TimeSpan>? awake = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        var origin = time.GetTimestamp();
        _awake = awake ?? (() => time.GetElapsedTime(origin));
        _wallSeen = time.GetUtcNow();
        _awakeSeen = _awake();
    }

    /// <summary>The power events reported that the machine slept <paramref name="slept"/>: the next look hands it out, unless the clocks already did.</summary>
    public void Report(TimeSpan slept)
    {
        if (slept > TimeSpan.Zero)
        {
            _reported += slept;
        }
    }

    /// <summary>
    /// Looks at both clocks and returns the time asleep not yet handed out: what the gaps between the wall clock and the awake
    /// clock found (a gap counts when it is more than <see cref="Tolerance"/>), or what the power events reported, whichever
    /// is more. A wall clock set forward counts too (nothing ran in that time either); one set back is not undone.
    /// </summary>
    public TimeSpan Observe()
    {
        _found += Gap();
        var due = (_found > _reported ? _found : _reported) - _handedOut;
        if (due <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        _handedOut += due;
        return due;
    }

    /// <summary>
    /// How much further the wall clock moved than the awake clock since the last look, when that is more than
    /// <see cref="Tolerance"/>. When the clocks move while they are read (the machine slept right then, or another thread
    /// advanced a fake clock), the pair is not one moment and is read again; if they keep moving, nothing is seen this time
    /// and the next look sees it all.
    /// </summary>
    private TimeSpan Gap()
    {
        for (var reading = 0; reading < 3; reading++)
        {
            var wallBefore = _time.GetUtcNow();
            var awake = _awake();
            var wall = _time.GetUtcNow();
            if (wall - wallBefore > Tolerance)
            {
                continue;
            }

            var gap = wall - _wallSeen - (awake - _awakeSeen);
            _wallSeen = wall;
            _awakeSeen = awake;
            return gap > Tolerance ? gap : TimeSpan.Zero;
        }

        return TimeSpan.Zero;
    }
}
