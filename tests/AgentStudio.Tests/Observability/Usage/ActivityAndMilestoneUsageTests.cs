using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Observability.Usage;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Observability.Usage.UsageAttempts;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// docs/16 D-34 and F9-6 (ADR-0040): the consumption by milestone and by the three axes of the activity, with the waits apart. The
/// groups of an axis add up to the total; an attempt of two milestones is in each and once in the total.
/// </summary>
public sealed class ActivityAndMilestoneUsageTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly MilestoneId MilA = new("mil-20261110-aaaaaa");
    private static readonly MilestoneId MilB = new("mil-20261110-bbbbbb");

    private static UsageQuery Query(params UsageDimension[] groupBy) => new(T0.AddYears(-1), T0.AddYears(1), Utc, groupBy);

    private static IReadOnlyList<UsageAttempt> Project(UsageLogBuilder log) => UsageProjector.Project(UsageLogBuilder.ProjectId, log.Entries).Attempts;

    private static UsageLogBuilder RunStart(UsageLogBuilder log, string run, string? follows = null) =>
        log.Record(DecisionKind.RunStarted, new RunStartedPayload(HashA, HashA, "c0ffee", null, HashA, [], follows) { RunId = run });

    private static UsageLogBuilder Link(UsageLogBuilder log, MilestoneId milestone, string run) =>
        log.Record(DecisionKind.MilestoneLinked, MilestoneLinkedPayload.Linking(milestone, MilestoneTargetKind.Run, run, "agent-studio"));

    private static UsageLogBuilder Scope(UsageLogBuilder log, string run, string task, params string[] scope) =>
        log.Record(DecisionKind.InstructionsIssued, new InstructionsIssuedPayload(new TaskId(task), 1, new EvidenceId("ev-prompt-1"), HashA, [], scope, []) { RunId = run });

    private static UsageLogBuilder Status(UsageLogBuilder log, string run, string task, WorkTaskStatus from, WorkTaskStatus to, int attempt, string? block = null) =>
        log.Record(DecisionKind.TaskStatusChanged, new TaskStatusChangedPayload(new TaskId(task), from, to, "test", block, [], attempt) { RunId = run });

    private static UsageLogBuilder Merged(UsageLogBuilder log, string run, string task) =>
        log.Record(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(new TaskId(task), "integration", ["ref"], "abc123", [], false) { RunId = run });

    private static UsageLogBuilder Delivered(UsageLogBuilder log, string run) =>
        log.Record(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, "main", ["integration"], "def456", [], true) { RunId = run });

    private static UsageLogBuilder Finished(UsageLogBuilder log, string run, RunPhase phase) =>
        log.Record(DecisionKind.RunFinished, new RunFinishedPayload(phase, "fin", null, null) { RunId = run });

    private static UsageLogBuilder Attempt(UsageLogBuilder log, string run, string? task, int attempt, AgentRunPurpose purpose, AttemptOutcome outcome = AttemptOutcome.Succeeded) =>
        log.Start(run, task, attempt, purpose: purpose).Finish(run, task, attempt, outcome, launchedAt: log.Now.AddSeconds(1));

    [Fact]
    public void A_delivered_run_keeps_the_attempts_of_its_completed_task_and_classifies_them_on_the_three_axes()
    {
        var log = new UsageLogBuilder();
        RunStart(log, "R1");
        Link(log, MilA, "R1");
        Attempt(log, "R1", null, 1, AgentRunPurpose.Planner);
        Scope(log, "R1", "T1", "tests/Calc/");
        Attempt(log, "R1", "T1", 1, AgentRunPurpose.Executor, AttemptOutcome.Failed);
        Status(log, "R1", "T1", WorkTaskStatus.Running, WorkTaskStatus.Failed, 1);
        Attempt(log, "R1", "T1", 2, AgentRunPurpose.Executor);
        Status(log, "R1", "T1", WorkTaskStatus.Running, WorkTaskStatus.Completed, 2);
        Merged(log, "R1", "T1");
        Attempt(log, "R1", null, 1, AgentRunPurpose.Judge);
        Delivered(log, "R1");
        Finished(log, "R1", RunPhase.Succeeded);

        var attempts = Project(log);

        Assert.Equal(4, attempts.Count);
        var planner = attempts.Single(a => a.Purpose == AgentRunPurpose.Planner);
        Assert.Equal(new ActivityClass(ActivityKind.Coordination, AttemptOrder.First, AttemptFate.Kept, false), planner.Activity);
        var first = attempts.Single(a => a.TaskId == "T1" && a.Attempt == 1);
        var retry = attempts.Single(a => a.TaskId == "T1" && a.Attempt == 2);
        Assert.Equal(new ActivityClass(ActivityKind.Tests, AttemptOrder.First, AttemptFate.Kept, true), first.Activity);
        Assert.Equal(new ActivityClass(ActivityKind.Tests, AttemptOrder.Retry, AttemptFate.Kept, true), retry.Activity);
        Assert.Equal(ActivityKind.Review, attempts.Single(a => a.Purpose == AgentRunPurpose.Judge).Activity!.Kind);
        Assert.All(attempts, a => Assert.Equal(["mil-20261110-aaaaaa"], a.Milestones));
    }

    [Fact]
    public void The_scope_of_the_task_tells_code_from_tests_and_documentation()
    {
        var log = new UsageLogBuilder();
        RunStart(log, "R1");
        foreach (var (task, scope) in new[] { ("code", new[] { "src/Calc/", "tests/Calc/" }), ("docs", ["docs/", "README.md"]), ("tests", ["tests/"]), ("both", ["docs/", "tests/"]) })
        {
            Scope(log, "R1", task, scope);
            Attempt(log, "R1", task, 1, AgentRunPurpose.Executor);
            Status(log, "R1", task, WorkTaskStatus.Running, WorkTaskStatus.Completed, 1);
        }

        Delivered(log, "R1");
        Finished(log, "R1", RunPhase.Succeeded);

        var kinds = Project(log).ToDictionary(a => a.TaskId!, a => a.Activity!.Kind);

        Assert.Equal(ActivityKind.Implementation, kinds["code"]);
        Assert.Equal(ActivityKind.Documentation, kinds["docs"]);
        Assert.Equal(ActivityKind.Tests, kinds["tests"]);
        Assert.Equal(ActivityKind.Tests, kinds["both"]);
    }

    [Fact]
    public void A_failed_task_and_a_dead_run_are_discarded_and_a_run_in_progress_is_pending()
    {
        var log = new UsageLogBuilder();
        RunStart(log, "dead");
        Scope(log, "dead", "T1", "src/");
        Attempt(log, "dead", "T1", 1, AgentRunPurpose.Executor, AttemptOutcome.Failed);
        Status(log, "dead", "T1", WorkTaskStatus.Running, WorkTaskStatus.Failed, 1);
        Finished(log, "dead", RunPhase.Failed);
        RunStart(log, "live");
        Scope(log, "live", "T1", "src/");
        Attempt(log, "live", "T1", 1, AgentRunPurpose.Executor);
        Status(log, "live", "T1", WorkTaskStatus.Running, WorkTaskStatus.Completed, 1);
        RunStart(log, "waiting");
        Scope(log, "waiting", "T1", "src/");
        Attempt(log, "waiting", "T1", 1, AgentRunPurpose.Executor, AttemptOutcome.Failed);
        Status(log, "waiting", "T1", WorkTaskStatus.Running, WorkTaskStatus.Failed, 1);

        var fates = Project(log).ToDictionary(a => a.RunId, a => a.Activity!.Fate);

        Assert.Equal(AttemptFate.Discarded, fates["dead"]);
        Assert.Equal(AttemptFate.Pending, fates["live"]);
        Assert.Equal(AttemptFate.Discarded, fates["waiting"]);
    }

    [Fact]
    public void A_finished_run_that_was_never_delivered_is_pending_not_discarded()
    {
        var log = new UsageLogBuilder();
        RunStart(log, "R1");
        Scope(log, "R1", "T1", "src/");
        Attempt(log, "R1", "T1", 1, AgentRunPurpose.Executor);
        Status(log, "R1", "T1", WorkTaskStatus.Running, WorkTaskStatus.Completed, 1);
        Finished(log, "R1", RunPhase.Succeeded);

        Assert.Equal(AttemptFate.Pending, Project(log).Single().Activity!.Fate);
    }

    [Fact]
    public void A_follow_up_after_a_conflict_is_conflicts_and_shares_the_milestone_of_the_run_it_follows()
    {
        var log = new UsageLogBuilder();
        RunStart(log, "R1");
        Link(log, MilA, "R1");
        log.Record(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, "integration", ["a", "b"], null, ["src/a.cs"], false) { RunId = "R1" });
        Finished(log, "R1", RunPhase.Failed);
        RunStart(log, "R2", "R1");
        Scope(log, "R2", "T1", "src/");
        Attempt(log, "R2", "T1", 1, AgentRunPurpose.Executor);
        RunStart(log, "R3", "R2");
        Scope(log, "R3", "T1", "src/");
        Attempt(log, "R3", "T1", 1, AgentRunPurpose.Executor);

        var attempts = Project(log).ToDictionary(a => a.RunId);

        Assert.Equal(ActivityKind.Conflicts, attempts["R2"].Activity!.Kind);
        Assert.Equal(ActivityKind.Implementation, attempts["R3"].Activity!.Kind); // R2 had no conflict of its own
        Assert.Equal(["mil-20261110-aaaaaa"], attempts["R2"].Milestones);
        Assert.Equal(["mil-20261110-aaaaaa"], attempts["R3"].Milestones); // two follow-ups deep
    }

    [Fact]
    public void A_debate_or_an_auxiliary_run_is_kept_when_it_ended_well_and_discarded_when_it_did_not()
    {
        var log = new UsageLogBuilder();
        Attempt(log, "deb-1", "r1-a", 1, AgentRunPurpose.DebateParticipant);
        Attempt(log, "deb-1", "r1-b", 1, AgentRunPurpose.DebateParticipant, AttemptOutcome.Failed);
        Attempt(log, "aux-1", null, 1, AgentRunPurpose.DailySummary);

        var attempts = Project(log);

        Assert.Equal([(ActivityKind.Analysis, AttemptFate.Kept), (ActivityKind.Analysis, AttemptFate.Discarded), (ActivityKind.Documentation, AttemptFate.Kept)], attempts.Select(a => (a.Activity!.Kind, a.Activity.Fate)));
    }

    [Fact]
    public void The_wait_for_quota_is_measured_in_the_log_apart_from_the_duration_and_an_open_one_has_no_length()
    {
        var log = new UsageLogBuilder();
        RunStart(log, "R1");
        Scope(log, "R1", "T1", "src/");
        Attempt(log, "R1", "T1", 1, AgentRunPurpose.Executor, AttemptOutcome.RateLimited);
        Status(log, "R1", "T1", WorkTaskStatus.Running, WorkTaskStatus.Blocked, 1, "RateLimitedUntil(2026-11-10T11:00:00.0000000+00:00)");
        log.Advance(TimeSpan.FromMinutes(10));
        Status(log, "R1", "T1", WorkTaskStatus.Blocked, WorkTaskStatus.Pending, 1);
        Attempt(log, "R1", "T2", 1, AgentRunPurpose.Executor, AttemptOutcome.RateLimited);
        Status(log, "R1", "T2", WorkTaskStatus.Running, WorkTaskStatus.Blocked, 1, "RateLimitedUntilEstimated(2026-11-10T11:00:00.0000000+00:00)");

        var attempts = Project(log);

        var waited = attempts.Single(a => a.TaskId == "T1");
        Assert.Equal(600_000, waited.QuotaWaitMilliseconds);
        Assert.Equal(600_000L, waited.QuotaWait().Value);
        var open = attempts.Single(a => a.TaskId == "T2");
        Assert.True(open.OpenQuotaWait);
        Assert.False(open.QuotaWait().HasValue);
        Assert.Equal(MetricOrigin.Missing, open.QuotaWait().Origin);
        Assert.True(UsageAggregator.Total(attempts).QuotaWaitMilliseconds!.IsPartial);
    }

    [Fact]
    public void The_wait_in_the_queue_is_the_time_between_the_start_and_the_launch_and_is_missing_without_a_launch()
    {
        var queued = Make(T0.AddMinutes(1), queued: TimeSpan.FromSeconds(7));
        var never = Make(T0.AddMinutes(1), launched: false);

        Assert.Equal(7_000L, queued.QueueWait().Value);
        Assert.False(never.QueueWait().HasValue);
        Assert.Equal(7_000L, UsageAggregator.Total([queued, never]).QueueWaitMilliseconds!.Sum);
    }

    [Fact]
    public void Grouping_by_an_axis_of_the_activity_adds_up_to_the_total()
    {
        var attempts = new[]
        {
            Make(T0, run: "a", activity: new ActivityClass(ActivityKind.Implementation, AttemptOrder.First, AttemptFate.Kept, true)),
            Make(T0, run: "b", activity: new ActivityClass(ActivityKind.Tests, AttemptOrder.Retry, AttemptFate.Discarded, true)),
            Make(T0, run: "c", activity: new ActivityClass(ActivityKind.Tests, AttemptOrder.First, AttemptFate.Discarded, false)),
            Make(T0, run: "d", activity: new ActivityClass(ActivityKind.Review, AttemptOrder.First, AttemptFate.Pending, false)),
            Make(T0, run: "e"),
        };

        foreach (var axis in new[] { UsageDimension.Activity, UsageDimension.ActivityAttempt, UsageDimension.ActivityDestination })
        {
            var groups = UsageAggregator.Group(attempts, Query(axis));
            Assert.Equal(attempts.Length, groups[0].Totals.Attempts);
            Assert.Equal(attempts.Length, groups.Skip(1).Sum(g => g.Totals.Attempts));
        }

        var destinations = UsageAggregator.Group(attempts, Query(UsageDimension.ActivityDestination)).Skip(1).ToDictionary(g => g.Key[0].Value, g => g.Totals.Attempts);
        Assert.Equal(1, destinations[UsageAggregator.DiscardedWithCommit]);
        Assert.Equal(1, destinations["Discarded"]);
        Assert.Equal(1, destinations["Kept"]);
        Assert.Equal(1, destinations["Pending"]);
        Assert.Equal(1, destinations[UsageAggregator.NoActivity]);
    }

    [Fact]
    public void An_attempt_of_two_milestones_is_in_each_group_marked_shared_and_once_in_the_total()
    {
        var attempts = new[]
        {
            Make(T0, run: "a", milestones: ["mil-a"]),
            Make(T0, run: "b", milestones: ["mil-a", "mil-b"]),
            Make(T0, run: "c"),
        };

        var groups = UsageAggregator.Group(attempts, Query(UsageDimension.Milestone));

        Assert.Equal(3, groups[0].Totals.Attempts);
        var byKey = groups.Skip(1).ToDictionary(g => g.Key[0].Value);
        Assert.Equal(2, byKey["mil-a"].Totals.Attempts);
        Assert.Equal(1, byKey["mil-a"].SharedAttempts);
        Assert.Equal(1, byKey["mil-b"].Totals.Attempts);
        Assert.Equal(1, byKey["mil-b"].SharedAttempts);
        Assert.Equal(1, byKey[UsageAggregator.NoMilestone].Totals.Attempts);
        Assert.Equal(0, byKey[UsageAggregator.NoMilestone].SharedAttempts);
        Assert.Equal(4, byKey.Values.Sum(g => g.Totals.Attempts)); // total + exactly what is shared
        Assert.Equal(0, groups[0].SharedAttempts);
    }

    [Fact]
    public void A_shared_attempt_crosses_with_the_other_dimensions_and_a_query_without_milestones_counts_it_once()
    {
        var shared = Make(T0, run: "x", milestones: ["mil-a", "mil-b"], provider: null);

        var crossed = UsageAggregator.Group([shared], Query(UsageDimension.Milestone, UsageDimension.Provider)).Skip(1).ToList();
        var plain = UsageAggregator.Group([shared], Query(UsageDimension.Provider));

        Assert.Equal(2, crossed.Count);
        Assert.Equal(["mil-a", "mil-b"], crossed.Select(g => g.Key[0].Value));
        Assert.Equal(1, plain.Skip(1).Sum(g => g.Totals.Attempts));
        Assert.Equal(0, plain[1].SharedAttempts);
        Assert.Equal("mil-a", UsageAggregator.KeyOf(shared, UsageDimension.Milestone, Utc));
    }

    [Fact]
    public void Every_dimension_has_a_key_and_none_throws()
    {
        var attempt = Make(T0, activity: new ActivityClass(ActivityKind.Review, AttemptOrder.Retry, AttemptFate.Kept, false));

        foreach (var dimension in Enum.GetValues<UsageDimension>())
        {
            Assert.NotEmpty(UsageAggregator.KeyOf(attempt, dimension, Utc));
        }
    }

    [Fact]
    public async Task The_report_groups_the_log_by_milestone_and_activity_end_to_end()
    {
        var log = new UsageLogBuilder();
        RunStart(log, "R1");
        Link(log, MilA, "R1");
        Link(log, MilB, "R1");
        Scope(log, "R1", "T1", "src/");
        Attempt(log, "R1", "T1", 1, AgentRunPurpose.Executor);
        Status(log, "R1", "T1", WorkTaskStatus.Running, WorkTaskStatus.Completed, 1);
        Delivered(log, "R1");
        Finished(log, "R1", RunPhase.Succeeded);
        Attempt(log, "aux", null, 1, AgentRunPurpose.SkillRecommendation);
        var projector = new UsageProjector(new FixedLogReader(UsageLogBuilder.ProjectId, log.Entries), new NoStorage());

        var report = await projector.QueryAsync(["C:\\proyecto"], Query(UsageDimension.Milestone, UsageDimension.Activity), TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Groups[0].Totals.Attempts);
        Assert.Equal(
            [("mil-20261110-aaaaaa", "Implementation", 1), ("mil-20261110-bbbbbb", "Implementation", 1), (UsageAggregator.NoMilestone, "Analysis", 1)],
            report.Groups.Skip(1).Select(g => (g.Key[0].Value, g.Key[1].Value, g.Totals.Attempts)));
    }

    private sealed class FixedLogReader(string projectId, IReadOnlyList<DecisionEntry> entries) : IProjectLogReader
    {
        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct) =>
            Task.FromResult(new ProjectLogSnapshot(projectId, projectRoot, entries, DecisionLogVerification.Valid, AnchorStatus.Anchored));
    }

    private sealed class NoStorage : IProjectStorageReader
    {
        public Stream OpenForRead(string projectRoot, string relativePath) => throw new FileNotFoundException("No journal.", relativePath);
    }
}
