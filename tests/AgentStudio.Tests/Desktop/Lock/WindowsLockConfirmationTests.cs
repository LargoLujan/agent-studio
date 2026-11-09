using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// The verified lock of Windows (docs/17 D-40 (a), ADR-0053): it trusts the state of the session, never a message. The session
/// is a double: no test locks the session of the person running it. The clock is fake and moves by hand.
/// </summary>
public sealed class WindowsLockConfirmationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Moves the fake clock a poll at a time until <paramref name="pending"/> ends.</summary>
    private static async Task<WindowsLockResult> RunAsync(FakeTimeProvider clock, Task<WindowsLockResult> pending)
    {
        for (var i = 0; i < 5000 && !pending.IsCompleted; i++)
        {
            clock.Advance(WindowsLockConfirmation.PollInterval);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(pending.IsCompleted, "The lock of Windows never answered.");
        return await pending;
    }

    [Fact]
    public async Task An_unlock_seen_after_the_session_was_seen_locked_confirms_the_person()
    {
        var clock = new FakeTimeProvider(T0);
        var session = FakeWindowsSessionState.Saying(
            SessionLockState.Unlocked,
            SessionLockState.Unlocked,
            SessionLockState.Locked,
            SessionLockState.Locked,
            SessionLockState.Unknown,
            SessionLockState.Unlocked);

        var result = await RunAsync(clock, new WindowsLockConfirmation(session, clock).LockAndConfirmAsync(Timeout, TestContext.Current.CancellationToken));

        Assert.Equal(WindowsLockResult.UnlockedAfterRequest, result);
        Assert.Equal(1, session.Locks);
    }

    /// <summary>A session never seen locked (the lock did not take, or someone says it unlocked) confirms nothing, however unlocked it reads.</summary>
    [Fact]
    public async Task A_session_never_seen_locked_confirms_nothing()
    {
        var clock = new FakeTimeProvider(T0);
        var session = FakeWindowsSessionState.Saying(SessionLockState.Unlocked);

        var result = await RunAsync(clock, new WindowsLockConfirmation(session, clock).LockAndConfirmAsync(Timeout, TestContext.Current.CancellationToken));

        Assert.Equal(WindowsLockResult.NotVerified, result);
        Assert.Equal(1, session.Locks);
        Assert.True(clock.GetUtcNow() - T0 >= WindowsLockConfirmation.LockedWithin);
        Assert.True(clock.GetUtcNow() - T0 < Timeout);
    }

    [Fact]
    public async Task A_lock_windows_refuses_confirms_nothing()
    {
        var clock = new FakeTimeProvider(T0);
        var session = new FakeWindowsSessionState { Refuses = true };

        var result = await new WindowsLockConfirmation(session, clock).LockAndConfirmAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(WindowsLockResult.LockFailed, result);
    }

    /// <summary>Before locking, the session must read as unlocked; otherwise its state cannot be trusted and nothing is locked.</summary>
    [Theory]
    [InlineData(SessionLockState.Locked)]
    [InlineData(SessionLockState.Unknown)]
    public async Task A_session_that_does_not_read_as_unlocked_before_the_request_is_not_locked_and_confirms_nothing(SessionLockState before)
    {
        var clock = new FakeTimeProvider(T0);
        var session = FakeWindowsSessionState.Saying(before, SessionLockState.Locked, SessionLockState.Unlocked);

        var result = await new WindowsLockConfirmation(session, clock).LockAndConfirmAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(WindowsLockResult.NotVerified, result);
        Assert.Equal(0, session.Locks);
    }

    [Fact]
    public async Task A_state_windows_cannot_give_confirms_nothing()
    {
        var clock = new FakeTimeProvider(T0);
        var session = new FakeWindowsSessionState { ReadFails = new System.ComponentModel.Win32Exception(5) };

        var result = await new WindowsLockConfirmation(session, clock).LockAndConfirmAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(WindowsLockResult.NotVerified, result);
        Assert.Equal(0, session.Locks);
    }

    [Fact]
    public async Task Nobody_unlocking_within_the_time_given_is_a_timeout()
    {
        var clock = new FakeTimeProvider(T0);
        var session = FakeWindowsSessionState.Saying(SessionLockState.Unlocked, SessionLockState.Locked);

        var result = await RunAsync(clock, new WindowsLockConfirmation(session, clock).LockAndConfirmAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));

        Assert.Equal(WindowsLockResult.TimedOut, result);
        Assert.True(clock.GetUtcNow() - T0 >= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_withdrawn_question_stops_the_wait_and_gets_no_result()
    {
        var clock = new FakeTimeProvider(T0);
        var session = FakeWindowsSessionState.Saying(SessionLockState.Unlocked, SessionLockState.Locked);
        using var withdrawn = new CancellationTokenSource();

        var pending = new WindowsLockConfirmation(session, clock).LockAndConfirmAsync(Timeout, withdrawn.Token);
        await withdrawn.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_time_that_is_not_positive_is_refused(int minutes)
    {
        var lockConfirmation = new WindowsLockConfirmation(new FakeWindowsSessionState(), new FakeTimeProvider(T0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lockConfirmation.LockAndConfirmAsync(TimeSpan.FromMinutes(minutes), CancellationToken.None));
    }

    /// <summary>
    /// The real session, read and never locked (LocalTools: it reads the session of whoever runs it). It checks the layout of
    /// <c>WTSINFOEXW</c> and the meaning of <c>SessionFlags</c> against what Windows shows: the lock screen of a session is
    /// <c>LogonUI.exe</c> running in it, so the session reads as locked exactly while it runs there (an agent may run the tests
    /// with the session locked; a person, with it unlocked).
    /// </summary>
    [Fact]
    [Trait("Category", "LocalTools")]
    public void The_real_session_of_whoever_runs_the_tests_reads_as_windows_shows_it()
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var lockScreen = System.Diagnostics.Process.GetProcessesByName("LogonUI").Any(p => p.SessionId == session);

        Assert.Equal(lockScreen ? SessionLockState.Locked : SessionLockState.Unlocked, new WindowsSessionState().Read());
    }
}
