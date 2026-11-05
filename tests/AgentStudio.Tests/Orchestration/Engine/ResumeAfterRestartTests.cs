using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// F8-4 (docs/16 D-39, ADR-0034): what decides whether a run resumes by itself after a restart of the app is in its log. A
/// person's pause of the dispatch is recorded (<c>RunDispatchChanged</c>), and so is every resume (<c>RunResumed</c>), before
/// anything is launched. With the consent of its header, a run left waiting for quota or cut by a close nobody asked for
/// resumes by itself; one a person paused or left on exit never does; and no resume goes beyond the approved budget.
/// </summary>
public sealed class ResumeAfterRestartTests
{
    private static readonly RunOptions Consent = new(AutoResumeConsent: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_with_consent_cut_by_a_close_nobody_asked_for_resumes_by_itself_and_says_why_before_anything_else()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal((first.Id, RunResumeCause.ConsentAfterUnrequestedClose), (outcome.Run, outcome.Cause));
        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var (entry, payload) = Assert.Single(harness.Payloads<RunResumedPayload>());
        var started = Assert.Single(harness.Payloads<RunStartedPayload>()).Payload;
        Assert.Equal(
            (ActorKind.System, "agent-studio", RunResumeCause.ConsentAfterUnrequestedClose, started.HeaderHash, first.Id.Value),
            (entry.Actor.Kind, payload.DecidedBy, payload.Cause, payload.ConsentHeaderHash, payload.RunId));
        var interrupted = harness.Payloads<AgentRunFinishedPayload>().Single(p => p.Payload.Status == AttemptOutcome.Interrupted).Entry;
        var relaunch = harness.Entries.Where(e => e.Kind == DecisionKind.AgentRunStarted).ElementAt(1);
        Assert.True(entry.Sequence < interrupted.Sequence && entry.Sequence < relaunch.Sequence, "The resume is recorded before the recovery and before any launch.");
        Assert.Contains(harness.Payloads<TaskStatusChangedPayload>(), p => p.Payload.Reason == "Reanudada sola por el consentimiento de la cabecera de su ejecución.");
        AssertEveryLaunchEnded(harness);
    }

    /// <summary>
    /// A resume by consent launches agents on the log it has just read: a critical moment (ADR-0010, addendum of 2026-11-04),
    /// so it reads the whole log. The pass lists the runs from the tail, and the run going on again loads it incrementally.
    /// </summary>
    [Fact]
    public async Task A_resume_by_consent_reads_the_whole_log_before_it_decides_and_the_run_going_on_again_reads_its_tail()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var spy = new LoadSpyRecorder(harness.Decisions);
        var engine = new Orchestrator(
            spy, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers, harness.Worktrees,
            harness.Verification, harness.Integrator, harness.Review, harness.Approvals, harness.Skills, harness.Paths, harness.Time, harness.WriteConfinement);

        var outcome = Assert.Single(await new ResumeScheduler(engine, harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal([LogLoad.Incremental, LogLoad.Full], spy.Loads.Take(2));
        Assert.Single(spy.Loads, l => l == LogLoad.Full);
    }

    /// <summary>
    /// The composition attaches every run the scheduler resumes to its monitor, as it follows the runs it starts (WP3): the
    /// scheduler tells it, with the project and the run, as soon as the run goes.
    /// </summary>
    [Fact]
    public async Task Every_run_resumed_by_itself_is_announced_with_its_project_so_the_app_can_follow_it()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs);
        var announced = new List<ScheduledResumeEventArgs>();
        scheduler.Resumed += (_, e) => announced.Add(e);

        var outcome = Assert.Single(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;
        await resumed.CompleteAsync(harness);

        var told = Assert.Single(announced);
        Assert.Equal((harness.Project.RootPath, first.Id, RunResumeCause.ConsentAfterUnrequestedClose), (told.Project.RootPath, told.Run.Id, told.Cause));
        Assert.Same(resumed, told.Run);
    }

    [Fact]
    public async Task A_run_waiting_for_quota_when_the_app_closed_resumes_by_itself_and_records_it_though_no_task_was_interrupted()
    {
        using var harness = new EngineHarness();
        var resetsAt = Start + TimeSpan.FromMinutes(30);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { RateLimitResetsAt = resetsAt });
        var first = await harness.StartAsync(Plan(Work("T1")), Consent);
        await harness.WaitForPhaseAsync(first, RunPhase.WaitingForQuota);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;

        Assert.Equal(RunResumeCause.ConsentAfterQuotaWait, outcome.Cause);
        Assert.Equal(RunResumeCause.ConsentAfterQuotaWait, Assert.Single(harness.Payloads<RunResumedPayload>()).Payload.Cause);
        await harness.WaitForPhaseAsync(resumed, RunPhase.WaitingForQuota);
        Assert.Single(harness.Claude.Starts);

        harness.Time.Advance(TimeSpan.FromMinutes(30) + RunOptions.DefaultQuotaResetMargin);
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Claude.Starts.Count);
        AssertEveryLaunchEnded(harness);
    }

    /// <summary>
    /// What the live run knew of a limit is rebuilt from the log (F8-2): after a resume, the other tasks of a provider still
    /// limited wait for its reset as they did before the close, and are not launched at once.
    /// </summary>
    [Fact]
    public async Task After_a_resume_the_other_tasks_of_a_provider_still_limited_wait_for_its_reset()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "A1", 1, new AgentScript { RateLimitResetsAt = Start + TimeSpan.FromMinutes(30) });
        var first = await harness.StartAsync(Plan(Work("A1"), Work("B1", agent: "ejecutor2")), Consent with { MaxParallelTasks = 1 });
        await harness.WaitForPhaseAsync(first, RunPhase.WaitingForQuota);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;
        await harness.WaitForPhaseAsync(resumed, RunPhase.WaitingForQuota);
        await Task.Delay(200, Ct);

        Assert.Equal(["A1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.Equal(WorkTaskStatus.Pending, resumed.Snapshot().View("B1").Status);

        harness.Time.Advance(TimeSpan.FromMinutes(30) + RunOptions.DefaultQuotaResetMargin);
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(["A1", "A1", "B1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
    }

    [Fact]
    public async Task A_run_without_the_consent_is_left_for_a_person_and_nothing_is_written()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), new RunOptions());
        var written = harness.Entries.Count;

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));

        Assert.Equal((first.Id, (RunResumeCause?)null, (IOrchestrationRun?)null), (outcome.Run, outcome.Cause, outcome.Resumed));
        Assert.Contains("consentimiento", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(written, harness.Entries.Count);
        Assert.Single(harness.Claude.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_a_person_paused_never_resumes_by_itself_unless_they_resumed_its_dispatch(bool resumedAgain)
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(Plan(Work("T1")), Consent);
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.PauseAsync();
        if (resumedAgain)
        {
            await first.ResumeDispatchAsync();
        }

        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));
        if (outcome.Resumed is { } resumed)
        {
            await resumed.CancelAsync("fin");
            await resumed.CompleteAsync(harness);
            await resumed.DisposeAsync();
        }

        Assert.Equal(resumedAgain, outcome.Resumed is not null);
        Assert.Equal(resumedAgain, harness.Payloads<RunResumedPayload>().Count == 1);
    }

    [Theory]
    [InlineData(RunExitChoice.PauseAndExitWhenIdle, false)]
    [InlineData(RunExitChoice.ExitNow, false)]
    [InlineData(RunExitChoice.SessionEnded, true)]
    public async Task Only_an_exit_nobody_chose_lets_a_run_resume_by_itself(RunExitChoice choice, bool resumes)
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var actor = choice == RunExitChoice.SessionEnded ? new Actor(ActorKind.System, "agent-studio") : new Actor(ActorKind.User, "user");
        await harness.Decisions.RecordAsync(harness.Project.ProjectId, actor, DecisionKind.RunExitChosen, $"Salida: {choice}", new RunExitChosenPayload(choice, actor.Id) { RunId = first.Id.Value }, null, Ct);

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));
        if (outcome.Resumed is { } resumed)
        {
            await resumed.CancelAsync("fin");
            await resumed.CompleteAsync(harness);
            await resumed.DisposeAsync();
        }

        Assert.Equal(resumes, outcome.Resumed is not null);
        Assert.Equal(resumes ? RunResumeCause.ConsentAfterSessionEnded : null, outcome.Cause);
    }

    /// <summary>
    /// The last choice of a person decides, not the last exit: a person chose to pause and leave when the attempts ended, and
    /// then Windows ended the session. The run never resumes by itself: Windows shutting down after a person chose does not
    /// turn into a resume.
    /// </summary>
    [Fact]
    public async Task A_persons_choice_on_exit_stays_in_force_over_a_later_end_of_the_session()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var user = new Actor(ActorKind.User, "user");
        var windows = new Actor(ActorKind.System, "windows");
        await harness.Decisions.RecordAsync(harness.Project.ProjectId, user, DecisionKind.RunExitChosen, "Pausar y salir", new RunExitChosenPayload(RunExitChoice.PauseAndExitWhenIdle, "user") { RunId = first.Id.Value }, null, Ct);
        await harness.Decisions.RecordAsync(harness.Project.ProjectId, windows, DecisionKind.RunExitChosen, "Windows cerró la sesión", new RunExitChosenPayload(RunExitChoice.SessionEnded, "windows") { RunId = first.Id.Value }, null, Ct);
        var written = harness.Entries.Count;

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));

        Assert.Equal(((IOrchestrationRun?)null, (RunResumeCause?)null), (outcome.Resumed, outcome.Cause));
        Assert.Contains("La dejaste al salir", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(written, harness.Entries.Count);
    }

    [Fact]
    public async Task A_resume_by_consent_never_goes_beyond_the_approved_budget()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent with { MaxAgentRuns = 1 });

        var outcome = Assert.Single(await new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Single(harness.Claude.Starts); // the budget was the interrupted launch
        Assert.Single(harness.Alerts("run.budget-exhausted"));
    }

    [Fact]
    public async Task A_resume_a_person_asks_for_is_recorded_as_theirs_and_names_no_header()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct);
        await resumed.CompleteAsync(harness);

        var (entry, payload) = Assert.Single(harness.Payloads<RunResumedPayload>());
        Assert.Equal((ActorKind.User, "user", RunResumeCause.UserRequested, (string?)null), (entry.Actor.Kind, payload.DecidedBy, payload.Cause, payload.ConsentHeaderHash));
        Assert.Contains(harness.Payloads<TaskStatusChangedPayload>(), p => p.Payload.Reason == "Reanudada a petición del usuario.");
    }

    // ---- the passes of the scheduler ----

    /// <summary>
    /// A run whose resume fails after writing to its log (here, the alert that its header changed after it started) is not
    /// tried again in the same session of the app: one alert, not one for each pass of the tray.
    /// </summary>
    [Fact]
    public async Task A_run_that_cannot_be_resumed_is_tried_once_in_a_session_of_the_app()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        harness.Runs.Replace(first.Id, r => r with { BaseCommit = new string('0', 40) });
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs);

        var once = Assert.Single(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        var twice = Assert.Single(await scheduler.ResumeConsentedAsync(harness.Project, Ct));

        Assert.Equal(((IOrchestrationRun?)null, (IOrchestrationRun?)null), (once.Resumed, twice.Resumed));
        Assert.Contains("No se pudo reanudar", once.Reason, StringComparison.Ordinal);
        Assert.Contains("esta sesión", twice.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Alerts("run.not-resumable"));
    }

    /// <summary>
    /// A run whose recovery fails after its <c>RunResumed</c> (the log refuses the end of its interrupted attempt) is not
    /// resumed again in the same session of the app, so it gets no second <c>RunResumed</c>; and its failure, whatever it is,
    /// is its alone: the other run of the project is resumed in the same pass.
    /// </summary>
    [Fact]
    public async Task A_run_whose_recovery_fails_is_not_resumed_again_and_the_others_of_the_pass_go_on()
    {
        using var harness = new EngineHarness();
        harness.Claude.Fallback = (_, _) => new AgentScript { Hang = true };
        var broken = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var sound = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        harness.LogStore.FailWhen = e => e.Kind == DecisionKind.AgentRunFinished && e.PayloadJson.Contains(broken.Id.Value, StringComparison.Ordinal);
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs);

        var first = await scheduler.ResumeConsentedAsync(harness.Project, Ct);
        var second = await scheduler.ResumeConsentedAsync(harness.Project, Ct);
        harness.LogStore.FailWhen = null;

        Assert.Null(Assert.Single(first, r => r.Run == broken.Id).Resumed);
        await using var resumed = Assert.Single(first, r => r.Run == sound.Id).Resumed!;
        Assert.Contains("esta sesión", Assert.Single(second, r => r.Run == broken.Id).Reason, StringComparison.Ordinal);
        Assert.Single(harness.Payloads<RunResumedPayload>(), p => p.Payload.RunId == broken.Id.Value);
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    /// <summary>
    /// A run that stopped in this session of the app on a system error (the log could not be written) was not cut by a close
    /// nobody asked for: the scheduler of that same session leaves it for a person.
    /// </summary>
    [Fact]
    public async Task A_run_stopped_by_a_system_error_in_this_session_of_the_app_is_left_for_a_person()
    {
        using var harness = new EngineHarness();
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });
        var orchestrator = harness.CreateOrchestrator();
        await using var run = await orchestrator.StartAsync(harness.Request(Plan(Work("T1")), Consent), Ct);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        var failures = 0;
        harness.LogStore.FailWhen = e => e.Kind == DecisionKind.TaskStatusChanged && e.Summary.StartsWith("T1: Running → ", StringComparison.Ordinal) && Interlocked.Increment(ref failures) == 1;
        gate.SetResult();
        Assert.Equal(RunPhase.Interrupted, (await run.Completion.WaitAsync(RunTestSupport.Guard, Ct)).Phase);
        harness.LogStore.FailWhen = null;

        var outcome = Assert.Single(await new ResumeScheduler(orchestrator, harness.Decisions, harness.Runs).ResumeConsentedAsync(harness.Project, Ct));

        Assert.Equal(((IOrchestrationRun?)null, (RunResumeCause?)null), (outcome.Resumed, outcome.Cause));
        Assert.Contains("error del sistema", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Payloads<RunResumedPayload>());
    }

    /// <summary>
    /// The resume by consent decides again on the log it has just read, under the reservation of the run: a person who chose
    /// to leave after the pass read the log is not overruled by what the pass read before.
    /// </summary>
    [Fact]
    public async Task A_resume_by_consent_decides_again_on_the_log_it_has_just_read()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var runs = new GatedRuns(harness.Runs);
        var pass = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, runs).ResumeConsentedAsync(harness.Project, Ct);
        await runs.Entered.Task.WaitAsync(RunTestSupport.Guard, Ct); // the pass read the log, and looks the run up
        var user = new Actor(ActorKind.User, "user");
        await harness.Decisions.RecordAsync(harness.Project.ProjectId, user, DecisionKind.RunExitChosen, "Salir ahora", new RunExitChosenPayload(RunExitChoice.ExitNow, "user") { RunId = first.Id.Value }, null, Ct);
        runs.Release.SetResult();

        var outcome = Assert.Single(await pass);

        Assert.Equal(((IOrchestrationRun?)null, (RunResumeCause?)null), (outcome.Resumed, outcome.Cause));
        Assert.Contains("La dejaste al salir", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Payloads<RunResumedPayload>());
    }

    /// <summary>
    /// The managed exit and the end of the Windows session suspend the resumes first: the pass in progress ends without
    /// resuming anything more, no pass resumes anything while the suspension holds, and they go on once it is released.
    /// </summary>
    [Fact]
    public async Task While_the_resumes_are_suspended_nothing_is_resumed_and_a_pass_in_progress_ends_first()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var runs = new GatedRuns(harness.Runs);
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, runs);
        var inProgress = scheduler.ResumeConsentedAsync(harness.Project, Ct);
        await runs.Entered.Task.WaitAsync(RunTestSupport.Guard, Ct);

        var suspending = scheduler.SuspendAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.False(suspending.IsCompleted, "The suspension waits for the pass in progress.");
        runs.Release.SetResult();
        var suspension = await suspending;

        var stopped = Assert.Single(await inProgress);
        Assert.Null(stopped.Resumed);
        Assert.Contains("suspendida", stopped.Reason, StringComparison.Ordinal);
        Assert.Empty(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        Assert.Empty(harness.Payloads<RunResumedPayload>());

        suspension.Dispose();
        var outcome = Assert.Single(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;
        Assert.Equal(RunResumeCause.ConsentAfterUnrequestedClose, outcome.Cause);
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    /// <summary>
    /// The end of the Windows session waits a short time for the pass in progress: a wait that gives up must not release the
    /// suspension. Nothing is resumed afterwards, by the pass in progress or by a later one.
    /// </summary>
    [Fact]
    public async Task A_suspension_holds_even_when_the_wait_for_the_pass_in_progress_is_cancelled()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var runs = new GatedRuns(harness.Runs);
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, runs);
        var inProgress = scheduler.ResumeConsentedAsync(harness.Project, Ct);
        await runs.Entered.Task.WaitAsync(RunTestSupport.Guard, Ct);

        using var timeBox = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.SuspendAsync(timeBox.Token));
        runs.Release.SetResult();

        var stopped = Assert.Single(await inProgress);
        Assert.Null(stopped.Resumed);
        Assert.Empty(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        Assert.Empty(harness.Payloads<RunResumedPayload>());
    }

    /// <summary>
    /// The suspension is taken at once and held until it is disposed: a wait for the pass in progress that gives up releases
    /// nothing, and the resumes go on only when the suspension is released.
    /// </summary>
    [Fact]
    public async Task A_suspension_taken_at_once_holds_while_its_wait_is_cancelled_and_until_it_is_released()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var runs = new GatedRuns(harness.Runs);
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, runs);
        var inProgress = scheduler.ResumeConsentedAsync(harness.Project, Ct);
        await runs.Entered.Task.WaitAsync(RunTestSupport.Guard, Ct);

        var suspension = scheduler.Suspend();
        using (var timeBox = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.WaitForIdleAsync(timeBox.Token));
        }

        runs.Release.SetResult();
        await scheduler.WaitForIdleAsync(Ct);

        Assert.Null(Assert.Single(await inProgress).Resumed);
        Assert.Empty(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        Assert.Empty(harness.Payloads<RunResumedPayload>());

        suspension.Dispose();
        var outcome = Assert.Single(await scheduler.ResumeConsentedAsync(harness.Project, Ct));
        await using var resumed = outcome.Resumed!;
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    /// <summary>
    /// What the desktop relies on: a run resumed by a pass has been announced through <c>Resumed</c>, its handlers returned,
    /// when a wait for the passes asked for before it completes.
    /// </summary>
    [Fact]
    public async Task A_run_resumed_by_a_pass_is_announced_before_the_wait_for_the_passes_completes()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var runs = new GatedRuns(harness.Runs);
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, runs);
        var announced = 0;
        scheduler.Resumed += (_, _) => Interlocked.Increment(ref announced);
        var pass = scheduler.ResumeConsentedAsync(harness.Project, Ct);
        await runs.Entered.Task.WaitAsync(RunTestSupport.Guard, Ct);

        var idle = scheduler.WaitForIdleAsync(Ct);
        runs.Release.SetResult();
        await idle;

        Assert.Equal(1, Volatile.Read(ref announced));
        await using var resumed = Assert.Single(await pass).Resumed!;
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    /// <summary>Passes asked for at once go one after the other: the run is resumed once, and the later pass sees it going.</summary>
    [Fact]
    public async Task Passes_asked_for_at_once_go_one_after_the_other_and_resume_a_run_once()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        await CutWhileRunningAsync(harness, Plan(Work("T1")), Consent);
        var scheduler = new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs);

        var passes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => scheduler.ResumeConsentedAsync(harness.Project, Ct)));

        var outcome = Assert.Single(passes.SelectMany(p => p));
        await using var resumed = outcome.Resumed!;
        Assert.Single(harness.Payloads<RunResumedPayload>());
        await resumed.CancelAsync("fin");
        await resumed.CompleteAsync(harness);
    }

    /// <summary>The run registry of the harness, whose first look-up of a run waits until <see cref="Release"/>.</summary>
    private sealed class GatedRuns(IRunRegistry inner) : IRunRegistry
    {
        private int _finds;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CreateAsync(RunRecord run, CancellationToken cancellationToken) => inner.CreateAsync(run, cancellationToken);

        public async Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _finds) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return await inner.FindAsync(projectId, id, cancellationToken);
        }

        public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => inner.ListAsync(projectId, cancellationToken);
    }

    /// <summary>A run started with <paramref name="options"/> whose T1 runs when the app closes without anyone asking: nothing is recorded.</summary>
    private static async Task<IOrchestrationRun> CutWhileRunningAsync(EngineHarness harness, PlanDefinition plan, RunOptions options)
    {
        var run = await harness.StartAsync(plan, options);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.DisposeAsync();
        await run.CompleteAsync(harness);
        return run;
    }

    /// <summary>No attempt was launched twice without an end: every <c>AgentRunStarted</c> of a task has its <c>AgentRunFinished</c>.</summary>
    private static void AssertEveryLaunchEnded(EngineHarness harness)
    {
        var started = harness.Payloads<AgentRunStartedPayload>().Where(p => p.Payload.Task is not null).Select(p => $"{p.Payload.Task}/{p.Payload.Attempt}").ToList();
        var finished = harness.Payloads<AgentRunFinishedPayload>().Where(p => p.Payload.Task is not null).Select(p => $"{p.Payload.Task}/{p.Payload.Attempt}").ToList();
        Assert.Equal(started.Count, started.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(started.Order(StringComparer.Ordinal), finished.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A pause the log does not show would let the run resume by itself after a close (D-39): one sent to a run that has ended
    /// fails, and nothing is recorded.
    /// </summary>
    [Fact]
    public async Task A_pause_sent_to_a_run_that_has_ended_fails_and_records_nothing()
    {
        using var harness = new EngineHarness();
        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await run.CompleteAsync(harness);

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.PauseAsync());

        Assert.Empty(harness.Payloads<RunDispatchChangedPayload>());
    }

    /// <summary>
    /// The same when the pause is still in the queue of the loop as the run stops on a system error (the log cannot be
    /// written): it was not applied, so it fails.
    /// </summary>
    [Fact]
    public async Task A_pause_still_queued_when_the_run_stops_on_a_system_error_fails()
    {
        using var harness = new EngineHarness();
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });
        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.LogStore.FailWhen = entry =>
        {
            if (entry.Kind != DecisionKind.TaskStatusChanged || !entry.Summary.StartsWith("T1: Running → ", StringComparison.Ordinal))
            {
                return false;
            }

            writing.Set();
            release.Wait(RunTestSupport.Guard, Ct);
            return true; // the end of T1 cannot be written: the loop stops on a system error
        };

        gate.SetResult();
        Assert.True(writing.Wait(RunTestSupport.Guard, Ct), "The loop writes the end of T1.");
        var pause = run.PauseAsync(); // in the queue, behind the end of T1
        release.Set();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pause);
        Assert.Equal(RunPhase.Interrupted, (await run.Completion.WaitAsync(RunTestSupport.Guard, Ct)).Phase);
        Assert.Empty(harness.Payloads<RunDispatchChangedPayload>());
    }

    [Fact]
    public async Task Pausing_and_resuming_the_dispatch_is_recorded_as_the_users_once_each()
    {
        using var harness = new EngineHarness();
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });

        await using var run = await harness.StartAsync(Plan(Work("T1"), Work("T2").After(On("T1"))));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.PauseAsync();
        await run.PauseAsync(); // already paused: nothing changes, nothing more is recorded
        var paused = harness.Payloads<RunDispatchChangedPayload>();
        await run.ResumeDispatchAsync();
        await run.ResumeDispatchAsync();
        gate.SetResult();
        await run.CompleteAsync(harness);

        var (entry, payload) = Assert.Single(paused);
        Assert.Equal((RunDispatchChange.Paused, false), (payload.Change, payload.AllowsAutomaticResume()));
        var changes = harness.Payloads<RunDispatchChangedPayload>();
        Assert.Equal([RunDispatchChange.Paused, RunDispatchChange.Resumed], changes.Select(c => c.Payload.Change));
        Assert.All(changes, c => Assert.Equal((ActorKind.User, "user", "user", run.Id.Value), (c.Entry.Actor.Kind, c.Entry.Actor.Id, c.Payload.DecidedBy, c.Payload.RunId)));
        Assert.Equal(entry.RecordedAt, payload.At);
        Assert.True(changes[^1].Payload.AllowsAutomaticResume());
    }
}
