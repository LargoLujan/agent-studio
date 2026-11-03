using AgentStudio.Application.Execution;
using AgentStudio.Desktop.Background;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-5 and docs/16 D-40 (the side of WP3): the power transitions behind <see cref="IPowerEvents"/>, with doubles of the
/// source and of the two clocks of the kernel, so nothing ever suspends. How long the computer slept is the time with the
/// sleep minus the time without it, never the wall clock.
/// </summary>
public sealed class PowerEventsTests
{
    private readonly FakeTransitions _source = new();
    private readonly FakeSleepClock _clock = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly List<PowerEvent> _seen = [];

    private WindowsPowerEvents Events(Action<Action>? raise = null)
    {
        var events = new WindowsPowerEvents(_source, _clock, _time, raise ?? (action => action()));
        events.Changed += (_, e) => _seen.Add(e);
        return events;
    }

    [Fact]
    public void A_suspension_and_its_wake_say_when_and_how_long_the_computer_slept()
    {
        using var events = Events();

        _source.Raise(PowerTransition.Suspending);

        // Asleep for 47 minutes: the clock with the sleep moves, the one without it does not.
        _clock.Advance(awake: TimeSpan.FromSeconds(2), asleep: TimeSpan.FromMinutes(47));
        _time.Advance(TimeSpan.FromMinutes(47));
        _source.Raise(PowerTransition.Resumed);

        Assert.Equal([new PowerEvent(PowerTransition.Suspending, T0), new PowerEvent(PowerTransition.Resumed, T0.AddMinutes(47), TimeSpan.FromMinutes(47))], _seen);
    }

    [Fact]
    public void A_wall_clock_that_jumps_does_not_change_the_time_slept()
    {
        using var events = Events();

        _source.Raise(PowerTransition.Suspending);
        _clock.Advance(awake: TimeSpan.Zero, asleep: TimeSpan.FromMinutes(10));

        // Windows corrected the time on waking: an hour forward on the wall.
        _time.Advance(TimeSpan.FromMinutes(70));
        _source.Raise(PowerTransition.Resumed);

        Assert.Equal(TimeSpan.FromMinutes(10), _seen[^1].Slept);
        Assert.Equal(T0.AddMinutes(70), _seen[^1].At);
    }

    [Fact]
    public void A_wake_whose_suspension_was_not_seen_has_no_time_slept()
    {
        using var events = Events();

        _source.Raise(PowerTransition.Resumed);

        Assert.Equal([new PowerEvent(PowerTransition.Resumed, T0)], _seen);
        Assert.Null(_seen[0].Slept);
    }

    [Fact]
    public void Windows_reporting_one_wake_twice_raises_it_once_per_suspension()
    {
        using var events = Events();

        _source.Raise(PowerTransition.Suspending);
        _clock.Advance(awake: TimeSpan.Zero, asleep: TimeSpan.FromMinutes(5));
        _source.Raise(PowerTransition.Resumed);
        _source.Raise(PowerTransition.Resumed);
        _source.Raise(PowerTransition.Suspending);
        _clock.Advance(awake: TimeSpan.Zero, asleep: TimeSpan.FromMinutes(1));
        _source.Raise(PowerTransition.Resumed);

        Assert.Equal(
            [PowerTransition.Suspending, PowerTransition.Resumed, PowerTransition.Suspending, PowerTransition.Resumed],
            _seen.Select(e => e.Transition));
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)], _seen.Where(e => e.Transition == PowerTransition.Resumed).Select(e => e.Slept!.Value));
    }

    [Fact]
    public async Task The_handlers_run_off_the_thread_that_reported_the_transition()
    {
        using var events = new WindowsPowerEvents(_source, _clock, _time);
        using var release = new ManualResetEventSlim();
        var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The handler holds until the report came back: run in place, it would hold the report until its own timeout.
        events.Changed += (_, _) => handled.TrySetResult(release.Wait(TimeSpan.FromSeconds(10)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _source.Raise(PowerTransition.Suspending);
        var reported = watch.Elapsed;
        release.Set();

        Assert.True(await handled.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "The handler never saw the report come back.");
        Assert.True(reported < TimeSpan.FromSeconds(5), $"The report took {reported}: the handler ran on its thread.");
    }

    [Fact]
    public async Task The_default_delivery_keeps_the_order_windows_reported_even_when_a_handler_is_slow()
    {
        using var events = new WindowsPowerEvents(_source, _clock, _time);
        using var gate = new ManualResetEventSlim();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<PowerTransition>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.Changed += (_, e) =>
        {
            // The handler of the suspension holds until the wake was reported: delivered apart, the wake would overtake it.
            if (e.Transition == PowerTransition.Suspending)
            {
                gate.Wait(TimeSpan.FromSeconds(10));
            }

            seen.Enqueue(e.Transition);
            if (seen.Count == 2)
            {
                both.TrySetResult();
            }
        };

        _source.Raise(PowerTransition.Suspending);
        _clock.Advance(awake: TimeSpan.Zero, asleep: TimeSpan.FromMinutes(3));
        _source.Raise(PowerTransition.Resumed);
        gate.Set();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal([PowerTransition.Suspending, PowerTransition.Resumed], seen);
    }

    [Fact]
    public void A_handler_that_throws_does_not_keep_the_others_from_knowing()
    {
        using var events = new WindowsPowerEvents(_source, _clock, _time, action => action());
        var later = 0;
        events.Changed += (_, _) => throw new InvalidOperationException("The engine could not re-evaluate (simulated).");
        events.Changed += (_, _) => later++;

        _source.Raise(PowerTransition.Suspending);

        Assert.Equal(1, later);
    }

    [Fact]
    public void Disposing_stops_listening_and_releases_the_source()
    {
        var events = Events();
        events.Dispose();

        _source.Raise(PowerTransition.Suspending);

        Assert.Empty(_seen);
        Assert.True(_source.Disposed);
    }

    [Fact]
    public void The_kernel_clocks_go_forward_and_the_one_without_sleep_never_passes_the_other()
    {
        var clock = new KernelSleepClock();

        var withSleep = clock.WithSleep();
        var withoutSleep = clock.WithoutSleep();

        Assert.True(withSleep > 0 && withoutSleep > 0);
        Assert.True(withoutSleep <= withSleep + 1000, $"Without sleep {withoutSleep} ms, with it {withSleep} ms.");
        Assert.True(clock.WithSleep() >= withSleep);
    }

    [Fact]
    public void Windows_accepts_and_drops_the_registration_of_the_notifications_without_changing_any_power_state()
    {
        var notifications = new SuspendResumeNotifications();
        Assert.True(notifications.IsRegistered);

        notifications.Dispose();
        notifications.Dispose();

        Assert.False(notifications.IsRegistered);
    }

    private sealed class FakeTransitions : IPowerTransitionSource
    {
        public bool Disposed { get; private set; }

        public event EventHandler<PowerTransition>? Changed;

        public void Raise(PowerTransition transition) => Changed?.Invoke(this, transition);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSleepClock : ISleepClock
    {
        private long _withSleep = 3_600_000;
        private long _withoutSleep = 3_000_000;

        public long WithSleep() => _withSleep;

        public long WithoutSleep() => _withoutSleep;

        public void Advance(TimeSpan awake, TimeSpan asleep)
        {
            _withSleep += (long)(awake + asleep).TotalMilliseconds;
            _withoutSleep += (long)awake.TotalMilliseconds;
        }
    }
}
