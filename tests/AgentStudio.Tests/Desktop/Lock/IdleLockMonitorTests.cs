using System.ComponentModel;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Services;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// F8-10: the idle lock. The time without input in the session (a double that follows the fake clock) and the settings are
/// read at each check; the interface locks once after the minutes set, and the count starts again at each unlock.
/// </summary>
public sealed class IdleLockMonitorTests
{
    /// <summary>The input of the session, scripted against the fake clock.</summary>
    private sealed class FakeIdleInput(TimeProvider clock) : IIdleInput
    {
        public DateTimeOffset LastInputAt { get; set; } = clock.GetUtcNow();

        public Exception? Fails { get; set; }

        public TimeSpan IdleFor() => Fails is { } failure ? throw failure : clock.GetUtcNow() - LastInputAt;
    }

    private sealed class Harness
    {
        public Harness(FakeUserPresenceVerifier? hello = null, TimeSpan? poll = null)
        {
            Hello = hello ?? new FakeUserPresenceVerifier();
            Input = new FakeIdleInput(Clock);
            Lock = new InterfaceLock(Hello, Session, Clock);
            Monitor = new IdleLockMonitor(Lock, Input, () => Settings, Clock, poll);
        }

        public FakeTimeProvider Clock { get; } = new(T0);

        public FakeUserPresenceVerifier Hello { get; }

        public FakeWindowsSession Session { get; } = new();

        public FakeIdleInput Input { get; }

        public AppSettings Settings { get; set; } = AppSettings.Default.WithIdleLock(enabled: true, minutes: 15);

        public InterfaceLock Lock { get; }

        public IdleLockMonitor Monitor { get; }

        public Task<bool> AtAsync(TimeSpan sinceStart)
        {
            Clock.SetUtcNow(T0 + sinceStart);
            return Monitor.CheckAsync(TestContext.Current.CancellationToken);
        }
    }

    private static TimeSpan Minutes(double value) => TimeSpan.FromMinutes(value);

    [Fact]
    public async Task Off_by_default_the_interface_never_locks_when_idle()
    {
        var harness = new Harness { Settings = AppSettings.Default };

        Assert.False(await harness.AtAsync(TimeSpan.FromDays(1)));
        Assert.False(harness.Lock.IsLocked);
        Assert.False(AppSettings.Default.IdleLockEnabled);
    }

    [Fact]
    public async Task On_the_interface_locks_after_the_minutes_without_input()
    {
        var harness = new Harness();

        Assert.False(await harness.AtAsync(Minutes(14) + TimeSpan.FromSeconds(59)));
        Assert.False(harness.Lock.IsLocked);
        Assert.True(await harness.AtAsync(Minutes(15)));

        Assert.True(harness.Lock.IsLocked);
        Assert.Equal((LockReason.Idle, T0 + Minutes(15)), (harness.Lock.Reason, harness.Lock.LockedAt));
    }

    /// <summary>Working in another app is input in the session: the interface stays unlocked.</summary>
    [Fact]
    public async Task Input_anywhere_in_the_session_keeps_it_unlocked()
    {
        var harness = new Harness();
        harness.Input.LastInputAt = T0 + Minutes(10);

        Assert.False(await harness.AtAsync(Minutes(15)));
        Assert.False(await harness.AtAsync(Minutes(24)));
        Assert.True(await harness.AtAsync(Minutes(25)));
    }

    /// <summary>
    /// After an unlock the count starts again, even when Windows saw no input since (an unlock of Windows that it did not count,
    /// say): it never locks again at once.
    /// </summary>
    [Fact]
    public async Task The_time_without_input_counts_from_the_last_unlock()
    {
        var harness = new Harness();
        Assert.True(await harness.AtAsync(Minutes(15)));
        harness.Clock.SetUtcNow(T0 + Minutes(20));
        await harness.Lock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);

        Assert.False(await harness.AtAsync(Minutes(21)));
        Assert.False(await harness.AtAsync(Minutes(34)));
        Assert.True(await harness.AtAsync(Minutes(35)));
    }

    /// <summary>A clock whose wall time can go back, as when Windows corrects it, while its timestamps only go forward.</summary>
    private sealed class JumpingClock : TimeProvider
    {
        private DateTimeOffset _now = T0;
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _ticks;

        /// <summary>Time passes: the wall clock and the timestamps move together.</summary>
        public void Pass(TimeSpan elapsed)
        {
            _now += elapsed;
            _ticks += elapsed.Ticks;
        }

        /// <summary>Only the wall clock moves, as when Windows sets it: no time passes.</summary>
        public void SetWallClock(DateTimeOffset now) => _now = now;
    }

    private sealed class FixedIdle : IIdleInput
    {
        public TimeSpan Idle { get; set; }

        public TimeSpan IdleFor() => Idle;
    }

    /// <summary>The time since the unlock is measured, not read from the wall clock: a clock set back after the unlock does not keep the interface unlocked.</summary>
    [Fact]
    public async Task The_time_since_the_unlock_does_not_go_back_with_the_wall_clock()
    {
        var clock = new JumpingClock();
        var input = new FixedIdle();
        var interfaceLock = new InterfaceLock(new FakeUserPresenceVerifier(), new FakeWindowsSession(), clock);
        var monitor = new IdleLockMonitor(interfaceLock, input, () => AppSettings.Default.WithIdleLock(enabled: true, minutes: 15), clock);
        await interfaceLock.LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);
        await interfaceLock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);

        clock.Pass(Minutes(20));
        input.Idle = Minutes(20);
        clock.SetWallClock(T0 - TimeSpan.FromHours(1));

        Assert.True(await monitor.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(LockReason.Idle, interfaceLock.Reason);
    }

    /// <summary>
    /// Windows never says whether Hello is there: the check that locks ends once the time limit passes, so the idle lock keeps
    /// working for the rest of the session.
    /// </summary>
    [Fact]
    public async Task A_windows_that_never_says_whether_hello_is_there_does_not_stop_the_idle_lock()
    {
        var harness = new Harness(new FakeUserPresenceVerifier { Availability = _ => new TaskCompletionSource<UserPresenceAvailability>().Task });

        var first = harness.AtAsync(Minutes(15));
        Assert.True(harness.Lock.IsLocked);
        harness.Clock.Advance(HelloAnswers.AvailabilityTimeout);
        Assert.True(await first.WaitAsync(TestContext.Current.CancellationToken));

        var unlocking = harness.Lock.UnlockWithHelloAsync(TestContext.Current.CancellationToken);
        harness.Clock.Advance(HelloAnswers.AvailabilityTimeout);
        await unlocking.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(harness.Lock.IsLocked);

        var second = harness.AtAsync(Minutes(31));
        Assert.True(harness.Lock.IsLocked);
        harness.Clock.Advance(HelloAnswers.AvailabilityTimeout);
        Assert.True(await second.WaitAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Without Windows Hello the idle lock locks Windows too, once: it does not lock Windows again at every check.</summary>
    [Fact]
    public async Task A_locked_interface_is_not_locked_again()
    {
        var harness = new Harness(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser));

        Assert.True(await harness.AtAsync(Minutes(15)));
        Assert.False(await harness.AtAsync(Minutes(16)));
        Assert.False(await harness.AtAsync(Minutes(60)));

        Assert.Equal(1, harness.Session.Locks);
    }

    [Fact]
    public async Task A_manual_lock_is_kept_as_it_is()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, TestContext.Current.CancellationToken);

        Assert.False(await harness.AtAsync(Minutes(30)));
        Assert.Equal(LockReason.Manual, harness.Lock.Reason);
    }

    [Fact]
    public async Task The_settings_are_read_at_each_check()
    {
        var harness = new Harness { Settings = AppSettings.Default };

        Assert.False(await harness.AtAsync(Minutes(5)));
        harness.Settings = AppSettings.Default.WithIdleLock(enabled: true, minutes: 5);

        Assert.True(await harness.AtAsync(Minutes(5)));
    }

    /// <summary>A time out of range (only a value built by hand can have one: the store reads it as the default) is the default.</summary>
    [Fact]
    public async Task A_time_out_of_range_counts_as_the_default()
    {
        var harness = new Harness { Settings = AppSettings.Default with { IdleLockEnabled = true, IdleLockMinutes = 0 } };

        Assert.False(await harness.AtAsync(Minutes(AppSettings.DefaultIdleLockMinutes - 1)));
        Assert.True(await harness.AtAsync(Minutes(AppSettings.DefaultIdleLockMinutes)));
    }

    /// <summary>When Windows cannot say when the last input was, the idle lock does nothing, rather than lock a person who is working.</summary>
    [Fact]
    public async Task When_windows_cannot_say_the_time_without_input_it_does_not_lock()
    {
        var harness = new Harness();
        harness.Input.Fails = new Win32Exception(5);

        Assert.False(await harness.AtAsync(Minutes(60)));
        Assert.False(harness.Lock.IsLocked);
    }

    [Fact]
    public async Task Once_started_it_checks_on_its_own_every_interval()
    {
        var harness = new Harness(poll: TimeSpan.FromSeconds(10)) { Settings = AppSettings.Default.WithIdleLock(enabled: true, minutes: 1) };
        harness.Monitor.Start();
        harness.Monitor.Start();

        harness.Clock.Advance(TimeSpan.FromSeconds(50));
        Assert.False(harness.Lock.IsLocked);
        harness.Clock.Advance(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 400 && !harness.Lock.IsLocked; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(harness.Lock.IsLocked);
        Assert.Equal(LockReason.Idle, harness.Lock.Reason);
    }

    [Fact]
    public async Task Disposed_it_checks_no_more()
    {
        var harness = new Harness(poll: TimeSpan.FromSeconds(10)) { Settings = AppSettings.Default.WithIdleLock(enabled: true, minutes: 1) };
        harness.Monitor.Start();

        harness.Monitor.Dispose();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(harness.Lock.IsLocked);
    }
}
