using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Lock;

/// <summary>
/// F8-10: the lock of the interface. It hides the content and the work goes on; the questions wait for the unlock, which
/// takes Windows Hello (a double) or, without Hello, an unlock of Windows the app asked for. Nothing here reaches Windows.
/// </summary>
public sealed class InterfaceLockTests
{
    private static readonly ApprovalRequest Delivery = new(
        "deliver-integration", "Entregar la ejecución 20261021-abc123 en main.", RiskLevel.Critical, null, [@"src\Calc.cs"], "p-1", "20261021-abc123");

    private sealed class Harness
    {
        public Harness(FakeUserPresenceVerifier? hello = null)
        {
            Hello = hello ?? new FakeUserPresenceVerifier();
            Lock = new InterfaceLock(Hello, Session, Clock);
            Lock.Changed += (_, _) => Changes++;
        }

        public FakeUserPresenceVerifier Hello { get; }

        public FakeWindowsSession Session { get; } = new();

        public FakeTimeProvider Clock { get; } = new(T0);

        public InterfaceLock Lock { get; }

        public int Changes { get; private set; }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unlocked_interface_keeps_no_question_waiting()
    {
        var harness = new Harness();

        var period = await harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);

        Assert.False(harness.Lock.IsLocked);
        Assert.False(period.IsCancellationRequested);
        Assert.Empty(harness.Lock.Waiting);
        Assert.Equal(T0, harness.Lock.LastUnlockedAt);
    }

    [Fact]
    public async Task Locking_hides_the_interface_and_a_question_waits_with_its_action_until_windows_hello_unlocks_it()
    {
        var harness = new Harness();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        var locked = await harness.Lock.LockAsync(LockReason.Manual, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var waiting = harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);

        Assert.Equal(LockOutcome.Locked, locked.Outcome);
        Assert.True(harness.Lock.IsLocked);
        Assert.Equal((LockReason.Manual, T0.AddMinutes(5)), (harness.Lock.Reason, harness.Lock.LockedAt));
        Assert.False(waiting.IsCompleted);
        Assert.Equal(new WaitingQuestion("deliver-integration", RiskLevel.Critical, T0.AddMinutes(7)), Assert.Single(harness.Lock.Waiting));

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var unlocked = await harness.Lock.UnlockWithHelloAsync(Ct);
        var period = await waiting.WaitAsync(Ct);

        Assert.Equal(UnlockOutcome.Unlocked, unlocked.Outcome);
        Assert.False(harness.Lock.IsLocked);
        Assert.Null(harness.Lock.Reason);
        Assert.False(period.IsCancellationRequested);
        Assert.Empty(harness.Lock.Waiting);
        Assert.Equal(T0.AddMinutes(8), harness.Lock.LastUnlockedAt);
        Assert.Equal(HelloAnswers.UnlockMessage, Assert.Single(harness.Hello.Messages));
        Assert.Equal(0, harness.Session.Locks);
    }

    [Fact]
    public async Task Locking_ends_the_unlocked_period_so_an_open_question_is_withdrawn()
    {
        var harness = new Harness();
        var period = await harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);

        await harness.Lock.LockAsync(LockReason.Idle, Ct);

        Assert.True(period.IsCancellationRequested);
        Assert.Equal(LockReason.Idle, harness.Lock.Reason);
    }

    [Fact]
    public async Task Locking_a_locked_interface_changes_nothing()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        var again = await harness.Lock.LockAsync(LockReason.Idle, Ct);

        Assert.Equal(LockOutcome.AlreadyLocked, again.Outcome);
        Assert.Equal((LockReason.Manual, T0), (harness.Lock.Reason, harness.Lock.LockedAt));
    }

    [Theory]
    [InlineData(UserPresenceResult.Canceled)]
    [InlineData(UserPresenceResult.RetriesExhausted)]
    [InlineData(UserPresenceResult.DeviceBusy)]
    [InlineData(UserPresenceResult.DeviceNotPresent)]
    [InlineData(UserPresenceResult.NotConfiguredForUser)]
    [InlineData(UserPresenceResult.DisabledByPolicy)]
    public async Task Only_a_confirmation_of_windows_hello_unlocks(UserPresenceResult result)
    {
        var harness = new Harness(FakeUserPresenceVerifier.Answering(result));
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        var waiting = harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);

        var attempt = await harness.Lock.UnlockWithHelloAsync(Ct);

        Assert.Equal((UnlockOutcome.NotConfirmed, (UserPresenceResult?)result), (attempt.Outcome, attempt.Result));
        Assert.True(harness.Lock.IsLocked);
        Assert.False(waiting.IsCompleted);
    }

    [Fact]
    public async Task A_verifier_that_fails_leaves_the_interface_locked()
    {
        var harness = new Harness(new FakeUserPresenceVerifier { Verify = (_, _) => throw new InvalidOperationException("No window.") });
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        var attempt = await harness.Lock.UnlockWithHelloAsync(Ct);

        Assert.Equal((UnlockOutcome.HelloFailed, "No window."), (attempt.Outcome, attempt.Problem));
        Assert.True(harness.Lock.IsLocked);
    }

    [Fact]
    public async Task When_windows_cannot_say_whether_hello_is_there_it_is_asked_to_unlock()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        harness.Hello.Availability = _ => throw new InvalidOperationException("WinRT failed.");

        var attempt = await harness.Lock.UnlockWithHelloAsync(Ct);

        Assert.Equal(UnlockOutcome.Unlocked, attempt.Outcome);
        Assert.Single(harness.Hello.Messages);
    }

    /// <summary>
    /// Windows never says whether Hello is there: the interface locks at once all the same, and the lock ends once the time
    /// limit passes, as with Hello (Windows is not locked: it never said Hello is absent). Unlocking then asks Hello.
    /// </summary>
    [Fact]
    public async Task When_windows_never_says_whether_hello_is_there_the_lock_still_locks_ends_and_unlocks()
    {
        var harness = new Harness(new FakeUserPresenceVerifier { Availability = _ => new TaskCompletionSource<UserPresenceAvailability>().Task });

        var locking = harness.Lock.LockAsync(LockReason.Idle, Ct);

        Assert.True(harness.Lock.IsLocked);
        Assert.False(locking.IsCompleted);
        harness.Clock.Advance(HelloAnswers.AvailabilityTimeout);
        var locked = await locking.WaitAsync(Ct);
        Assert.Equal(LockOutcome.Locked, locked.Outcome);
        Assert.Equal(0, harness.Session.Locks);

        var unlocking = harness.Lock.UnlockWithHelloAsync(Ct);
        harness.Clock.Advance(HelloAnswers.AvailabilityTimeout);
        var unlocked = await unlocking.WaitAsync(Ct);

        Assert.Equal(UnlockOutcome.Unlocked, unlocked.Outcome);
        Assert.Single(harness.Hello.Messages);
    }

    [Fact]
    public async Task Unlocking_a_lock_that_is_not_there_asks_nothing()
    {
        var harness = new Harness();

        var attempt = await harness.Lock.UnlockWithHelloAsync(Ct);

        Assert.Equal(UnlockOutcome.NotLocked, attempt.Outcome);
        Assert.Equal(0, harness.Hello.AvailabilityChecks);
        Assert.Empty(harness.Hello.Messages);
    }

    /// <summary>
    /// Without Windows Hello on this computer the app cannot unlock by itself, and it never asks for the Windows password:
    /// «Bloquear» locks Windows too (LockWorkStation), and the unlock of Windows, which verified the person, unlocks it.
    /// </summary>
    [Theory]
    [InlineData(UserPresenceAvailability.NotConfiguredForUser)]
    [InlineData(UserPresenceAvailability.DeviceNotPresent)]
    [InlineData(UserPresenceAvailability.DisabledByPolicy)]
    public async Task Without_windows_hello_locking_locks_windows_and_its_unlock_unlocks_the_interface(UserPresenceAvailability absent)
    {
        var harness = new Harness(FakeUserPresenceVerifier.Without(absent));

        var locked = await harness.Lock.LockAsync(LockReason.Idle, Ct);
        var waiting = harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);

        Assert.Equal((LockOutcome.LockedWithWindows, (UserPresenceAvailability?)absent), (locked.Outcome, locked.HelloAbsent));
        Assert.True(harness.Lock.IsLocked);
        Assert.True(harness.Lock.WaitsForWindows);
        Assert.Equal(absent, harness.Lock.HelloAbsent);
        Assert.Equal(1, harness.Session.Locks);

        harness.Session.RaiseUnlocked();
        await waiting.WaitAsync(Ct);

        Assert.False(harness.Lock.IsLocked);
        Assert.False(harness.Lock.WaitsForWindows);
        Assert.Null(harness.Lock.HelloAbsent);
        Assert.Empty(harness.Hello.Messages);
    }

    [Fact]
    public async Task Unlocking_when_windows_says_hello_is_not_there_never_unlocks_and_offers_to_lock_windows()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        harness.Hello.Availability = _ => Task.FromResult(UserPresenceAvailability.NotConfiguredForUser);

        var attempt = await harness.Lock.UnlockWithHelloAsync(Ct);

        Assert.Equal((UnlockOutcome.HelloAbsent, (UserPresenceAvailability?)UserPresenceAvailability.NotConfiguredForUser), (attempt.Outcome, attempt.Availability));
        Assert.True(harness.Lock.IsLocked);
        Assert.Equal(UserPresenceAvailability.NotConfiguredForUser, harness.Lock.HelloAbsent);
        Assert.Empty(harness.Hello.Messages);

        Assert.True(harness.Lock.LockWindows());
        harness.Session.RaiseUnlocked();

        Assert.False(harness.Lock.IsLocked);
    }

    [Fact]
    public async Task Bloquear_Windows_from_the_lock_screen_lets_the_unlock_of_windows_unlock_the_interface()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        Assert.True(harness.Lock.LockWindows());
        Assert.True(harness.Lock.WaitsForWindows);
        harness.Session.RaiseUnlocked();

        Assert.False(harness.Lock.IsLocked);
        Assert.Equal(1, harness.Session.Locks);
    }

    /// <summary>An unlock of Windows the app did not ask for (the person pressed Win+L, say) leaves the interface locked: only Hello unlocks it then.</summary>
    [Fact]
    public async Task An_unlock_of_windows_the_app_did_not_ask_for_does_not_unlock_the_interface()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);

        harness.Session.RaiseUnlocked();

        Assert.True(harness.Lock.IsLocked);
    }

    [Fact]
    public async Task When_windows_refuses_to_lock_the_interface_stays_locked_and_its_unlock_is_not_awaited()
    {
        var harness = new Harness(FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser));
        harness.Session.Refuses = true;

        var locked = await harness.Lock.LockAsync(LockReason.Manual, Ct);

        Assert.Equal(LockOutcome.WindowsRefused, locked.Outcome);
        Assert.True(harness.Lock.IsLocked);
        Assert.False(harness.Lock.WaitsForWindows);
        Assert.False(harness.Lock.LockWindows());
    }

    [Fact]
    public async Task Locking_windows_while_the_interface_is_unlocked_does_not_arm_the_unlock()
    {
        var harness = new Harness();

        Assert.True(harness.Lock.LockWindows());
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        harness.Session.RaiseUnlocked();

        Assert.True(harness.Lock.IsLocked);
    }

    [Fact]
    public async Task A_question_withdrawn_while_it_waits_leaves_the_lock_screen()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        using var withdrawn = new CancellationTokenSource();

        var waiting = harness.Lock.WaitUntilUnlockedAsync(Delivery, withdrawn.Token);
        Assert.Single(harness.Lock.Waiting);
        await withdrawn.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Empty(harness.Lock.Waiting);
    }

    [Fact]
    public async Task Every_change_the_lock_screen_shows_is_announced()
    {
        var harness = new Harness();

        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        var afterLock = harness.Changes;
        var waiting = harness.Lock.WaitUntilUnlockedAsync(Delivery, Ct);
        var afterQuestion = harness.Changes;
        await harness.Lock.UnlockWithHelloAsync(Ct);
        await waiting.WaitAsync(Ct);

        Assert.True(afterLock >= 1);
        Assert.True(afterQuestion > afterLock);
        Assert.True(harness.Changes > afterQuestion);
    }

    [Fact]
    public async Task Disposing_stops_listening_to_windows()
    {
        var harness = new Harness();
        await harness.Lock.LockAsync(LockReason.Manual, Ct);
        harness.Lock.LockWindows();

        harness.Lock.Dispose();
        harness.Session.RaiseUnlocked();

        Assert.True(harness.Lock.IsLocked);
    }
}
