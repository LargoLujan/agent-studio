using System.Collections.Concurrent;
using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase8;

/// <summary>
/// F8-5 (docs/16 §2.14 and D-40, adenda de WP1 en ADR-0034) on the real engine, SQLite and git: the machine sleeps for half
/// an hour while an attempt of Claude runs. The wall clock jumps and the awake clock does not (QueryUnbiasedInterruptTime,
/// the app's <c>AwakeTimeProvider</c>), and no timer fires: the healthy attempt is not taken for abandoned on waking, nor
/// timed out, and ends well. A clock whose timestamp counts the sleep (QueryPerformanceCounter) cannot tell it: then the
/// power events of the app say how long the machine slept. An attempt that is really hung is still abandoned, once it has
/// been silent for the stall threshold of awake time. No test suspends the machine or calls the power APIs: the clock and
/// the power events are the test's.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class SleepIntegrationTests
{
    /// <summary>Longer than the stall threshold (10 minutes) and than the deadline of the attempt (20 minutes) of the engine.</summary>
    private static readonly TimeSpan Nap = TimeSpan.FromMinutes(30);

    /// <summary>The engine's heartbeat: the longest its loop waits on its timer before it looks again.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task A_healthy_long_attempt_goes_through_a_sleep_on_the_awake_clock_of_the_app_without_being_stalled() =>
        HealthyAttemptSleepsAsync(timestampCountsSleep: false, powerEvents: false);

    [Fact]
    public Task On_a_clock_that_counts_the_sleep_the_power_events_tell_it_and_the_healthy_attempt_is_not_stalled() =>
        HealthyAttemptSleepsAsync(timestampCountsSleep: true, powerEvents: true);

    /// <summary>
    /// The sleep is left out of the silence of an attempt, and the awake time after it is not: an attempt that hung (its CLI
    /// started its session and said nothing more) is abandoned once it has been silent for the stall threshold of awake time
    /// after waking, its process is killed, and the task is retried within its budget.
    /// </summary>
    [Fact]
    public async Task A_hung_attempt_is_still_abandoned_after_the_stall_threshold_of_awake_time_once_the_machine_woke()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1 ? CliTurns.Hangs(launch) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), new RunOptions());
        var events = EventTrail.Of(run);
        await AliveAndRestingAsync(app, run, events);
        clock.Sleep(Nap);
        await Phase8Runs.TurnAsync(run, () => clock.Advance(Heartbeat));

        // Woken: the half hour asleep is not silence, so it is not abandoned (nor timed out: its deadline counts awake time).
        Assert.Equal((WorkTaskStatus.Running, 1), (run.Snapshot().View("T1").Status, run.Snapshot().View("T1").Attempt));
        Assert.Empty(await Phase8Log.OfAsync<AuditAlertPayload>(app, run.Id));
        await events.WaitForAsync(SleepNotice, "the notice of the sleep");

        // Ten minutes and a bit of awake time later, still silent: abandoned.
        clock.Advance(TimeSpan.FromMinutes(11));
        await Phase8Runs.SucceededAsync(app, run);

        var entries = await Phase8Log.OfAsync(app, run.Id);
        var (alertEntry, alert) = Assert.Single(entries.Where(e => e.Payload is AuditAlertPayload).Select(e => (e.Entry, (AuditAlertPayload)e.Payload)));
        Assert.Equal(("task.stalled", "T1"), (alert.Code, Assert.Single(alert.Tasks).Value));
        Assert.Equal(
            [
                "started",
                "launch T1#1",
                "T1 Pending>Running",
                "end T1#1 Cancelled Cancelled",
                "T1 Running>Failed",
                "T1 Failed>Pending",
                "launch T1#2",
                "T1 Pending>Running",
                "end T1#2 Succeeded Completed",
                "T1 Running>Completed",
                "finished Succeeded",
            ],
            Phase8Log.Story(entries.Where(e => e.Payload is not AuditAlertPayload)));
        var failed = entries.Single(e => e.Payload is TaskStatusChangedPayload { To: WorkTaskStatus.Failed });
        Assert.True(alertEntry.Sequence < failed.Entry.Sequence, "The alert of the abandonment goes before the failure it causes.");
        Assert.StartsWith("Abandonada", ((TaskStatusChangedPayload)failed.Payload).Reason, StringComparison.Ordinal);
        Assert.Equal((2, 1), (world.Claude.Launches.Count, run.Snapshot().View("T1").RetryCount));
        Assert.Equal(0, world.Claude.Running); // the hung process was killed
        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>
    /// A healthy attempt gives its first sign of life, the machine sleeps half an hour and wakes: within a heartbeat of awake
    /// time the loop looks, finds the sleep and leaves it out of the silence of the attempt, and says so to the monitor. The
    /// attempt is neither abandoned nor timed out, and ends well when its CLI answers.
    /// </summary>
    private static async Task HealthyAttemptSleepsAsync(bool timestampCountsSleep, bool powerEvents)
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start, timestampCountsSleep: timestampCountsSleep, powerEvents: powerEvents);
        var clock = world.Clock;
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Claude.Turn = launch => CliTurns.Held(launch, answer, ("src/T1/a.txt", "a\n"));
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), new RunOptions());
        var events = EventTrail.Of(run);
        await AliveAndRestingAsync(app, run, events);
        clock.Sleep(Nap);
        world.Power?.Resume(clock.Now, Nap);
        await Phase8Runs.TurnAsync(run, () => clock.Advance(Heartbeat));

        Assert.Equal((WorkTaskStatus.Running, 1), (run.Snapshot().View("T1").Status, run.Snapshot().View("T1").Attempt));
        Assert.Empty(await Phase8Log.OfAsync<AuditAlertPayload>(app, run.Id));
        await events.WaitForAsync(SleepNotice, "the notice of the sleep");

        answer.SetResult(); // the CLI goes on where it was, and answers
        await Phase8Runs.SucceededAsync(app, run);
        await events.Reading.WaitAsync(Phase8Runs.Guard, Ct);

        Assert.Single(events.Seen, e => e is RunPhaseChangedEvent { Detail: { } d } && d.StartsWith("El equipo estuvo suspendido", StringComparison.Ordinal));
        Assert.Equal(
            [
                "started",
                "launch T1#1",
                "T1 Pending>Running",
                "end T1#1 Succeeded Completed",
                "T1 Running>Completed",
                "finished Succeeded",
            ],
            await Phase8Log.StoryAsync(app, run.Id));
        Assert.Single(world.Claude.Launches);
        if (world.Power is { } power)
        {
            Assert.Equal(0, power.Listeners); // the engine listened only while its run lasted
        }

        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>T1 runs and its CLI gave its first sign of life, which the engine took as a heartbeat, and the loop rests on its timer.</summary>
    private static async Task AliveAndRestingAsync(Phase4App app, IOrchestrationRun run, EventTrail events)
    {
        await Phase8Runs.UntilStatusAsync(app, run, "T1", WorkTaskStatus.Running);
        await events.WaitForAsync(e => e is AgentActivityEvent { Event: SessionStarted }, "the first event of the CLI of T1");
        await Phase8Runs.RestingAsync(run);
    }

    /// <summary>The notice the engine gives the monitor when it finds the machine slept the nap, for T1.</summary>
    private static bool SleepNotice(OrchestrationEvent e) =>
        e is RunPhaseChangedEvent { Detail: { } detail }
        && detail.StartsWith("El equipo estuvo suspendido", StringComparison.Ordinal)
        && detail.Contains(string.Create(CultureInfo.InvariantCulture, $" {Nap.TotalMinutes:0.#} min:"), StringComparison.Ordinal)
        && detail.Contains("T1", StringComparison.Ordinal);

    /// <summary>The events of a run, read in the background by their one reader (no monitor reads them in these tests).</summary>
    private sealed class EventTrail
    {
        private readonly ConcurrentQueue<OrchestrationEvent> _seen = new();

        private EventTrail(IOrchestrationRun run) =>
            Reading = Task.Run(
                async () =>
                {
                    await foreach (var e in run.Events)
                    {
                        _seen.Enqueue(e);
                    }
                },
                Ct);

        /// <summary>Ends once the run has given its last event.</summary>
        public Task Reading { get; }

        public IReadOnlyCollection<OrchestrationEvent> Seen => _seen;

        public static EventTrail Of(IOrchestrationRun run) => new(run);

        public Task WaitForAsync(Func<OrchestrationEvent, bool> match, string what) => Phase8Runs.UntilAsync(() => _seen.Any(match), what);
    }
}
