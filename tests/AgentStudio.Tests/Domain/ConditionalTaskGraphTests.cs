using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

/// <summary>Skipped tasks, branch edges, join policies, CompleteCondition and PropagateSkips.</summary>
public sealed class ConditionalTaskGraphTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static void Add(TaskGraph graph, string id, WorkTaskKind kind = WorkTaskKind.AgentWork, JoinPolicy join = JoinPolicy.AllCompleted, int maxRetries = 2) =>
        graph.Add(NewTask(id, kind, maxRetries, join: join));

    private static void Dep(TaskGraph graph, string dependent, string prerequisite, string? branch = null) =>
        graph.AddDependency(Tid(dependent), Tid(prerequisite), branch);

    /// <summary>Condition "c" with a "yes" target "y" and a "no" target "n".</summary>
    private static TaskGraph Branching()
    {
        var graph = new TaskGraph();
        Add(graph, "c", WorkTaskKind.Condition);
        Add(graph, "y");
        Add(graph, "n");
        Dep(graph, "y", "c", "yes");
        Dep(graph, "n", "c", "no");
        return graph;
    }

    private static void Decide(TaskGraph graph, string condition, string branch)
    {
        graph[Tid(condition)].TransitionTo(WorkTaskStatus.Running, T0, "start");
        graph.CompleteCondition(Tid(condition), branch, T0, $"chose {branch}");
    }

    private static void Finish(TaskGraph graph, string id)
    {
        graph[Tid(id)].TransitionTo(WorkTaskStatus.Running, T0, "start");
        graph[Tid(id)].Complete([Eid("e")], T0, "done");
    }

    private static void Cancel(TaskGraph graph, string id) => graph[Tid(id)].TransitionTo(WorkTaskStatus.Cancelled, T0, "cancel");

    private static IEnumerable<string> Ready(TaskGraph graph) => graph.ReadyTasks().Select(t => t.Id.Value);

    // ---- branch edges ----

    [Fact]
    public void Edges_leaving_a_condition_carry_their_branch_and_other_edges_carry_none()
    {
        var graph = Branching();
        Add(graph, "after");
        Dep(graph, "after", "y");

        Assert.Equal([new DependencyEdge(Tid("c"), "yes")], graph.EdgesOf(Tid("y")));
        Assert.Equal([new DependencyEdge(Tid("c"), "no")], graph.EdgesOf(Tid("n")));
        Assert.Equal([new DependencyEdge(Tid("y"), null)], graph.EdgesOf(Tid("after")));
        Assert.Empty(graph.EdgesOf(Tid("c")));
        Assert.Equal([Tid("c")], graph.PrerequisitesOf(Tid("y")));
    }

    [Fact]
    public void A_branch_on_an_edge_that_does_not_leave_a_condition_is_rejected()
    {
        var graph = new TaskGraph();
        Add(graph, "a");
        Add(graph, "b");

        var violation = Violation(() => Dep(graph, "b", "a", "yes"));

        Assert.Equal("graph.branch-on-non-condition", violation.Rule);
        Assert.Empty(graph.PrerequisitesOf(Tid("b")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_edge_leaving_a_condition_needs_a_branch(string? branch)
    {
        var graph = new TaskGraph();
        Add(graph, "c", WorkTaskKind.Condition);
        Add(graph, "x");

        var violation = Violation(() => Dep(graph, "x", "c", branch));

        Assert.Equal("graph.branch-required", violation.Rule);
        Assert.Empty(graph.PrerequisitesOf(Tid("x")));
    }

    [Fact]
    public void Repeating_an_edge_with_the_same_branch_is_harmless()
    {
        var graph = Branching();

        Dep(graph, "y", "c", "yes");

        Assert.Single(graph.EdgesOf(Tid("y")));
    }

    [Fact]
    public void Repeating_an_edge_with_another_branch_is_rejected_and_keeps_the_original()
    {
        var graph = Branching();

        var violation = Violation(() => Dep(graph, "y", "c", "no"));

        Assert.Equal("graph.dependency-branch-conflict", violation.Rule);
        Assert.Equal([new DependencyEdge(Tid("c"), "yes")], graph.EdgesOf(Tid("y")));
    }

    [Fact]
    public void A_branch_edge_that_would_close_a_cycle_is_rejected_with_the_closed_path()
    {
        var graph = Branching(); // y needs c(yes)
        Add(graph, "z", WorkTaskKind.Condition);
        Dep(graph, "z", "y"); // z needs y

        // c would need z(yes): c -> z -> y -> c
        var ex = Assert.Throws<DependencyCycleException>(() => Dep(graph, "c", "z", "yes"));

        Assert.Equal(ex.Cycle[0], ex.Cycle[^1]);
        Assert.Equal([Tid("c"), Tid("z"), Tid("y"), Tid("c")], ex.Cycle);
        Assert.Empty(graph.PrerequisitesOf(Tid("c")));
    }

    [Fact]
    public void A_skipped_task_cannot_acquire_prerequisites()
    {
        var graph = Branching();
        Decide(graph, "c", "yes");
        graph.PropagateSkips(T0);
        Add(graph, "late");

        Assert.Equal(WorkTaskStatus.Skipped, graph[Tid("n")].Status);
        Assert.Equal("graph.dependency-on-started-task", Violation(() => Dep(graph, "n", "late")).Rule);
    }

    // ---- CompleteCondition / TakenBranch ----

    [Fact]
    public void CompleteCondition_completes_the_task_and_remembers_the_branch()
    {
        var graph = Branching();
        graph[Tid("c")].TransitionTo(WorkTaskStatus.Running, T0, "start");

        graph.CompleteCondition(Tid("c"), "yes", T0.AddMinutes(1), "tests passed");

        var condition = graph[Tid("c")];
        Assert.Equal(WorkTaskStatus.Completed, condition.Status);
        Assert.Equal("yes", graph.TakenBranch(Tid("c")));
        Assert.Empty(condition.CompletionEvidence);
        var last = condition.History[^1];
        Assert.Equal((WorkTaskStatus.Running, WorkTaskStatus.Completed, "tests passed", T0.AddMinutes(1)), (last.From, last.To, last.Reason, last.At));
    }

    [Fact]
    public void TakenBranch_is_null_until_the_condition_is_decided()
    {
        var graph = Branching();

        Assert.Null(graph.TakenBranch(Tid("c")));
        Assert.Null(graph.TakenBranch(Tid("y")));
        Assert.Throws<KeyNotFoundException>(() => graph.TakenBranch(Tid("ghost")));
    }

    [Fact]
    public void CompleteCondition_rejects_tasks_that_are_not_conditions()
    {
        var graph = Branching();
        graph[Tid("y")].TransitionTo(WorkTaskStatus.Running, T0, "start");

        var violation = Violation(() => graph.CompleteCondition(Tid("y"), "yes", T0, "x"));

        Assert.Equal("graph.not-a-condition", violation.Rule);
        Assert.Equal(WorkTaskStatus.Running, graph[Tid("y")].Status);
        Assert.Null(graph.TakenBranch(Tid("y")));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Pending)]
    [InlineData(WorkTaskStatus.Blocked)]
    [InlineData(WorkTaskStatus.Failed)]
    [InlineData(WorkTaskStatus.Cancelled)]
    public void CompleteCondition_needs_a_condition_that_is_running_and_stores_nothing_otherwise(WorkTaskStatus status)
    {
        var graph = Branching();
        var condition = graph[Tid("c")];
        switch (status)
        {
            case WorkTaskStatus.Blocked or WorkTaskStatus.Cancelled:
                condition.TransitionTo(status, T0, "x");
                break;
            case WorkTaskStatus.Failed:
                condition.TransitionTo(WorkTaskStatus.Running, T0, "x");
                condition.TransitionTo(WorkTaskStatus.Failed, T0, "x");
                break;
        }

        Violation(() => graph.CompleteCondition(Tid("c"), "yes", T0, "x"));

        Assert.Equal(status, condition.Status);
        Assert.Null(graph.TakenBranch(Tid("c")));
    }

    [Fact]
    public void A_condition_cannot_be_decided_twice()
    {
        var graph = Branching();
        Decide(graph, "c", "yes");

        Violation(() => graph.CompleteCondition(Tid("c"), "no", T0, "again"));

        Assert.Equal("yes", graph.TakenBranch(Tid("c")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CompleteCondition_rejects_a_blank_branch(string branch)
    {
        var graph = Branching();
        graph[Tid("c")].TransitionTo(WorkTaskStatus.Running, T0, "start");

        Assert.ThrowsAny<ArgumentException>(() => graph.CompleteCondition(Tid("c"), branch, T0, "x"));

        Assert.Equal(WorkTaskStatus.Running, graph[Tid("c")].Status);
    }

    [Fact]
    public void CompleteCondition_of_an_unknown_task_is_a_KeyNotFound()
    {
        Assert.Throws<KeyNotFoundException>(() => Branching().CompleteCondition(Tid("ghost"), "yes", T0, "x"));
    }

    // ---- ready tasks and skips ----

    [Fact]
    public void Only_the_taken_branch_becomes_ready()
    {
        var graph = Branching();

        Assert.Equal(["c"], Ready(graph));

        Decide(graph, "c", "yes");
        Assert.Equal(["y"], Ready(graph)); // "n" is on a branch that was not taken: dead, not ready
    }

    [Fact]
    public void Nothing_is_skipped_before_the_condition_is_decided()
    {
        var graph = Branching();

        Assert.Empty(graph.PropagateSkips(T0));
        Assert.All(graph.Tasks, t => Assert.Equal(WorkTaskStatus.Pending, t.Status));
    }

    [Fact]
    public void PropagateSkips_skips_the_branch_not_taken_and_reports_it()
    {
        var graph = Branching();
        Decide(graph, "c", "yes");

        var skipped = graph.PropagateSkips(T0.AddMinutes(5));

        Assert.Equal([Tid("n")], skipped);
        var task = graph[Tid("n")];
        Assert.Equal(WorkTaskStatus.Skipped, task.Status);
        var change = Assert.Single(task.History);
        Assert.Equal((WorkTaskStatus.Pending, WorkTaskStatus.Skipped, T0.AddMinutes(5)), (change.From, change.To, change.At));
        Assert.Equal(WorkTaskStatus.Pending, graph[Tid("y")].Status);
    }

    [Fact]
    public void The_skip_reason_names_the_edge_that_killed_the_task()
    {
        var graph = Branching();
        Decide(graph, "c", "yes");
        graph.PropagateSkips(T0);

        var reason = graph[Tid("n")].History[^1].Reason;

        Assert.Contains("'no'", reason, StringComparison.Ordinal);
        Assert.Contains("'c'", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PropagateSkips_is_idempotent()
    {
        var graph = Branching();
        Decide(graph, "c", "no");

        Assert.Equal([Tid("y")], graph.PropagateSkips(T0));
        Assert.Empty(graph.PropagateSkips(T0.AddMinutes(1)));
        Assert.Single(graph[Tid("y")].History);
    }

    [Fact]
    public void Skips_cascade_to_everything_that_waits_on_a_skipped_task_in_topological_order()
    {
        var graph = Branching();
        Add(graph, "m");
        Add(graph, "k");
        Add(graph, "free");
        Dep(graph, "m", "n");
        Dep(graph, "k", "m");
        Decide(graph, "c", "yes");

        var skipped = graph.PropagateSkips(T0);

        Assert.Equal([Tid("n"), Tid("m"), Tid("k")], skipped);
        Assert.Equal(WorkTaskStatus.Pending, graph[Tid("free")].Status);
        Assert.Contains("'n'", graph[Tid("m")].History[^1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Cascading_skips_do_not_depend_on_insertion_order()
    {
        var graph = new TaskGraph();
        Add(graph, "k");
        Add(graph, "m");
        Add(graph, "n");
        Add(graph, "c", WorkTaskKind.Condition);
        Dep(graph, "k", "m");
        Dep(graph, "m", "n");
        Dep(graph, "n", "c", "no");
        Decide(graph, "c", "yes");

        Assert.Equal([Tid("n"), Tid("m"), Tid("k")], graph.PropagateSkips(T0));
    }

    [Fact]
    public void Tasks_that_already_started_or_finished_are_never_skipped()
    {
        var graph = new TaskGraph();
        Add(graph, "c", WorkTaskKind.Condition);
        foreach (var id in new[] { "running", "done", "failed", "cancelled", "approval" })
        {
            Add(graph, id);
            Dep(graph, id, "c", "no");
        }

        // The graph does not stop a task from being started out of order; skipping must still leave it alone.
        graph[Tid("running")].TransitionTo(WorkTaskStatus.Running, T0, "go");
        Finish(graph, "done");
        graph[Tid("failed")].TransitionTo(WorkTaskStatus.Running, T0, "go");
        graph[Tid("failed")].TransitionTo(WorkTaskStatus.Failed, T0, "boom");
        Cancel(graph, "cancelled");
        graph[Tid("approval")].TransitionTo(WorkTaskStatus.AwaitingApproval, T0, "ask");
        Decide(graph, "c", "yes");

        Assert.Empty(graph.PropagateSkips(T0));
    }

    [Fact]
    public void A_blocked_task_with_a_dead_edge_is_skipped()
    {
        var graph = Branching();
        graph[Tid("n")].TransitionTo(WorkTaskStatus.Blocked, T0, "provider down");
        Decide(graph, "c", "yes");

        Assert.Equal([Tid("n")], graph.PropagateSkips(T0));
        Assert.Equal(WorkTaskStatus.Skipped, graph[Tid("n")].Status);
    }

    [Fact]
    public void A_skipped_prerequisite_is_a_dead_edge_for_plain_edges_too()
    {
        var graph = Branching();
        Add(graph, "after");
        Dep(graph, "after", "n");
        Decide(graph, "c", "yes");

        Assert.Equal([Tid("n"), Tid("after")], graph.PropagateSkips(T0));
    }

    [Fact]
    public void A_condition_that_was_completed_without_choosing_a_branch_kills_every_branch_edge()
    {
        var graph = Branching();
        Finish(graph, "c"); // the plain Complete() path: the graph cannot know it should have been CompleteCondition

        Assert.Null(graph.TakenBranch(Tid("c")));
        Assert.Equal([Tid("y"), Tid("n")], graph.PropagateSkips(T0));
    }

    // ---- join policies ----

    [Fact]
    public void AllCompleted_waits_for_every_prerequisite()
    {
        var graph = new TaskGraph();
        Add(graph, "a");
        Add(graph, "b");
        Add(graph, "j");
        Dep(graph, "j", "a");
        Dep(graph, "j", "b");

        Finish(graph, "a");
        Assert.DoesNotContain("j", Ready(graph));

        Finish(graph, "b");
        Assert.Contains("j", Ready(graph));
    }

    [Fact]
    public void AllCompleted_is_skipped_as_soon_as_one_edge_is_dead_even_if_another_is_still_pending()
    {
        var graph = Branching();
        Add(graph, "slow");
        Add(graph, "j");
        Dep(graph, "j", "n");
        Dep(graph, "j", "slow");
        Decide(graph, "c", "yes");

        Assert.Equal([Tid("n"), Tid("j")], graph.PropagateSkips(T0));
        Assert.Equal(WorkTaskStatus.Pending, graph[Tid("slow")].Status);
    }

    [Fact]
    public void AnyCompleted_waits_until_no_prerequisite_is_pending_even_when_one_is_already_satisfied()
    {
        var graph = new TaskGraph();
        Add(graph, "a");
        Add(graph, "b");
        Add(graph, "j", join: JoinPolicy.AnyCompleted);
        Dep(graph, "j", "a");
        Dep(graph, "j", "b");

        Finish(graph, "a");
        Assert.DoesNotContain("j", Ready(graph)); // b may still complete or die

        Finish(graph, "b");
        Assert.Contains("j", Ready(graph));
    }

    [Fact]
    public void AnyCompleted_merges_the_two_branches_of_a_condition()
    {
        var graph = Branching();
        Add(graph, "merge", join: JoinPolicy.AnyCompleted);
        Dep(graph, "merge", "y");
        Dep(graph, "merge", "n");
        Decide(graph, "c", "no");
        graph.PropagateSkips(T0); // y is skipped

        Assert.DoesNotContain("merge", Ready(graph)); // n has not run yet

        Finish(graph, "n");
        Assert.Equal(["merge"], Ready(graph));
        Assert.Empty(graph.PropagateSkips(T0)); // ready and skippable are exclusive
    }

    [Fact]
    public void AnyCompleted_is_skipped_only_when_every_edge_is_dead()
    {
        var graph = new TaskGraph();
        Add(graph, "c1", WorkTaskKind.Condition);
        Add(graph, "c2", WorkTaskKind.Condition);
        Add(graph, "j", join: JoinPolicy.AnyCompleted);
        Add(graph, "k", join: JoinPolicy.AnyCompleted);
        Dep(graph, "j", "c1", "yes");
        Dep(graph, "j", "c2", "yes");
        Dep(graph, "k", "c1", "yes");
        Dep(graph, "k", "c2", "no");
        Decide(graph, "c1", "no");
        Assert.Empty(graph.PropagateSkips(T0)); // c2 is still undecided: nothing is dead for good yet

        Decide(graph, "c2", "no");

        // j: both edges dead -> skipped. k: c2/no is satisfied -> alive and ready.
        Assert.Equal([Tid("j")], graph.PropagateSkips(T0));
        Assert.Equal(["k"], Ready(graph));
    }

    [Fact]
    public void AnyCompleted_is_not_ready_when_a_prerequisite_is_broken()
    {
        var graph = new TaskGraph();
        Add(graph, "a");
        Add(graph, "b", maxRetries: 0);
        Add(graph, "j", join: JoinPolicy.AnyCompleted);
        Dep(graph, "j", "a");
        Dep(graph, "j", "b");
        Finish(graph, "a");
        graph[Tid("b")].TransitionTo(WorkTaskStatus.Running, T0, "go");
        graph[Tid("b")].TransitionTo(WorkTaskStatus.Failed, T0, "boom"); // no retries left: broken

        Assert.DoesNotContain("j", Ready(graph));
        Assert.Empty(graph.PropagateSkips(T0));
        Assert.Contains(Tid("j"), graph.Analyze(T0, Hour).UnreachableTasks);
    }

    [Theory]
    [InlineData(JoinPolicy.AllCompleted)]
    [InlineData(JoinPolicy.AnyCompleted)]
    public void A_root_task_is_ready_and_never_skipped_whatever_its_join_policy(JoinPolicy join)
    {
        var graph = new TaskGraph();
        Add(graph, "root", join: join);

        Assert.Equal(["root"], Ready(graph));
        Assert.Empty(graph.PropagateSkips(T0));
    }

    [Fact]
    public void A_task_waiting_for_a_failed_task_with_retries_left_is_neither_ready_nor_skipped()
    {
        var graph = new TaskGraph();
        Add(graph, "flaky");
        Add(graph, "after");
        Dep(graph, "after", "flaky");
        graph[Tid("flaky")].TransitionTo(WorkTaskStatus.Running, T0, "go");
        graph[Tid("flaky")].TransitionTo(WorkTaskStatus.Failed, T0, "boom");

        Assert.Empty(Ready(graph));
        Assert.Empty(graph.PropagateSkips(T0));
    }

    // ---- health analysis with skipped work ----

    [Fact]
    public void Skipped_work_is_finished_work_for_the_health_analysis()
    {
        var graph = Branching();
        Decide(graph, "c", "yes");
        Finish(graph, "y");
        graph.PropagateSkips(T0);

        var report = graph.Analyze(T0, Hour);

        Assert.True(report.IsHealthy);
        Assert.False(report.IsDeadlocked);
        Assert.Empty(report.UnreachableTasks);
    }

    [Fact]
    public void Analyze_does_not_need_PropagateSkips_to_have_run_to_avoid_a_false_deadlock()
    {
        var graph = Branching();
        Decide(graph, "c", "yes");
        Finish(graph, "y");

        // "n" is still Pending but doomed: the engine skips it on its next pass, so this is not a deadlock.
        var report = graph.Analyze(T0, Hour);

        Assert.True(report.IsHealthy);
        Assert.Equal(WorkTaskStatus.Pending, graph[Tid("n")].Status); // Analyze does not change anything
    }

    [Fact]
    public void Analyze_sees_a_task_that_a_pending_skip_will_unblock()
    {
        var graph = Branching();
        Add(graph, "merge", join: JoinPolicy.AnyCompleted);
        Dep(graph, "merge", "y");
        Dep(graph, "merge", "n");
        Decide(graph, "c", "yes");
        Finish(graph, "y");

        // merge waits for n; n is doomed. After the pending skip merge is ready, so the graph can progress.
        Assert.False(graph.Analyze(T0, Hour).IsDeadlocked);
        Assert.DoesNotContain("merge", Ready(graph)); // the skip has not been applied yet

        graph.PropagateSkips(T0);
        Assert.Contains("merge", Ready(graph));
    }

    [Fact]
    public void Work_behind_a_cancelled_task_is_still_unreachable_next_to_conditions()
    {
        var graph = Branching();
        Add(graph, "gone");
        Add(graph, "stuck");
        Dep(graph, "stuck", "gone");
        Dep(graph, "stuck", "y");
        Cancel(graph, "gone");
        Decide(graph, "c", "yes");

        var report = graph.Analyze(T0, Hour);

        Assert.Equal([Tid("stuck")], report.UnreachableTasks);
    }

    [Fact]
    public void A_skipped_task_stops_unreachability_from_travelling_through_it()
    {
        var graph = new TaskGraph();
        Add(graph, "c", WorkTaskKind.Condition);
        Add(graph, "x");
        Add(graph, "s");
        Add(graph, "alive");
        Add(graph, "t", join: JoinPolicy.AnyCompleted);
        Dep(graph, "s", "x");
        Dep(graph, "s", "c", "no");
        Dep(graph, "t", "s");
        Dep(graph, "t", "alive");
        Cancel(graph, "x");
        Decide(graph, "c", "yes");
        graph.PropagateSkips(T0); // s has a dead edge (and a broken one): skipped

        var report = graph.Analyze(T0, Hour);

        Assert.Equal(WorkTaskStatus.Skipped, graph[Tid("s")].Status);
        Assert.Empty(report.UnreachableTasks); // t only waits for "alive"
        Assert.Equal(["alive"], Ready(graph));
    }

    [Fact]
    public void A_failed_condition_without_retries_makes_both_branches_unreachable()
    {
        var graph = new TaskGraph();
        graph.Add(NewTask("c", WorkTaskKind.Condition, maxRetries: 0));
        Add(graph, "y");
        Add(graph, "n");
        Dep(graph, "y", "c", "yes");
        Dep(graph, "n", "c", "no");
        graph[Tid("c")].TransitionTo(WorkTaskStatus.Running, T0, "go");
        graph[Tid("c")].TransitionTo(WorkTaskStatus.Failed, T0, "boom");

        var report = graph.Analyze(T0, Hour);

        Assert.Equal([Tid("y"), Tid("n")], report.UnreachableTasks);
        Assert.Equal([Tid("c")], report.RetryExhaustedTasks);
        Assert.True(report.IsDeadlocked);
    }
}
