using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// F8-2 (docs/16 D-36 and D-37, ADR-0034): with the consent of its header, a task its provider limited waits for the reset
/// the provider announced plus the margin, however long that is; without a reset time, the waits are progressive (5, 10,
/// 20, 40 and 60 minutes), capped and «estimada». The other tasks of the provider wait too and those of other providers go
/// on; the task is never given to another provider, account or model, and every relaunch counts against the budget. Without
/// the consent nothing changes (<see cref="StallTimeoutAndCancelTests"/>).
/// </summary>
public sealed class QuotaWaitTests
{
    private static readonly RunOptions Consent = new(AutoResumeConsent: true);

    [Fact]
    public async Task With_consent_a_task_waits_for_the_announced_reset_plus_the_margin_however_long_it_is()
    {
        using var harness = new EngineHarness();
        var resetsAt = Start + TimeSpan.FromHours(2);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = resetsAt });

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        Assert.Equal($"RateLimitedUntil({resetsAt + RunOptions.DefaultQuotaResetMargin:O})", run.Snapshot().View("T1").BlockReason);

        harness.Time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(harness.Claude.Starts); // the reset came, the margin did not

        harness.Time.Advance(RunOptions.DefaultQuotaResetMargin);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        var (first, second) = (harness.Claude.Starts[0].Request, harness.Claude.Starts[1].Request);
        Assert.Equal((first.Agent.Id, first.Agent.Model), (second.Agent.Id, second.Agent.Model)); // the same agent, provider and model
        Assert.Empty(harness.Codex.Starts);
        Assert.Equal("session-ejecutor-T1-1", second.ResumeSessionId);
    }

    [Fact]
    public async Task The_margin_after_the_reset_is_an_option_of_the_run()
    {
        using var harness = new EngineHarness();
        var resetsAt = Start + TimeSpan.FromMinutes(30);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = resetsAt });

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent with { QuotaResetMargin = TimeSpan.FromMinutes(1) });
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        Assert.Equal($"RateLimitedUntil({resetsAt + TimeSpan.FromMinutes(1):O})", run.Snapshot().View("T1").BlockReason);

        harness.Time.Advance(TimeSpan.FromMinutes(31));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
    }

    /// <summary>
    /// The loop works out how long to sleep from the clock, and then arms a timer for that long: a clock that reaches the end
    /// of the wait in between (a test that advances it as soon as the run shows the wait, or a machine that falls asleep right
    /// then) leaves a timer that counts from later. On a fake clock nobody advances again it never came, and the run waited
    /// for quota for ever (docs/18 P42); the loop now looks again at once. The heartbeat is longer than the wait, so the
    /// timer of the wait is the only one armed for its 13 minutes.
    /// </summary>
    [Fact]
    public async Task A_wait_ends_when_the_clock_reaches_its_end_while_the_loop_arms_its_timer()
    {
        var clock = new JumpingFakeTimeProvider(Start);
        using var harness = new EngineHarness(time: clock);
        var wait = TimeSpan.FromMinutes(10) + RunOptions.DefaultQuotaResetMargin;
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });
        clock.JumpWhenArming(wait, wait);

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent with { HeartbeatInterval = TimeSpan.FromHours(1) });
        var result = await run.CompleteAsync(harness);

        Assert.True(clock.Jumped);
        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal("session-ejecutor-T1-1", harness.Claude.Starts[1].Request.ResumeSessionId);
    }

    [Fact]
    public async Task With_consent_and_no_reset_time_the_waits_are_progressive_capped_and_estimated()
    {
        using var harness = new EngineHarness();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            harness.Claude.Script("ejecutor", "T1", attempt, Limited());
        }

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent with { MaxEstimatedQuotaWait = TimeSpan.FromMinutes(15) });
        var at = Start;
        foreach (var wait in new[] { 5, 10, 15, 15 }.Select(m => TimeSpan.FromMinutes(m)))
        {
            var expected = $"RateLimitedUntilEstimated({at + wait:O})";
            await harness.WaitUntilAsync(run, s => s.View("T1").BlockReason == expected && s.Phase == RunPhase.WaitingForQuota, expected);
            harness.Time.Advance(wait);
            at += wait;
        }

        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(5, harness.Claude.Starts.Count);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        Assert.Empty(harness.Alerts("graph.deadlock"));
    }

    [Fact]
    public async Task The_wait_of_an_estimated_limit_says_so_in_the_phase_of_the_run()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, Limited());
        string? detail = null;

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent);
        var reading = Task.Run(
            async () =>
            {
                await foreach (var e in run.Events)
                {
                    if (e is RunPhaseChangedEvent { Phase: RunPhase.WaitingForQuota } phase)
                    {
                        Volatile.Write(ref detail, phase.Detail);
                    }
                }
            },
            TestContext.Current.CancellationToken);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        await RunTestSupport.WaitUntilAsync(() => Volatile.Read(ref detail) is not null, "the detail of the phase");

        Assert.Equal($"En espera de cuota hasta {Start + TimeSpan.FromMinutes(5):O} (estimada).", Volatile.Read(ref detail));
        await run.CancelAsync("fin");
        await run.CompleteAsync(harness);
        await reading.WaitAsync(RunTestSupport.Guard, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_other_tasks_of_the_limited_provider_wait_for_the_margin_and_those_of_other_providers_go_on()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "A1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });
        var codexStarted = new TaskCompletionSource();
        harness.Codex.Script("ejecutorCodex", "C1", 1, AgentScript.Completed() with { Started = codexStarted });

        // One task at a time, in the order A1, B1, C1: A1 hits the limit, B1 (same provider) waits, C1 goes.
        var plan = Plan(Work("A1"), Work("B1", agent: "ejecutor2"), Work("C1", agent: "ejecutorCodex"));
        await using var run = await harness.StartAsync(plan, Consent with { MaxParallelTasks = 1 });
        await codexStarted.Task.WaitAsync(RunTestSupport.Guard, TestContext.Current.CancellationToken);
        await harness.WaitForStatusAsync(run, "C1", WorkTaskStatus.Completed);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        harness.Time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(["A1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("B1").Status);

        harness.Time.Advance(RunOptions.DefaultQuotaResetMargin);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(["A1", "A1", "B1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
    }

    [Fact]
    public async Task Every_relaunch_after_a_wait_counts_against_the_budget_so_waiting_never_loops()
    {
        using var harness = new EngineHarness();
        harness.Claude.Fallback = (_, _) => Limited();

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent with { MaxAgentRuns = 3 });
        for (var launches = 1; launches < 3; launches++)
        {
            var expected = launches;
            await harness.WaitUntilAsync(run, s => harness.Claude.Starts.Count == expected && s.Phase == RunPhase.WaitingForQuota, $"limit {expected}");
            harness.Time.Advance(TimeSpan.FromHours(1));
        }

        await harness.WaitUntilAsync(run, s => harness.Claude.Starts.Count == 3 && s.Phase == RunPhase.WaitingForQuota, "limit 3");
        harness.Time.Advance(TimeSpan.FromHours(1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(3, harness.Claude.Starts.Count);
        Assert.Single(harness.Alerts("run.budget-exhausted"));
    }

    [Fact]
    public async Task A_resumed_run_goes_on_with_the_progression_of_its_waits()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, Limited());
        harness.Claude.Script("ejecutor", "T1", 2, Limited());
        harness.Claude.Script("ejecutor", "T1", 3, Limited());

        var first = await harness.StartAsync(Plan(Work("T1")), Consent);
        await harness.WaitUntilAsync(first, s => s.View("T1").BlockReason == $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(5):O})", "the first wait");
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await harness.WaitUntilAsync(first, s => s.View("T1").BlockReason == $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(15):O})", "the second wait");
        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromMinutes(10));
        var third = $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(35):O})"; // the third wait is the third step: 20 minutes
        await harness.WaitUntilAsync(resumed, s => s.View("T1").BlockReason == third, third);
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    // ---- decided by the class of the end (D-41), not by its status ----

    [Fact]
    public async Task A_window_that_ran_out_is_waited_for_even_when_the_attempt_ends_as_a_failure_and_spends_no_retry()
    {
        using var harness = new EngineHarness();
        var resetsAt = Start + TimeSpan.FromMinutes(40);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing("You've hit your session limit"));
        ClassifiedEnds.Install(harness, (_, attempt) => attempt == 1 ? new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", resetsAt) : null);

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);

        Assert.Equal($"RateLimitedUntil({resetsAt + RunOptions.DefaultQuotaResetMargin:O})", run.Snapshot().View("T1").BlockReason);
        harness.Time.Advance(TimeSpan.FromMinutes(43));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        Assert.Equal("session-ejecutor-T1-1", harness.Claude.Starts[1].Request.ResumeSessionId);
    }

    [Fact]
    public async Task A_window_whose_reset_codex_does_not_say_asks_its_quota_when_it_runs_out()
    {
        using var harness = new EngineHarness();
        var resetsAt = Start + TimeSpan.FromMinutes(50);
        harness.Codex.Script("ejecutorCodex", "C1", 1, AgentScript.Failing("You've hit your usage limit."));
        ClassifiedEnds.Install(harness, (_, _) => null, (_, attempt) => attempt == 1 ? new AgentRunEnd(AttemptEndClass.WindowQuota) : null);
        var quota = new FakeQuotaReader(ProviderId.CodexCli, call => call == 1 ? StillLimited(resetsAt) : Allowed());

        await using var run = await StartAsync(harness, Plan(Work("C1", agent: "ejecutorCodex")), Consent, [], [quota]);
        var expected = $"RateLimitedUntil({resetsAt + RunOptions.DefaultQuotaResetMargin:O})";
        await harness.WaitUntilAsync(run, s => s.View("C1").BlockReason == expected, expected);

        var (_, read) = Assert.Single(harness.Payloads<AccessCheckedPayload>());
        Assert.Equal((new TaskId("C1"), AccessCheckSource.QuotaRead, AccessCheckResult.StillLimited, (DateTimeOffset?)resetsAt), (read.Task!.Value, read.Source, read.Result, read.ResetsAt));
        harness.Time.Advance(TimeSpan.FromMinutes(53));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, quota.Calls); // when the window ran out, and before the relaunch
    }

    [Fact]
    public async Task A_window_with_no_reset_anywhere_waits_the_estimated_steps()
    {
        using var harness = new EngineHarness();
        harness.Codex.Script("ejecutorCodex", "C1", 1, AgentScript.Failing("You've hit your usage limit."));
        ClassifiedEnds.Install(harness, (_, _) => null, (_, attempt) => attempt == 1 ? new AgentRunEnd(AttemptEndClass.WindowQuota) : null);

        await using var run = await harness.StartAsync(Plan(Work("C1", agent: "ejecutorCodex")), Consent);
        var expected = $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(5):O})";
        await harness.WaitUntilAsync(run, s => s.View("C1").BlockReason == expected, expected);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(0, run.Snapshot().View("C1").RetryCount);
    }

    [Fact]
    public async Task An_overload_waits_short_estimated_steps_in_the_same_session()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing("Overloaded"));
        ClassifiedEnds.Install(harness, (_, attempt) => attempt == 1 ? new AgentRunEnd(AttemptEndClass.RateOrOverload) : null);

        await using var run = await harness.StartAsync(Plan(Work("T1")), Consent);
        var expected = $"RateLimitedUntilEstimated({Start + QuotaWaitPolicyShortStep:O})";
        await harness.WaitUntilAsync(run, s => s.View("T1").BlockReason == expected, expected);
        harness.Time.Advance(QuotaWaitPolicyShortStep);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal((0, "session-ejecutor-T1-1"), (run.Snapshot().View("T1").RetryCount, harness.Claude.Starts[1].Request.ResumeSessionId));
    }

    [Fact]
    public async Task A_cli_that_ends_signed_out_stops_the_task_for_a_person_with_the_command_and_no_retry()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing("Invalid API key · Please run /login"));
        ClassifiedEnds.Install(harness, (_, attempt) => attempt == 1 ? new AgentRunEnd(AttemptEndClass.SignedOut) : null);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);

        var view = run.Snapshot().View("T1");
        Assert.Equal(("AwaitingHuman", 0), (view.BlockReason, view.RetryCount));
        Assert.Contains("claude auth login", harness.Payloads<TaskStatusChangedPayload>()[^1].Payload.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Claude.Starts);
        await run.CancelAsync("fin");
        await run.CompleteAsync(harness);
    }

    /// <summary>The first short step of an overload (docs/16 D-41: a short and progressive wait).</summary>
    private static readonly TimeSpan QuotaWaitPolicyShortStep = TimeSpan.FromMinutes(1);

    // ---- the check before going on (F8-3) ----

    [Fact]
    public async Task Before_relaunching_after_a_wait_the_session_is_probed_and_the_check_is_in_the_log()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });
        var probe = new FakeProbe(ProviderId.ClaudeCode, AuthenticationState.Authenticated);
        var claudeQuota = new FakeQuotaReader(ProviderId.ClaudeCode, _ => throw new InvalidOperationException("Claude has no read of its quota without a turn."));

        await using var run = await StartAsync(harness, Plan(Work("T1")), Consent, [probe], [claudeQuota]);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        Assert.Equal(0, probe.Calls); // nothing is checked before the wait is over

        harness.Time.Advance(TimeSpan.FromMinutes(13));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var (entry, check) = Assert.Single(harness.Payloads<AccessCheckedPayload>());
        Assert.Equal(
            (new TaskId("T1"), ProviderId.ClaudeCode, AccessCheckSource.SessionProbe, AccessCheckResult.Ready, (DateTimeOffset?)null, run.Id.Value),
            (check.Task, check.Provider, check.Source, check.Result, check.ResetsAt, check.RunId));
        Assert.Equal((ActorKind.System, entry.RecordedAt), (entry.Actor.Kind, check.CheckedAt));
        var relaunch = harness.Entries.Where(e => e.Kind == DecisionKind.AgentRunStarted).ElementAt(1);
        Assert.True(entry.Sequence < relaunch.Sequence, "The check goes before the launch it allowed.");
        Assert.Equal(0, claudeQuota.Calls); // Claude gets only its session probe
    }

    [Fact]
    public async Task A_cli_signed_out_after_the_wait_stops_the_task_for_a_person_with_the_command_that_fixes_it()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });

        await using var run = await StartAsync(harness, Plan(Work("T1")), Consent, [new FakeProbe(ProviderId.ClaudeCode, AuthenticationState.NotAuthenticated)], []);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        harness.Time.Advance(TimeSpan.FromMinutes(13));
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);

        var view = run.Snapshot().View("T1");
        Assert.Equal(("AwaitingHuman", 0), (view.BlockReason, view.RetryCount));
        Assert.Contains("claude auth login", harness.Payloads<TaskStatusChangedPayload>()[^1].Payload.Reason, StringComparison.Ordinal);
        Assert.Equal(AccessCheckResult.SignedOut, Assert.Single(harness.Payloads<AccessCheckedPayload>()).Payload.Result);
        Assert.Single(harness.Claude.Starts);
        Assert.Equal(1, run.Snapshot().AgentRunsUsed); // the check launched nothing
        await run.CancelAsync("fin");
        await run.CompleteAsync(harness);
    }

    [Fact]
    public async Task For_codex_its_quota_is_read_too_and_one_still_limited_waits_again_for_its_reset()
    {
        using var harness = new EngineHarness();
        harness.Codex.Script("ejecutorCodex", "C1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });
        var later = Start + TimeSpan.FromMinutes(40);
        var quota = new FakeQuotaReader(ProviderId.CodexCli, call => call == 1 ? StillLimited(later) : Allowed());

        await using var run = await StartAsync(harness, Plan(Work("C1", agent: "ejecutorCodex")), Consent, [new FakeProbe(ProviderId.CodexCli, AuthenticationState.Authenticated)], [quota]);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        harness.Time.Advance(TimeSpan.FromMinutes(13));
        var again = $"RateLimitedUntil({later + RunOptions.DefaultQuotaResetMargin:O})";
        await harness.WaitUntilAsync(run, s => s.View("C1").BlockReason == again, again);

        Assert.Single(harness.Codex.Starts); // still limited: nothing was launched

        harness.Time.Advance(TimeSpan.FromMinutes(30));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Codex.Starts.Count);
        Assert.Equal(2, run.Snapshot().AgentRunsUsed);
        Assert.Equal(
            [
                (AccessCheckSource.SessionProbe, AccessCheckResult.Ready, (DateTimeOffset?)null),
                (AccessCheckSource.QuotaRead, AccessCheckResult.StillLimited, later),
                (AccessCheckSource.SessionProbe, AccessCheckResult.Ready, null),
                (AccessCheckSource.QuotaRead, AccessCheckResult.Ready, null),
            ],
            harness.Payloads<AccessCheckedPayload>().Select(p => (p.Payload.Source, p.Payload.Result, p.Payload.ResetsAt)));
    }

    [Fact]
    public async Task Without_the_opt_in_the_read_of_the_quota_launches_nothing_says_so_and_the_task_goes_on()
    {
        using var harness = new EngineHarness();
        harness.Codex.Script("ejecutorCodex", "C1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10) });
        var quota = new FakeQuotaReader(ProviderId.CodexCli, _ => new QuotaReadResult(QuotaReadStatus.NotOptedIn, []));

        await using var run = await StartAsync(harness, Plan(Work("C1", agent: "ejecutorCodex")), Consent, [], [quota]);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        harness.Time.Advance(TimeSpan.FromMinutes(13));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var (_, check) = Assert.Single(harness.Payloads<AccessCheckedPayload>());
        Assert.Equal((AccessCheckSource.QuotaRead, AccessCheckResult.NotOptedIn), (check.Source, check.Result));
    }

    [Fact]
    public async Task The_judge_is_checked_too_before_its_review_runs_again()
    {
        using var harness = new EngineHarness();
        var reviews = 0;
        harness.Review.Script = _ => Interlocked.Increment(ref reviews) == 1 ? ReviewDisposition.RateLimited : ReviewDisposition.Approved;
        harness.Review.RetryAfter = Start + TimeSpan.FromMinutes(10);
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")));
        var judge = harness.Roster.Agents.Single(a => a.Id.Value == "juez").Model.Provider;
        var probe = new FakeProbe(judge, AuthenticationState.Authenticated);

        await using var run = await StartAsync(harness, plan, Consent, [probe], []);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        harness.Time.Advance(TimeSpan.FromMinutes(13));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var (_, check) = Assert.Single(harness.Payloads<AccessCheckedPayload>());
        Assert.Equal((new TaskId("R1"), judge, AccessCheckSource.SessionProbe, AccessCheckResult.Ready), (check.Task, check.Provider, check.Source, check.Result));
        Assert.Equal(2, harness.Review.Requests.Count);
    }

    // ---- the checks never go round (ADR-0034, decision 9) ----

    /// <summary>
    /// A read of the quota whose reset already passed (a window read stale, or a clock that differs from the provider's) is
    /// no reset to wait for: the next check waits ahead, as an estimated step with the consent or the cooldown without it,
    /// never at once. A check that finds the limit still on launches nothing and spends no budget, but the run never checks
    /// a task more times in a row than it may launch agents (<c>MaxAgentRuns</c>): then it rests in pause for a person.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reset_already_past_is_no_wait_and_the_checks_end_in_a_pause_at_the_budget(bool consent)
    {
        using var harness = new EngineHarness();
        var reset = Start + TimeSpan.FromMinutes(10);
        harness.Codex.Script("ejecutorCodex", "C1", 1, new AgentScript { RateLimitResetsAt = reset });
        var quota = new FakeQuotaReader(ProviderId.CodexCli, _ => StillLimited(reset));

        await using var run = await StartAsync(harness, Plan(Work("C1", agent: "ejecutorCodex")), (consent ? Consent : new RunOptions()) with { MaxAgentRuns = 3 }, [], [quota]);
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        var now = consent ? reset + RunOptions.DefaultQuotaResetMargin : reset;
        harness.Time.Advance(now - Start);
        int[] steps = consent ? [5, 10, 20] : [5, 5, 5];
        for (var check = 1; check <= steps.Length; check++)
        {
            var until = now + TimeSpan.FromMinutes(steps[check - 1]);
            var expected = consent ? $"RateLimitedUntilEstimated({until:O})" : $"RateLimitedUntil({until:O})";
            await harness.WaitUntilAsync(run, s => s.View("C1").BlockReason == expected, expected);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(check, quota.Calls); // one read for each check: the wait after it is ahead
            if (check < steps.Length)
            {
                harness.Time.Advance(until - now);
                now = until;
            }
        }

        await harness.WaitForPhaseAsync(run, RunPhase.Paused);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal((3, 1, 1), (quota.Calls, harness.Codex.Starts.Count, run.Snapshot().AgentRunsUsed));
        await run.CancelAsync("fin");
        await run.CompleteAsync(harness);
    }

    /// <summary>
    /// What a resumed run counts is what its live run counted (F8-4): a review the check stopped before its judge was launched
    /// spent no launch, live or replayed.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_counts_the_budget_as_its_live_run_did_when_a_check_stopped_a_review()
    {
        using var harness = new EngineHarness();
        var reviews = 0;
        harness.Review.Script = _ => Interlocked.Increment(ref reviews) == 1 ? ReviewDisposition.RateLimited : ReviewDisposition.Approved;
        harness.Review.RetryAfter = Start + TimeSpan.FromMinutes(10);
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")));
        var judge = harness.Roster.Agents.Single(a => a.Id.Value == "juez").Model.Provider;
        var first = await StartAsync(harness, plan, Consent, [new FakeProbe(judge, AuthenticationState.NotAuthenticated)], []);
        await harness.WaitForPhaseAsync(first, RunPhase.WaitingForQuota);
        harness.Time.Advance(TimeSpan.FromMinutes(13));
        await harness.WaitForPhaseAsync(first, RunPhase.AwaitingHuman);
        var live = first.Snapshot().AgentRunsUsed;
        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, TestContext.Current.CancellationToken);
        await harness.WaitForPhaseAsync(resumed, RunPhase.AwaitingHuman);

        Assert.Equal((2, 2), (live, resumed.Snapshot().AgentRunsUsed)); // T1 and the review whose judge ran
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    /// <summary>
    /// A resumed run goes on with the steps of the waits and the count of the checks its live run had: a check that stopped a
    /// launch leaves its task where it was (pending, for an executor), and the read of the quota when the window ran out
    /// (while the attempt ran) is no check.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_goes_on_with_the_steps_and_the_count_of_the_checks_of_its_live_run()
    {
        using var harness = new EngineHarness();
        harness.Codex.Script("ejecutorCodex", "C1", 1, Limited());
        var quota = new FakeQuotaReader(ProviderId.CodexCli, _ => StillLimited(null));
        var first = await StartAsync(harness, Plan(Work("C1", agent: "ejecutorCodex")), Consent with { MaxAgentRuns = 3 }, [], [quota]);
        var limited = $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(5):O})"; // the window ran out: the first step
        await harness.WaitUntilAsync(first, s => s.View("C1").BlockReason == limited, limited);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        var checkedOnce = $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(15):O})"; // the first check: the second step
        await harness.WaitUntilAsync(first, s => s.View("C1").BlockReason == checkedOnce, checkedOnce);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        await using var resumed = await Engine(harness, [], [quota]).ResumeAsync(harness.Project, first.Id, TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromMinutes(10));
        var checkedTwice = $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(35):O})"; // the second check: the third step
        await harness.WaitUntilAsync(resumed, s => s.View("C1").BlockReason == checkedTwice && s.Phase == RunPhase.WaitingForQuota, checkedTwice);
        harness.Time.Advance(TimeSpan.FromMinutes(20));
        await harness.WaitForPhaseAsync(resumed, RunPhase.Paused); // the third check in a row, as many as MaxAgentRuns

        Assert.Equal((4, 1), (quota.Calls, harness.Codex.Starts.Count));
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    /// <summary>
    /// The waits for quota of a new run must make sense (ADR-0034, adenda del 2026-10-29): a negative margin, or a cap of the
    /// estimated waits under a minute (none at all, say), would end every wait at once and check or launch in a burst. The run
    /// is refused before anything is written.
    /// </summary>
    [Theory]
    [InlineData(-60, null)]
    [InlineData(null, 0)]
    [InlineData(null, -300)]
    [InlineData(null, 30)]
    public async Task A_run_whose_waits_for_quota_make_no_sense_is_refused_before_anything_is_written(int? marginSeconds, int? capSeconds)
    {
        using var harness = new EngineHarness();
        var options = Consent with
        {
            QuotaResetMargin = marginSeconds is { } margin ? TimeSpan.FromSeconds(margin) : null,
            MaxEstimatedQuotaWait = capSeconds is { } cap ? TimeSpan.FromSeconds(cap) : null,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => harness.StartAsync(Plan(Work("T1")), options));

        Assert.Empty(harness.Entries);
        Assert.Empty(await harness.Runs.ListAsync(harness.Project.ProjectId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A person's retry starts the checks in a row again: after they were used up and the run rested in pause, the retry
    /// checks, and a reset announced ahead is waited for by itself. The retry is the person's in the log.
    /// </summary>
    [Fact]
    public async Task A_persons_retry_arms_the_automatic_wait_again()
    {
        using var harness = new EngineHarness();
        var (quota, announced) = LimitedUntilARetry(harness);
        await using var run = await StartAsync(harness, Plan(Work("C1", agent: "ejecutorCodex")), Consent with { MaxAgentRuns = 2 }, [], [quota]);
        await UseUpTheChecksAsync(harness, run);

        await run.RetryBlockedAsync(new TaskId("C1"));
        var waiting = $"RateLimitedUntil({announced + RunOptions.DefaultQuotaResetMargin:O})";
        await harness.WaitUntilAsync(run, s => s.View("C1").BlockReason == waiting && s.Phase == RunPhase.WaitingForQuota, waiting);
        harness.Time.Advance(announced + RunOptions.DefaultQuotaResetMargin - harness.Time.GetUtcNow());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal((4, 2), (quota.Calls, harness.Codex.Starts.Count));
        var retry = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload is { From: WorkTaskStatus.Blocked, To: WorkTaskStatus.Pending } && p.Entry.Actor.Kind == ActorKind.User);
        Assert.Equal(("user", "C1"), (retry.Entry.Actor.Id, retry.Payload.Task.Value));
    }

    /// <summary>The same after a resume: the count of the checks rebuilt from the log starts again at the person's retry.</summary>
    [Fact]
    public async Task A_persons_retry_arms_the_automatic_wait_again_also_after_a_resume()
    {
        using var harness = new EngineHarness();
        var (quota, announced) = LimitedUntilARetry(harness);
        var first = await StartAsync(harness, Plan(Work("C1", agent: "ejecutorCodex")), Consent with { MaxAgentRuns = 2 }, [], [quota]);
        await UseUpTheChecksAsync(harness, first);
        await first.RetryBlockedAsync(new TaskId("C1"));
        var waiting = $"RateLimitedUntil({announced + RunOptions.DefaultQuotaResetMargin:O})";
        await harness.WaitUntilAsync(first, s => s.View("C1").BlockReason == waiting, waiting);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        await using var resumed = await Engine(harness, [], [quota]).ResumeAsync(harness.Project, first.Id, TestContext.Current.CancellationToken);
        await harness.WaitForPhaseAsync(resumed, RunPhase.WaitingForQuota);
        harness.Time.Advance(announced + RunOptions.DefaultQuotaResetMargin - harness.Time.GetUtcNow());
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal((4, 2), (quota.Calls, harness.Codex.Starts.Count));
    }

    /// <summary>
    /// C1, of Codex, hits its limit with a reset ten minutes ahead; its quota reads give that reset, already past, twice, then a
    /// reset ahead, at the returned time, and then the quota back.
    /// </summary>
    private static (FakeQuotaReader Quota, DateTimeOffset Announced) LimitedUntilARetry(EngineHarness harness)
    {
        var reset = Start + TimeSpan.FromMinutes(10);
        var announced = Start + TimeSpan.FromMinutes(69);
        harness.Codex.Script("ejecutorCodex", "C1", 1, new AgentScript { RateLimitResetsAt = reset });
        return (new FakeQuotaReader(ProviderId.CodexCli, call => call <= 2 ? StillLimited(reset) : call == 3 ? StillLimited(announced) : Allowed()), announced);
    }

    /// <summary>With <c>MaxAgentRuns</c> = 2, spends the two checks in a row of C1, whose reset already passed, until the run rests in pause.</summary>
    private static async Task UseUpTheChecksAsync(EngineHarness harness, IOrchestrationRun run)
    {
        await harness.WaitForPhaseAsync(run, RunPhase.WaitingForQuota);
        harness.Time.Advance(TimeSpan.FromMinutes(13));
        var checkedOnce = $"RateLimitedUntilEstimated({Start + TimeSpan.FromMinutes(18):O})";
        await harness.WaitUntilAsync(run, s => s.View("C1").BlockReason == checkedOnce, checkedOnce);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await harness.WaitForPhaseAsync(run, RunPhase.Paused);
    }

    /// <summary>The real engine with the harness's ports, and these session probes and quota readers.</summary>
    private static Task<IOrchestrationRun> StartAsync(EngineHarness harness, PlanDefinition plan, RunOptions options, IReadOnlyList<IProviderProbe> probes, IReadOnlyList<IProviderQuotaReader> quota) =>
        Engine(harness, probes, quota).StartAsync(harness.Request(plan, options), TestContext.Current.CancellationToken);

    /// <summary>An orchestrator on the harness's ports, with these session probes and quota readers.</summary>
    private static Orchestrator Engine(EngineHarness harness, IReadOnlyList<IProviderProbe> probes, IReadOnlyList<IProviderQuotaReader> quota) =>
        new(
            harness.Decisions, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers, harness.Worktrees,
            harness.Verification, harness.Integrator, harness.Review, harness.Approvals, harness.Skills, harness.Paths, harness.Time, harness.WriteConfinement,
            probes: probes,
            quotaReaders: quota);

    /// <summary>A read of the quota of Codex: a window full, with the limit named, until <paramref name="resetsAt"/> (null: it does not say).</summary>
    private static QuotaReadResult StillLimited(DateTimeOffset? resetsAt) =>
        new(QuotaReadStatus.Read, [new QuotaObservation(ProviderId.CodexCli, "primary", MetricValue<double>.Exact(1, "prueba"), TimeSpan.FromHours(5), resetsAt, Start, "prueba") { Reached = "primary" }])
        {
            OrdinaryUsageAllowed = false,
        };

    private static QuotaReadResult Allowed() =>
        new(QuotaReadStatus.Read, [new QuotaObservation(ProviderId.CodexCli, "primary", MetricValue<double>.Exact(0.2, "prueba"), TimeSpan.FromHours(5), Start + TimeSpan.FromHours(5), Start, "prueba")])
        {
            OrdinaryUsageAllowed = true,
        };

    /// <summary>A launch the provider refuses for its usage limit without saying when it resets (Codex <c>exec</c>, say).</summary>
    private static AgentScript Limited() => new() { Status = AgentRunStatus.RateLimited, StructuredOutput = null, Error = "límite de uso" };

    /// <summary>A session probe that answers as told, and counts its calls.</summary>
    private sealed class FakeProbe(ProviderId provider, AuthenticationState state) : IProviderProbe
    {
        private int _calls;

        public ProviderId Provider => provider;

        public VendorId Vendor => provider == ProviderId.CodexCli ? VendorId.OpenAI : VendorId.Anthropic;

        public string DisplayName => provider == ProviderId.CodexCli ? "Codex CLI" : "Claude Code";

        public int Calls => Volatile.Read(ref _calls);

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProviderProbeResult(provider, DisplayName, InstallationState.Installed, "cli.exe", "1.0.0", state, null, []));
        }
    }

    /// <summary>A quota reader that gives the n-th answer on its n-th call.</summary>
    private sealed class FakeQuotaReader(ProviderId provider, Func<int, QuotaReadResult> answer) : IProviderQuotaReader
    {
        private int _calls;

        public ProviderId Provider => provider;

        public int Calls => Volatile.Read(ref _calls);

        public Task<QuotaReadResult> ReadAsync(CancellationToken ct) => Task.FromResult(answer(Interlocked.Increment(ref _calls)));
    }
}

/// <summary>
/// A scripted CLI whose attempts end classified as its adapter would classify them (docs/16 D-41): <c>classify</c> gives the
/// class of the end of a request and its attempt, or null to leave the end as the script says.
/// </summary>
internal sealed class ClassifiedEnds(IAgentProvider inner, Func<AgentRunRequest, int, AgentRunEnd?> classify) : IAgentProvider
{
    public ProviderId Provider => inner.Provider;

    public VendorId Vendor => inner.Vendor;

    public ProviderCapabilities Capabilities => inner.Capabilities;

    /// <summary>The CLIs of the harness, classified, in place of the plain scripted ones.</summary>
    public static void Install(EngineHarness harness, Func<AgentRunRequest, int, AgentRunEnd?> claude, Func<AgentRunRequest, int, AgentRunEnd?>? codex = null) =>
        harness.Providers = new FakeProjectAgentProviders(
        [
            AgentStudio.Providers.Composition.AgentProviderPipeline.Compose(new ClassifiedEnds(harness.Claude, claude), harness.Journal, harness.Time),
            AgentStudio.Providers.Composition.AgentProviderPipeline.Compose(new ClassifiedEnds(harness.Codex, codex ?? ((_, _) => null)), harness.Journal, harness.Time),
        ]);

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var run = await inner.StartAsync(request, cancellationToken);
        return classify(request, ScriptedAgentProvider.AttemptOf(request) ?? 1) is { } end ? new Classified(run, end) : run;
    }

    private sealed class Classified(IAgentRun inner, AgentRunEnd end) : IAgentRun
    {
        public string RunId => inner.RunId;

        public IAsyncEnumerable<AgentEvent> Events => inner.Events;

        public Task<AgentRunOutcome> Completion { get; } = WithEnd(inner.Completion, end);

        public Task CancelAsync() => inner.CancelAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static async Task<AgentRunOutcome> WithEnd(Task<AgentRunOutcome> completion, AgentRunEnd end) => (await completion) with { End = end };
    }
}
