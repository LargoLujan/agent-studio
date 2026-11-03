using System.IO;
using System.Threading.Channels;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>F3-3 and docs/13 section 4.5: abandonment, the three timeouts, user cancellation, closing the app and quota waits.</summary>
public sealed class StallTimeoutAndCancelTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task A_silent_attempt_is_abandoned_cancelled_reset_to_its_base_and_retried()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            Hang = true,
            Files = new Dictionary<string, string> { ["src/T1/a medias.cs"] = "sin terminar" },
        });

        await using var run = await harness.StartAsync(Plan(Work("T1")), new RunOptions(StallThreshold: Stall));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        var partial = Path.Combine(harness.Claude.Starts.Single().Request.WorkingDirectory, "src", "T1", "a medias.cs");
        await RunTestSupport.WaitUntilAsync(() => File.Exists(partial), "the partial work of T1");
        harness.Time.Advance(Stall + TimeSpan.FromMinutes(1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var (_, alert) = Assert.Single(harness.Alerts("task.stalled"));
        Assert.Equal([new TaskId("T1")], alert.Tasks);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains("Abandonada", failed.Reason, StringComparison.Ordinal);
        Assert.Contains(failed.Evidence, e => harness.Evidence.Record(e).Kind == EvidenceKind.CodeDiff);
        Assert.Contains($"checkout T1 {harness.Worktrees.InitialCommit}", harness.Worktrees.Calls);
        Assert.False(File.Exists(Path.Combine(harness.Claude.Starts[1].Request.WorkingDirectory, "src", "T1", "a medias.cs")));
        Assert.Equal(AttemptOutcome.Cancelled, harness.Payloads<AgentRunFinishedPayload>().First().Payload.Status);
        Assert.Equal(1, run.Snapshot().View("T1").RetryCount);
        Assert.Equal(0, harness.Claude.Active);
    }

    [Fact]
    public async Task Events_are_heartbeats_so_a_busy_agent_is_never_abandoned()
    {
        using var harness = new EngineHarness();
        var live = Channel.CreateUnbounded<AgentEvent>();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { LiveEvents = live.Reader });

        // A long task timeout: only the abandonment monitor is under test here (the default timeout is 20 minutes).
        await using var run = await harness.StartAsync(Plan(Work("T1")), new RunOptions(StallThreshold: Stall, DefaultTaskTimeout: TimeSpan.FromHours(2)));
        var heartbeats = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var e in run.Events)
            {
                if (e is AgentActivityEvent { Event: MessageEmitted })
                {
                    Interlocked.Increment(ref heartbeats);
                }
            }
        }, TestContext.Current.CancellationToken);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        for (var i = 1; i <= 6; i++)
        {
            // Six minutes at a time: never ten without a sign of life, but thirty-six in all.
            harness.Time.Advance(TimeSpan.FromMinutes(6));
            live.Writer.TryWrite(new MessageEmitted(harness.Time.GetUtcNow(), "trabajando", true));
            var expected = i;
            await RunTestSupport.WaitUntilAsync(() => Volatile.Read(ref heartbeats) >= expected, $"heartbeat {expected}");
        }

        live.Writer.TryComplete();
        var result = await run.CompleteAsync(harness);
        await reader.WaitAsync(RunTestSupport.Guard, TestContext.Current.CancellationToken);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Empty(harness.Alerts("task.stalled"));
        Assert.Single(harness.Claude.Starts);
    }

    [Fact]
    public async Task The_task_timeout_cancels_the_attempt_and_it_is_retried()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var plan = Plan(Work("T1") with { Timeout = TimeSpan.FromMinutes(5) });

        await using var run = await harness.StartAsync(plan, new RunOptions(StallThreshold: Stall));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains("Tiempo agotado", failed.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts("task.stalled"));
    }

    [Fact]
    public async Task The_default_task_timeout_of_the_run_applies_when_the_task_has_none()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });

        await using var run = await harness.StartAsync(Plan(Work("T1") with { MaxRetries = 0 }), new RunOptions(StallThreshold: TimeSpan.FromHours(1), DefaultTaskTimeout: TimeSpan.FromMinutes(3)));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        harness.Time.Advance(TimeSpan.FromMinutes(4));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("Tiempo agotado", harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rate_limited_judge_waits_for_its_provider_to_cool_down_and_the_review_runs_again_without_spending_a_retry()
    {
        using var harness = new EngineHarness();
        var resetsAt = Start + TimeSpan.FromMinutes(10);
        var reviews = 0;
        harness.Review.Script = _ => Interlocked.Increment(ref reviews) == 1 ? ReviewDisposition.RateLimited : ReviewDisposition.Approved;
        harness.Review.RetryAfter = resetsAt;
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        var view = run.Snapshot().View("R1");
        Assert.Equal(WorkTaskStatus.Blocked, view.Status);
        Assert.Equal($"RateLimitedUntil({resetsAt:O})", view.BlockReason);
        Assert.Empty(harness.Alerts("run.blocked"));
        Assert.Empty(harness.Approvals.Requests);
        Assert.Single(harness.Review.Requests);

        harness.Time.Advance(TimeSpan.FromMinutes(11));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Review.Requests.Count);
        Assert.Equal(0, run.Snapshot().View("R1").RetryCount);
        Assert.Equal(
            ["R1 Pending>Running", "R1 Running>Blocked", "R1 Blocked>Pending", "R1 Pending>Running", "R1 Running>Completed"],
            harness.Transitions("R1"));
    }

    [Fact]
    public async Task A_rate_limited_judge_that_does_not_say_when_the_limit_resets_waits_the_default_cooldown()
    {
        using var harness = new EngineHarness();
        harness.Review.Script = _ => ReviewDisposition.RateLimited;
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        Assert.StartsWith("RateLimitedUntil(", run.Snapshot().View("R1").BlockReason, StringComparison.Ordinal);
        await run.CancelAsync("fin");
        await run.CompleteAsync(harness);
    }

    [Fact]
    public async Task The_task_timeout_also_stops_a_review_whose_judge_never_answers()
    {
        using var harness = new EngineHarness();
        harness.Review.Gate = new TaskCompletionSource(); // the judge never answers
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")) with { Timeout = TimeSpan.FromMinutes(5) });

        await using var run = await harness.StartAsync(plan, new RunOptions(StallThreshold: TimeSpan.FromHours(10)));
        await RunTestSupport.WaitUntilAsync(() => harness.Review.Requests.Count == 1, "the judge was asked");
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("R1").Status);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.Task == new TaskId("R1") && p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains("no terminó a tiempo", failed.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Review.Requests);
        Assert.Empty(harness.Alerts("task.stalled"));
    }

    [Fact]
    public async Task Cancelling_the_run_cancels_every_attempt_records_cancelled_and_keeps_the_worktrees()
    {
        using var harness = new EngineHarness();
        var started = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true, Started = started, Files = new Dictionary<string, string> { ["src/T1/x.cs"] = "x" } });
        var plan = Plan(Work("T1"), Work("T2").After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.CancelAsync("el usuario cambió de idea");
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Cancelled, result.Phase);
        Assert.Contains("el usuario cambió de idea", result.Summary, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Cancelled, run.Snapshot().View("T1").Status);
        Assert.Equal(WorkTaskStatus.Cancelled, run.Snapshot().View("T2").Status);
        Assert.Equal(RunPhase.Cancelled, Assert.Single(harness.Payloads<RunFinishedPayload>()).Payload.Phase);
        Assert.Equal(AttemptOutcome.Cancelled, Assert.Single(harness.Payloads<AgentRunFinishedPayload>()).Payload.Status);
        Assert.Equal(0, harness.Claude.Active);
        Assert.True(harness.Worktrees.HasWorktree("T1"));
        Assert.DoesNotContain(harness.Worktrees.Calls, c => c.StartsWith("checkout", StringComparison.Ordinal));
        Assert.Single(harness.Claude.Starts);
    }

    [Fact]
    public async Task Closing_the_app_kills_the_agents_and_records_nothing_as_cancelled()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });

        var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        var entriesBefore = harness.Entries.Count;
        await run.DisposeAsync();
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Interrupted, result.Phase);
        Assert.Equal(0, harness.Claude.Active);
        Assert.Equal(entriesBefore, harness.Entries.Count);
        Assert.Empty(harness.Payloads<RunFinishedPayload>());
        Assert.DoesNotContain(harness.Transitions(), t => t.EndsWith(">Cancelled", StringComparison.Ordinal));
        var listed = Assert.Single(await harness.CreateOrchestrator().ListRunsAsync(harness.Project, TestContext.Current.CancellationToken));
        Assert.Equal(RunPhase.Interrupted, listed.Phase);
    }

    [Fact]
    public async Task A_rate_limit_blocks_the_task_until_the_reset_without_using_a_retry()
    {
        using var harness = new EngineHarness();
        var resetsAt = Start + TimeSpan.FromMinutes(10);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = resetsAt });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        var view = run.Snapshot().View("T1");
        Assert.Equal(WorkTaskStatus.Blocked, view.Status);
        Assert.StartsWith("RateLimitedUntil(", view.BlockReason!, StringComparison.Ordinal);
        Assert.Equal(0, view.RetryCount);
        Assert.Empty(harness.Alerts("graph.deadlock"));
        Assert.Empty(harness.Alerts("run.blocked"));

        harness.Time.Advance(TimeSpan.FromMinutes(10));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        Assert.Equal("session-ejecutor-T1-1", harness.Claude.Starts[1].Request.ResumeSessionId);
        Assert.Equal(AttemptOutcome.RateLimited, harness.Payloads<AgentRunFinishedPayload>().First().Payload.Status);
    }

    [Fact]
    public async Task The_cooldown_of_a_limited_provider_holds_its_other_tasks_but_not_another_provider()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "A1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(5) });
        var codexStarted = new TaskCompletionSource();
        harness.Codex.Script("ejecutorCodex", "C1", 1, AgentScript.Completed() with { Started = codexStarted });

        // Dispatch order is A1, B1, C1 and only one task runs at a time: A1 hits the limit, B1 (same provider) must wait, C1 goes.
        var plan = Plan(Work("A1"), Work("B1", agent: "ejecutor2"), Work("C1", agent: "ejecutorCodex"));

        await using var run = await harness.StartAsync(plan, new RunOptions(MaxParallelTasks: 1));
        await codexStarted.Task.WaitAsync(RunTestSupport.Guard, TestContext.Current.CancellationToken);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        Assert.Equal(["A1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.Equal(WorkTaskStatus.Blocked, run.Snapshot().View("A1").Status);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("B1").Status);
        Assert.Equal(WorkTaskStatus.Completed, run.Snapshot().View("C1").Status);

        harness.Time.Advance(TimeSpan.FromMinutes(5));
        var result = await run.CompleteAsync(harness);
        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(["A1", "A1", "B1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
    }

    [Fact]
    public async Task A_quota_wait_longer_than_the_automatic_limit_pauses_until_the_user_retries()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromHours(2) });

        await using var run = await harness.StartAsync(Plan(Work("T1")), new RunOptions(MaxAutoWaitForQuota: TimeSpan.FromMinutes(15)));
        await harness.WaitForPhaseAsync(run, RunPhase.Paused);
        harness.Time.Advance(TimeSpan.FromMinutes(20));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(WorkTaskStatus.Blocked, run.Snapshot().View("T1").Status);
        Assert.Single(harness.Claude.Starts);

        await run.RetryBlockedAsync(new TaskId("T1"));
        var result = await run.CompleteAsync(harness);
        Assert.Equal(RunPhase.Succeeded, result.Phase);
    }

    [Fact]
    public async Task Pausing_stops_dispatching_but_lets_running_work_finish()
    {
        using var harness = new EngineHarness();
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });
        var plan = Plan(Work("T1"), Work("T2").After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.PauseAsync();
        gate.SetResult();
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Completed);
        await harness.WaitForPhaseAsync(run, RunPhase.Paused);

        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);
        Assert.Single(harness.Claude.Starts);

        await run.ResumeDispatchAsync();
        var result = await run.CompleteAsync(harness);
        Assert.Equal(RunPhase.Succeeded, result.Phase);
    }
}
