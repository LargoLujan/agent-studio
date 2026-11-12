using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Domain.Work.Milestones;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Work;

/// <summary>docs/16 section 4.6 (P0 of Phase 9): the milestone, its replay, the backlog, the rule of the board and the budget.</summary>
public sealed class WorkDomainTests
{
    private static readonly MilestoneId Mil = new("mil-20261115-a1b2c3");

    private static MilestoneHeader Header(WorkBudget? budget = null) => new(Mil, "Calculadora", "Una calculadora con pruebas", HashA, budget, T0);

    private static Milestone OpenWithRun()
    {
        var milestone = Milestone.Open(Header());
        milestone.Link(new MilestoneTarget(MilestoneTargetKind.Run, "20261015-a1b2c3"), T0.AddMinutes(1));
        return milestone;
    }

    private static string Rule(Action act) => Assert.Throws<DomainRuleViolationException>(act).Rule;

    // ---- ids ----

    [Theory]
    [InlineData("")]
    [InlineData("mil-")]
    [InlineData("20261015-a1b2c3")]
    [InlineData("mil-a b")]
    public void A_milestone_id_starts_with_its_prefix_and_has_the_characters_of_a_run_id(string value) =>
        Assert.Throws<ArgumentException>(() => new MilestoneId(value));

    [Fact]
    public void A_backlog_item_id_starts_with_its_prefix() => Assert.Throws<ArgumentException>(() => new BacklogItemId("item-1"));

    // ---- milestone ----

    [Fact]
    public void A_milestone_opens_open_with_its_first_set_of_requirements()
    {
        var milestone = Milestone.Open(Header());

        Assert.Equal(MilestoneStatus.Open, milestone.Status);
        Assert.Equal(HashA, milestone.CurrentRequirementSetHash);
        Assert.Equal([HashA], milestone.RequirementSetHistory);
    }

    [Fact]
    public void A_header_needs_a_name_an_objective_a_sha256_and_a_budget_for_the_whole_milestone()
    {
        Assert.Equal("milestone.name", Rule(() => Milestone.Open(Header() with { Name = " " })));
        Assert.Equal("milestone.name", Rule(() => Milestone.Open(Header() with { Name = "a\nb" })));
        Assert.Equal("milestone.objective", Rule(() => Milestone.Open(Header() with { Objective = "" })));
        Assert.Equal("milestone.requirements-hash", Rule(() => Milestone.Open(Header() with { RequirementSetHash = "abc" })));
        Assert.Equal("budget.no-limit", Rule(() => Milestone.Open(Header(new WorkBudget(null, null)))));
        Assert.Equal("milestone.budget-period", Rule(() => Milestone.Open(Header(new WorkBudget(1000, null, BudgetPeriod.Week)))));
        Assert.Equal(MilestoneStatus.Open, Milestone.Open(Header(new WorkBudget(1000, 5))).Status);
    }

    [Fact]
    public void Revising_the_requirements_keeps_the_earlier_set_and_refuses_the_same_one()
    {
        var milestone = Milestone.Open(Header());

        milestone.ReviseRequirements(HashB, T0.AddMinutes(1));

        Assert.Equal(HashB, milestone.CurrentRequirementSetHash);
        Assert.Equal([HashA, HashB], milestone.RequirementSetHistory);
        Assert.Equal("milestone.requirements-unchanged", Rule(() => milestone.ReviseRequirements(HashB, T0.AddMinutes(2))));
    }

    [Fact]
    public void A_run_or_a_debate_is_linked_once()
    {
        var milestone = Milestone.Open(Header());
        var debate = new MilestoneTarget(MilestoneTargetKind.Debate, "deb-20261015-a1b2c3");

        milestone.Link(debate, T0.AddMinutes(1));

        Assert.Equal([debate], milestone.Targets);
        Assert.Equal("milestone.already-linked", Rule(() => milestone.Link(debate, T0.AddMinutes(2))));
        Assert.Equal("milestone.target", Rule(() => milestone.Link(new MilestoneTarget(MilestoneTargetKind.Run, " "), T0.AddMinutes(2))));
    }

    [Fact]
    public void A_milestone_closes_only_with_a_run_linked_and_the_next_revision_of_the_report()
    {
        var empty = Milestone.Open(Header());
        empty.Link(new MilestoneTarget(MilestoneTargetKind.Debate, "deb-1"), T0.AddMinutes(1));
        Assert.Equal("milestone.nothing-to-close", Rule(() => empty.Close(new MilestoneClosure(T0.AddMinutes(2), 1, "aud-1", HashB, null))));

        var milestone = OpenWithRun();
        Assert.Equal("milestone.revision", Rule(() => milestone.Close(new MilestoneClosure(T0.AddMinutes(2), 2, "aud-1", HashB, null))));
        Assert.Equal("milestone.audit", Rule(() => milestone.Close(new MilestoneClosure(T0.AddMinutes(2), 1, " ", HashB, null))));
        Assert.Equal("milestone.report-hash", Rule(() => milestone.Close(new MilestoneClosure(T0.AddMinutes(2), 1, "aud-1", "x", null))));

        milestone.Close(new MilestoneClosure(T0.AddMinutes(2), 1, "aud-1", HashB, CommitA));

        Assert.Equal(MilestoneStatus.Closed, milestone.Status);
    }

    [Fact]
    public void A_closed_milestone_takes_nothing_but_a_reopening_which_keeps_every_report()
    {
        var milestone = OpenWithRun();
        milestone.Close(new MilestoneClosure(T0.AddMinutes(2), 1, "aud-1", HashB, CommitA));

        Assert.Equal("milestone.not-open", Rule(() => milestone.ReviseRequirements(HashB, T0.AddMinutes(3))));
        Assert.Equal("milestone.not-open", Rule(() => milestone.Link(new MilestoneTarget(MilestoneTargetKind.Run, "r2"), T0.AddMinutes(3))));
        Assert.Equal("milestone.reopen-evidence", Rule(() => milestone.Reopen(new MilestoneReopening(T0.AddMinutes(3), MilestoneReopenReason.Revert, null))));

        milestone.Reopen(new MilestoneReopening(T0.AddMinutes(3), MilestoneReopenReason.Revert, HashA));
        Assert.Equal("milestone.not-closed", Rule(() => milestone.Reopen(new MilestoneReopening(T0.AddMinutes(4), MilestoneReopenReason.ByPerson, null))));
        milestone.Close(new MilestoneClosure(T0.AddMinutes(5), 2, "aud-2", HashA, CommitB));

        Assert.Equal(["aud-1", "aud-2"], milestone.Closures.Select(c => c.Audit));
        Assert.Single(milestone.Reopenings);
    }

    [Fact]
    public void A_person_may_reopen_a_milestone_without_evidence_and_the_time_only_goes_forward()
    {
        var milestone = OpenWithRun();
        milestone.Close(new MilestoneClosure(T0.AddMinutes(2), 1, "aud-1", HashB, null));

        Assert.Equal("milestone.time-order", Rule(() => milestone.Reopen(new MilestoneReopening(T0, MilestoneReopenReason.ByPerson, null))));
        milestone.Reopen(new MilestoneReopening(T0.AddMinutes(3), MilestoneReopenReason.ByPerson, null));

        Assert.Equal(MilestoneStatus.Open, milestone.Status);
    }

    [Fact]
    public void Replaying_the_events_gives_the_state_the_aggregate_reaches()
    {
        var run = new MilestoneTarget(MilestoneTargetKind.Run, "20261015-a1b2c3");
        var closure = new MilestoneClosure(T0.AddMinutes(3), 1, "aud-1", HashB, CommitA);

        var replay = MilestoneStateReplayer.Replay(
            Header(),
            [new TargetLinked(T0.AddMinutes(1), run), new RequirementsRevised(T0.AddMinutes(2), HashB), new ClosureRecorded(closure), new ReopeningRecorded(new MilestoneReopening(T0.AddMinutes(4), MilestoneReopenReason.Challenge, HashA))]);

        Assert.True(replay.IsConsistent);
        var milestone = replay.Milestone!;
        Assert.Equal((MilestoneStatus.Open, HashB), (milestone.Status, milestone.CurrentRequirementSetHash));
        Assert.Equal([run], milestone.Targets);
        Assert.Equal([closure], milestone.Closures);
    }

    [Fact]
    public void A_log_that_breaks_a_rule_is_a_problem_not_a_crash_and_the_state_stops_before_it()
    {
        var run = new MilestoneTarget(MilestoneTargetKind.Run, "r1");

        var replay = MilestoneStateReplayer.Replay(Header(), [new TargetLinked(T0.AddMinutes(1), run), new TargetLinked(T0.AddMinutes(2), run), new RequirementsRevised(T0.AddMinutes(3), HashB)]);

        Assert.False(replay.IsConsistent);
        Assert.Contains("Event 2", Assert.Single(replay.Problems), StringComparison.Ordinal);
        Assert.Contains("milestone.already-linked", replay.Problems[0], StringComparison.Ordinal);
        Assert.Equal(HashA, replay.Milestone!.CurrentRequirementSetHash);
        Assert.Null(MilestoneStateReplayer.Replay(Header() with { Name = "" }, []).Milestone);
    }

    // ---- backlog ----

    private static BacklogItem Item(string id, int version, string statement = "Sumar", DateTimeOffset? at = null, RequirementPriority priority = RequirementPriority.Desirable) =>
        new(new BacklogItemId(id), version, statement, priority, [], at ?? T0.AddMinutes(version));

    [Fact]
    public void An_item_is_added_changed_with_a_new_version_and_never_deleted()
    {
        var replay = BacklogReplayer.Replay([new ItemRecorded(Item("bk-1", 1)), new ItemRecorded(Item("bk-1", 2, "Sumar y restar")), new ItemRecorded(Item("bk-2", 1, "Dividir", T0.AddMinutes(5)))]);

        Assert.True(replay.IsConsistent);
        Assert.Equal(["Sumar y restar", "Dividir"], replay.Backlog.Items.Select(i => i.Statement));
        Assert.Equal([1, 2], replay.Backlog.History(new BacklogItemId("bk-1")).Select(i => i.Version));
    }

    [Fact]
    public void A_version_is_the_next_one_and_a_retired_item_takes_none()
    {
        var skipped = BacklogReplayer.Replay([new ItemRecorded(Item("bk-1", 1)), new ItemRecorded(Item("bk-1", 3))]);
        Assert.Contains("backlog.version-order", skipped.Problems[0], StringComparison.Ordinal);

        var retired = BacklogReplayer.Replay([new ItemRecorded(Item("bk-1", 1)), new ItemRetired(T0.AddMinutes(2), new BacklogItemId("bk-1"), 1), new ItemRecorded(Item("bk-1", 2, at: T0.AddMinutes(3)))]);
        Assert.Contains("backlog.retired", retired.Problems[0], StringComparison.Ordinal);

        var twice = BacklogReplayer.Replay([new ItemRecorded(Item("bk-1", 1)), new ItemRetired(T0.AddMinutes(2), new BacklogItemId("bk-1"), 1), new ItemRetired(T0.AddMinutes(3), new BacklogItemId("bk-1"), 1)]);
        Assert.Contains("backlog.retired", twice.Problems[0], StringComparison.Ordinal);

        var wrongVersion = BacklogReplayer.Replay([new ItemRecorded(Item("bk-1", 1)), new ItemRetired(T0.AddMinutes(2), new BacklogItemId("bk-1"), 4)]);
        Assert.Contains("backlog.version-order", wrongVersion.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_retired_item_leaves_the_list_but_keeps_its_history()
    {
        var replay = BacklogReplayer.Replay([new ItemRecorded(Item("bk-1", 1)), new ItemRetired(T0.AddMinutes(2), new BacklogItemId("bk-1"), 1)]);

        Assert.Empty(replay.Backlog.Items);
        Assert.True(replay.Backlog.IsRetired(new BacklogItemId("bk-1")));
        Assert.Single(replay.Backlog.History(new BacklogItemId("bk-1")));
    }

    [Fact]
    public void The_order_puts_the_listed_items_first_and_the_rest_in_the_order_they_were_added()
    {
        var a = new BacklogItemId("bk-a");
        var b = new BacklogItemId("bk-b");
        var c = new BacklogItemId("bk-c");
        var added = new BacklogEvent[] { new ItemRecorded(Item("bk-a", 1, at: T0.AddMinutes(1))), new ItemRecorded(Item("bk-b", 1, at: T0.AddMinutes(2))), new ItemRecorded(Item("bk-c", 1, at: T0.AddMinutes(3))) };

        var replay = BacklogReplayer.Replay([.. added, new OrderChanged(T0.AddMinutes(4), [c])]);

        Assert.Equal([c, a, b], replay.Backlog.Items.Select(i => i.Id));
        Assert.Contains("backlog.order-duplicate", BacklogReplayer.Replay([.. added, new OrderChanged(T0.AddMinutes(4), [a, a])]).Problems[0], StringComparison.Ordinal);
        Assert.Contains("backlog.order-unknown", BacklogReplayer.Replay([.. added, new OrderChanged(T0.AddMinutes(4), [new BacklogItemId("bk-z")])]).Problems[0], StringComparison.Ordinal);
        Assert.Equal([c, b], BacklogReplayer.Replay([.. added, new OrderChanged(T0.AddMinutes(4), [c, b]), new ItemRetired(T0.AddMinutes(5), a, 1)]).Backlog.Items.Select(i => i.Id));
    }

    [Fact]
    public void An_invalid_version_is_refused_and_the_hash_covers_the_content_but_not_the_time()
    {
        Assert.Contains("backlog.statement", Item("bk-1", 1, " ").Validate());
        Assert.Contains("backlog.criterion", (Item("bk-1", 1) with { Criteria = [new AcceptanceCriterion("", "x", VerificationMethod.HumanReview)] }).Validate());
        var one = Item("bk-1", 1);

        Assert.Equal(one.Sha256(), (one with { CreatedAt = T0.AddDays(3) }).Sha256());
        Assert.NotEqual(one.Sha256(), (one with { Priority = RequirementPriority.Mandatory }).Sha256());
        Assert.NotEqual(one.Sha256(), (one with { Version = 2 }).Sha256());
        Assert.True(ContentHash.IsSha256Hex(one.Sha256()));
    }

    [Fact]
    public void An_item_becomes_a_requirement_when_it_is_planned()
    {
        var criteria = new[] { new AcceptanceCriterion("c1", "Pasa", VerificationMethod.AutomatedTest) };

        var requirement = (Item("bk-1", 1, priority: RequirementPriority.Mandatory) with { Criteria = criteria }).ToRequirement(Rid("RQ-01"));

        Assert.Equal((Rid("RQ-01"), "Sumar", RequirementPriority.Mandatory), (requirement.Id, requirement.Statement, requirement.Priority));
        Assert.Single(requirement.Criteria);
    }

    // ---- board ----

    [Fact]
    public void Each_column_of_a_requirement_follows_the_table_of_the_decision()
    {
        static RequirementBoardFacts F(WorkTaskStatus[] tasks, bool ended = false, bool failed = false, BoardVerdictFacts? verdict = null, bool challenged = false, bool delivered = false) =>
            new(tasks, ended, failed, verdict, challenged, delivered);
        var approved = new BoardVerdictFacts(VerdictDecision.Approved, AssessmentStatus.Satisfied);

        Assert.Equal(BoardColumn.Backlog, BoardRules.ColumnOf(F([])));
        Assert.Equal(BoardColumn.Planned, BoardRules.ColumnOf(F([WorkTaskStatus.Pending])));
        Assert.Equal(BoardColumn.InProgress, BoardRules.ColumnOf(F([WorkTaskStatus.Pending, WorkTaskStatus.Blocked])));
        Assert.Equal(BoardColumn.InProgress, BoardRules.ColumnOf(F([WorkTaskStatus.Completed, WorkTaskStatus.Pending])));
        Assert.Equal(BoardColumn.InReview, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true)));
        Assert.Equal(BoardColumn.Returned, BoardRules.ColumnOf(F([WorkTaskStatus.Failed], ended: true, failed: true)));
        Assert.Equal(BoardColumn.Done, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: approved)));
        Assert.Equal(BoardColumn.Integrated, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: approved, delivered: true)));
        Assert.Equal(BoardColumn.InReview, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: approved, challenged: true)));
        Assert.Equal(BoardColumn.Returned, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: new BoardVerdictFacts(VerdictDecision.ChangesRequested, AssessmentStatus.Satisfied))));
        Assert.Equal(BoardColumn.Returned, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: new BoardVerdictFacts(VerdictDecision.Rejected, null))));
        Assert.Equal(BoardColumn.InReview, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: new BoardVerdictFacts(VerdictDecision.EscalateToHuman, null))));
        Assert.Equal(BoardColumn.InReview, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: new BoardVerdictFacts(VerdictDecision.Unresolved, AssessmentStatus.Satisfied))));
        Assert.Equal(BoardColumn.InReview, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: new BoardVerdictFacts(VerdictDecision.Approved, AssessmentStatus.NotVerifiable))));
        Assert.Equal(BoardColumn.Returned, BoardRules.ColumnOf(F([WorkTaskStatus.Completed], ended: true, verdict: new BoardVerdictFacts(VerdictDecision.Approved, AssessmentStatus.NotSatisfied))));
    }

    /// <summary>F9-2: with random facts (a seeded generator), no card is done or integrated without an approved verdict that gives it as satisfied.</summary>
    [Fact]
    public void No_requirement_card_reaches_done_without_an_approving_verdict_whatever_the_facts()
    {
        var random = new Random(9);
        var statuses = Enum.GetValues<WorkTaskStatus>();
        var decisions = Enum.GetValues<VerdictDecision>();
        var assessments = new AssessmentStatus?[] { null, AssessmentStatus.Satisfied, AssessmentStatus.NotSatisfied, AssessmentStatus.NotVerifiable };
        var reached = new HashSet<BoardColumn>();

        for (var i = 0; i < 5000; i++)
        {
            var tasks = Enumerable.Range(0, random.Next(0, 4)).Select(_ => statuses[random.Next(statuses.Length)]).ToArray();
            var verdict = random.Next(3) == 0 ? null : new BoardVerdictFacts(decisions[random.Next(decisions.Length)], assessments[random.Next(assessments.Length)]);
            var facts = new RequirementBoardFacts(tasks, random.Next(2) == 0, random.Next(2) == 0, verdict, random.Next(4) == 0, random.Next(2) == 0);

            var column = BoardRules.ColumnOf(facts);
            reached.Add(column);

            if (column is BoardColumn.Done or BoardColumn.Integrated)
            {
                Assert.Equal(new BoardVerdictFacts(VerdictDecision.Approved, AssessmentStatus.Satisfied), facts.Verdict);
                Assert.False(facts.VerdictChallenged);
                Assert.NotEmpty(facts.CoveringTasks);
            }

            Assert.Equal(column == BoardColumn.Backlog, tasks.Length == 0);
        }

        Assert.Equal(Enum.GetValues<BoardColumn>().Length, reached.Count);
    }

    [Fact]
    public void A_task_card_follows_its_status_and_the_verdict_of_its_run()
    {
        Assert.Equal(BoardColumn.Planned, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Pending, null)));
        Assert.Equal(BoardColumn.InProgress, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.AwaitingApproval, null)));
        Assert.Equal(BoardColumn.InReview, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Completed, null)));
        Assert.Equal(BoardColumn.Done, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Completed, VerdictDecision.Approved)));
        Assert.Equal(BoardColumn.Returned, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Completed, VerdictDecision.Rejected)));
        Assert.Equal(BoardColumn.Returned, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Cancelled, null)));
        Assert.Null(BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Skipped, null)));
        foreach (var status in Enum.GetValues<WorkTaskStatus>().Where(s => s != WorkTaskStatus.Completed))
        {
            Assert.NotEqual(BoardColumn.Done, BoardRules.ColumnOf(new TaskBoardFacts(status, VerdictDecision.Approved)));
        }
    }

    [Fact]
    public void The_columns_are_the_documented_ones_in_order() =>
        Assert.Equal(["Backlog", "Planned", "InProgress", "InReview", "Returned", "Done", "Integrated"], Enum.GetNames<BoardColumn>());

    // ---- budget ----

    [Fact]
    public void A_budget_needs_a_limit_and_positive_numbers()
    {
        Assert.Contains("budget.no-limit", new WorkBudget(null, null).Validate());
        Assert.Contains("budget.tokens-not-positive", new WorkBudget(0, null).Validate());
        Assert.Contains("budget.agent-runs-not-positive", new WorkBudget(null, -1).Validate());
        Assert.Contains("budget.period-unknown", new WorkBudget(1, null, (BudgetPeriod)9).Validate());
        Assert.Empty(new WorkBudget(1000, 4, BudgetPeriod.Month).Validate());
    }

    [Fact]
    public void An_attempt_that_would_pass_a_limit_does_not_launch_and_reaching_it_exactly_is_within()
    {
        var budget = new WorkBudget(1000, 3);

        Assert.True(budget.Check(new BudgetUsage(600, 1), new BudgetUsage(400, 1)).Allowed);
        Assert.Equal(new BudgetCheck(false, BudgetLimit.Tokens), budget.Check(new BudgetUsage(600, 1), new BudgetUsage(401, 1)));
        Assert.Equal(new BudgetCheck(false, BudgetLimit.AgentRuns), budget.Check(new BudgetUsage(0, 3), new BudgetUsage(10, 1)));
        Assert.True(new WorkBudget(null, 3).Check(new BudgetUsage(long.MaxValue / 2, 0), new BudgetUsage(1, 1)).Allowed);
    }
}
