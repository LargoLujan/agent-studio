using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Models;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Observability.Estimates;
using AgentStudio.Observability.Usage;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Observability.Usage.UsageAttempts;

namespace AgentStudio.Tests.Observability.Estimates;

/// <summary>
/// docs/16 D-20 and F9-7, on top of the estimator of Phase 13 (ADR-0050): the estimate from the history of one activity or milestone,
/// and the real against the estimated. Never a comparison with a number made up.
/// </summary>
public sealed class ScopedEstimationTests
{
    private static readonly ModelCatalog Catalog = BuiltInModelCatalog.Shipped.Catalog;

    private static readonly UsageEstimateRequest Request = new(new ProjectContext("p1", "C:\\repo"), new AgentId("ejecutor"), ProviderId.ClaudeCode, "claude-sonnet-5-5", AgentRunPurpose.Executor);

    private static UsageAttempt Of(ActivityKind kind, long input, string run, params string[] milestones) =>
        Make(T0, run: run, own: Tokens(input, 10), activity: new ActivityClass(kind, AttemptOrder.First, AttemptFate.Kept, true), milestones: milestones);

    [Fact]
    public void The_estimate_of_an_activity_uses_only_the_attempts_of_that_activity()
    {
        var history = new[]
        {
            Of(ActivityKind.Tests, 100, "a"), Of(ActivityKind.Tests, 200, "b"), Of(ActivityKind.Tests, 300, "c"),
            Of(ActivityKind.Implementation, 9_000, "d"), Of(ActivityKind.Implementation, 9_100, "e"), Of(ActivityKind.Implementation, 9_200, "f"),
        };

        var tests = ScopedEstimation.EstimateForActivity(history, Catalog, Request, ActivityKind.Tests);
        var code = ScopedEstimation.EstimateForActivity(history, Catalog, Request, ActivityKind.Implementation);

        Assert.Equal(3, tests.References);
        Assert.True(tests.IsKnown);
        Assert.InRange(tests.InputTokens.High.Value!.Value, 100, 300);
        Assert.InRange(code.InputTokens.Low.Value!.Value, 9_000, 9_200);
    }

    [Fact]
    public void An_activity_or_a_milestone_with_fewer_than_three_attempts_is_unknown_and_never_a_number_made_up()
    {
        var history = new[] { Of(ActivityKind.Review, 100, "a", "mil-a"), Of(ActivityKind.Review, 200, "b", "mil-a"), Of(ActivityKind.Tests, 300, "c", "mil-b") };

        var review = ScopedEstimation.EstimateForActivity(history, Catalog, Request, ActivityKind.Review);
        var milestone = ScopedEstimation.EstimateForMilestone(history, Catalog, Request, "mil-a");

        Assert.False(review.IsKnown);
        Assert.Equal(2, review.References);
        Assert.False(review.InputTokens.Low.HasValue);
        Assert.False(milestone.IsKnown);
    }

    [Fact]
    public void The_estimate_of_a_milestone_counts_an_attempt_shared_with_another_in_each()
    {
        var history = new[] { Of(ActivityKind.Tests, 100, "a", "mil-a", "mil-b"), Of(ActivityKind.Tests, 200, "b", "mil-a"), Of(ActivityKind.Tests, 300, "c", "mil-a", "mil-b"), Of(ActivityKind.Tests, 400, "d") };

        Assert.Equal(3, ScopedEstimation.EstimateForMilestone(history, Catalog, Request, "mil-a").References);
        Assert.Equal(2, ScopedEstimation.EstimateForMilestone(history, Catalog, Request, "mil-b").References);
    }

    private static PlanEstimate Plan(EstimateInterval<long> input, EstimateInterval<long> output, EstimateInterval<long> runs) =>
        new(new Dictionary<TaskId, UsageEstimate>(), runs, input, output, EstimateInterval<TimeSpan>.Unknown("n/a"), []);

    private static EstimateInterval<long> Known(long low, long high) =>
        new(MetricValue<long>.EstimatedBySystem(low, "p25"), MetricValue<long>.EstimatedBySystem(high, "p75"));

    private static UsageTotals Actual(params UsageAttempt[] attempts) => UsageAggregator.Total(attempts);

    [Fact]
    public void What_was_spent_is_inside_below_or_above_the_interval_of_the_plan()
    {
        var plan = Plan(Known(1_000, 2_000), Known(100, 200), Known(2, 4));

        Assert.Equal(
            new EstimateDeviations(EstimateDeviation.Within, EstimateDeviation.Within, EstimateDeviation.Within),
            ScopedEstimation.CompareWithPlan(plan, Actual(Make(T0, own: Tokens(1_000, 100)), Make(T0, own: Tokens(500, 50)))));
        Assert.Equal(EstimateDeviation.Below, ScopedEstimation.CompareWithPlan(plan, Actual(Make(T0, own: Tokens(900, 10)), Make(T0, own: Tokens(50, 10)))).InputTokens);
        Assert.Equal(EstimateDeviation.Above, ScopedEstimation.CompareWithPlan(plan, Actual(Make(T0, own: Tokens(2_001, 10)))).InputTokens);
        Assert.Equal(EstimateDeviation.Below, ScopedEstimation.CompareWithPlan(plan, Actual(Make(T0))).AgentRuns);
        Assert.Equal(EstimateDeviation.Above, ScopedEstimation.CompareWithPlan(plan, Actual(Make(T0), Make(T0), Make(T0), Make(T0), Make(T0))).AgentRuns);
    }

    [Fact]
    public void An_interval_that_is_not_known_or_a_total_with_gaps_is_compared_with_nothing()
    {
        var unknown = Plan(EstimateInterval<long>.Unknown("unknown: 2 of 3 references"), Known(1, 2), Known(1, 2));
        var known = Plan(Known(1_000, 2_000), Known(1, 2), Known(1, 2));

        var gaps = Actual(Make(T0, own: Tokens(10, 1)), Make(T0, own: NoFigures()));

        Assert.Equal(EstimateDeviation.Unknown, ScopedEstimation.CompareWithPlan(unknown, Actual(Make(T0, own: Tokens(1_500, 1)))).InputTokens);
        Assert.Equal(EstimateDeviation.Unknown, ScopedEstimation.CompareWithPlan(known, gaps).InputTokens);
        Assert.Equal(EstimateDeviation.Unknown, ScopedEstimation.CompareWithPlan(known, gaps).OutputTokens);
        Assert.Equal(EstimateDeviation.Unknown, ScopedEstimation.Compare(known.InputTokens, null));
    }
}
