using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Background;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase8;

/// <summary>
/// F8-4 and D-39 (ADR-0034, which amends decision 3 of ADR-0010 for the runs whose header carries the consent): the app dies
/// in the middle of a run, with no <c>RunExitChosen</c>, and another instance opens the same project from disk. With the
/// consent, <see cref="ResumeScheduler"/> resumes the run, writing <c>RunResumed</c> before anything is recovered or launched;
/// without it, nothing launches. A person's choice wins: a pause, or a choice on leaving, is never undone by the consent,
/// not even by a <c>SessionEnded</c> of Windows after it. Nothing announced is ever repeated: no attempt is launched twice
/// without its end, and an integration is never done again. Real engine, SQLite, git and the real adapters over fake
/// processes; the desktop's own recorder of exit choices (<see cref="DecisionLogRunExitRecorder"/>).
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class ResumeIntegrationTests
{
    private const string FiveHourCapture = "01-ventana-cinco-horas-rechazada.jsonl";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The app dies while the attempt of T1 runs. With the consent, the scheduler of the next instance resumes the run by
    /// itself, as a close nobody asked for: <c>RunResumed</c> is the first entry after the cut, with the hash of the header
    /// that carried the consent; then the recovery closes the attempt as interrupted and reopens T1 without spending a retry;
    /// the next attempt goes in a new session (never the interrupted one's, D-43) with the system's handoff on its standard
    /// input; and the run goes on to its integration and the judge's review, and succeeds.
    /// </summary>
    [Fact]
    public async Task With_consent_a_run_cut_mid_attempt_resumes_by_itself_RunResumed_first_and_completes_through_the_judge()
    {
        using var repo = Phase5Runs.Repo();
        var world = new Phase8World(Start, reviewed: true);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1 ? CliTurns.Hangs(launch) : CliTurns.Works(launch, ("src/feature.txt", "función nueva\n"));

        RunId id;
        long cut;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, Phase5Runs.Reviewed(), Phase8Runs.Checked(), Phase8Runs.Consent);
            id = run.Id;
            await Phase8Runs.UntilStatusAsync(first, run, "T1", WorkTaskStatus.Running);
            cut = await Phase8Runs.DieAsync(first, run);
        }

        Assert.Equal(0, world.Claude.Running); // the process of the attempt died with the app

        await using var second = await world.OpenAsync(repo.Root);
        var scheduler = Phase8Runs.Scheduler(second);
        var announced = new List<ScheduledResumeEventArgs>();
        scheduler.Resumed += (_, e) => announced.Add(e);
        var outcome = Assert.Single(await scheduler.ResumeConsentedAsync(second.Project, Ct));
        Assert.Equal((id, RunResumeCause.ConsentAfterUnrequestedClose), (outcome.Run, outcome.Cause));
        var resumed = outcome.Resumed ?? throw new InvalidOperationException("The run was not resumed: " + outcome.Reason);
        Assert.Same(resumed, Assert.Single(announced).Run);
        await using (resumed)
        {
            await Phase8Runs.SucceededAsync(second, resumed);
        }

        var entries = await Phase8Log.OfAsync(second, id);
        var after = entries.Where(e => e.Entry.Sequence > cut).ToList();
        Assert.Equal(
            [
                "resumed ConsentAfterUnrequestedClose",
                "end T1#1 Interrupted Interrupted",
                "T1 Running>Blocked Interrupted",
                "T1 Blocked>Pending",
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
            Phase8Log.Story(after));

        // RunResumed before anything else of the second instance, by the system and with the header that carried the consent.
        var (resumedEntry, cause) = Assert.Single(entries.Where(e => e.Payload is RunResumedPayload).Select(e => (e.Entry, (RunResumedPayload)e.Payload)));
        Assert.Equal(cut + 1, resumedEntry.Sequence);
        Assert.Equal((ActorKind.System, "agent-studio", true), (resumedEntry.Actor.Kind, cause.DecidedBy, cause.ByConsent()));
        Assert.Equal(entries.Select(e => e.Payload).OfType<RunStartedPayload>().Single().HeaderHash, cause.ConsentHeaderHash);

        // D-43: the interrupted session is never resumed; the next attempt is a new session with the handoff of the system.
        var relaunch = world.Claude.Launches[1];
        Assert.Null(relaunch.ArgumentAfter("--resume"));
        Assert.NotNull(relaunch.ArgumentAfter("--session-id"));
        Assert.Contains("<handoff task=\"T1\" attempt=\"2\" reason=\"Interrupted\">", relaunch.StandardInput, StringComparison.Ordinal);
        Assert.Equal(0, resumed.Snapshot().View("T1").RetryCount);

        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        Assert.Single(entries, e => e.Payload is IntegrationPerformedPayload);
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// The app dies while the judge reviews: the integration was done before the cut, and the resume does not do it again,
    /// nor relaunch T1. Only the review goes again: the judge's launch the app never saw end is closed as interrupted, and the
    /// judge is asked again on the same integration.
    /// </summary>
    [Fact]
    public async Task With_consent_a_run_cut_during_its_review_resumes_without_repeating_the_integration_or_the_work()
    {
        using var repo = Phase5Runs.Repo();
        var world = new Phase8World(Start, reviewed: true);
        var clock = world.Clock;
        world.Claude.Turn = launch => CliTurns.Works(launch, ("src/feature.txt", "función nueva\n"));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var judged = 0;
        world.Judges.Hold = request => request.Agent.Role == AgentRole.Judge && Interlocked.Increment(ref judged) == 1 ? held : null;

        RunId id;
        long cut;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, Phase5Runs.Reviewed(), Phase8Runs.Checked(), Phase8Runs.Consent);
            id = run.Id;
            await Phase8Runs.UntilAsync(first, run, s => s.View("R1").Status == WorkTaskStatus.Running && world.Judges.Codex.Script.Starts.Count == 1, "the judge reviewing R1");
            cut = await Phase8Runs.DieAsync(first, run);
            Assert.Single(await Phase8Log.OfAsync<IntegrationPerformedPayload>(first, id));
        }

        await using var second = await world.OpenAsync(repo.Root);
        var outcome = Assert.Single(await Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct));
        Assert.Equal(RunResumeCause.ConsentAfterUnrequestedClose, outcome.Cause);
        await using (var resumed = outcome.Resumed ?? throw new InvalidOperationException(outcome.Reason))
        {
            await Phase8Runs.SucceededAsync(second, resumed);
            Assert.Equal(3, resumed.Snapshot().AgentRunsUsed); // T1 once, and the review twice: the first one ran before the cut
        }

        var entries = await Phase8Log.OfAsync(second, id);
        var story = Phase8Log.Story(entries.Where(e => e.Entry.Sequence > cut));
        Assert.Equal("resumed ConsentAfterUnrequestedClose", story[0]);
        Assert.Equal(
            [
                "R1 Running>Blocked Interrupted",
                "R1 Blocked>Pending",
                "R1 Pending>Running",
                "launch juez#1",
                "end -#1 Succeeded Completed",
                "verdict R1 Approved",
                "R1 Running>Completed",
                "finished Succeeded",
            ],
            story.SkipWhile(line => !line.StartsWith("R1 Running>Blocked", StringComparison.Ordinal)));
        Assert.DoesNotContain(story, line => line.StartsWith("launch T1", StringComparison.Ordinal) || line.StartsWith("integration", StringComparison.Ordinal));

        Assert.Single(world.Claude.Launches); // the work of T1 is never repeated
        Assert.Equal(2, world.Judges.Codex.Script.Starts.Count);
        Assert.Single(entries, e => e.Payload is IntegrationPerformedPayload);
        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// Without the consent, the next instance launches nothing: the scheduler leaves the run for a person and writes nothing,
    /// and the run shows as interrupted. A person can still resume it, which is recorded as theirs.
    /// </summary>
    [Fact]
    public async Task Without_consent_a_cut_run_is_left_interrupted_nothing_launches_and_a_person_can_resume_it()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1 ? CliTurns.Hangs(launch) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));

        RunId id;
        long cut;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), new RunOptions());
            id = run.Id;
            await Phase8Runs.UntilStatusAsync(first, run, "T1", WorkTaskStatus.Running);
            cut = await Phase8Runs.DieAsync(first, run);
        }

        await using var second = await world.OpenAsync(repo.Root);
        var outcome = Assert.Single(await Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct));
        Assert.Equal((id, (RunResumeCause?)null, (IOrchestrationRun?)null), (outcome.Run, outcome.Cause, outcome.Resumed));
        Assert.Contains("consentimiento", outcome.Reason, StringComparison.Ordinal);

        Assert.Equal(cut, (await second.LoadLogAsync()).Entries[^1].Sequence); // nothing written
        Assert.Single(world.Claude.Launches);
        var listed = Assert.Single(await second.Orchestrator.ListRunsAsync(second.Project, Ct));
        Assert.Equal(RunPhase.Interrupted, listed.Phase);
        Assert.StartsWith("Interrumpida", listed.Summary, StringComparison.Ordinal);

        // A person resumes it: then it goes, and the log says it was the person.
        await using (var resumed = await second.Orchestrator.ResumeAsync(second.Project, id, Ct))
        {
            await Phase8Runs.SucceededAsync(second, resumed);
        }

        var entries = await Phase8Log.OfAsync(second, id);
        var (entry, cause) = Assert.Single(entries.Where(e => e.Payload is RunResumedPayload).Select(e => (e.Entry, (RunResumedPayload)e.Payload)));
        Assert.Equal((RunResumeCause.UserRequested, ActorKind.User, "user", (string?)null), (cause.Cause, entry.Actor.Kind, cause.DecidedBy, cause.ConsentHeaderHash));
        Assert.Equal(cut + 1, entry.Sequence);
        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// Two ways of reopening at once (the start of the app and the tray, say) ask the scheduler together, and a second
    /// scheduler over the same engine too: the run is resumed once, with one <c>RunResumed</c> and one relaunch, and the
    /// other passes leave it (the passes of a scheduler go one at a time, and the engine reserves the run of a resume).
    /// </summary>
    [Fact]
    public async Task Two_reopen_paths_racing_resume_the_same_run_only_once()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1 ? CliTurns.Hangs(launch) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));

        RunId id;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), Phase8Runs.Consent);
            id = run.Id;
            await Phase8Runs.UntilStatusAsync(first, run, "T1", WorkTaskStatus.Running);
            await Phase8Runs.DieAsync(first, run);
        }

        await using var second = await world.OpenAsync(repo.Root);
        var scheduler = Phase8Runs.Scheduler(second); // the one of the app: the start and the tray ask it
        var passes = await Task.WhenAll(
            Task.Run(() => scheduler.ResumeConsentedAsync(second.Project, Ct), Ct),
            Task.Run(() => scheduler.ResumeConsentedAsync(second.Project, Ct), Ct),
            Task.Run(() => Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct), Ct)); // and another over the same engine
        var resumes = passes.SelectMany(p => p).Where(r => r.Resumed is not null).ToList();
        var resumed = Assert.Single(resumes).Resumed!;
        await using (resumed)
        {
            await Phase8Runs.SucceededAsync(second, resumed);
        }

        var entries = await Phase8Log.OfAsync(second, id);
        Assert.Single(entries, e => e.Payload is RunResumedPayload);
        Assert.Equal(2, world.Claude.Launches.Count);
        Assert.Equal(["launch T1#1", "launch T1#2"], Phase8Log.Story(entries).Where(l => l.StartsWith("launch", StringComparison.Ordinal)));
        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// While the app leaves (the managed exit asks the person, or Windows ends the session), the resumes are suspended
    /// (<see cref="ResumeScheduler.SuspendAsync"/>): a pass resumes nothing and writes nothing. Once the suspension is
    /// released (the person stayed), the next pass resumes the run by the consent of its header.
    /// </summary>
    [Fact]
    public async Task While_the_app_leaves_the_resumes_are_suspended_and_once_released_the_run_resumes()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1 ? CliTurns.Hangs(launch) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));

        RunId id;
        long cut;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), Phase8Runs.Consent);
            id = run.Id;
            await Phase8Runs.UntilStatusAsync(first, run, "T1", WorkTaskStatus.Running);
            cut = await Phase8Runs.DieAsync(first, run);
        }

        await using var second = await world.OpenAsync(repo.Root);
        var scheduler = Phase8Runs.Scheduler(second);
        using (await scheduler.SuspendAsync(Ct))
        {
            Assert.Empty(await scheduler.ResumeConsentedAsync(second.Project, Ct));
            Assert.Equal(cut, (await second.LoadLogAsync()).Entries[^1].Sequence); // nothing written
            Assert.Single(world.Claude.Launches);
        }

        var outcome = Assert.Single(await scheduler.ResumeConsentedAsync(second.Project, Ct));
        Assert.Equal((id, RunResumeCause.ConsentAfterUnrequestedClose), (outcome.Run, outcome.Cause));
        await using (var resumed = outcome.Resumed ?? throw new InvalidOperationException(outcome.Reason))
        {
            await Phase8Runs.SucceededAsync(second, resumed);
        }

        var entries = await Phase8Log.OfAsync(second, id);
        Assert.Equal(cut + 1, entries.Single(e => e.Payload is RunResumedPayload).Entry.Sequence);
        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// The app dies while the run only waits for the quota of Claude (the window of the synthetic capture, three hours away).
    /// With the consent, the next instance resumes it as one that was waiting for quota: <c>RunResumed</c>, and nothing to
    /// recover; the wait goes on from the log until the reset plus the margin, and only then is the session checked and the
    /// task launched again.
    /// </summary>
    [Fact]
    public async Task With_consent_a_run_cut_while_waiting_for_quota_resumes_as_such_and_waits_for_the_reset()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Phase8Clock.ThreeHoursBeforeReset);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1 ? CliTurns.Capture(ProviderId.ClaudeCode, FiveHourCapture) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));
        var relaunchAt = Phase8Clock.FiveHourReset + RunOptions.DefaultQuotaResetMargin;
        var waiting = Phase8Log.Until(relaunchAt);

        RunId id;
        long cut;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), Phase8Runs.Consent);
            id = run.Id;
            await Phase8Runs.UntilAsync(first, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("T1").BlockReason == waiting, waiting);
            cut = await Phase8Runs.DieAsync(first, run);
        }

        clock.Advance(TimeSpan.FromHours(1)); // the app was closed for an hour
        await using var second = await world.OpenAsync(repo.Root);
        var outcome = Assert.Single(await Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct));
        Assert.Equal(RunResumeCause.ConsentAfterQuotaWait, outcome.Cause);
        await using var resumed = outcome.Resumed ?? throw new InvalidOperationException(outcome.Reason);
        await Phase8Runs.UntilAsync(second, resumed, s => s.Phase == RunPhase.WaitingForQuota && s.View("T1").BlockReason == waiting, "the wait, again");
        await Phase8Runs.TurnAsync(resumed, () => clock.AdvanceTo(relaunchAt - TimeSpan.FromSeconds(1)));
        Assert.Equal((RunPhase.WaitingForQuota, WorkTaskStatus.Blocked, 1), (resumed.Snapshot().Phase, resumed.Snapshot().View("T1").Status, resumed.Snapshot().View("T1").Attempt));
        Assert.Single(world.Claude.Launches);
        Assert.Equal(0, world.ClaudeProbe.Calls);

        clock.AdvanceTo(relaunchAt);
        await Phase8Runs.SucceededAsync(second, resumed);
        var entries = await Phase8Log.OfAsync(second, id);
        Assert.Equal(
            [
                "resumed ConsentAfterQuotaWait",
                "T1 Blocked>Pending",
                "check T1 claude-code SessionProbe Ready",
                "launch T1#2",
                "T1 Pending>Running",
                "end T1#2 Succeeded Completed",
                "T1 Running>Completed",
                "finished Succeeded",
            ],
            Phase8Log.Story(entries.Where(e => e.Entry.Sequence > cut)));
        // A wait is not an interruption: the relaunch goes on in the session the window cut, as it would have live.
        Assert.Equal("00000000-0000-4000-8000-000000000101", world.Claude.Launches[1].ArgumentAfter("--resume"));
        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// Two tasks of Claude: T1 hits the window and T2, ready, waits for the cooldown of its provider. The app dies during the
    /// wait and the next instance resumes the run by consent before the reset: T2 must still wait for the reset of its
    /// provider, as it did before the cut (WP1 review, finding 2, fixed in fa3e79d: the resume rebuilds the cooldowns of the
    /// providers from the blocks in the log).
    /// </summary>
    [Fact]
    public async Task With_consent_a_resume_before_the_reset_keeps_the_other_tasks_of_the_limited_provider_waiting()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Phase8Clock.ThreeHoursBeforeReset);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch is { Task: "T1", Attempt: 1 }
            ? CliTurns.Capture(ProviderId.ClaudeCode, FiveHourCapture)
            : CliTurns.Works(launch, ($"src/{launch.Task}/a.txt", "a\n"));
        var relaunchAt = Phase8Clock.FiveHourReset + RunOptions.DefaultQuotaResetMargin;
        var plan = Plan(Work("T1"), Work("T2", "ejecutor2"));

        RunId id;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, plan, Phase8Runs.NoSteps(), Phase8Runs.Consent with { MaxParallelTasks = 1 });
            id = run.Id;
            await Phase8Runs.UntilAsync(first, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("T1").Status == WorkTaskStatus.Blocked, "T1 waiting for the window");
            await Phase8Runs.RestingAsync(run);
            Assert.Equal((WorkTaskStatus.Pending, 0), (run.Snapshot().View("T2").Status, run.Snapshot().View("T2").Attempt)); // held by the cooldown of Claude
            await Phase8Runs.DieAsync(first, run);
        }

        await using var second = await world.OpenAsync(repo.Root);
        var outcome = Assert.Single(await Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct));
        Assert.Equal(RunResumeCause.ConsentAfterQuotaWait, outcome.Cause);
        await using var resumed = outcome.Resumed ?? throw new InvalidOperationException(outcome.Reason);
        await Phase8Runs.RestingAsync(resumed);
        await Phase8Runs.TurnAsync(resumed, () => clock.AdvanceTo(relaunchAt - TimeSpan.FromSeconds(1)));

        // The loop dispatches in its turn (the attempt counter moves then), well before the CLI is launched: T2 was never dispatched.
        Assert.Equal((WorkTaskStatus.Pending, 0), (resumed.Snapshot().View("T2").Status, resumed.Snapshot().View("T2").Attempt));
        Assert.DoesNotContain(await Phase8Log.OfAsync<AgentRunStartedPayload>(second, id), s => s.Payload.Task == new TaskId("T2"));

        clock.AdvanceTo(relaunchAt);
        await Phase8Runs.SucceededAsync(second, resumed);
        Assert.Equal(3, world.Claude.Launches.Count);
        Phase8Log.AssertEveryLaunchEndedOnce(await Phase8Log.OfAsync(second, id));
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// D-39 on the log of a real run, cut while its attempt runs, with the consent in its header: what decides is what a
    /// person did since the last resume. A choice on leaving (pause and leave, or leave now) is never undone, not by a
    /// <c>SessionEnded</c> of Windows after it nor by resuming the dispatch («No salir»); a pause that is the last change of
    /// the dispatch is never undone by the consent either. A <c>SessionEnded</c> alone, or a pause the person resumed,
    /// lets the consent resume the run.
    /// </summary>
    [Theory]
    [InlineData("PauseAndExitWhenIdle SessionEnded", null)]
    [InlineData("ExitNow SessionEnded", null)]
    [InlineData("ExitNow", null)]
    [InlineData("PauseAndExitWhenIdle pause resume", null)]
    [InlineData("pause", null)]
    [InlineData("SessionEnded", RunResumeCause.ConsentAfterSessionEnded)]
    [InlineData("pause resume", RunResumeCause.ConsentAfterUnrequestedClose)]
    public async Task The_persons_choice_wins_over_the_consent_of_the_header(string done, RunResumeCause? expected)
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt == 1 ? CliTurns.Hangs(launch) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));
        var steps = done.Split(' ');

        RunId id;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var exits = new DecisionLogRunExitRecorder(first.Get<IDecisionRecorder>());
            var run = await Phase8Runs.StartAsync(first, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), Phase8Runs.Consent);
            id = run.Id;
            await Phase8Runs.UntilStatusAsync(first, run, "T1", WorkTaskStatus.Running);
            foreach (var step in steps)
            {
                switch (step)
                {
                    case "pause":
                        await run.PauseAsync();
                        break;
                    case "resume":
                        await run.ResumeDispatchAsync();
                        break;
                    default:
                        await exits.RecordAsync(first.Project, id.Value, Enum.Parse<RunExitChoice>(step), Ct);
                        break;
                }
            }

            await Phase8Runs.DieAsync(first, run);
            Assert.Equal(
                [.. steps.Select(s => s switch { "pause" => "dispatch Paused", "resume" => "dispatch Resumed", _ => "exit " + s })],
                Phase8Log.Story(await Phase8Log.OfAsync(first, id)).Where(l => l.StartsWith("dispatch", StringComparison.Ordinal) || l.StartsWith("exit", StringComparison.Ordinal)));
        }

        await using var second = await world.OpenAsync(repo.Root);
        var before = (await second.LoadLogAsync()).Entries.Count;
        var outcome = Assert.Single(await Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct));
        Assert.Equal(expected, outcome.Cause);
        if (expected is null)
        {
            Assert.Null(outcome.Resumed);
            Assert.Contains(steps.Contains("pause") && !steps.Contains("resume") ? "La pausaste" : "La dejaste al salir", outcome.Reason, StringComparison.Ordinal);
            Assert.Equal(before, (await second.LoadLogAsync()).Entries.Count); // nothing written
            Assert.Single(world.Claude.Launches);
            Assert.Equal(RunPhase.Interrupted, Assert.Single(await second.Orchestrator.ListRunsAsync(second.Project, Ct)).Phase);

            // The attempt the app never saw end stays as the log left it, for whoever resumes the run to close it.
            var launches = Phase8Log.Story(await Phase8Log.OfAsync(second, id)).Where(l => l.StartsWith("launch", StringComparison.Ordinal) || l.StartsWith("end", StringComparison.Ordinal));
            Assert.Equal(["launch T1#1"], launches);
        }
        else
        {
            await using (var resumed = outcome.Resumed ?? throw new InvalidOperationException(outcome.Reason))
            {
                await Phase8Runs.SucceededAsync(second, resumed);
            }

            var entries = await Phase8Log.OfAsync(second, id);
            var resumes = entries.Select(e => e.Payload).OfType<RunResumedPayload>().ToList();
            Assert.Equal(expected, Assert.Single(resumes).Cause);
            Assert.Equal(2, world.Claude.Launches.Count);
            Phase8Log.AssertEveryLaunchEndedOnce(entries);
        }

        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// Two passes overlap (two schedulers over one engine, so the queue of one does not order them): the first resumes the
    /// run, the person then leaves it («Salir ahora»), and the second, which read the log before that, goes on. It must not
    /// resume a run the person left, now nor at the next start (WP1 review, finding 4, fixed in fa3e79d: the orchestrator
    /// decides again on the log it reads under the reservation of the run, so a pass that read the log before never overrules
    /// a person's choice).
    /// </summary>
    [Fact]
    public async Task Overlapping_passes_never_resume_a_run_the_person_left_in_between()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.Claude.Turn = launch => launch.Attempt <= 2 ? CliTurns.Hangs(launch) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));

        RunId id;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), Phase8Runs.Consent);
            id = run.Id;
            await Phase8Runs.UntilStatusAsync(first, run, "T1", WorkTaskStatus.Running);
            await Phase8Runs.DieAsync(first, run);
        }

        await using (var second = await world.OpenAsync(repo.Root))
        {
            // The second pass reads the log and lists the run as left unfinished, and waits there.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new HeldRunRegistry(second.Get<AgentStudio.Application.Persistence.IRunRegistry>(), release.Task);
            var late = Task.Run(() => Phase8Runs.Scheduler(second, held).ResumeConsentedAsync(second.Project, Ct), Ct);
            await held.Reached.WaitAsync(Phase8Runs.Guard, Ct);

            // The first pass resumes it, and the person leaves it at once: «Salir ahora».
            var resumed = Assert.Single(await Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct)).Resumed!;
            await Phase8Runs.UntilAsync(second, resumed, s => s.View("T1").Status == WorkTaskStatus.Running, "T1 running again");
            await new DecisionLogRunExitRecorder(second.Get<IDecisionRecorder>()).RecordAsync(second.Project, id.Value, RunExitChoice.ExitNow, Ct);
            await Phase8Runs.DieAsync(second, resumed);

            release.SetResult();
            var leftOver = await late.WaitAsync(Phase8Runs.Guard, Ct);
            foreach (var other in leftOver.Where(r => r.Resumed is not null))
            {
                await other.Resumed!.DisposeAsync();
            }

            Assert.DoesNotContain(leftOver, r => r.Resumed is not null);
            Assert.Single(await Phase8Log.OfAsync<RunResumedPayload>(second, id));
        }

        // The next start: the person left it, so it is not resumed either.
        await using var third = await world.OpenAsync(repo.Root);
        var next = Assert.Single(await Phase8Runs.Scheduler(third).ResumeConsentedAsync(third.Project, Ct));
        if (next.Resumed is { } again)
        {
            await again.DisposeAsync();
        }

        Assert.Null(next.Cause);
        Assert.Equal(2, world.Claude.Launches.Count);
        await Phase8Log.AssertVerifiedAsync(third);
    }

    /// <summary>
    /// The judge's provider limits it, the review waits, and the check before going on finds the quota of Codex still
    /// limited, so that review launched nothing and spent no launch (F8-3). The app dies during the next wait. The resumed run
    /// counts the launches the live run counted (WP1 review, finding 5, fixed in fa3e79d): with <c>MaxAgentRuns</c> 3 there is still one for the
    /// judge, who reviews, and the run ends on the person's approval of the escalation.
    /// </summary>
    [Fact]
    public async Task After_a_resume_the_budget_counts_the_launches_the_live_run_counted()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start, reviewed: true);
        var clock = world.Clock;
        world.Claude.Turn = launch => CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));
        var answer = world.Judges.Codex.Script.Fallback!;
        var judged = 0;
        world.Judges.Codex.Script.Fallback = (request, attempt) =>
            request.Agent.Role == AgentRole.Judge && Interlocked.Increment(ref judged) == 1
                ? new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(10), StructuredOutput = null, Error = "límite de uso" }
                : answer(request, attempt);
        world.Judges.Decision = _ => VerdictDecision.EscalateToHuman;
        var stillLimited = Start + TimeSpan.FromHours(1);
        world.CodexQuota.Answer = call => call == 1 ? FakeQuotaRead.StillLimited(stillLimited, clock.Now) : new QuotaReadResult(QuotaReadStatus.NotOptedIn, []);
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")));
        var options = Phase8Runs.Consent with { MaxAgentRuns = 3 };

        RunId id;
        int counted;
        await using (var first = await world.OpenAsync(repo.Root, Roster()))
        {
            var run = await Phase8Runs.StartAsync(first, clock, plan, Phase8Runs.NoSteps(), options);
            id = run.Id;
            var limited = Phase8Log.Until(Start + TimeSpan.FromMinutes(10) + RunOptions.DefaultQuotaResetMargin);
            await Phase8Runs.UntilAsync(first, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("R1").BlockReason == limited, limited);
            clock.Advance(TimeSpan.FromMinutes(13));
            var again = Phase8Log.Until(stillLimited + RunOptions.DefaultQuotaResetMargin);
            await Phase8Runs.UntilAsync(first, run, s => s.Phase == RunPhase.WaitingForQuota && s.View("R1").BlockReason == again, again);
            await Phase8Runs.RestingAsync(run);
            counted = run.Snapshot().AgentRunsUsed;
            Assert.Equal(2, counted); // T1 and the judge's first launch; the stopped review gave its launch back
            await Phase8Runs.DieAsync(first, run);
        }

        await using var second = await world.OpenAsync(repo.Root);
        var outcome = Assert.Single(await Phase8Runs.Scheduler(second).ResumeConsentedAsync(second.Project, Ct));
        await using var resumed = outcome.Resumed ?? throw new InvalidOperationException(outcome.Reason);
        await Phase8Runs.RestingAsync(resumed);
        Assert.Equal(counted, resumed.Snapshot().AgentRunsUsed);

        clock.AdvanceTo(stillLimited + RunOptions.DefaultQuotaResetMargin);
        await Phase8Runs.SucceededAsync(second, resumed);
        Assert.Equal(2, world.Judges.Codex.Script.Starts.Count);
        await Phase8Log.AssertVerifiedAsync(second);
    }

    /// <summary>
    /// A pause the engine reports as done is in the log (D-39): a pause that a run whose loop already ended takes as done,
    /// without writing <c>RunDispatchChanged</c>, would let the consent resume it after the close (WP1 review, finding 6, fixed in fa3e79d: the pause of an ended run fails).
    /// </summary>
    [Fact]
    public async Task A_pause_the_engine_reports_as_done_is_in_the_log()
    {
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Start);
        var clock = world.Clock;
        world.Claude.Turn = launch => CliTurns.Hangs(launch);
        await using var app = await world.OpenAsync(repo.Root, Roster());

        var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single(), Phase8Runs.NoSteps(), Phase8Runs.Consent);
        await Phase8Runs.UntilStatusAsync(app, run, "T1", WorkTaskStatus.Running);
        await Phase8Runs.DieAsync(app, run);

        var refused = await Record.ExceptionAsync(() => run.PauseAsync());
        var paused = await Phase8Log.OfAsync<RunDispatchChangedPayload>(app, run.Id);
        Assert.True(
            refused is InvalidOperationException || paused.Any(p => p.Payload.Change == RunDispatchChange.Paused),
            "The pause of a run whose loop had ended was reported as done, and nothing of it is in the log.");
        await Phase8Log.AssertVerifiedAsync(app);
    }
}
