using System.Diagnostics;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-9 and docs/16 D-16: exiting with work going on asks (pause and exit when the attempts going end, exit now, or
/// cancel), and the choice is written in the log of each piece of work before anything else happens. With nothing going on
/// the app exits without asking; «Cancelar» writes and touches nothing; a log that cannot be written holds neither the
/// others nor the exit; and Windows ending the session is written as <see cref="RunExitChoice.SessionEnded"/>.
/// </summary>
public sealed class ManagedExitTests
{
    private readonly BackgroundWorkRegistry _work = Registry();
    private readonly FakeExitPrompt _prompt = new();
    private readonly FakeRunExitRecorder _recorder = new();
    private readonly FakeShellWindow _window = new();
    private readonly FakeAppShutdown _shutdown = new();

    private ManagedExit Exit(Func<Task<bool>>? confirmLeaving = null) =>
        new(_work, _prompt, _recorder, _window, _shutdown, confirmLeaving is null ? null : _ => confirmLeaving());

    private FakeWork Run(string id, ProjectContext project, int attempts = 0)
    {
        var run = new FakeWork(BackgroundWorkKind.Run, project, id) { AttemptsInFlight = attempts };
        _work.Track(run);
        return run;
    }

    [Fact]
    public async Task Exiting_with_nothing_going_on_exits_without_asking_or_writing()
    {
        var exit = Exit();

        var outcome = await exit.ExitAsync();

        Assert.Equal(CloseOutcome.Exiting, outcome);
        Assert.Empty(_prompt.Asked);
        Assert.Empty(_recorder.Recorded);
        Assert.Equal(1, _shutdown.Calls);
        Assert.Equal(ExitState.Exiting, exit.State);
        Assert.True(exit.IsExiting);
    }

    [Fact]
    public async Task Cancelling_writes_nothing_pauses_nothing_and_leaves_the_window_as_it_was()
    {
        var run = Run("20261021-aaaaaa", ProjectA, attempts: 2);
        _prompt.Answer = ExitChoice.Cancel;
        var exit = Exit();

        var outcome = await exit.ExitAsync();

        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Single(_prompt.Asked);
        Assert.Empty(_recorder.Recorded);
        Assert.Equal(0, run.Pauses);
        Assert.Equal(0, _window.Hidden);
        Assert.Equal(0, _shutdown.Calls);
        Assert.Equal(ExitState.None, exit.State);
    }

    [Fact]
    public async Task Exiting_now_writes_the_choice_in_the_log_of_each_piece_of_work_with_an_id_and_exits()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        Run("20261021-bbbbbb", ProjectB);
        _work.Track(new FakeWork(BackgroundWorkKind.Debate, ProjectC, "deb-20261021-cccccc", pausable: false) { AttemptsInFlight = 2 });
        _work.Track(new FakeWork(BackgroundWorkKind.Audit, ProjectC, workId: null, pausable: false, root: RootC + "-auditoria"));
        _prompt.Answer = ExitChoice.ExitNow;

        var outcome = await Exit().ExitAsync();

        Assert.Equal(CloseOutcome.Exiting, outcome);
        Assert.Equal(
            [("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow), ("p-b", "20261021-bbbbbb", RunExitChoice.ExitNow), ("p-c", "deb-20261021-cccccc", RunExitChoice.ExitNow)],
            _recorder.Recorded.ToList());
        Assert.Equal(1, _shutdown.Calls);

        // The question listed the four, the audit too, and did not offer to keep going in the tray (this is «Salir»).
        var question = Assert.Single(_prompt.Asked);
        Assert.False(question.OffersKeepInTray);
        Assert.True(question.HasWorkThatCannotPause);
        Assert.Equal(["monorepo", "tienda", "docs-internas", "docs-internas-auditoria"], question.Work.Select(w => w.Project));
        Assert.Equal([true, true, false, false], question.Work.Select(w => w.CanPause));
    }

    [Fact]
    public async Task Pausing_and_exiting_pauses_the_runs_hides_the_window_and_exits_once_no_attempt_runs()
    {
        var busy = Run("20261021-aaaaaa", ProjectA, attempts: 2);
        var idle = Run("20261021-bbbbbb", ProjectB);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        var states = new List<(ExitState State, int? Left)>();
        exit.StateChanged += (_, _) => states.Add((exit.State, exit.AttemptsLeft));

        var outcome = await exit.ExitAsync();

        Assert.Equal(CloseOutcome.Exiting, outcome);
        Assert.Equal(
            [("p-a", "20261021-aaaaaa", RunExitChoice.PauseAndExitWhenIdle), ("p-b", "20261021-bbbbbb", RunExitChoice.PauseAndExitWhenIdle)],
            _recorder.Recorded.ToList());
        Assert.Equal((1, 1), (busy.Pauses, idle.Pauses));
        Assert.Equal(1, _window.Hidden);
        Assert.Equal(0, _shutdown.Calls);
        Assert.Equal(ExitState.WaitingForIdle, exit.State);
        Assert.Equal(2, exit.AttemptsLeft);

        busy.Move(attempts: 1);
        Assert.Equal(1, exit.AttemptsLeft);
        Assert.Equal(0, _shutdown.Calls);

        busy.Move(attempts: 0);
        Assert.Equal(1, _shutdown.Calls);
        Assert.Equal(ExitState.Exiting, exit.State);
        Assert.Equal([(ExitState.WaitingForIdle, 2), (ExitState.WaitingForIdle, 1), (ExitState.Exiting, null)], states);

        // Nothing more is written once the choice is in the logs.
        Assert.Equal(2, _recorder.Recorded.Count);
    }

    [Fact]
    public async Task Pausing_and_exiting_waits_for_a_debate_that_cannot_be_paused_until_it_ends()
    {
        var debate = new FakeWork(BackgroundWorkKind.Debate, ProjectC, "deb-20261021-cccccc", pausable: false) { AttemptsInFlight = 1 };
        _work.Track(debate);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();

        await exit.ExitAsync();

        Assert.Equal(0, debate.Pauses);
        Assert.Equal(ExitState.WaitingForIdle, exit.State);

        // Its turns ended, but it would open the next round: only its end lets the app exit.
        debate.Move(attempts: 0);
        Assert.Equal(0, _shutdown.Calls);

        debate.Move(active: false);
        Assert.Equal(1, _shutdown.Calls);
        Assert.Equal([("p-c", "deb-20261021-cccccc", RunExitChoice.PauseAndExitWhenIdle)], _recorder.Recorded.ToList());
    }

    [Fact]
    public async Task Work_whose_attempts_this_process_cannot_count_is_waited_for_until_it_ends()
    {
        var audit = new FakeWork(BackgroundWorkKind.Audit, ProjectC, workId: null, pausable: false) { AttemptsInFlight = null };
        _work.Track(audit);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();

        await exit.ExitAsync();

        Assert.Empty(_recorder.Recorded);
        Assert.Null(exit.AttemptsLeft);
        Assert.Equal(0, _shutdown.Calls);

        audit.Move(active: false);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task Pausing_and_exiting_with_nothing_running_exits_at_once()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 0);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();

        await exit.ExitAsync();

        Assert.Equal(1, _shutdown.Calls);
        Assert.Equal(ExitState.Exiting, exit.State);
    }

    [Fact]
    public async Task A_run_that_cannot_be_paused_is_waited_for_all_the_same()
    {
        var run = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        run.PauseFailure = new InvalidOperationException("The run already ended (simulated).");
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();

        await exit.ExitAsync();

        Assert.Equal(1, run.Pauses);
        Assert.Equal(ExitState.WaitingForIdle, exit.State);
        run.Move(attempts: 0);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task Closing_the_window_with_work_going_on_asks_with_keep_in_tray_and_keeping_writes_nothing()
    {
        var run = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.KeepInTray;

        var outcome = await Exit().WindowClosingAsync(CloseWindowAction.Ask);

        Assert.Equal(CloseOutcome.HiddenToTray, outcome);
        Assert.True(Assert.Single(_prompt.Asked).OffersKeepInTray);
        Assert.Equal(1, _window.Hidden);
        Assert.Empty(_recorder.Recorded);
        Assert.Equal(0, run.Pauses);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task Keeping_in_the_tray_is_not_an_answer_to_the_exit_from_the_tray()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.KeepInTray;

        var outcome = await Exit().ExitAsync();

        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(0, _window.Hidden);
        Assert.Empty(_recorder.Recorded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_the_window_set_to_keep_in_the_tray_hides_it_without_asking(bool hasWork)
    {
        if (hasWork)
        {
            Run("20261021-aaaaaa", ProjectA, attempts: 1);
        }

        var outcome = await Exit().WindowClosingAsync(CloseWindowAction.KeepInTray);

        Assert.Equal(CloseOutcome.HiddenToTray, outcome);
        Assert.Empty(_prompt.Asked);
        Assert.Equal(1, _window.Hidden);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task Closing_the_window_set_to_exit_with_work_asks_the_managed_exit_without_keep_in_tray()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.ExitNow;

        var outcome = await Exit().WindowClosingAsync(CloseWindowAction.Exit);

        Assert.Equal(CloseOutcome.Exiting, outcome);
        Assert.False(Assert.Single(_prompt.Asked).OffersKeepInTray);
        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow)], _recorder.Recorded.ToList());
        Assert.Equal(1, _shutdown.Calls);
    }

    [Theory]
    [InlineData(CloseWindowAction.Ask)]
    [InlineData(CloseWindowAction.Exit)]
    public async Task Closing_the_window_without_work_exits_without_asking(CloseWindowAction setting)
    {
        var outcome = await Exit().WindowClosingAsync(setting);

        Assert.Equal(CloseOutcome.Exiting, outcome);
        Assert.Empty(_prompt.Asked);
        Assert.Empty(_recorder.Recorded);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_holds_neither_the_others_nor_the_exit()
    {
        Run("20261021-aaaaaa", ProjectA);
        Run("20261021-bbbbbb", ProjectB);
        _recorder.FailingProjects.Add("p-a");
        _prompt.Answer = ExitChoice.ExitNow;

        await Exit().ExitAsync();

        Assert.Equal([("p-b", "20261021-bbbbbb", RunExitChoice.ExitNow)], _recorder.Recorded.ToList());
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task The_shell_can_stop_the_exit_before_anything_is_written()
    {
        var run = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var asked = 0;

        var outcome = await Exit(() =>
        {
            asked++;
            return Task.FromResult(false);
        }).ExitAsync();

        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(1, asked);
        Assert.Empty(_recorder.Recorded);
        Assert.Equal(0, run.Pauses);
        Assert.Equal(0, _shutdown.Calls);
        Assert.Equal(0, _window.Hidden);
    }

    [Fact]
    public async Task A_second_request_while_the_question_is_open_asks_nothing_more_and_the_first_answer_decides()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        var answer = new TaskCompletionSource<ExitChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new BlockingExitPrompt(answer.Task);
        var exit = new ManagedExit(_work, prompt, _recorder, _window, _shutdown);

        var first = exit.ExitAsync();
        var second = await exit.ExitAsync();
        var closing = await exit.WindowClosingAsync(CloseWindowAction.Ask);
        answer.SetResult(ExitChoice.ExitNow);

        Assert.Equal(CloseOutcome.StayOpen, second);
        Assert.Equal(CloseOutcome.StayOpen, closing);
        Assert.Equal(CloseOutcome.Exiting, await first);
        Assert.Equal(1, prompt.Asked);
        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow)], _recorder.Recorded.ToList());
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task Exiting_now_while_waiting_writes_it_for_what_is_still_going_on_and_exits()
    {
        var busy = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        await exit.ExitAsync();

        await exit.ExitNowAsync();

        Assert.Equal(
            [("p-a", "20261021-aaaaaa", RunExitChoice.PauseAndExitWhenIdle), ("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow)],
            _recorder.Recorded.ToList());
        Assert.Equal(1, _shutdown.Calls);

        // The attempts ending afterwards do not end the app twice.
        busy.Move(attempts: 0);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task Not_exiting_while_waiting_shows_the_window_and_stops_waiting()
    {
        var busy = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        await exit.ExitAsync();

        exit.CancelPendingExit();

        Assert.Equal(ExitState.None, exit.State);
        Assert.Equal(1, _window.Shown);
        busy.Move(attempts: 0);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task Closing_the_window_while_waiting_only_hides_it()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        await exit.ExitAsync();

        var outcome = await exit.WindowClosingAsync(CloseWindowAction.Ask);

        Assert.Equal(CloseOutcome.HiddenToTray, outcome);
        Assert.Single(_prompt.Asked);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public void The_end_of_the_session_is_written_as_session_ended_for_the_work_going_on()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _work.Track(new FakeWork(BackgroundWorkKind.Debate, ProjectC, "deb-20261021-cccccc", pausable: false));
        var exit = Exit();

        exit.RecordSessionEnded(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [("p-a", "20261021-aaaaaa", RunExitChoice.SessionEnded), ("p-c", "deb-20261021-cccccc", RunExitChoice.SessionEnded)],
            _recorder.Recorded.ToList());
        Assert.Empty(_prompt.Asked);
        Assert.True(exit.IsExiting);

        // Windows ends the process: the app does not shut itself down on top of it.
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public void The_end_of_the_session_waits_no_longer_than_its_budget()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _recorder.Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = Exit();
        var watch = Stopwatch.StartNew();

        exit.RecordSessionEnded(TimeSpan.FromMilliseconds(100));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"It waited {watch.Elapsed} for a write that never ends.");
        Assert.True(exit.IsExiting);
        Assert.Empty(_recorder.Recorded);
        _recorder.Hang.SetResult();
    }

    [Fact]
    public async Task Pausing_a_followed_run_asks_its_monitor_and_writes_in_the_log_of_its_project()
    {
        var run = new FakeRun(Snapshot("20261021-dddddd", RunPhase.Running, 3, TaskOf("T1", WorkTaskStatus.Running, "ejecutor"), TaskOf("T2", WorkTaskStatus.Pending)));
        var monitor = Follow(run, ProjectA);
        _work.TrackRun(RootA, monitor, () => ProjectA);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();

        await exit.ExitAsync();

        Assert.Contains("pause", run.Calls);
        Assert.Equal([("p-a", "20261021-dddddd", RunExitChoice.PauseAndExitWhenIdle)], _recorder.Recorded.ToList());
        Assert.Equal(1, exit.AttemptsLeft);
        var line = Assert.Single(Assert.Single(_prompt.Asked).Work);
        Assert.Equal(("monorepo", "Ejecución 20261021-dddddd", "En ejecución · 1 intento en curso", true), (line.Project, line.Title, line.State, line.CanPause));
    }

    // ---- A person's choice stays in force over a later end of the session (review of WP3, 1) ----

    [Fact]
    public async Task Not_exiting_and_then_the_end_of_the_session_writes_nothing_over_the_choice_of_the_person()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        await exit.ExitAsync();
        exit.CancelPendingExit();

        exit.RecordSessionEnded(TimeSpan.FromSeconds(5));

        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.PauseAndExitWhenIdle)], _recorder.Recorded.ToList());
        Assert.True(exit.IsExiting);
    }

    [Fact]
    public async Task The_end_of_the_session_while_waiting_to_exit_writes_nothing_over_the_choice()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        Run("20261021-bbbbbb", ProjectB, attempts: 2);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        await exit.ExitAsync();
        Assert.Equal(ExitState.WaitingForIdle, exit.State);

        exit.RecordSessionEnded(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(_recorder.Recorded, r => r.Choice == RunExitChoice.SessionEnded);
        Assert.Equal(2, _recorder.Recorded.Count);
        Assert.Equal(ExitState.Exiting, exit.State);
    }

    [Fact]
    public async Task The_end_of_the_session_while_exiting_writes_nothing()
    {
        var run = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _recorder.Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompt.Answer = ExitChoice.ExitNow;
        var exit = Exit();
        var exiting = exit.ExitAsync();
        Assert.True(exit.IsExiting);

        // Windows ends the session while the choice of the person is still being written.
        exit.RecordSessionEnded(TimeSpan.FromSeconds(5));
        _recorder.Hang.SetResult();
        await exiting;

        Assert.Equal([("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow)], _recorder.Recorded.ToList());
        Assert.True(run.IsActive);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task Work_nobody_decided_still_gets_the_end_of_the_session_after_a_choice_on_other_work()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        await exit.ExitAsync();
        exit.CancelPendingExit();

        // Started after «No salir»: nobody chose anything for it.
        Run("20261021-cccccc", ProjectB, attempts: 1);
        exit.RecordSessionEnded(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [("p-a", "20261021-aaaaaa", RunExitChoice.PauseAndExitWhenIdle), ("p-b", "20261021-cccccc", RunExitChoice.SessionEnded)],
            _recorder.Recorded.ToList());
    }

    // ---- Opening the window cancels a pending exit (review of WP3, 2) ----

    [Fact]
    public async Task Opening_the_window_while_waiting_cancels_the_exit_as_not_exiting_does_and_says_so()
    {
        var run = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        var notices = new List<string>();
        exit.PendingExitCancelled += (_, notice) => notices.Add(notice);
        await exit.ExitAsync();

        exit.ShowWindow();

        Assert.Equal(ExitState.None, exit.State);
        Assert.Equal(1, _window.Shown);
        Assert.Equal([ManagedExit.PendingExitCancelledNotice], notices);
        Assert.StartsWith("Abriste Agent Studio, así que ya no sale", ManagedExit.PendingExitCancelledNotice, StringComparison.Ordinal);

        run.Move(attempts: 0);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public void Opening_the_window_when_not_exiting_only_shows_it()
    {
        var exit = Exit();
        var notices = 0;
        exit.PendingExitCancelled += (_, _) => notices++;

        exit.ShowWindow();

        Assert.Equal(1, _window.Shown);
        Assert.Equal(0, notices);
        Assert.Equal(ExitState.None, exit.State);
    }

    // ---- What is written is what was asked about, still the same (review of WP3, 4) ----

    [Fact]
    public async Task A_run_that_ended_while_the_question_was_open_gets_no_entry()
    {
        var ending = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        Run("20261021-bbbbbb", ProjectB, attempts: 1);
        var answer = new TaskCompletionSource<ExitChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new BlockingExitPrompt(answer.Task);
        var exit = new ManagedExit(_work, prompt, _recorder, _window, _shutdown);

        var exiting = exit.ExitAsync();
        ending.Move(active: false);
        answer.SetResult(ExitChoice.ExitNow);
        await exiting;

        Assert.Equal([("p-b", "20261021-bbbbbb", RunExitChoice.ExitNow)], _recorder.Recorded.ToList());
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task Work_that_follows_another_run_after_the_question_gets_no_entry_and_neither_does_the_new_run()
    {
        var document = Run("20261021-aaaaaa", ProjectA, attempts: 1);
        var answer = new TaskCompletionSource<ExitChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new ManagedExit(_work, new BlockingExitPrompt(answer.Task), _recorder, _window, _shutdown);

        var exiting = exit.ExitAsync();
        document.WorkId = "20261021-dddddd";
        answer.SetResult(ExitChoice.ExitNow);
        await exiting;

        Assert.Empty(_recorder.Recorded);
    }

    // ---- «Salir ahora» is exiting from its first moment (review of WP3, 6) ----

    [Fact]
    public async Task Exiting_now_twice_or_not_exiting_while_the_choice_is_written_changes_nothing()
    {
        Run("20261021-aaaaaa", ProjectA, attempts: 1);
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();
        await exit.ExitAsync();
        _recorder.Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = exit.ExitNowAsync();
        Assert.True(exit.IsExiting);
        await exit.ExitNowAsync();
        exit.CancelPendingExit();
        exit.ShowWindow();
        _recorder.Hang.SetResult();
        await first;

        Assert.Equal(
            [("p-a", "20261021-aaaaaa", RunExitChoice.PauseAndExitWhenIdle), ("p-a", "20261021-aaaaaa", RunExitChoice.ExitNow)],
            _recorder.Recorded.ToList());
        Assert.Equal(1, _shutdown.Calls);
        Assert.Equal(ExitState.Exiting, exit.State);
    }

    // ---- A dispatched attempt not launched yet is in flight (review of WP3, 3) ----

    [Fact]
    public async Task The_exit_waits_for_an_attempt_that_was_dispatched_and_not_launched_yet()
    {
        var run = new FakeRun(Snapshot("20261021-eeeeee", RunPhase.Running, 1, TaskOf("T1", WorkTaskStatus.Pending, "ejecutor")));
        var monitor = Follow(run, ProjectA);
        _work.TrackRun(RootA, monitor, () => ProjectA);
        run.Emit(new TaskQueuedEvent(T0, new AgentStudio.Domain.Common.TaskId("T1"), AgentStudio.Domain.Providers.ProviderId.ClaudeCode));
        await WaitUntilAsync(() => monitor.Tasks[0].IsQueued, "The task was not queued.");
        _prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        var exit = Exit();

        await exit.ExitAsync();
        Assert.Equal(ExitState.WaitingForIdle, exit.State);
        Assert.Equal(1, exit.AttemptsLeft);
        Assert.Equal("En ejecución · 1 intento en curso", Assert.Single(_prompt.Asked.Single().Work).State);

        // Launched, it is still in flight; finished, nothing is left and the app exits.
        run.CurrentSnapshot = Snapshot("20261021-eeeeee", RunPhase.Running, 1, TaskOf("T1", WorkTaskStatus.Running, "ejecutor"));
        run.Emit(new TaskStatusChangedEvent(T0, new AgentStudio.Domain.Common.TaskId("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Lanzada"));
        await WaitUntilAsync(() => monitor.Tasks[0].Status == WorkTaskStatus.Running, "The task did not launch.");
        Assert.Equal(0, _shutdown.Calls);

        run.CurrentSnapshot = Snapshot("20261021-eeeeee", RunPhase.Running, 1, TaskOf("T1", WorkTaskStatus.Completed, "ejecutor"));
        run.Emit(new TaskStatusChangedEvent(T0, new AgentStudio.Domain.Common.TaskId("T1"), WorkTaskStatus.Running, WorkTaskStatus.Completed, "Terminada"));
        await WaitUntilAsync(() => _shutdown.Calls == 1, "The app did not exit once the attempt ended.");
    }

    /// <summary>A question that stays open until the test answers it, as the modal window does.</summary>
    private sealed class BlockingExitPrompt(Task<ExitChoice> answer) : IExitPrompt
    {
        public int Asked { get; private set; }

        public Task<ExitChoice> AskAsync(ExitQuestion question, CancellationToken cancellationToken)
        {
            Asked++;
            return answer;
        }
    }
}
