using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Localization;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// The managed exit waits for what the app's services finish (the mailbox of GitHub, docs/21 D-25 and P78) without blocking: the wait comes after the choice was written in the logs and the
/// runs were paused, it says so while it lasts, and the app ends only when it is over. Whatever happens in the wait, the app ends.
/// </summary>
public sealed class ManagedExitFarewellTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly BackgroundWorkRegistry _work = Registry();
    private readonly FakeExitPrompt _prompt = new();
    private readonly FakeRunExitRecorder _recorder = new();
    private readonly FakeShellWindow _window = new();
    private readonly EndingShutdown _shutdown = new();

    /// <summary>The end of the app, counted, with a signal the test can wait for (the exit that comes from an event is not awaited by anyone).</summary>
    private sealed class EndingShutdown : IAppShutdown
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Shutdown()
        {
            Interlocked.Increment(ref _calls);
            Ended.TrySetResult();
        }
    }

    private ManagedExit Exit(ExitFarewell? farewell) => new(_work, _prompt, _recorder, _window, _shutdown, null, null, null, farewell);

    private static ExitFarewell Waiting(TaskCompletionSource entered, TaskCompletionSource release, bool pending = true) =>
        new(() => pending, async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Exiting_waits_for_the_mailbox_without_blocking_says_so_and_ends_the_app_only_when_it_is_over()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = Signal();
        var release = Signal();
        var exit = Exit(Waiting(entered, release));
        var notices = new List<string>();
        exit.Finishing += (_, notice) => notices.Add(notice);

        // The call returns a task that has not ended: the thread that made it is free (a blocking wait would not have come back).
        var leaving = exit.ExitAsync();
        await entered.Task.WaitAsync(Guard, ct);

        Assert.False(leaving.IsCompleted);
        Assert.Equal(0, _shutdown.Calls);
        Assert.True(exit.IsFinishing);
        Assert.Equal(ExitState.Exiting, exit.State);
        Assert.Equal([ManagedExit.FinishingNotice], notices);
        Assert.Equal("Terminando lo pendiente con GitHub…", ManagedExit.FinishingNotice);

        // While it waits, the window is gone: nothing new can be asked of an app that is leaving.
        Assert.False(_window.IsVisible);

        release.SetResult();
        Assert.Equal(CloseOutcome.Exiting, await leaving.WaitAsync(Guard, ct));
        Assert.Equal(1, _shutdown.Calls);
        Assert.False(exit.IsFinishing);
    }

    [Fact]
    public async Task The_wait_comes_after_the_choice_is_written_in_the_logs_and_before_the_app_ends()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = Signal();
        var entered = Signal();
        var seen = new List<string>();
        var farewell = new ExitFarewell(() => true, async () =>
        {
            seen.Add($"recorded:{_recorder.Recorded.Count} shutdown:{_shutdown.Calls}");
            entered.SetResult();
            await release.Task;
        });
        _work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
        _prompt.Answer = ExitChoice.ExitNow;
        var exit = Exit(farewell);

        var leaving = exit.ExitAsync();
        await entered.Task.WaitAsync(Guard, ct);

        // The choice was written before the wait (nothing the exit does with the work is reordered), and the app had not ended.
        Assert.Equal(["recorded:1 shutdown:0"], seen);
        release.SetResult();
        await leaving.WaitAsync(Guard, ct);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task Pausing_and_exiting_waits_for_the_mailbox_once_the_attempts_ended()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = Signal();
        var release = Signal();
        var busy = new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 };
        _work.Track(busy);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit(Waiting(entered, release));

        await exit.ExitAsync().WaitAsync(Guard, ct);
        Assert.Equal(ExitState.WaitingForIdle, exit.State);
        Assert.False(entered.Task.IsCompleted, "it waited for the mailbox while an attempt was still running");

        busy.Move(attempts: 0);
        await entered.Task.WaitAsync(Guard, ct);

        // The attempts ended: the exit is under way but the app does not end until the mailbox did.
        Assert.Equal(ExitState.Exiting, exit.State);
        Assert.Equal(0, _shutdown.Calls);
        release.SetResult();
        await _shutdown.Ended.Task.WaitAsync(Guard, ct);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task With_nothing_going_on_in_the_mailbox_it_waits_without_telling_anyone()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = Signal();
        var release = Signal();
        release.SetResult();
        var exit = Exit(Waiting(entered, release, pending: false));
        var notices = new List<string>();
        exit.Finishing += (_, notice) => notices.Add(notice);

        await exit.ExitAsync().WaitAsync(Guard, ct);

        Assert.True(entered.Task.IsCompleted);
        Assert.Empty(notices);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task A_wait_that_fails_does_not_keep_the_app_from_ending()
    {
        var ct = TestContext.Current.CancellationToken;
        var exit = Exit(new ExitFarewell(() => true, () => Task.FromException(new InvalidOperationException("the mailbox failed"))));

        Assert.Equal(CloseOutcome.Exiting, await exit.ExitAsync().WaitAsync(Guard, ct));

        Assert.Equal(1, _shutdown.Calls);
        Assert.False(exit.IsFinishing);
    }

    [Fact]
    public async Task Exiting_now_from_the_tray_while_waiting_for_the_attempts_also_waits_for_the_mailbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = Signal();
        var release = Signal();
        _work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 2 });
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit(Waiting(entered, release));
        await exit.ExitAsync().WaitAsync(Guard, ct);

        var now = exit.ExitNowAsync();
        await entered.Task.WaitAsync(Guard, ct);

        Assert.False(now.IsCompleted);
        Assert.Equal(0, _shutdown.Calls);
        release.SetResult();
        await now.WaitAsync(Guard, ct);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public void The_tooltip_says_that_it_is_finishing_and_not_only_exiting()
    {
        var tip = TrayToolTip.Compose([], ExitState.Exiting, null, false, TimeZoneInfo.Utc, finishing: true);
        var plain = TrayToolTip.Compose([], ExitState.Exiting, null, false, TimeZoneInfo.Utc);

        Assert.Contains(Texts.Get("Background_Tooltip_Finishing"), tip, StringComparison.Ordinal);
        Assert.DoesNotContain(Texts.Get("Background_Tooltip_Finishing"), plain, StringComparison.Ordinal);
        Assert.True(tip.Length <= TrayToolTip.MaxLength);
    }
}
