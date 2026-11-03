using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class WorkTaskTests
{
    // The specification, written independently of WorkTaskTransitions.
    private static readonly Dictionary<WorkTaskStatus, WorkTaskStatus[]> Spec = new()
    {
        [WorkTaskStatus.Pending] = [WorkTaskStatus.Running, WorkTaskStatus.Blocked, WorkTaskStatus.AwaitingApproval, WorkTaskStatus.Cancelled, WorkTaskStatus.Skipped],
        [WorkTaskStatus.Running] = [WorkTaskStatus.Completed, WorkTaskStatus.Failed, WorkTaskStatus.Cancelled, WorkTaskStatus.Blocked, WorkTaskStatus.AwaitingApproval],
        [WorkTaskStatus.Blocked] = [WorkTaskStatus.Pending, WorkTaskStatus.Cancelled, WorkTaskStatus.Skipped],
        [WorkTaskStatus.AwaitingApproval] = [WorkTaskStatus.Running, WorkTaskStatus.Completed, WorkTaskStatus.Failed, WorkTaskStatus.Cancelled],
        [WorkTaskStatus.Failed] = [WorkTaskStatus.Pending],
        [WorkTaskStatus.Completed] = [],
        [WorkTaskStatus.Cancelled] = [],
        [WorkTaskStatus.Skipped] = [],
    };

    public static TheoryData<WorkTaskStatus, WorkTaskStatus, bool> AllPairs()
    {
        var data = new TheoryData<WorkTaskStatus, WorkTaskStatus, bool>();
        foreach (var from in Enum.GetValues<WorkTaskStatus>())
        {
            foreach (var to in Enum.GetValues<WorkTaskStatus>())
            {
                data.Add(from, to, Spec[from].Contains(to));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void The_transition_table_matches_the_specification(WorkTaskStatus from, WorkTaskStatus to, bool allowed)
    {
        Assert.Equal(allowed, WorkTaskTransitions.IsAllowed(from, to));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void TransitionTo_accepts_exactly_the_allowed_pairs_except_the_three_guarded_ones(WorkTaskStatus from, WorkTaskStatus to, bool allowed)
    {
        var task = TaskIn(from);

        // Completing, retrying and skipping have their own entry points (Complete, Retry, TaskGraph.PropagateSkips), so
        // TransitionTo refuses them even when the pair is legal.
        if (to is WorkTaskStatus.Completed or WorkTaskStatus.Skipped || (from == WorkTaskStatus.Failed && to == WorkTaskStatus.Pending))
        {
            Violation(() => task.TransitionTo(to, T0.AddMinutes(1), "x"));
            Assert.Equal(from, task.Status);
            return;
        }

        if (allowed)
        {
            task.TransitionTo(to, T0.AddMinutes(1), "x");
            Assert.Equal(to, task.Status);
        }
        else
        {
            Violation(() => task.TransitionTo(to, T0.AddMinutes(1), "x"));
            Assert.Equal(from, task.Status);
        }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Complete_works_only_from_states_that_allow_Completed(WorkTaskStatus from, WorkTaskStatus to, bool allowed)
    {
        if (to != WorkTaskStatus.Completed)
        {
            return; // Only the Completed column is relevant; the other rows are covered above.
        }

        var task = TaskIn(from);
        var evidenceBefore = task.CompletionEvidence.Count;
        if (allowed)
        {
            task.Complete([Eid("e1")], T0.AddMinutes(1), "done");
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
        }
        else
        {
            Violation(() => task.Complete([Eid("e1")], T0.AddMinutes(1), "done"));
            Assert.Equal(from, task.Status);
            Assert.Equal(evidenceBefore, task.CompletionEvidence.Count);
        }
    }

    [Fact]
    public void Retry_is_the_only_way_out_of_Failed_and_counts_the_attempt()
    {
        var task = TaskIn(WorkTaskStatus.Failed);

        task.Retry(T0.AddMinutes(1), "again");

        Assert.Equal(WorkTaskStatus.Pending, task.Status);
        Assert.Equal(1, task.RetryCount);
    }

    [Fact]
    public void Retry_is_rejected_once_MaxRetries_is_used_up()
    {
        var task = TaskIn(WorkTaskStatus.Failed, maxRetries: 1);
        task.Retry(T0.AddMinutes(1), "again");
        task.TransitionTo(WorkTaskStatus.Running, T0.AddMinutes(2), "go");
        task.TransitionTo(WorkTaskStatus.Failed, T0.AddMinutes(3), "boom");

        Assert.True(task.RetriesExhausted);
        Violation(() => task.Retry(T0.AddMinutes(4), "again"));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.RetryCount);
    }

    [Fact]
    public void Retry_with_zero_MaxRetries_is_rejected_straight_away()
    {
        var task = TaskIn(WorkTaskStatus.Failed, maxRetries: 0);

        Violation(() => task.Retry(T0.AddMinutes(1), "again"));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Pending)]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.Completed)]
    [InlineData(WorkTaskStatus.Skipped)]
    public void Retry_is_rejected_when_the_task_has_not_failed(WorkTaskStatus status)
    {
        var task = TaskIn(status);

        Violation(() => task.Retry(T0.AddMinutes(1), "again"));
        Assert.Equal(0, task.RetryCount);
    }

    [Theory]
    [InlineData(WorkTaskKind.AgentWork, true)]
    [InlineData(WorkTaskKind.ToolRun, true)]
    [InlineData(WorkTaskKind.Integration, true)]
    [InlineData(WorkTaskKind.Condition, false)]
    [InlineData(WorkTaskKind.HumanApproval, false)]
    public void Evidence_is_required_by_kind(WorkTaskKind kind, bool requiresEvidence)
    {
        var task = NewTask("t", kind);
        task.TransitionTo(WorkTaskStatus.Running, T0, "start");

        Assert.Equal(requiresEvidence, task.RequiresEvidence);
        if (requiresEvidence)
        {
            Violation(() => task.Complete([], T0.AddMinutes(1), "done"));
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            task.Complete([Eid("e1")], T0.AddMinutes(1), "done");
        }
        else
        {
            task.Complete([], T0.AddMinutes(1), "done");
        }

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
    }

    [Fact]
    public void Forcing_requiresEvidence_overrides_the_kind_default_in_both_directions()
    {
        var conditionForced = NewTask("c", WorkTaskKind.Condition, requiresEvidence: true);
        conditionForced.TransitionTo(WorkTaskStatus.Running, T0, "start");
        Violation(() => conditionForced.Complete([], T0, "done"));

        var agentWorkRelaxed = NewTask("a", WorkTaskKind.AgentWork, requiresEvidence: false);
        agentWorkRelaxed.TransitionTo(WorkTaskStatus.Running, T0, "start");
        agentWorkRelaxed.Complete([], T0, "done");
        Assert.Equal(WorkTaskStatus.Completed, agentWorkRelaxed.Status);
    }

    [Fact]
    public void Complete_records_distinct_evidence()
    {
        var task = TaskIn(WorkTaskStatus.Running);

        task.Complete([Eid("e1"), Eid("e1"), Eid("e2")], T0.AddMinutes(1), "done");

        Assert.Equal([Eid("e1"), Eid("e2")], task.CompletionEvidence);
        Assert.Equal([Eid("e1"), Eid("e2")], task.Snapshot().CompletionEvidence);
    }

    [Fact]
    public void RecordActivity_is_only_allowed_while_running_and_never_moves_backwards()
    {
        var task = NewTask("t");
        Violation(() => task.RecordActivity(T0.AddMinutes(1)));

        task.TransitionTo(WorkTaskStatus.Running, T0, "start");
        task.RecordActivity(T0.AddMinutes(5));
        task.RecordActivity(T0.AddMinutes(2));
        Assert.Equal(T0.AddMinutes(5), task.LastActivityAt);

        task.TransitionTo(WorkTaskStatus.Blocked, T0.AddMinutes(6), "wait");
        Violation(() => task.RecordActivity(T0.AddMinutes(7)));
    }

    [Fact]
    public void Every_transition_is_recorded_in_History_in_order()
    {
        var task = NewTask("t");
        task.TransitionTo(WorkTaskStatus.Running, T0.AddMinutes(1), "start");
        task.TransitionTo(WorkTaskStatus.Failed, T0.AddMinutes(2), "boom");
        task.Retry(T0.AddMinutes(3), "again");

        Assert.Collection(
            task.History,
            h => Assert.Equal((WorkTaskStatus.Pending, WorkTaskStatus.Running, "start"), (h.From, h.To, h.Reason)),
            h => Assert.Equal((WorkTaskStatus.Running, WorkTaskStatus.Failed, "boom"), (h.From, h.To, h.Reason)),
            h => Assert.Equal((WorkTaskStatus.Failed, WorkTaskStatus.Pending, "again"), (h.From, h.To, h.Reason)));
        Assert.Equal(T0.AddMinutes(3), task.History[^1].At);
    }

    [Fact]
    public void A_rejected_transition_leaves_no_trace_in_History()
    {
        var task = TaskIn(WorkTaskStatus.Cancelled);
        var before = task.History.Count;

        Violation(() => task.TransitionTo(WorkTaskStatus.Running, T0, "zombie"));

        Assert.Equal(before, task.History.Count);
    }

    [Fact]
    public void Transitions_require_a_reason()
    {
        var task = NewTask("t");

        Assert.ThrowsAny<ArgumentException>(() => task.TransitionTo(WorkTaskStatus.Running, T0, " "));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Pending)]
    [InlineData(WorkTaskStatus.Blocked)]
    [InlineData(WorkTaskStatus.Running)]
    public void Only_the_graph_can_skip_a_task(WorkTaskStatus from)
    {
        var task = TaskIn(from);

        var violation = Violation(() => task.TransitionTo(WorkTaskStatus.Skipped, T0.AddMinutes(1), "nope"));

        Assert.Equal("task.skip-via-graph", violation.Rule);
        Assert.Equal(from, task.Status);
    }

    [Fact]
    public void Skipped_is_terminal_and_nothing_leaves_it()
    {
        var task = TaskIn(WorkTaskStatus.Skipped);

        Assert.True(WorkTaskTransitions.IsTerminal(WorkTaskStatus.Skipped));
        Assert.Empty(WorkTaskTransitions.NextStates(WorkTaskStatus.Skipped));
        foreach (var next in Enum.GetValues<WorkTaskStatus>().Where(s => s is not (WorkTaskStatus.Completed or WorkTaskStatus.Skipped)))
        {
            Violation(() => task.TransitionTo(next, T0.AddMinutes(1), "x"));
        }

        Violation(() => task.Complete([Eid("e")], T0.AddMinutes(1), "x"));
        Violation(() => task.Retry(T0.AddMinutes(1), "x"));
        Violation(() => task.Assign(Aid("a")));
        Violation(() => task.RecordActivity(T0.AddMinutes(1)));
        Assert.Equal(WorkTaskStatus.Skipped, task.Status);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Completed, true)]
    [InlineData(WorkTaskStatus.Cancelled, true)]
    [InlineData(WorkTaskStatus.Skipped, true)]
    [InlineData(WorkTaskStatus.Pending, false)]
    [InlineData(WorkTaskStatus.Running, false)]
    [InlineData(WorkTaskStatus.Blocked, false)]
    [InlineData(WorkTaskStatus.AwaitingApproval, false)]
    [InlineData(WorkTaskStatus.Failed, false)]
    public void Terminal_statuses_are_completed_cancelled_and_skipped(WorkTaskStatus status, bool terminal)
    {
        Assert.Equal(terminal, WorkTaskTransitions.IsTerminal(status));
    }

    [Fact]
    public void A_skipped_task_records_the_skip_in_its_history()
    {
        var task = TaskIn(WorkTaskStatus.Skipped);

        var change = Assert.Single(task.History);
        Assert.Equal((WorkTaskStatus.Pending, WorkTaskStatus.Skipped), (change.From, change.To));
        Assert.False(string.IsNullOrWhiteSpace(change.Reason));
    }

    [Fact]
    public void The_join_policy_defaults_to_AllCompleted_and_is_kept()
    {
        Assert.Equal(JoinPolicy.AllCompleted, NewTask("a").Join);
        Assert.Equal(JoinPolicy.AnyCompleted, NewTask("b", join: JoinPolicy.AnyCompleted).Join);
    }

    [Fact]
    public void Random_walks_only_ever_use_specified_transitions_and_History_matches_successes()
    {
        var random = new Random(20260101);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var task = NewTask("t", maxRetries: random.Next(0, 3));
            var successes = 0;
            for (var step = 0; step < 25; step++)
            {
                var next = (WorkTaskStatus)random.Next(0, 7);
                var from = task.Status;
                try
                {
                    switch (random.Next(0, 3))
                    {
                        case 0 when next == WorkTaskStatus.Completed:
                            task.Complete([Eid("e")], T0.AddMinutes(step), "done");
                            break;
                        case 1 when from == WorkTaskStatus.Failed:
                            task.Retry(T0.AddMinutes(step), "again");
                            next = WorkTaskStatus.Pending;
                            break;
                        default:
                            task.TransitionTo(next, T0.AddMinutes(step), "step");
                            break;
                    }

                    successes++;
                    Assert.Contains(next, Spec[from]);
                    Assert.Equal(next, task.Status);
                }
                catch (AgentStudio.Domain.Common.DomainRuleViolationException)
                {
                    Assert.Equal(from, task.Status);
                }
            }

            Assert.Equal(successes, task.History.Count);
            Assert.All(task.History, h => Assert.Contains(h.To, Spec[h.From]));
            Assert.True(task.RetryCount <= task.MaxRetries);
        }
    }
}
