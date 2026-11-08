using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Clocks;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// F8-5 and the finding of docs/16 section 2.14: the machine sleeps while an attempt runs or a task waits for quota, and
/// wakes. The real engine runs on a <see cref="SuspendableClock"/>, a clock that jumps without firing timers: awake time
/// goes by on the harness's fake clock, which also runs the timers, and a sleep moves only the wall clock (and the
/// timestamp, when the clock stands for <see cref="TimeProvider.System"/>). That is how .NET 10 behaves on Windows: its
/// timer queue reads QueryUnbiasedInterruptTime and waits with WaitOne, and since Windows 8 neither counts the time
/// asleep, while the wall clock and QueryPerformanceCounter do.
/// </summary>
public sealed class SleepTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(10);

    /// <summary>The engine's default heartbeat (<c>RunContext.DefaultHeartbeat</c>): the longest the loop waits before it looks again.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    /// <summary>Longer than the stall threshold, and than what is left of an attempt's 20-minute deadline after five minutes of work.</summary>
    private static readonly TimeSpan Nap = TimeSpan.FromMinutes(30);

    /// <summary>How long the loop's snapshot must stay the same, in real time, for the loop to be taken as resting.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- abandonment ----

    /// <summary>
    /// The finding was this, on <see cref="TimeProvider.System"/>, whose timestamp counts sleep (reproduced before the fix):
    /// an attempt that had just spoken was taken for abandoned on waking and cancelled before its CLI could say anything,
    /// because its silence is the wall clock minus its last sign of life, and the loop looks again within a heartbeat of
    /// waking. From that clock alone the engine cannot tell the sleep, since both of its readings count it; the power events
    /// of the app (<see cref="IPowerEvents"/>, WP3) tell it how long the machine slept, and it leaves that out.
    /// </summary>
    [Fact]
    public Task A_healthy_attempt_survives_a_sleep_longer_than_the_stall_threshold_on_the_system_clock_told_by_the_power_events() =>
        HealthyAttemptSurvivesASleepAsync(timestampCountsSleep: true, toldByThePowerEvents: true);

    /// <summary>
    /// When the awake clock and the power events both see the same sleep, the engine leaves it out once: the larger of the
    /// two counts, not their sum.
    /// </summary>
    [Fact]
    public Task A_sleep_the_awake_clock_and_the_power_events_both_see_is_left_out_once() =>
        HealthyAttemptSurvivesASleepAsync(timestampCountsSleep: false, toldByThePowerEvents: true);

    /// <summary>The engine listens to the power events only while its run lasts.</summary>
    [Fact]
    public async Task The_engine_stops_listening_to_the_power_events_when_the_run_ends()
    {
        using var harness = new EngineHarness();
        var power = new FakePowerEvents();
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });

        await using var run = await StartAsync(harness, harness.Time, Plan(Work("T1")), power: power);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        Assert.Equal(1, power.Listeners);

        gate.SetResult();
        await run.CompleteAsync(harness);

        Assert.Equal(0, power.Listeners);
    }

    /// <summary>
    /// The same with an awake clock, one that stops while the machine sleeps (QueryUnbiasedInterruptTime): the engine sees
    /// the sleep as the gap between the wall clock and that one, leaves it out of the silence, and says so.
    /// </summary>
    [Fact]
    public Task A_healthy_attempt_survives_a_sleep_longer_than_the_stall_threshold_on_an_awake_clock() =>
        HealthyAttemptSurvivesASleepAsync(timestampCountsSleep: false);

    /// <summary>
    /// The same on the clock the app is to compose, <see cref="AwakeTimeProvider"/>, in real time: its wall clock and its
    /// timers are the system's, and its timestamp is QueryUnbiasedInterruptTime. The test sleeps the machine as far as a
    /// test can, moving only the wall clock forward, which is what a sleep does to it; that the unbiased count stops in the
    /// meantime is what the documentation of Windows says, and no test can measure it.
    /// </summary>
    [Fact]
    public async Task A_healthy_attempt_survives_a_sleep_on_the_clock_of_the_app()
    {
        using var harness = new EngineHarness();
        var clock = new SuspendableClock(new AwakeTimeProvider(), timestampCountsSleep: false);
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });

        // Real time: the loop looks every 50 ms, and the threshold is far above what the test takes and far below the sleep.
        await using var run = await StartAsync(
            harness,
            clock,
            Plan(Work("T1")),
            new RunOptions(StallThreshold: TimeSpan.FromMinutes(1), HeartbeatInterval: TimeSpan.FromMilliseconds(50), DefaultTaskTimeout: TimeSpan.FromHours(2)));
        var events = EventTrail.Of(run);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await events.WaitForAsync(e => e is AgentActivityEvent { Event: SessionStarted }, "the first event of the agent of T1");
        clock.Sleep(Nap);
        await events.WaitForAsync(SleepNotice(Nap), "the notice of the sleep");

        Assert.Empty(harness.Alerts("task.stalled").Select(a => a.Payload.Message));

        gate.SetResult();
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Claude.Starts);
        Assert.Equal(["T1 Pending>Running", "T1 Running>Completed"], harness.Transitions("T1"));
    }

    /// <summary>
    /// The sleep is left out of the silence of an attempt, and the awake time after it is not: an attempt that stays silent
    /// for more than the stall threshold after waking is abandoned as before.
    /// </summary>
    [Fact]
    public async Task An_attempt_still_silent_ten_minutes_of_awake_time_after_waking_is_abandoned()
    {
        using var harness = new EngineHarness();
        var clock = new SuspendableClock(harness.Time, timestampCountsSleep: false);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });

        // A long deadline: only the abandonment is under test.
        await using var run = await StartAsync(harness, clock, Plan(Work("T1")), new RunOptions(StallThreshold: Stall, DefaultTaskTimeout: TimeSpan.FromHours(2)));
        var events = EventTrail.Of(run);
        await AliveAndRestingAsync(harness, run, events);
        clock.Sleep(Nap);
        await WakeAsync(harness, run);

        Assert.Empty(harness.Alerts("task.stalled").Select(a => a.Payload.Message));
        await events.WaitForAsync(SleepNotice(Nap), "the notice of the sleep");

        harness.Time.Advance(Stall + TimeSpan.FromMinutes(1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var (_, alert) = Assert.Single(harness.Alerts("task.stalled"));
        Assert.Equal([new TaskId("T1")], alert.Tasks);
        Assert.Contains("Abandonada", harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload.Reason, StringComparison.Ordinal);
        Assert.Equal(2, harness.Claude.Starts.Count);
    }

    // ---- the deadline of an attempt ----

    /// <summary>
    /// The deadline of an attempt is a .NET timer and nothing else: the engine never holds it against the wall clock. The
    /// timer queue of .NET 10 does not count sleep, so an attempt that went to sleep five minutes into its twenty is not
    /// timed out on waking, 35 minutes later by the wall clock, and is after twenty minutes of work in all. The double runs
    /// its timers on awake time as that queue does; what this checks is that the engine relies on the timer alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_deadline_of_an_attempt_counts_only_awake_time(bool timestampCountsSleep)
    {
        using var harness = new EngineHarness();
        var clock = new SuspendableClock(harness.Time, timestampCountsSleep);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = new TaskCompletionSource() });

        // The abandonment is out of the way: only the deadline is under test.
        await using var run = await StartAsync(harness, clock, Plan(Work("T1") with { Timeout = TimeSpan.FromMinutes(20) }), new RunOptions(StallThreshold: TimeSpan.FromHours(10)));
        await AliveAndRestingAsync(harness, run, EventTrail.Of(run));
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await RestingAsync(run);
        clock.Sleep(Nap);
        await WakeAsync(harness, run);

        Assert.Equal(WorkTaskStatus.Running, run.Snapshot().View("T1").Status);
        Assert.Single(harness.Claude.Starts);

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var (_, failed) = Assert.Single(harness.Payloads<TaskStatusChangedPayload>(), p => p.Payload.To == WorkTaskStatus.Failed);
        Assert.Contains("Tiempo agotado", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Empty(harness.Alerts("task.stalled"));
    }

    // ---- waits for quota ----

    /// <summary>
    /// A wait for quota ends at the provider's reset, a time on the wall clock that goes on passing while the machine
    /// sleeps: a reset that passed during the sleep is over on waking, within a heartbeat of awake time (the loop waits at
    /// most a heartbeat and then compares the reset with the wall clock), and the task runs again without spending a retry.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_quota_wait_whose_reset_passed_during_the_sleep_is_over_on_waking(bool timestampCountsSleep)
    {
        using var harness = new EngineHarness();
        var clock = new SuspendableClock(harness.Time, timestampCountsSleep);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });

        await using var run = await StartAsync(harness, clock, Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        await RestingAsync(run);
        var asleepAt = harness.Time.GetUtcNow();
        clock.Sleep(Nap);
        await WakeAsync(harness, run, () => run.Completion.IsCompleted);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        Assert.True(harness.Time.GetUtcNow() - asleepAt < TimeSpan.FromMinutes(1), "The wait ended on waking, not ten minutes of awake time later.");
    }

    /// <summary>
    /// A reset still ahead on waking is waited for on the wall clock: the sleep counted toward it (the provider's window
    /// went on), and the wait is not cut short either.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_quota_wait_whose_reset_is_still_ahead_on_waking_lasts_until_the_reset(bool timestampCountsSleep)
    {
        using var harness = new EngineHarness();
        var clock = new SuspendableClock(harness.Time, timestampCountsSleep);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });

        await using var run = await StartAsync(harness, clock, Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        await RestingAsync(run);
        clock.Sleep(TimeSpan.FromMinutes(4));
        await WakeAsync(harness, run);

        Assert.Equal(RunPhase.WaitingForQuota, run.Snapshot().Phase);
        Assert.Equal(WorkTaskStatus.Blocked, run.Snapshot().View("T1").Status);
        Assert.Single(harness.Claude.Starts);

        harness.Time.Advance(TimeSpan.FromMinutes(6)); // four minutes asleep and six awake: the reset, by the wall clock
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
    }

    // ---- the grace of a cancellation ----

    /// <summary>
    /// A cancellation gives the attempts in flight ten seconds to stop, on the clock of the run like every other time of the
    /// engine: an agent that does not stop is left behind once that clock says the grace is over, not after ten seconds of
    /// the real one.
    /// </summary>
    [Fact]
    public async Task The_grace_of_a_cancellation_runs_on_the_clock_of_the_run()
    {
        using var harness = new EngineHarness();
        using var stuck = new ManualResetEventSlim();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { OnStart = _ => stuck.Wait(Ct) }); // deaf to the cancellation

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        try
        {
            await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
            await run.CancelAsync("ya no hace falta");
            harness.Time.Advance(TimeSpan.FromSeconds(11));

            await harness.WaitUntilAsync(run, s => s.Phase == RunPhase.Cancelled, "the end of the grace, well before ten real seconds", TimeSpan.FromSeconds(5));
        }
        finally
        {
            stuck.Set();
        }

        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Cancelled, result.Phase);
        Assert.Equal(WorkTaskStatus.Cancelled, run.Snapshot().View("T1").Status);
    }

    // ---- support ----

    /// <param name="timestampCountsSleep">The timestamp of the clock counts sleep, as QueryPerformanceCounter does.</param>
    /// <param name="toldByThePowerEvents">The power events tell the engine how long the machine slept when it wakes.</param>
    private static async Task HealthyAttemptSurvivesASleepAsync(bool timestampCountsSleep, bool toldByThePowerEvents = false)
    {
        using var harness = new EngineHarness();
        var clock = new SuspendableClock(harness.Time, timestampCountsSleep);
        var power = toldByThePowerEvents ? new FakePowerEvents() : null;
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });

        // A long deadline: only the abandonment is under test.
        await using var run = await StartAsync(harness, clock, Plan(Work("T1")), new RunOptions(StallThreshold: Stall, DefaultTaskTimeout: TimeSpan.FromHours(2)), power);
        var events = EventTrail.Of(run);
        await AliveAndRestingAsync(harness, run, events);
        clock.Sleep(Nap);
        power?.Resume(clock.GetUtcNow(), Nap);
        await WakeAsync(harness, run);

        Assert.Empty(harness.Alerts("task.stalled").Select(a => a.Payload.Message));
        await events.WaitForAsync(SleepNotice(Nap), "the notice of the sleep");

        gate.SetResult(); // the agent goes on where it was, and finishes
        var result = await run.CompleteAsync(harness);
        await events.Reading.WaitAsync(RunTestSupport.Guard, Ct);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Claude.Starts);
        Assert.Equal(["T1 Pending>Running", "T1 Running>Completed"], harness.Transitions("T1"));
        Assert.Single(events.Seen, e => e is RunPhaseChangedEvent { Detail: { } d } && d.StartsWith("El equipo estuvo suspendido", StringComparison.Ordinal));
    }

    /// <summary>The notice the engine gives the monitor when it finds the machine slept for <paramref name="asleep"/>.</summary>
    private static Func<OrchestrationEvent, bool> SleepNotice(TimeSpan asleep) =>
        e => e is RunPhaseChangedEvent { Detail: { } detail }
            && detail.StartsWith("El equipo estuvo suspendido", StringComparison.Ordinal)
            && detail.Contains(FormattableString.Invariant($" {asleep.TotalMinutes:0.#} min:"), StringComparison.Ordinal)
            && detail.Contains("T1", StringComparison.Ordinal);

    /// <summary>The real engine with the harness's ports, on <paramref name="clock"/> instead of the harness's own, and these power events.</summary>
    private static Task<IOrchestrationRun> StartAsync(EngineHarness harness, TimeProvider clock, PlanDefinition plan, RunOptions? options = null, IPowerEvents? power = null) =>
        new Orchestrator(
            harness.Decisions, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers, harness.Worktrees,
            harness.Verification, harness.Integrator, harness.Review, harness.Approvals, harness.Skills, harness.Paths, clock, harness.WriteConfinement,
            power: power)
            .StartAsync(harness.Request(plan, options), Ct);

    /// <summary>
    /// T1 runs and its agent gave its first sign of life (the engine takes the heartbeat of an event before the event
    /// reaches the UI, so it is in before the machine sleeps), and the loop rests on its timer.
    /// </summary>
    private static async Task AliveAndRestingAsync(EngineHarness harness, IOrchestrationRun run, EventTrail events)
    {
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await events.WaitForAsync(e => e is AgentActivityEvent { Event: SessionStarted }, "the first event of the agent of T1");
        await RestingAsync(run);
    }

    /// <summary>
    /// Waits until the loop rests: the snapshot it publishes at the end of every turn stays the same for a while, so it is
    /// waiting on its heartbeat timer, which only awake time moves (nothing else is going to wake it).
    /// </summary>
    private static async Task RestingAsync(IOrchestrationRun run)
    {
        var last = run.Snapshot();
        var since = DateTime.UtcNow;
        var deadline = since + RunTestSupport.Guard;
        while (DateTime.UtcNow - since < Settle)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The loop of the run never rested.\n" + RunTestSupport.Describe(run.Snapshot()));
            }

            await Task.Delay(10, Ct);
            var now = run.Snapshot();
            if (!ReferenceEquals(now, last))
            {
                last = now;
                since = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// The machine wakes: awake time goes by, a heartbeat and then a second at a time, until the loop has taken a turn (it
    /// publishes a new snapshot at the end of each, after it looked for abandoned tasks and ended the waits that are over)
    /// and <paramref name="until"/> holds, when given.
    /// </summary>
    private static async Task WakeAsync(EngineHarness harness, IOrchestrationRun run, Func<bool>? until = null)
    {
        var before = run.Snapshot();
        harness.Time.Advance(Heartbeat);
        for (var second = 0; second < 60; second++)
        {
            var settle = DateTime.UtcNow + Settle;
            while (DateTime.UtcNow < settle)
            {
                if (!ReferenceEquals(run.Snapshot(), before) && (until?.Invoke() ?? true))
                {
                    return;
                }

                await Task.Delay(10, Ct);
            }

            harness.Time.Advance(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("The loop did not take a turn after the machine woke.\n" + RunTestSupport.Describe(run.Snapshot()) + "\n" + harness.Describe());
    }

    /// <summary>The events of a run, read in the background (they have a single reader).</summary>
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

        public bool Any(Func<OrchestrationEvent, bool> match) => _seen.Any(match);

        public Task WaitForAsync(Func<OrchestrationEvent, bool> match, string what) => RunTestSupport.WaitUntilAsync(() => Any(match), what);
    }
}

/// <summary>The power events of the app (WP3's, with PowerRegisterSuspendResumeNotification), as a test raises them.</summary>
internal sealed class FakePowerEvents : IPowerEvents
{
    private EventHandler<PowerEvent>? _changed;

    public event EventHandler<PowerEvent>? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    /// <summary>How many listen now.</summary>
    public int Listeners => _changed?.GetInvocationList().Length ?? 0;

    /// <summary>The machine woke at <paramref name="at"/> after sleeping <paramref name="slept"/>.</summary>
    public void Resume(DateTimeOffset at, TimeSpan slept) => _changed?.Invoke(this, new PowerEvent(PowerTransition.Resumed, at, slept));
}

/// <summary>
/// The clock of a machine that sleeps. Awake time goes by on the clock it wraps, which runs the timers too: a
/// <see cref="FakeTimeProvider"/> in most tests, or the app's <see cref="AwakeTimeProvider"/> in real time. Its timers
/// behave as the timer queue of .NET 10 does on Windows, which reads QueryUnbiasedInterruptTime and waits with WaitOne,
/// neither of which counts the time asleep. <see cref="Sleep"/> moves only the wall clock and, when the clock stands for
/// <see cref="TimeProvider.System"/>, the timestamp, which is QueryPerformanceCounter there and counts the time asleep;
/// when it stands for an awake clock (QueryUnbiasedInterruptTime), the timestamp does not move either.
/// </summary>
internal sealed class SuspendableClock(TimeProvider awake, bool timestampCountsSleep) : TimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _timers = new();
    private long _sleptTicks;

    public TimeSpan Slept => TimeSpan.FromTicks(Interlocked.Read(ref _sleptTicks));

    /// <summary>
    /// The due time of every timer armed through this clock, in the order they were armed. A timer is here once it is armed on
    /// the clock it wraps: advancing that clock afterwards fires it.
    /// </summary>
    public IReadOnlyCollection<TimeSpan> Timers => _timers;

    public override TimeZoneInfo LocalTimeZone => awake.LocalTimeZone;

    public override long TimestampFrequency => awake.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => awake.GetUtcNow() + Slept;

    public override long GetTimestamp() =>
        awake.GetTimestamp() + (timestampCountsSleep ? Slept.Ticks * TimestampFrequency / TimeSpan.TicksPerSecond : 0);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = awake.CreateTimer(callback, state, dueTime, period);
        _timers.Enqueue(dueTime);
        return timer;
    }

    /// <summary>Waits, in real time, until <paramref name="count"/> timers whose due time <paramref name="due"/> accepts are armed.</summary>
    public Task ArmedAsync(Func<TimeSpan, bool> due, int count, string what) =>
        RunTestSupport.WaitUntilAsync(() => _timers.Count(due) >= count, what);

    /// <summary>The machine sleeps for <paramref name="duration"/> and wakes: no timer fires, and no awake time goes by.</summary>
    public void Sleep(TimeSpan duration) => Interlocked.Add(ref _sleptTicks, duration.Ticks);
}
