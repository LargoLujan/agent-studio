using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Integration.Phase8;

/// <summary>Tests the clock barrier itself: no git, process, provider or interval of wall time establishes a condition.</summary>
public sealed class QuotaAlarmHandshakeTests
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APublishedQuotaSnapshotStillWaitsForItsAlarmAndCannotUseACancelledOne()
    {
        var clock = new Phase8Clock(Phase8Clock.ThreeHoursBeforeReset);
        var until = clock.Now + TimeSpan.FromMinutes(10);
        await using var run = new FakeRun(new RunSnapshot(new RunId("quota-alarm"), RunPhase.WaitingForQuota,
            [FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: Phase8Log.Until(until))], FakeRun.Healthy, 1));

        var cancelled = clock.Time.CreateTimer(_ => throw new InvalidOperationException("A cancelled alarm fired."), null, Heartbeat, Timeout.InfiniteTimeSpan);
        cancelled.Dispose();
        await clock.Time.ArmedAsync(due => due == Heartbeat, 1, "the historical alarm");
        var waiting = Phase8Runs.QuotaAlarmAsync(run, clock);
        Assert.False(waiting.IsCompleted); // The snapshot is already published, but its loop alarm is not armed yet.

        using var memoryPoll = clock.Time.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        using var periodic = clock.Time.CreateTimer(_ => { }, null, Heartbeat, Heartbeat);
        Assert.False(waiting.IsCompleted);

        using var alarm = clock.Time.CreateTimer(_ => { }, null, Heartbeat, Timeout.InfiniteTimeSpan);
        var sequence = await waiting.WaitAsync(Guard, Ct);
        Assert.NotNull(sequence);
        Assert.Contains(clock.Time.ReadAlarms().Active, active => active.Sequence == sequence && active.DueTime == Heartbeat);
    }

    [Fact]
    public async Task ChangeFireAndDisposeRetireAlarmsWhileForwardingTheTimersBehaviour()
    {
        var clock = new Phase8Clock(Phase8Clock.ThreeHoursBeforeReset);
        var calls = 0;
        var state = new object();
        var timer = clock.Time.CreateTimer(received =>
        {
            Assert.Same(state, received);
            calls++;
        }, state, Heartbeat, Timeout.InfiniteTimeSpan);
        try
        {
            var first = Assert.Single(clock.Time.ReadAlarms().Active);
            var changed = clock.Time.ReadAlarms().Changed;
            Assert.True(timer.Change(Heartbeat + Heartbeat, Timeout.InfiniteTimeSpan));
            await changed.WaitAsync(Guard, Ct);
            var second = Assert.Single(clock.Time.ReadAlarms().Active);
            Assert.True(second.Sequence > first.Sequence);
            Assert.Equal(Heartbeat + Heartbeat, second.DueTime);

            changed = clock.Time.ReadAlarms().Changed;
            clock.Advance(Heartbeat + Heartbeat);
            await changed.WaitAsync(Guard, Ct);
            Assert.Equal(1, calls);
            Assert.Empty(clock.Time.ReadAlarms().Active);

            Assert.True(timer.Change(Heartbeat, Timeout.InfiniteTimeSpan));
            Assert.Single(clock.Time.ReadAlarms().Active);
            Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
            Assert.Empty(clock.Time.ReadAlarms().Active);
            Assert.True(timer.Change(Heartbeat, Heartbeat));
            Assert.Empty(clock.Time.ReadAlarms().Active);
            clock.Advance(Heartbeat + Heartbeat);
            Assert.Equal(3, calls);

            Assert.True(timer.Change(Heartbeat, Timeout.InfiniteTimeSpan));
            Assert.Single(clock.Time.ReadAlarms().Active);
            changed = clock.Time.ReadAlarms().Changed;
            await timer.DisposeAsync();
            await changed.WaitAsync(Guard, Ct);
            Assert.Empty(clock.Time.ReadAlarms().Active);
            clock.Advance(Heartbeat);
            Assert.Equal(3, calls);
            Assert.Equal([Heartbeat], clock.Time.Timers); // The existing history still records only CreateTimer calls.
        }
        finally
        {
            timer.Dispose();
        }
    }
}
