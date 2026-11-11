using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Usage;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>docs/16 D-34 (ADR-0040): the classification of an attempt is a pure function of what the log says; no agent is asked.</summary>
public sealed class ActivityClassifierTests
{
    private static ActivityInput Input(
        AgentRunPurpose? purpose,
        int attempt = 1,
        AttemptOutcome outcome = AttemptOutcome.Succeeded,
        ActivityRunFacts? run = null,
        ActivityTaskFacts? task = null,
        bool? hasTask = null) =>
        new(purpose, hasTask ?? task is not null, attempt, outcome, run, task);

    [Theory]
    [InlineData(AgentRunPurpose.Planner, ActivityKind.Coordination)]
    [InlineData(AgentRunPurpose.OrderReview, ActivityKind.Coordination)]
    [InlineData(AgentRunPurpose.PreparationChat, ActivityKind.Coordination)]
    [InlineData(AgentRunPurpose.DebateParticipant, ActivityKind.Analysis)]
    [InlineData(AgentRunPurpose.SkillRecommendation, ActivityKind.Analysis)]
    [InlineData(AgentRunPurpose.ConsumptionAnalysis, ActivityKind.Analysis)]
    [InlineData(AgentRunPurpose.Help, ActivityKind.Analysis)]
    [InlineData(AgentRunPurpose.Judge, ActivityKind.Review)]
    [InlineData(AgentRunPurpose.DebateJudge, ActivityKind.Review)]
    [InlineData(AgentRunPurpose.ProjectAudit, ActivityKind.Review)]
    [InlineData(AgentRunPurpose.DailySummary, ActivityKind.Documentation)]
    [InlineData(AgentRunPurpose.ClientDocumentDraft, ActivityKind.Documentation)]
    public void The_purpose_decides_what_the_attempt_was_for(AgentRunPurpose purpose, ActivityKind expected) =>
        Assert.Equal(expected, ActivityClassifier.KindOf(Input(purpose)));

    [Fact]
    public void Every_purpose_has_a_kind()
    {
        foreach (var purpose in Enum.GetValues<AgentRunPurpose>())
        {
            Assert.True(Enum.IsDefined(ActivityClassifier.KindOf(Input(purpose))), purpose.ToString());
        }
    }

    [Theory]
    [InlineData(new[] { "src/A.cs" }, ActivityKind.Implementation)]
    [InlineData(new[] { "src/A.cs", "tests/A.cs" }, ActivityKind.Implementation)]
    [InlineData(new[] { "tests/A.cs" }, ActivityKind.Tests)]
    [InlineData(new[] { "docs/a.md", "README.md" }, ActivityKind.Documentation)]
    [InlineData(new[] { "tests/A.cs", "docs/a.md" }, ActivityKind.Tests)]
    [InlineData(new string[0], ActivityKind.Implementation)]
    public void An_executor_is_told_by_the_paths_it_touches(string[] paths, ActivityKind expected) =>
        Assert.Equal(expected, ActivityClassifier.KindOf(Input(AgentRunPurpose.Executor, task: new ActivityTaskFacts(WorkTaskStatus.Completed, paths, false))));

    [Fact]
    public void The_test_paths_of_the_profile_replace_the_default()
    {
        var input = Input(AgentRunPurpose.Executor, task: new ActivityTaskFacts(WorkTaskStatus.Completed, ["spec/A.cs"], false));

        Assert.Equal(ActivityKind.Implementation, ActivityClassifier.KindOf(input));
        Assert.Equal(ActivityKind.Tests, ActivityClassifier.KindOf(input, ["spec/"]));
    }

    [Fact]
    public void A_start_without_a_purpose_is_an_executor_with_a_task_and_the_planner_without()
    {
        Assert.Equal(ActivityKind.Implementation, ActivityClassifier.KindOf(Input(null, task: new ActivityTaskFacts(null, ["src/"], false))));
        Assert.Equal(ActivityKind.Coordination, ActivityClassifier.KindOf(Input(null, hasTask: false)));
    }

    [Fact]
    public void An_executor_of_a_follow_up_after_a_conflict_is_conflicts_whatever_its_paths()
    {
        var input = Input(AgentRunPurpose.Executor, run: new ActivityRunFacts(ActivityRunState.Open, FollowsConflict: true), task: new ActivityTaskFacts(null, ["tests/"], false));

        Assert.Equal(ActivityKind.Conflicts, ActivityClassifier.KindOf(input));
    }

    [Fact]
    public void The_order_is_first_or_retry_and_the_correction_of_the_planner_is_a_retry()
    {
        Assert.Equal(AttemptOrder.First, ActivityClassifier.Classify(Input(AgentRunPurpose.Executor)).Order);
        Assert.Equal(AttemptOrder.Retry, ActivityClassifier.Classify(Input(AgentRunPurpose.Executor, attempt: 2)).Order);
        Assert.Equal(AttemptOrder.Retry, ActivityClassifier.Classify(Input(AgentRunPurpose.Planner, attempt: 2)).Order);
    }

    [Theory]
    [InlineData(ActivityRunState.Delivered, WorkTaskStatus.Completed, AttemptFate.Kept)]
    [InlineData(ActivityRunState.Delivered, WorkTaskStatus.Failed, AttemptFate.Discarded)]
    [InlineData(ActivityRunState.Delivered, WorkTaskStatus.Skipped, AttemptFate.Discarded)]
    [InlineData(ActivityRunState.Dead, WorkTaskStatus.Completed, AttemptFate.Discarded)]
    [InlineData(ActivityRunState.Open, WorkTaskStatus.Running, AttemptFate.Pending)]
    [InlineData(ActivityRunState.Open, WorkTaskStatus.Completed, AttemptFate.Pending)]
    [InlineData(ActivityRunState.Open, WorkTaskStatus.Cancelled, AttemptFate.Discarded)]
    [InlineData(ActivityRunState.Undelivered, WorkTaskStatus.Completed, AttemptFate.Pending)]
    public void The_destination_follows_the_run_and_the_task(ActivityRunState state, WorkTaskStatus status, AttemptFate expected) =>
        Assert.Equal(expected, ActivityClassifier.FateOf(Input(AgentRunPurpose.Executor, run: new ActivityRunFacts(state), task: new ActivityTaskFacts(status, null, false))));

    [Fact]
    public void An_attempt_of_the_run_itself_follows_the_run_and_a_unit_without_a_run_follows_its_outcome()
    {
        Assert.Equal(AttemptFate.Kept, ActivityClassifier.FateOf(Input(AgentRunPurpose.Judge, run: new ActivityRunFacts(ActivityRunState.Delivered), hasTask: false)));
        Assert.Equal(AttemptFate.Discarded, ActivityClassifier.FateOf(Input(AgentRunPurpose.Judge, run: new ActivityRunFacts(ActivityRunState.Dead), hasTask: false)));
        Assert.Equal(AttemptFate.Kept, ActivityClassifier.FateOf(Input(AgentRunPurpose.DebateParticipant)));
        Assert.Equal(AttemptFate.Discarded, ActivityClassifier.FateOf(Input(AgentRunPurpose.DebateParticipant, outcome: AttemptOutcome.Failed)));
    }

    [Fact]
    public void An_attempt_whose_work_was_merged_has_a_commit()
    {
        Assert.True(ActivityClassifier.Classify(Input(AgentRunPurpose.Executor, task: new ActivityTaskFacts(WorkTaskStatus.Failed, null, true))).HasCommit);
        Assert.False(ActivityClassifier.Classify(Input(AgentRunPurpose.Planner)).HasCommit);
    }

    [Fact]
    public void The_classification_is_deterministic()
    {
        var input = Input(AgentRunPurpose.Executor, attempt: 3, run: new ActivityRunFacts(ActivityRunState.Open), task: new ActivityTaskFacts(WorkTaskStatus.Running, ["docs/x.md"], false));

        Assert.Equal(ActivityClassifier.Classify(input), ActivityClassifier.Classify(input));
    }
}
