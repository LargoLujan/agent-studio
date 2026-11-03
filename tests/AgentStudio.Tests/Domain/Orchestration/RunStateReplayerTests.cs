using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

public sealed class RunStateReplayerTests
{
    private sealed record UnknownEvent(DateTimeOffset At) : RunEvent(At);

    /// <summary>
    /// The valid plan, run to the end: T1 and T2 in parallel, integration, a condition, a human approval on the "yes"
    /// branch (or a write task on the "no" branch), the review, and a tool run that fails once and is retried.
    /// </summary>
    private static LiveRun FullRun(string branch = ConditionBranches.Yes)
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Start("T2");
        run.Complete("T1");
        run.Complete("T2");
        run.Start("T3");
        run.Complete("T3");
        run.Start("T4");
        run.Decide("T4", branch);
        run.Propagate();
        if (branch == ConditionBranches.Yes)
        {
            run.AwaitApproval("T5");
            run.Complete("T5");
        }
        else
        {
            run.Start("T6");
            run.Complete("T6");
        }

        run.Start("T7");
        run.Complete("T7");
        run.Start("T8");
        run.Fail("T8");
        run.Retry("T8");
        run.Start("T8");
        run.Complete("T8");
        return run;
    }

    private static int IndexOf(IReadOnlyList<RunEvent> events, string task, WorkTaskStatus to, WorkTaskStatus? from = null) =>
        events.Select((e, i) => (e, i)).First(x => x.e is TaskTransitioned t && t.Task == Tid(task) && t.To == to && (from is null || t.From == from)).i;

    private static void AssertReplayEqualsLive(LiveRun run)
    {
        var replay = run.Replay();

        Assert.Empty(replay.Problems);
        Assert.True(replay.IsResumable);
        Assert.Equal(LiveRun.Signature(run.Graph), LiveRun.Signature(replay.Graph));
    }

    // ---- round trip ----

    [Fact]
    public void A_log_with_no_events_gives_the_plan_as_it_starts()
    {
        var replay = RunStateReplayer.Replay(Plan(), T0, []);

        Assert.Empty(replay.Problems);
        Assert.Equal(LiveRun.Signature(Plan().BuildGraph(T0)), LiveRun.Signature(replay.Graph));
        Assert.All(replay.Graph.Tasks, t => Assert.Equal(WorkTaskStatus.Pending, t.Status));
    }

    [Theory]
    [InlineData(ConditionBranches.Yes)]
    [InlineData(ConditionBranches.No)]
    public void A_whole_run_replays_to_the_same_state_it_had_live(string branch)
    {
        var run = FullRun(branch);

        AssertReplayEqualsLive(run);
    }

    [Fact]
    public void The_replay_keeps_what_the_run_decided_skipped_retried_and_proved()
    {
        var replay = FullRun().Replay();
        var graph = replay.Graph;

        Assert.Equal("yes", graph.TakenBranch(Tid("T4")));
        Assert.Equal(WorkTaskStatus.Skipped, graph[Tid("T6")].Status);
        Assert.Equal(WorkTaskStatus.Completed, graph[Tid("T5")].Status);
        Assert.Equal(1, graph[Tid("T8")].RetryCount);
        Assert.Equal([Eid("ev-T8")], graph[Tid("T8")].CompletionEvidence);
        Assert.All(graph.Tasks.Where(t => t.Id.Value != "T6"), t => Assert.Equal(WorkTaskStatus.Completed, t.Status));
    }

    [Fact]
    public void A_run_that_stopped_half_way_replays_with_its_running_tasks_still_running()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Start("T2");
        run.Complete("T1");

        var replay = run.Replay();

        Assert.Empty(replay.Problems);
        Assert.Equal(WorkTaskStatus.Running, replay.Graph[Tid("T2")].Status);
        Assert.Equal(LiveRun.Signature(run.Graph), LiveRun.Signature(replay.Graph));
    }

    [Fact]
    public void Blocking_unblocking_cancelling_and_failing_for_good_replay_exactly()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Block("T1");
        run.Unblock("T1");
        run.Start("T1");
        run.Fail("T1");
        run.Retry("T1");
        run.Start("T1");
        run.Fail("T1"); // retries exhausted
        run.Cancel("T2");

        AssertReplayEqualsLive(run);
        Assert.True(run.Replay().Graph[Tid("T1")].RetriesExhausted);
    }

    [Fact]
    public void A_pending_task_can_be_blocked_before_it_ever_runs()
    {
        var run = new LiveRun(Plan(), T0);
        run.Block("T1"); // the provider was not available when it was dispatched
        run.Unblock("T1");

        AssertReplayEqualsLive(run);
    }

    [Fact]
    public void The_returned_graph_can_continue_the_run()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Complete("T1");
        run.Start("T2");

        var graph = run.Replay().Graph;
        graph[Tid("T2")].Complete([Eid("ev-T2")], T0.AddHours(1), "done");

        Assert.Equal(["T3"], graph.ReadyTasks().Select(t => t.Id.Value));
    }

    [Fact]
    public void Replaying_twice_gives_the_same_state_and_leaves_the_events_untouched()
    {
        var run = FullRun();
        var before = run.Events.ToList();

        var first = LiveRun.Signature(run.Replay().Graph);
        var second = LiveRun.Signature(run.Replay().Graph);

        Assert.Equal(first, second);
        Assert.Equal(before, run.Events);
    }

    [Fact]
    public void A_skip_batch_recorded_one_event_per_task_replays_with_the_first_event()
    {
        var plan = PlanDefinition.Create(
            "batch",
            [
                Spec("C", WorkTaskKind.Condition),
                Spec("Y") with { DependsOn = [Dep("C", "yes")] },
                Spec("N") with { DependsOn = [Dep("C", "no")] },
                Spec("M") with { DependsOn = [Dep("N")] },
                Spec("K") with { DependsOn = [Dep("M")] },
            ],
            [],
            PlanSource.User,
            null);
        var run = new LiveRun(plan, T0);
        run.Start("C");
        run.Decide("C", "yes");

        var skipped = run.Propagate();

        Assert.Equal(["N", "M", "K"], skipped.Select(t => t.Value));
        Assert.Equal(3, run.Events.OfType<TaskTransitioned>().Count(e => e.To == WorkTaskStatus.Skipped));
        AssertReplayEqualsLive(run);
    }

    [Fact]
    public void A_condition_interrupted_after_it_was_decided_replays_with_the_branch_chosen_when_it_finally_completed()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Start("T2");
        run.Complete("T1");
        run.Complete("T2");
        run.Start("T3");
        run.Complete("T3");
        run.Start("T4");
        run.Events.Add(new ConditionResolved(T0.AddMinutes(30), Tid("T4"), "yes")); // decided, then the app died
        var graph = run.Graph;
        graph[Tid("T4")].TransitionTo(WorkTaskStatus.Blocked, T0.AddMinutes(31), "interrupted");
        run.Events.Add(new TaskTransitioned(T0.AddMinutes(31), Tid("T4"), WorkTaskStatus.Running, WorkTaskStatus.Blocked, "interrupted", []));
        graph[Tid("T4")].TransitionTo(WorkTaskStatus.Pending, T0.AddMinutes(32), "resumed");
        run.Events.Add(new TaskTransitioned(T0.AddMinutes(32), Tid("T4"), WorkTaskStatus.Blocked, WorkTaskStatus.Pending, "resumed", []));
        graph[Tid("T4")].TransitionTo(WorkTaskStatus.Running, T0.AddMinutes(33), "start");
        run.Events.Add(new TaskTransitioned(T0.AddMinutes(33), Tid("T4"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "start", []));
        run.Events.Add(new ConditionResolved(T0.AddMinutes(34), Tid("T4"), "no"));
        graph.CompleteCondition(Tid("T4"), "no", T0.AddMinutes(34), "chose no");
        run.Events.Add(new TaskTransitioned(T0.AddMinutes(34), Tid("T4"), WorkTaskStatus.Running, WorkTaskStatus.Completed, "chose no", []));

        var replay = run.Replay();

        Assert.Empty(replay.Problems);
        Assert.Equal("no", replay.Graph.TakenBranch(Tid("T4")));
        Assert.Equal(LiveRun.Signature(graph), LiveRun.Signature(replay.Graph));
    }

    [Fact]
    public void A_branch_announced_before_an_interruption_is_not_carried_over_to_a_later_completion()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Start("T2");
        run.Complete("T1");
        run.Complete("T2");
        run.Start("T3");
        run.Complete("T3");
        run.Start("T4");
        var events = run.Events.ToList();
        events.Add(new ConditionResolved(T0.AddMinutes(30), Tid("T4"), "yes"));
        events.Add(new TaskTransitioned(T0.AddMinutes(31), Tid("T4"), WorkTaskStatus.Running, WorkTaskStatus.Blocked, "interrupted", []));
        events.Add(new TaskTransitioned(T0.AddMinutes(32), Tid("T4"), WorkTaskStatus.Blocked, WorkTaskStatus.Pending, "resumed", []));
        events.Add(new TaskTransitioned(T0.AddMinutes(33), Tid("T4"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "start", []));
        events.Add(new TaskTransitioned(T0.AddMinutes(34), Tid("T4"), WorkTaskStatus.Running, WorkTaskStatus.Completed, "done", []));

        var replay = run.Replay(events);

        Assert.Contains(replay.Problems, p => p.Contains("T4", StringComparison.Ordinal) && p.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    // ---- corruption ----

    [Fact]
    public void A_missing_event_is_detected_by_the_events_that_follow_it()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        events.RemoveAt(IndexOf(events, "T1", WorkTaskStatus.Running, WorkTaskStatus.Pending));

        var replay = run.Replay(events);

        var problem = Assert.Single(replay.Problems);
        Assert.Contains("T1", problem, StringComparison.Ordinal);
        Assert.False(replay.IsResumable);
    }

    [Fact]
    public void A_duplicated_event_is_detected()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        var start = IndexOf(events, "T1", WorkTaskStatus.Running);
        events.Insert(start + 1, events[start]);

        Assert.NotEmpty(run.Replay(events).Problems);
    }

    [Fact]
    public void A_task_that_started_before_its_prerequisites_were_done_is_detected()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        var integrationStart = IndexOf(events, "T3", WorkTaskStatus.Running);
        var firstCompletion = IndexOf(events, "T1", WorkTaskStatus.Completed);
        (events[integrationStart], events[firstCompletion]) = (events[firstCompletion], events[integrationStart]);

        var replay = run.Replay(events);

        Assert.NotEmpty(replay.Problems);
        Assert.Contains(replay.Problems, p => p.Contains("T3", StringComparison.Ordinal));
    }

    [Fact]
    public void A_task_that_started_on_a_branch_that_was_not_taken_is_detected()
    {
        var run = FullRun(); // "yes": T6 was skipped, so a start of T6 is forged
        var events = run.Events.ToList();
        events.Add(new TaskTransitioned(T0.AddHours(2), Tid("T6"), WorkTaskStatus.Skipped, WorkTaskStatus.Running, "forged", []));

        Assert.NotEmpty(run.Replay(events).Problems);
    }

    [Fact]
    public void A_transition_that_starts_from_the_wrong_status_is_detected()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        var index = IndexOf(events, "T2", WorkTaskStatus.Completed);
        var original = (TaskTransitioned)events[index];
        events[index] = original with { From = WorkTaskStatus.Pending };

        var problem = Assert.Single(run.Replay(events).Problems);
        Assert.Contains("T2", problem, StringComparison.Ordinal);
        Assert.Contains("Running", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_about_a_task_that_is_not_in_the_plan_is_detected()
    {
        var run = new LiveRun(Plan(), T0);
        var events = new List<RunEvent> { new TaskTransitioned(T0, Tid("ghost"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "go", []) };

        var problem = Assert.Single(run.Replay(events).Problems);

        Assert.Contains("ghost", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transition_the_state_machine_forbids_is_detected()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        events.Add(new TaskTransitioned(T0.AddHours(3), Tid("T1"), WorkTaskStatus.Completed, WorkTaskStatus.Running, "zombie", []));

        var problem = Assert.Single(run.Replay(events).Problems);

        Assert.Contains("T1", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Completing_work_without_evidence_is_detected()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        var index = IndexOf(events, "T1", WorkTaskStatus.Completed);
        events[index] = ((TaskTransitioned)events[index]) with { Evidence = [] };

        var problem = Assert.Single(run.Replay(events).Problems);

        Assert.Contains("evidence", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_retry_beyond_the_limit_is_detected()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Fail("T1");
        run.Retry("T1");
        run.Start("T1");
        run.Fail("T1"); // T1 allows one retry and has used it
        var events = run.Events.ToList();
        events.Add(new TaskTransitioned(T0.AddMinutes(10), Tid("T1"), WorkTaskStatus.Failed, WorkTaskStatus.Pending, "forged retry", []));

        var problem = Assert.Single(run.Replay(events).Problems);

        Assert.Contains("T1", problem, StringComparison.Ordinal);
        Assert.Contains("retries", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_skip_that_the_state_of_the_graph_does_not_justify_is_detected()
    {
        var run = new LiveRun(Plan(), T0);
        var events = new List<RunEvent> { new TaskTransitioned(T0, Tid("T2"), WorkTaskStatus.Pending, WorkTaskStatus.Skipped, "forged", []) };

        var problem = Assert.Single(run.Replay(events).Problems);

        Assert.Contains("T2", problem, StringComparison.Ordinal);
        Assert.Contains("skipped", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_skip_recorded_from_a_status_that_cannot_be_skipped_is_detected()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        var events = run.Events.ToList();
        events.Add(new TaskTransitioned(T0.AddMinutes(1), Tid("T1"), WorkTaskStatus.Running, WorkTaskStatus.Skipped, "forged", []));

        Assert.NotEmpty(run.Replay(events).Problems);
    }

    [Fact]
    public void A_condition_resolved_on_a_task_that_is_not_a_condition_is_detected()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        var events = run.Events.ToList();
        events.Add(new ConditionResolved(T0.AddMinutes(1), Tid("T1"), "yes"));

        var problem = Assert.Single(run.Replay(events).Problems);

        Assert.Contains("T1", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_resolved_before_it_started_is_detected()
    {
        var run = new LiveRun(Plan(), T0);
        var events = new List<RunEvent> { new ConditionResolved(T0, Tid("T4"), "yes") };

        Assert.Single(run.Replay(events).Problems);
    }

    [Fact]
    public void A_condition_resolved_with_a_blank_branch_is_detected()
    {
        var run = new LiveRun(Plan(), T0);
        run.Start("T1");
        run.Start("T2");
        run.Complete("T1");
        run.Complete("T2");
        run.Start("T3");
        run.Complete("T3");
        run.Start("T4");
        var events = run.Events.ToList();
        events.Add(new ConditionResolved(T0.AddHours(1), Tid("T4"), " "));

        Assert.Single(run.Replay(events).Problems);
    }

    [Fact]
    public void A_condition_completed_without_a_recorded_branch_is_detected()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        events.RemoveAt(events.FindIndex(e => e is ConditionResolved));

        var replay = run.Replay(events);

        var problem = Assert.Single(replay.Problems);
        Assert.Contains("T4", problem, StringComparison.Ordinal);
        Assert.Contains("branch", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_event_of_an_unknown_kind_and_a_null_event_are_detected()
    {
        var run = new LiveRun(Plan(), T0);

        Assert.Single(run.Replay([new UnknownEvent(T0)]).Problems);
        Assert.Single(run.Replay([null!]).Problems);
    }

    [Fact]
    public void A_plan_that_is_not_the_one_the_run_started_with_is_refused()
    {
        var run = FullRun();

        var replay = run.Replay(expectedPlanHash: new string('f', 64));

        var problem = Assert.Single(replay.Problems);
        Assert.Contains("hash", problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(replay.Graph.Tasks);
    }

    [Fact]
    public void The_hash_of_the_plan_the_run_started_with_is_accepted()
    {
        var run = FullRun();

        var replay = run.Replay(expectedPlanHash: run.Plan.ContentHash);

        Assert.Empty(replay.Problems);
    }

    [Fact]
    public void A_plan_that_cannot_be_built_gives_a_problem_and_an_empty_graph()
    {
        var cyclic = Plan(Replace("T1", t => t with { DependsOn = [Dep("T3")] }));

        var replay = RunStateReplayer.Replay(cyclic, T0, []);

        Assert.Single(replay.Problems);
        Assert.Empty(replay.Graph.Tasks);
        Assert.False(replay.IsResumable);
    }

    [Fact]
    public void Replay_stops_at_the_first_problem_and_keeps_the_state_before_it()
    {
        var run = FullRun();
        var events = run.Events.ToList();
        var corruptAt = IndexOf(events, "T3", WorkTaskStatus.Running);
        events[corruptAt] = ((TaskTransitioned)events[corruptAt]) with { From = WorkTaskStatus.Blocked };

        var replay = run.Replay(events);
        var untilTheProblem = run.Replay(events.Take(corruptAt));

        Assert.Single(replay.Problems);
        Assert.Equal(LiveRun.Signature(untilTheProblem.Graph), LiveRun.Signature(replay.Graph));
        Assert.Equal(WorkTaskStatus.Completed, replay.Graph[Tid("T1")].Status);
        Assert.Equal(WorkTaskStatus.Pending, replay.Graph[Tid("T3")].Status);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => RunStateReplayer.Replay(null!, T0, []));
        Assert.Throws<ArgumentNullException>(() => RunStateReplayer.Replay(Plan(), T0, null!));
    }
}
