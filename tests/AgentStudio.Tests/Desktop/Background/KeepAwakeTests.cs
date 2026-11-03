using System.Collections.Concurrent;
using AgentStudio.Desktop.Background;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-5 and docs/16 D-40 (the side of WP3): «mantener despierto» behind <c>IKeepAwake</c>. The request to Windows is a
/// double that records its flags and its thread: no test changes the power state of the computer.
/// </summary>
public sealed class KeepAwakeTests
{
    [Fact]
    public void The_first_lease_keeps_the_computer_awake_and_the_last_one_lets_it_sleep()
    {
        var state = new FakeExecutionState();
        var keepAwake = new KeepAwakeService(state);

        var first = keepAwake.Hold("ejecución 20261021-aaaaaa");
        var second = keepAwake.Hold("debate deb-20261021-bbbbbb");
        Assert.True(keepAwake.IsHeld);
        Assert.Equal([true], state.Requests);

        first.Dispose();
        first.Dispose();
        Assert.True(keepAwake.IsHeld);
        Assert.Equal([true], state.Requests);

        second.Dispose();
        Assert.False(keepAwake.IsHeld);
        Assert.Equal([true, false], state.Requests);

        using (keepAwake.Hold("otra vez"))
        {
            Assert.True(keepAwake.IsHeld);
        }

        Assert.Equal([true, false, true, false], state.Requests);
    }

    [Fact]
    public void A_lease_needs_a_reason()
    {
        var keepAwake = new KeepAwakeService(new FakeExecutionState());

        Assert.Throws<ArgumentException>(() => keepAwake.Hold(" "));
        Assert.False(keepAwake.IsHeld);
    }

    [Fact]
    public async Task Every_request_to_windows_is_made_from_one_thread_of_its_own_with_the_right_flags()
    {
        var calls = new ConcurrentQueue<(int Thread, uint Flags)>();
        var thread = new ExecutionStateThread(flags =>
        {
            calls.Enqueue((Environment.CurrentManagedThreadId, flags));
            return 0x80000000;
        });

        // From several threads, as the UI and the workers would.
        await Task.Run(() => thread.KeepSystemAwake(true), TestContext.Current.CancellationToken);
        thread.KeepSystemAwake(false);
        await Task.Run(() => thread.KeepSystemAwake(true), TestContext.Current.CancellationToken);
        thread.Dispose();
        thread.Dispose();

        // On, off, on, and the end of the thread clears what it held.
        Assert.Equal([0x80000001u, 0x80000000u, 0x80000001u, 0x80000000u], calls.Select(c => c.Flags));
        Assert.Single(calls.Select(c => c.Thread).Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, calls.First().Thread);
        Assert.Throws<ObjectDisposedException>(() => thread.KeepSystemAwake(true));
    }

    [Fact]
    public void A_zero_from_windows_is_a_refusal_that_reaches_the_caller_and_the_thread_goes_on()
    {
        uint answer = 0;
        using var thread = new ExecutionStateThread(_ => answer);

        var refused = Assert.Throws<InvalidOperationException>(() => thread.KeepSystemAwake(true));
        Assert.Contains("SetThreadExecutionState returned 0", refused.Message, StringComparison.Ordinal);
        Assert.Contains("keep the computer awake", refused.Message, StringComparison.Ordinal);
        Assert.Contains("sleep again", Assert.Throws<InvalidOperationException>(() => thread.KeepSystemAwake(false)).Message, StringComparison.Ordinal);

        answer = 0x80000000;
        thread.KeepSystemAwake(true);
    }

    [Fact]
    public void A_call_that_throws_reaches_the_caller_as_it_is_and_the_thread_goes_on()
    {
        var fail = true;
        using var thread = new ExecutionStateThread(_ => fail ? throw new InvalidOperationException("The native call failed (simulated).") : 0x80000000);

        Assert.Equal("The native call failed (simulated).", Assert.Throws<InvalidOperationException>(() => thread.KeepSystemAwake(true)).Message);
        fail = false;
        thread.KeepSystemAwake(true);
    }

    [Fact]
    public void A_refusal_of_windows_is_written_in_the_log_and_the_lease_still_counts()
    {
        var log = new RecordingLogger<KeepAwakeService>();
        var keepAwake = new KeepAwakeService(new RefusingExecutionState(), log);

        var lease = keepAwake.Hold("ejecución 20261021-aaaaaa");
        Assert.True(keepAwake.IsHeld);
        lease.Dispose();

        Assert.False(keepAwake.IsHeld);
        Assert.Equal(
            ["Windows did not keep the computer awake (ejecución 20261021-aaaaaa).", "Windows did not let the computer sleep again (ejecución 20261021-aaaaaa)."],
            log.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Select(e => e.Message));
    }

    [Theory]
    [InlineData(true, 0x80000001u)]
    [InlineData(false, 0x80000000u)]
    public void On_is_continuous_and_system_required_and_off_is_continuous_alone(bool on, uint flags) =>
        Assert.Equal(flags, ExecutionStateThread.Flags(on));

    [Fact]
    public void While_working_the_computer_is_kept_awake_only_with_the_setting_and_only_while_there_is_work()
    {
        var state = new FakeExecutionState();
        var keepAwake = new KeepAwakeService(state);
        using var registry = Registry();
        var enabled = true;
        using var whileWorking = new KeepAwakeWhileWorking(registry, keepAwake, () => enabled);
        Assert.False(whileWorking.IsHolding);

        var run = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 };
        registry.Track(run);
        Assert.True(whileWorking.IsHolding);
        Assert.True(keepAwake.IsHeld);

        run.Move(active: false);
        Assert.False(whileWorking.IsHolding);
        Assert.False(keepAwake.IsHeld);

        run.Move(active: true);
        Assert.True(whileWorking.IsHolding);

        // The setting is turned off and saved: released at once, and not taken again while it stays off.
        enabled = false;
        whileWorking.Refresh();
        Assert.False(keepAwake.IsHeld);
        run.Move(attempts: 2);
        Assert.False(keepAwake.IsHeld);

        Assert.Equal([true, false, true, false], state.Requests);
    }

    [Fact]
    public void Without_the_setting_nothing_is_ever_asked_of_windows()
    {
        var state = new FakeExecutionState();
        using var registry = Registry();
        using var whileWorking = new KeepAwakeWhileWorking(registry, new KeepAwakeService(state), () => false);

        registry.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa"));

        Assert.False(whileWorking.IsHolding);
        Assert.Empty(state.Requests);
    }

    [Fact]
    public void Disposing_releases_the_lease()
    {
        var state = new FakeExecutionState();
        var keepAwake = new KeepAwakeService(state);
        using var registry = Registry();
        registry.Track(new FakeWork(BackgroundWorkKind.Debate, ProjectC, "deb-20261021-cccccc", pausable: false));
        var whileWorking = new KeepAwakeWhileWorking(registry, keepAwake, () => true);
        Assert.True(keepAwake.IsHeld);

        whileWorking.Dispose();
        whileWorking.Dispose();

        Assert.False(keepAwake.IsHeld);
        Assert.Equal([true, false], state.Requests);
    }

    private sealed class FakeExecutionState : IExecutionState
    {
        public List<bool> Requests { get; } = [];

        public void KeepSystemAwake(bool on) => Requests.Add(on);
    }

    /// <summary>Windows that refuses every request, as a 0 from <c>SetThreadExecutionState</c> does.</summary>
    private sealed class RefusingExecutionState : IExecutionState
    {
        public void KeepSystemAwake(bool on) => throw new InvalidOperationException("SetThreadExecutionState returned 0 (simulated).");
    }
}
