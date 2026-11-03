using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// A pause or a retry the run did not take (docs/16 D-39; <see cref="IOrchestrationRun.PauseAsync"/> and
/// <see cref="IOrchestrationRun.RetryBlockedAsync"/> fail when the run ended or stopped before applying them): whoever asked
/// is told in Spanish. The monitor says it; the tray, whose window may be hidden, notifies it, also when the run could no longer
/// take the command when the person clicked; and the managed exit goes on. The engine is a fake.
/// </summary>
public sealed class RunCommandsNotTakenTests
{
    private static readonly RunId Run = new("20261021-aaaaaa");

    [Fact]
    public async Task The_monitor_says_in_spanish_that_a_pause_or_a_retry_was_not_applied()
    {
        var monitor = Monitor(new RunThatEnded(Snapshot(RunPhase.Running, WorkTaskStatus.Blocked)));

        await monitor.PauseCommand.ExecuteAsync(null);
        var paused = monitor.ErrorMessage;
        await monitor.RetryBlockedCommand.ExecuteAsync(monitor.Tasks.Single());
        var retried = monitor.ErrorMessage;

        Assert.Equal("No se pudo pausar la ejecución: " + RunMonitorViewModel.PauseNotApplied, paused);
        Assert.Equal("No se pudo reintentar T1: " + RunMonitorViewModel.RetryNotApplied, retried);
        Assert.DoesNotContain("has ended", paused, StringComparison.Ordinal);
        Assert.False(await monitor.TryPauseAsync());
    }

    [Fact]
    public async Task A_pause_the_run_did_not_take_throws_in_english_and_the_monitor_says_it_in_spanish()
    {
        var monitor = Monitor(new RunThatEnded(Snapshot(RunPhase.Running, WorkTaskStatus.Running)));
        var work = new RunWork(RootA, monitor, () => ProjectA);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(work.PauseAsync);

        Assert.Equal($"Run {Run} did not take the pause: it had ended, or stopped before the pause was in its log.", thrown.Message);
        Assert.Equal("No se pudo pausar la ejecución: " + RunMonitorViewModel.PauseNotApplied, monitor.ErrorMessage);
    }

    [Fact]
    public async Task The_tray_notifies_a_pause_the_run_did_not_take_without_naming_the_project()
    {
        var (tray, work) = Tray(new RunThatEnded(Snapshot(RunPhase.Running, WorkTaskStatus.Running)));

        await Menus.Choose(tray.OpenMenu(), "Pausar");

        var notice = Assert.Single(tray.Notifications);
        Assert.Equal(new TrayNotification("No se pudo pausar", TrayController.RunCommandNotApplied), notice);
        Assert.DoesNotContain("monorepo", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Run.Value, notice.Text, StringComparison.Ordinal);
        Assert.Equal("No se pudo pausar la ejecución: " + RunMonitorViewModel.PauseNotApplied, Assert.IsType<RunWork>(Assert.Single(work.Active)).Monitor.ErrorMessage);
    }

    [Fact]
    public async Task The_tray_notifies_a_command_the_run_could_no_longer_take_when_the_person_clicked()
    {
        var run = new FakeRun(Snapshot(RunPhase.Running, WorkTaskStatus.Running));
        var (tray, work) = Tray(run);
        var menu = tray.OpenMenu();
        var monitor = Assert.IsType<RunWork>(Assert.Single(work.Active)).Monitor;

        // Between opening the menu and the click, the run was paused from elsewhere: «Pausar» no longer applies.
        run.Emit(new RunPhaseChangedEvent(T0, RunPhase.Paused, null));
        await WaitUntilAsync(() => monitor.Phase == RunPhase.Paused, "The monitor did not see the pause.");
        await Menus.Find(menu, "Pausar").Action!();

        Assert.Equal(new TrayNotification("No se pudo pausar", TrayController.RunCommandNotApplied), Assert.Single(tray.Notifications));
        Assert.DoesNotContain("pause", run.Calls);
    }

    [Fact]
    public async Task Pausing_to_exit_a_run_that_ended_meanwhile_does_not_stop_the_exit()
    {
        var work = Registry();
        var monitor = Monitor(new RunThatEnded(Snapshot(RunPhase.Running, WorkTaskStatus.Pending)));
        work.Track(new RunWork(RootA, monitor, () => ProjectA));
        var recorder = new FakeRunExitRecorder();
        var shutdown = new FakeAppShutdown();
        var exit = new ManagedExit(work, new FakeExitPrompt(ExitChoice.PauseAndExitWhenIdle), recorder, new FakeShellWindow(), shutdown);

        await exit.ExitAsync();

        Assert.Equal([("p-a", Run.Value, RunExitChoice.PauseAndExitWhenIdle)], recorder.Recorded.ToList());
        Assert.Equal(1, shutdown.Calls);
        Assert.Equal("No se pudo pausar la ejecución: " + RunMonitorViewModel.PauseNotApplied, monitor.ErrorMessage);
    }

    private static RunSnapshot Snapshot(RunPhase phase, WorkTaskStatus status) =>
        new(Run, phase, [FakeRun.View("T1", status: status, blockReason: status == WorkTaskStatus.Blocked ? "Interrupted" : null)], FakeRun.Healthy, 1);

    private static RunMonitorViewModel Monitor(IOrchestrationRun run)
    {
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        monitor.Attach(run, ProjectA);
        return monitor;
    }

    private static (FakeTrayIcon Tray, BackgroundWorkRegistry Work) Tray(IOrchestrationRun run)
    {
        var work = Registry();
        work.Track(new RunWork(RootA, Monitor(run), () => ProjectA));
        var tray = new FakeTrayIcon();
        var exit = new ManagedExit(work, new FakeExitPrompt(), new FakeRunExitRecorder(), new FakeShellWindow(), new FakeAppShutdown());
        var controller = new TrayController(tray, work, exit, new FakePanelPresenter(), TrayShellActions.None, new FakeFolderOpener(), @"C:\registros", new FakeTimeProvider(T0));
        controller.Start();
        return (tray, work);
    }

    /// <summary>A run whose loop ended without its monitor knowing yet: it refuses a pause and a retry, as the engine does then.</summary>
    private sealed class RunThatEnded(RunSnapshot snapshot) : IOrchestrationRun
    {
        private readonly FakeRun _run = new(snapshot);

        public RunId Id => _run.Id;

        public IAsyncEnumerable<OrchestrationEvent> Events => _run.Events;

        public Task<RunResult> Completion => _run.Completion;

        public RunSnapshot Snapshot() => _run.Snapshot();

        public Task PauseAsync() =>
            Task.FromException(new InvalidOperationException($"Run {Id} has ended: its dispatch was not paused, and nothing was recorded."));

        public Task ResumeDispatchAsync() => Task.CompletedTask;

        public Task RetryBlockedAsync(TaskId task) => Task.FromException(new InvalidOperationException($"Run {Id} has ended; resume it to retry its tasks."));

        public Task CancelAsync(string reason) => Task.CompletedTask;

        public ValueTask DisposeAsync() => _run.DisposeAsync();
    }
}
