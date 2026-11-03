using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Domain.Metrics;

/// <summary>
/// docs/14 D-04: an aggregate never mixes origins silently (the weakest wins) and says how many values it summed and how
/// many were missing, so a sum with gaps is shown as «parcial (n de m)», never as a whole.
/// </summary>
public sealed class MetricAggregateTests
{
    [Fact]
    public void Values_reported_by_the_provider_add_up_exactly_and_keep_their_origin()
    {
        var aggregate = MetricAggregate.Sum([MetricValue<long>.Exact(100, "claude result.usage"), MetricValue<long>.Exact(23, "claude result.usage")]);

        Assert.Equal(123L, aggregate.Sum);
        Assert.Equal((MetricOrigin.ReportedByProvider, MetricProvenance.Exact), (aggregate.Origin, aggregate.Provenance));
        Assert.Equal((2, 0), (aggregate.Reported, aggregate.Missing));
        Assert.False(aggregate.IsPartial);
        Assert.Equal(["claude result.usage"], aggregate.Sources);
    }

    [Fact]
    public void One_estimate_makes_the_whole_sum_an_estimate()
    {
        var aggregate = MetricAggregate.Sum([MetricValue<long>.Exact(100, "codex turn.completed.usage"), MetricValue<long>.EstimatedBySystem(50, "estimate: 4 chars/token")]);

        Assert.Equal(150L, aggregate.Sum);
        Assert.Equal((MetricOrigin.EstimatedBySystem, MetricProvenance.Estimated), (aggregate.Origin, aggregate.Provenance));
    }

    [Fact]
    public void A_missing_value_makes_the_sum_partial_with_how_many_of_how_many()
    {
        var aggregate = MetricAggregate.Sum(
        [
            MetricValue<decimal>.Estimated(0.10m, "claude total_cost_usd"),
            MetricValue<decimal>.Missing("the attempt was interrupted"),
            MetricValue<decimal>.Estimated(0.05m, "claude total_cost_usd"),
        ]);

        Assert.Equal(0.15m, aggregate.Sum);
        Assert.Equal(MetricOrigin.EstimatedByProvider, aggregate.Origin);
        Assert.Equal((2, 1, 3), (aggregate.Reported, aggregate.Missing, aggregate.Count));
        Assert.True(aggregate.IsPartial);
        Assert.Equal(["claude total_cost_usd", "the attempt was interrupted"], aggregate.Sources);
    }

    [Fact]
    public void With_no_value_at_all_there_is_no_sum_and_the_weakest_reason_is_kept()
    {
        var aggregate = MetricAggregate.Sum([MetricValue<decimal>.NotExposed("codex exec --json does not report cost"), MetricValue<decimal>.Missing("interrupted")]);

        Assert.Null(aggregate.Sum);
        Assert.Equal((MetricOrigin.Missing, MetricProvenance.Unavailable), (aggregate.Origin, aggregate.Provenance));
        Assert.Equal((0, 2), (aggregate.Reported, aggregate.Missing));
        Assert.False(aggregate.IsPartial);
    }

    [Fact]
    public void A_figure_that_is_never_exposed_stays_not_exposed()
    {
        var aggregate = MetricAggregate.Sum([MetricValue<long>.NotExposed("codex exec"), MetricValue<long>.NotExposed("codex exec")]);

        Assert.Null(aggregate.Sum);
        Assert.Equal(MetricOrigin.NotExposed, aggregate.Origin);
    }

    [Fact]
    public void Nothing_to_add_up_is_no_sum_rather_than_zero()
    {
        var aggregate = MetricAggregate.Sum(Array.Empty<MetricValue<long>>());

        Assert.Null(aggregate.Sum);
        Assert.Equal((MetricOrigin.Missing, 0, 0), (aggregate.Origin, aggregate.Reported, aggregate.Missing));
        Assert.Empty(aggregate.Sources);
    }

    [Fact]
    public void A_zero_that_was_reported_is_a_sum_of_zero_not_a_missing_value()
    {
        var aggregate = MetricAggregate.Sum([MetricValue<long>.Exact(0, "codex turn.completed.usage")]);

        Assert.Equal(0L, aggregate.Sum);
        Assert.Equal((1, 0), (aggregate.Reported, aggregate.Missing));
    }

    [Fact]
    public void The_sources_are_listed_once_in_a_fixed_order()
    {
        var aggregate = MetricAggregate.Sum(
        [
            MetricValue<long>.Exact(1, "b source"),
            MetricValue<long>.Measured(1, "a source"),
            MetricValue<long>.Exact(1, "b source"),
        ]);

        Assert.Equal(["a source", "b source"], aggregate.Sources);
        Assert.Equal(MetricOrigin.MeasuredBySystem, aggregate.Origin);
    }

    [Fact]
    public void A_sum_that_does_not_fit_is_an_error_not_a_wrapped_number()
    {
        Assert.Throws<OverflowException>(() => MetricAggregate.Sum([MetricValue<long>.Exact(long.MaxValue, "x"), MetricValue<long>.Exact(1, "x")]));
    }

    [Fact]
    public void An_aggregate_that_contradicts_itself_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MetricAggregate<long>(null, MetricOrigin.ReportedByProvider, 1, 0, []));
        Assert.Throws<ArgumentException>(() => new MetricAggregate<long>(5, MetricOrigin.Missing, 0, 1, []));
        Assert.Throws<ArgumentException>(() => new MetricAggregate<long>(5, MetricOrigin.ReportedByProvider, 0, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricAggregate<long>(null, MetricOrigin.Missing, 0, -1, []));
        Assert.Throws<ArgumentNullException>(() => new MetricAggregate<long>(null, MetricOrigin.Missing, 0, 0, null!));
    }

    [Fact]
    public void Summing_needs_the_values()
    {
        Assert.Throws<ArgumentNullException>(() => MetricAggregate.Sum<long>(null!));
    }
}
