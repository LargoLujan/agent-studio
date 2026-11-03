using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase8;

/// <summary>
/// F8-2 and F8-3 (docs/16 D-36 to D-38, ADR-0034) on the real engine, SQLite and git, with the real adapters of the CLIs
/// over fake processes that play the synthetic captures of WP2: a limit is waited for as the header says, the access of
/// the provider is checked before the relaunch, every launch counts against the budget, and the judge still reviews the
/// work (rule P of ADR-0014). The clock is the test's: nothing waits in real time.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class QuotaWaitIntegrationTests
{
    private const string FiveHourCapture = "01-ventana-cinco-horas-rechazada.jsonl";
    private const string WeeklyCapture = "02-ventana-semanal-rechazada.jsonl";
    private const string CodexUsageLimit = "01-limite-de-uso.jsonl";

    /// <summary>The session of the synthetic capture of the five-hour window: the relaunch resumes it.</summary>
    private const string CapturedSession = "00000000-0000-4000-8000-000000000101";

    /// <summary>
    /// With the consent of the header, the window of Claude that ran out (the synthetic capture, played by the real adapter
    /// and classified by its parser) is waited for until its reset plus the margin of D-36, three hours here, past the
    /// 15 minutes the engine waits by itself without the consent. Not a second before: the reset came and the margin did not,
    /// and nothing was checked or launched. Then the session of Claude is checked (<c>AccessChecked</c>, before the launch it
    /// allows), the same agent goes again on the same provider, model and session, and the work still goes through the
    /// integration and the review of Codex's judge, who approves it on the tests the system captured.
    /// </summary>
    [Fact]
    public async Task With_consent_a_window_of_claude_that_ran_out_is_waited_for_until_its_reset_plus_the_margin_checked_relaunched_and_still_judged()
    {
        using var repo = Phase5Runs.Repo();
        var world = new Phase8World(Phase8Clock.ThreeHoursBeforeReset, reviewed: true);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1
            ? CliTurns.Capture(ProviderId.ClaudeCode, FiveHourCapture)
            : CliTurns.Works(launch, ("src/feature.txt", "función nueva\n"));
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase5Runs.Reviewed(), Phase8Runs.Checked(), Phase8Runs.Consent);
        var relaunchAt = Phase8Clock.FiveHourReset + RunOptions.DefaultQuotaResetMargin;
        var waiting = Phase8Log.Until(relaunchAt);
        await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("T1").BlockReason == waiting, waiting);

        // The end of the attempt as the adapter classified the capture (AgentRunFinished v3): the window, its type and its reset.
        var (_, limited) = Assert.Single(await Phase8Log.OfAsync<AgentRunFinishedPayload>(app, run.Id));
        Assert.True(limited.RecordsEndClass(), $"AgentRunFinished version {limited.V}");
        Assert.Equal((AttemptOutcome.RateLimited, new AttemptEndSnapshot(AttemptEndClass.WindowQuota, "five_hour", Phase8Clock.FiveHourReset)), (limited.Status, limited.End));

        // The reset came, the margin did not: nothing is checked, nothing is launched.
        await Phase8Runs.TurnAsync(run, () => clock.AdvanceTo(Phase8Clock.FiveHourReset));
        await Phase8Runs.TurnAsync(run, () => clock.AdvanceTo(relaunchAt - TimeSpan.FromSeconds(1)));
        Assert.Equal((RunPhase.WaitingForQuota, WorkTaskStatus.Blocked, 1), (run.Snapshot().Phase, run.Snapshot().View("T1").Status, run.Snapshot().View("T1").Attempt));
        Assert.Single(world.Claude.Launches);
        Assert.Equal(0, world.ClaudeProbe.Calls);

        clock.AdvanceTo(relaunchAt);
        var result = await Phase8Runs.SucceededAsync(app, run);

        var entries = await Phase8Log.OfAsync(app, run.Id);
        Assert.Equal(
            [
                "started",
                "launch T1#1",
                "T1 Pending>Running",
                $"end T1#1 RateLimited WindowQuota five_hour {Phase8Log.Time(Phase8Clock.FiveHourReset)}",
                $"T1 Running>Blocked {waiting}",
                "T1 Blocked>Pending",
                "check T1 claude-code SessionProbe Ready",
                "launch T1#2",
                "T1 Pending>Running",
                "end T1#2 Succeeded Completed",
                "T1 Running>Completed",
                "I1 Pending>Running",
                "integration I1",
                "I1 Running>Completed",
                "R1 Pending>Running",
                "launch juez#1",
                "end -#1 Succeeded Completed",
                "verdict R1 Approved",
                "R1 Running>Completed",
                "finished Succeeded",
            ],
            Phase8Log.Story(entries));

        // The check is the system's, at the end of the wait, and nothing of what the probe printed is in it.
        var (checkEntry, check) = Assert.Single(await Phase8Log.OfAsync<AccessCheckedPayload>(app, run.Id));
        Assert.Equal((ActorKind.System, relaunchAt, (DateTimeOffset?)null), (checkEntry.Actor.Kind, check.CheckedAt, check.ResetsAt));
        Assert.Equal((1, 0), (world.ClaudeProbe.Calls, world.CodexQuota.Calls));

        // Never another provider, account or model: the same agent, on its model, in the session the window cut.
        var (first, second) = (world.Claude.Launches[0], world.Claude.Launches[1]);
        Assert.Equal(("ejecutor", "haiku", null), (first.Agent, first.ArgumentAfter("--model"), first.ArgumentAfter("--resume")));
        Assert.Equal(("ejecutor", "haiku", CapturedSession), (second.Agent, second.ArgumentAfter("--model"), second.ArgumentAfter("--resume")));
        Assert.Empty(world.CodexExec.Launches);

        // Rule P (ADR-0014): the judge of the other vendor reviewed the relaunched work, and the run ends on its verdict.
        var judge = Assert.Single(world.Judges.Codex.Script.Starts);
        Assert.Equal("juez", judge.Request.Agent.Id.Value);
        var (_, verdict) = Assert.Single(await Phase8Log.OfAsync<VerdictIssuedPayload>(app, run.Id));
        Assert.True(verdict.Valid);
        Assert.Equal(verdict.VerdictId, result.VerdictId);
        Assert.Equal(verdict.VerdictId, Assert.Single(await Phase8Log.OfAsync<RunFinishedPayload>(app, run.Id)).Payload.VerdictId);

        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        Assert.All(entries.Select(e => e.Payload).OfType<AgentRunFinishedPayload>(), end => Assert.True(end.RecordsEndClass(), $"AgentRunFinished version {end.V}"));
        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>
    /// F8-3: the check before the relaunch finds the session of Claude expired while the run waited. Nothing is launched and
    /// no launch is spent: the task waits for a person, with the command that fixes it, and the check is in the log.
    /// </summary>
    [Fact]
    public async Task With_consent_a_session_found_expired_after_the_wait_stops_the_task_for_a_person_and_launches_nothing()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Phase8Clock.ThreeHoursBeforeReset);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1
            ? CliTurns.Capture(ProviderId.ClaudeCode, FiveHourCapture)
            : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), Phase8Runs.Consent);
        var relaunchAt = Phase8Clock.FiveHourReset + RunOptions.DefaultQuotaResetMargin;
        var waiting = Phase8Log.Until(relaunchAt);
        await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("T1").BlockReason == waiting, waiting);
        world.ClaudeProbe.State = AuthenticationState.NotAuthenticated; // the session expired meanwhile
        clock.AdvanceTo(relaunchAt);
        await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.AwaitingHuman, "the run waiting for a person");

        Assert.Equal(("AwaitingHuman", 1), (run.Snapshot().View("T1").BlockReason, run.Snapshot().AgentRunsUsed)); // the check launched nothing
        Assert.Single(world.Claude.Launches);
        var entries = await Phase8Log.OfAsync(app, run.Id);
        var blocked = entries.Select(e => e.Payload).OfType<TaskStatusChangedPayload>().Last();
        Assert.Contains("claude auth login", blocked.Reason, StringComparison.Ordinal);

        await run.CancelAsync("fin de la prueba");
        await Phase8Runs.EndAsync(app, run);
        Assert.Equal(
            [
                "started",
                "launch T1#1",
                "T1 Pending>Running",
                $"end T1#1 RateLimited WindowQuota five_hour {Phase8Log.Time(Phase8Clock.FiveHourReset)}",
                $"T1 Running>Blocked {waiting}",
                "T1 Blocked>Pending",
                "check T1 claude-code SessionProbe SignedOut",
                "T1 Pending>Blocked AwaitingHuman",
                "T1 Blocked>Cancelled",
                "finished Cancelled",
            ],
            await Phase8Log.StoryAsync(app, run.Id));
        Phase8Log.AssertEveryLaunchEndedOnce(await Phase8Log.OfAsync(app, run.Id));
        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>
    /// With the consent, a window of <c>codex exec</c> has no reset time (the capture says none and the read of its quota
    /// launches nothing without the opt-in): the waits are the estimated ones of D-37, 5 and 10 minutes and then 20 capped by
    /// <c>MaxEstimatedQuotaWait</c> (15 here), each marked «estimada». Every relaunch is a launch of the budget, so with
    /// <c>MaxAgentRuns</c> 3 the third wait ends the run instead of a fourth launch: the waits never loop, and once the run
    /// ended no amount of time launches anything.
    /// </summary>
    [Fact]
    public async Task With_consent_a_codex_window_without_a_reset_waits_capped_estimated_steps_and_its_budget_ends_the_run()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.CodexExec.Turn = _ => CliTurns.Capture(ProviderId.CodexCli, CodexUsageLimit);
        await using var app = await world.OpenAsync(repo.Root, Roster());
        var options = Phase8Runs.Consent with { MaxEstimatedQuotaWait = TimeSpan.FromMinutes(15), MaxAgentRuns = 3 };

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single("C1", "ejecutorCodex"), Phase8Runs.NoSteps(), options);
        var blocks = new List<string>();
        var at = Start;
        foreach (var wait in new[] { 5, 10, 15 }.Select(m => TimeSpan.FromMinutes(m)))
        {
            var expected = Phase8Log.Until(at + wait, estimated: true);
            await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("C1").BlockReason == expected, expected);
            blocks.Add(expected);
            clock.Advance(wait);
            at += wait;
        }

        var result = await Phase8Runs.EndAsync(app, run);
        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("presupuesto de ejecuciones de agente (3)", result.Summary, StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(3, world.CodexExec.Launches.Count);

        string[] Attempt(int n) =>
        [
            $"launch C1#{n}",
            "C1 Pending>Running",
            $"end C1#{n} RateLimited WindowQuota",
            "check C1 codex-cli QuotaRead NotOptedIn",
            $"C1 Running>Blocked {blocks[n - 1]}",
            "C1 Blocked>Pending",
        ];

        string[] check = ["check C1 codex-cli SessionProbe Ready", "check C1 codex-cli QuotaRead NotOptedIn"];
        Assert.Equal(
            [
                "started",
                .. Attempt(1),
                .. check,
                .. Attempt(2),
                .. check,
                .. Attempt(3),
                "alert run.budget-exhausted",
                "finished Failed",
            ],
            await Phase8Log.StoryAsync(app, run.Id));
        Assert.Empty(world.Claude.Launches); // never another provider
        var entries = await Phase8Log.OfAsync(app, run.Id);
        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>
    /// The app-server of Codex keeps saying, with the opt-in, that the window is still full and that it reset ten minutes ago
    /// (WP1 review, finding 1, fixed in fa3e79d). A reset that already passed is no reset to wait for: each stopped check waits
    /// the next estimated step ahead, so with the clock still the loop rests and the checks are bounded. The checks launch
    /// nothing and spend no launch, and after <c>MaxAgentRuns</c> of them in a row (3 here) the task no longer waits by itself:
    /// the run rests in pause for a person (ADR-0034, decision 9), however far the clock goes. The person retries it, and
    /// once the check finds the quota available the task is launched again in the same budget.
    /// </summary>
    [Fact]
    public async Task A_quota_read_still_limited_until_a_past_reset_waits_ahead_and_after_the_budget_of_checks_in_a_row_waits_for_a_person()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.CodexExec.Turn = launch => launch.Attempt == 1
            ? CliTurns.Capture(ProviderId.CodexCli, CodexUsageLimit)
            : CliTurns.Works(launch, ("src/C1/a.txt", "a\n"));
        world.CodexQuota.Answer = call => call is > 1 and <= 4
            ? FakeQuotaRead.StillLimited(clock.Now - TimeSpan.FromMinutes(10), clock.Now)
            : new QuotaReadResult(QuotaReadStatus.NotOptedIn, []);
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single("C1", "ejecutorCodex"), Phase8Runs.NoSteps(), Phase8Runs.Consent with { MaxAgentRuns = 3 });
        var waits = new[] { 5, 10, 20, 40 }.Select(m => TimeSpan.FromMinutes(m)).ToList();
        var blocks = new List<string>();
        var at = Start;
        foreach (var wait in waits)
        {
            at += wait;
            var expected = Phase8Log.Until(at, estimated: true);
            await Phase8Runs.UntilAsync(app, run, s => s.View("C1").BlockReason == expected, expected);
            blocks.Add(expected);
            if (blocks.Count == 2)
            {
                // The first check found a reset already past: it waits ahead, and with the clock still it checks no more.
                Assert.True(await Phase8Runs.RestsWithinAsync(run, TimeSpan.FromSeconds(10)), "The loop did not rest after a check that found the limit still on.");
                Assert.Equal((3, 2), ((await Phase8Log.OfAsync<AccessCheckedPayload>(app, run.Id)).Count, world.CodexQuota.Calls));
            }

            if (blocks.Count < waits.Count)
            {
                clock.AdvanceTo(at);
            }
        }

        // Three checks in a row found the limit on: the run waits for a person, and no amount of time checks again.
        await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.Paused, "the pause for a person");
        await Phase8Runs.TurnAsync(run, () => clock.AdvanceTo(at + TimeSpan.FromHours(2)));
        Assert.Equal((RunPhase.Paused, 1, 4), (run.Snapshot().Phase, run.Snapshot().AgentRunsUsed, world.CodexQuota.Calls)); // the checks spent no launch
        Assert.Single(world.CodexExec.Launches);

        // The person retries it: checked again, and launched.
        await run.RetryBlockedAsync(new TaskId("C1"));
        await Phase8Runs.SucceededAsync(app, run);

        string[] stopped(int n) =>
        [
            "C1 Blocked>Pending",
            "check C1 codex-cli SessionProbe Ready",
            $"check C1 codex-cli QuotaRead StillLimited {Phase8Log.Time(Start + waits.Take(n).Aggregate(TimeSpan.Zero, (a, b) => a + b) - TimeSpan.FromMinutes(10))}",
            $"C1 Pending>Blocked {blocks[n]}",
        ];
        Assert.Equal(
            [
                "started",
                "launch C1#1",
                "C1 Pending>Running",
                "end C1#1 RateLimited WindowQuota",
                "check C1 codex-cli QuotaRead NotOptedIn",
                $"C1 Running>Blocked {blocks[0]}",
                .. stopped(1),
                .. stopped(2),
                .. stopped(3),
                "C1 Blocked>Pending",
                "check C1 codex-cli SessionProbe Ready",
                "check C1 codex-cli QuotaRead NotOptedIn",
                "launch C1#5", // each stopped check took the number of the attempt it stopped, and launched nothing
                "C1 Pending>Running",
                "end C1#5 Succeeded Completed",
                "C1 Running>Completed",
                "finished Succeeded",
            ],
            await Phase8Log.StoryAsync(app, run.Id));
        Assert.Equal(2, run.Snapshot().AgentRunsUsed);
        Assert.Equal([1, 5], world.CodexExec.Launches.Select(l => l.Attempt));
        Phase8Log.AssertEveryLaunchEndedOnce(await Phase8Log.OfAsync(app, run.Id));
        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>
    /// Without the consent, as before Phase 8: a window that resets within the bound (<c>MaxAutoWaitForQuota</c>, 15 minutes)
    /// is waited for until its reset, with no margin, by itself; the next one, the weekly window three days away, is past the
    /// bound, and the run rests in pause until a person retries it. Nothing relaunches after that, not even once the reset
    /// came: the wait past the bound is never the engine's.
    /// </summary>
    [Fact]
    public async Task Without_consent_a_limit_is_waited_for_only_up_to_the_bound_and_past_it_the_run_pauses_and_nothing_relaunches()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Phase8Clock.FiveHourReset - TimeSpan.FromMinutes(10));
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt switch
        {
            1 => CliTurns.Capture(ProviderId.ClaudeCode, FiveHourCapture),
            2 => CliTurns.Capture(ProviderId.ClaudeCode, WeeklyCapture),
            _ => CliTurns.Works(launch, ("src/T1/a.txt", "a\n")),
        };
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), new RunOptions());
        var bounded = Phase8Log.Until(Phase8Clock.FiveHourReset);
        await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("T1").BlockReason == bounded, bounded);

        // One second before the reset: still waiting, nothing checked and nothing launched again.
        await Phase8Runs.TurnAsync(run, () => clock.AdvanceTo(Phase8Clock.FiveHourReset - TimeSpan.FromSeconds(1)));
        Assert.Equal((WorkTaskStatus.Blocked, 1), (run.Snapshot().View("T1").Status, run.Snapshot().View("T1").Attempt));
        Assert.Single(world.Claude.Launches);
        Assert.Equal(0, world.ClaudeProbe.Calls);

        // The reset: the session is checked and the task goes again, and its next limit is three days away.
        clock.AdvanceTo(Phase8Clock.FiveHourReset);
        var paused = Phase8Log.Until(Phase8Clock.WeeklyReset);
        await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.Paused && s.View("T1").BlockReason == paused, paused);

        // Past the reset of the weekly window, the run is still in pause: nothing relaunches by itself.
        await Phase8Runs.TurnAsync(run, () => clock.AdvanceTo(Phase8Clock.WeeklyReset + TimeSpan.FromDays(1)));
        Assert.Equal((RunPhase.Paused, WorkTaskStatus.Blocked, 2), (run.Snapshot().Phase, run.Snapshot().View("T1").Status, run.Snapshot().View("T1").Attempt));
        Assert.Equal(2, world.Claude.Launches.Count);
        Assert.Equal(1, world.ClaudeProbe.Calls);

        await run.CancelAsync("fin de la prueba");
        Assert.Equal(RunPhase.Cancelled, (await Phase8Runs.EndAsync(app, run)).Phase);
        Assert.Equal(
            [
                "started",
                "launch T1#1",
                "T1 Pending>Running",
                $"end T1#1 RateLimited WindowQuota five_hour {Phase8Log.Time(Phase8Clock.FiveHourReset)}",
                $"T1 Running>Blocked {bounded}",
                "T1 Blocked>Pending",
                "check T1 claude-code SessionProbe Ready",
                "launch T1#2",
                "T1 Pending>Running",
                $"end T1#2 RateLimited WindowQuota seven_day {Phase8Log.Time(Phase8Clock.WeeklyReset)}",
                $"T1 Running>Blocked {paused}",
                "T1 Blocked>Cancelled",
                "finished Cancelled",
            ],
            await Phase8Log.StoryAsync(app, run.Id));
        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>
    /// Without the consent, a window of <c>codex exec</c> without a reset time waits the engine's cooldown of five minutes,
    /// which is within the bound, so it is waited for by itself (not «estimada»: that is only with the consent), and the
    /// task goes again after the check of the session and of the quota.
    /// </summary>
    [Fact]
    public async Task Without_consent_a_codex_window_without_a_reset_cools_down_five_minutes_by_itself_and_relaunches()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.CodexExec.Turn = launch => launch.Attempt == 1
            ? CliTurns.Capture(ProviderId.CodexCli, CodexUsageLimit)
            : CliTurns.Works(launch, ("src/C1/a.txt", "a\n"));
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single("C1", "ejecutorCodex"), Phase8Runs.NoSteps(), new RunOptions());
        var cooldown = Phase8Log.Until(Start + TimeSpan.FromMinutes(5));
        await Phase8Runs.UntilAsync(app, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("C1").BlockReason == cooldown, cooldown);
        await Phase8Runs.TurnAsync(run, () => clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)));
        Assert.Equal((WorkTaskStatus.Blocked, 1), (run.Snapshot().View("C1").Status, run.Snapshot().View("C1").Attempt));
        Assert.Single(world.CodexExec.Launches);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Phase8Runs.SucceededAsync(app, run);

        Assert.Equal(
            [
                "started",
                "launch C1#1",
                "C1 Pending>Running",
                "end C1#1 RateLimited WindowQuota",
                "check C1 codex-cli QuotaRead NotOptedIn",
                $"C1 Running>Blocked {cooldown}",
                "C1 Blocked>Pending",
                "check C1 codex-cli SessionProbe Ready",
                "check C1 codex-cli QuotaRead NotOptedIn",
                "launch C1#2",
                "C1 Pending>Running",
                "end C1#2 Succeeded Completed",
                "C1 Running>Completed",
                "finished Succeeded",
            ],
            await Phase8Log.StoryAsync(app, run.Id));
        Phase8Log.AssertEveryLaunchEndedOnce(await Phase8Log.OfAsync(app, run.Id));
        await Phase8Log.AssertVerifiedAsync(app);
    }
}
