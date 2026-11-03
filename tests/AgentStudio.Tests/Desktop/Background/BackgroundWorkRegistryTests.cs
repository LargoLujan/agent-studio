using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Background;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// The work the tray, its panel, the managed exit and «mantener despierto» follow (docs/16 D-14 to D-16 and D-40): read
/// from the monitor of each run, the snapshot of each debate and <see cref="IProjectActivity"/>, a project counted once.
/// </summary>
public sealed class BackgroundWorkRegistryTests
{
    private static RunResult Result(string run, RunPhase phase) => new(new RunId(run), phase, FakeRun.Healthy, null, null, "Terminada.");

    [Fact]
    public async Task A_run_is_active_while_its_monitor_follows_it_unfinished_and_its_end_is_said()
    {
        var run = new FakeRun(Snapshot("20261021-aaaaaa", RunPhase.Running, 2, TaskOf("T1", WorkTaskStatus.Running, "ejecutor"), TaskOf("T2", WorkTaskStatus.Pending)));
        var monitor = Follow(run, ProjectA);
        using var registry = Registry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        registry.TrackRun(RootA, monitor, () => ProjectA);

        var work = Assert.IsType<RunWork>(Assert.Single(registry.Active));
        Assert.True(registry.HasActiveWork);
        Assert.Equal((BackgroundWorkKind.Run, "20261021-aaaaaa", 1, true), (work.Kind, work.WorkId, work.AttemptsInFlight, work.IsPausable));
        Assert.Equal(ProjectA, work.Project);
        Assert.Equal("Ejecución 20261021-aaaaaa", work.Title);
        Assert.Equal("En ejecución · 1 intento en curso", work.StateText);

        var before = changes;
        run.Finish(Result("20261021-aaaaaa", RunPhase.Succeeded));
        await WaitUntilAsync(() => !registry.HasActiveWork && changes > before, "The registry did not say the run ended.");

        Assert.Empty(registry.Active);
        Assert.Single(registry.All.OfType<RunWork>());
    }

    [Fact]
    public async Task The_status_of_a_task_moves_the_attempts_in_flight()
    {
        var run = new FakeRun(Snapshot("20261021-aaaaaa", RunPhase.Running, 2, TaskOf("T1", WorkTaskStatus.Running, "ejecutor")));
        var monitor = Follow(run, ProjectA);
        using var registry = Registry();
        registry.TrackRun(RootA, monitor, () => ProjectA);
        var work = Assert.IsType<RunWork>(Assert.Single(registry.Active));

        run.CurrentSnapshot = Snapshot("20261021-aaaaaa", RunPhase.Running, 2, TaskOf("T1", WorkTaskStatus.Completed, "ejecutor"));
        run.Emit(new TaskStatusChangedEvent(T0, new AgentStudio.Domain.Common.TaskId("T1"), WorkTaskStatus.Running, WorkTaskStatus.Completed, "Terminada"));
        await WaitUntilAsync(() => work.AttemptsInFlight == 0, "The task did not change status.");

        Assert.True(work.IsActive);
        Assert.Equal("En ejecución", work.StateText);
    }

    [Fact]
    public async Task An_attempt_dispatched_but_not_launched_yet_is_in_flight_and_says_so()
    {
        var run = new FakeRun(Snapshot("20261021-aaaaaa", RunPhase.Running, 1, TaskOf("T1", WorkTaskStatus.Pending, "ejecutor"), TaskOf("T2", WorkTaskStatus.Pending)));
        var monitor = Follow(run, ProjectA);
        using var registry = Registry();
        registry.TrackRun(RootA, monitor, () => ProjectA);
        var work = Assert.IsType<RunWork>(Assert.Single(registry.Active));
        Assert.Equal(0, work.AttemptsInFlight);
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        // The engine says a task is queued as soon as it dispatches it: its worker may already be handing the prompt to the CLI.
        run.Emit(new TaskQueuedEvent(T0, new AgentStudio.Domain.Common.TaskId("T1"), AgentStudio.Domain.Providers.ProviderId.ClaudeCode));
        await WaitUntilAsync(() => work.AttemptsInFlight == 1 && changes > 0, "The queued attempt was not seen.");

        Assert.Equal("En ejecución · 1 intento en curso", work.StateText);
        Assert.Equal(WorkTaskStatus.Pending, monitor.Tasks[0].Status);
    }

    [Fact]
    public async Task Pausing_a_followed_run_asks_its_monitor_and_only_when_it_can_be_paused()
    {
        var running = new FakeRun(Snapshot("20261021-aaaaaa", RunPhase.Running, 0));
        var paused = new FakeRun(Snapshot("20261021-bbbbbb", RunPhase.Paused, 0));
        using var first = new RunWork(RootA, Follow(running, ProjectA), () => ProjectA);
        using var second = new RunWork(RootB, Follow(paused, ProjectB), () => ProjectB);

        Assert.True(first.CanPause);
        Assert.False(second.CanPause);
        Assert.True(second.CanResume);
        await first.PauseAsync();
        await second.PauseAsync();
        await second.ResumeAsync();
        await first.CancelAsync();

        Assert.Equal(["pause", $"cancel:{AgentStudio.Desktop.Orchestrator.Monitor.RunMonitorViewModel.CancelReason}"], running.Calls);
        Assert.Equal(["resume"], paused.Calls);
    }

    [Fact]
    public async Task A_debate_is_active_until_it_ends_and_its_turns_in_course_are_its_attempts()
    {
        var debate = new FakeDebateRun(DebateSamples.Snapshot(turnsInFlight: 2));
        using var registry = Registry();

        registry.TrackDebate(RootC, ProjectC, debate);

        var work = Assert.IsType<DebateWork>(Assert.Single(registry.Active));
        Assert.Equal((BackgroundWorkKind.Debate, "deb-20261019-a1b2c3", 2, false), (work.Kind, work.WorkId, work.AttemptsInFlight, work.IsPausable));
        Assert.Equal("En deliberación · ronda 2 · 2 turnos en curso", work.StateText);

        debate.Fail(new OperationCanceledException());
        await WaitUntilAsync(() => !registry.HasActiveWork, "The registry did not see the debate end.");
    }

    [Fact]
    public void What_holds_a_project_that_no_screen_follows_is_listed_from_its_activity()
    {
        var activity = new FakeProjectActivity();
        using var registry = Registry(activity);
        registry.TrackProject(RootC, () => ProjectC);
        Assert.Empty(registry.Active);

        activity.Holding["p-c"] = ProjectActivityKind.Audit;
        var changes = 0;
        registry.Changed += (_, _) => changes++;
        registry.Poll();

        Assert.Equal(1, changes);
        var work = Assert.IsType<ProjectActivityWork>(Assert.Single(registry.Active));
        Assert.Equal((BackgroundWorkKind.Audit, (string?)null, (int?)null, false), (work.Kind, work.WorkId, work.AttemptsInFlight, work.IsPausable));
        Assert.Equal("Auditoría del proyecto", work.Title);

        // Nothing changed: no event.
        registry.Poll();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_project_held_by_a_run_that_is_followed_is_counted_once()
    {
        var activity = new FakeProjectActivity { Holding = { ["p-a"] = ProjectActivityKind.Run } };
        var monitor = Follow(new FakeRun(Snapshot("20261021-aaaaaa", RunPhase.Running, 0)), ProjectA);
        using var registry = Registry(activity);

        registry.TrackRun(RootA, monitor, () => ProjectA);
        registry.TrackProject(RootA, () => ProjectA);

        Assert.IsType<RunWork>(Assert.Single(registry.Active));
        Assert.Single(registry.All.OfType<ProjectActivityWork>());
    }

    [Fact]
    public void A_project_that_was_not_opened_yet_holds_nothing()
    {
        var activity = new FakeProjectActivity { Holding = { ["p-c"] = ProjectActivityKind.Debate } };
        using var registry = Registry(activity);

        registry.TrackProject(RootC, () => null);

        Assert.Empty(registry.Active);
    }

    [Fact]
    public void The_clock_polls_the_activity_every_few_seconds()
    {
        var time = new FakeTimeProvider(T0);
        var activity = new FakeProjectActivity();
        using var registry = new BackgroundWorkRegistry(time, activity, new InlineSynchronizationContext());
        registry.TrackProject(RootC, () => ProjectC);
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        activity.Holding["p-c"] = ProjectActivityKind.Planning;
        time.Advance(BackgroundWorkRegistry.DefaultPollInterval);

        Assert.Equal(1, changes);
        Assert.Equal(BackgroundWorkKind.Planning, Assert.Single(registry.Active).Kind);
    }

    [Fact]
    public void Disposing_the_token_of_a_run_forgets_it()
    {
        var monitor = Follow(new FakeRun(Snapshot("20261021-aaaaaa", RunPhase.Running, 0)), ProjectA);
        using var registry = Registry();
        var token = registry.TrackRun(RootA, monitor, () => ProjectA);

        token.Dispose();
        token.Dispose();

        Assert.Empty(registry.All);
        Assert.False(registry.HasActiveWork);
    }
}
