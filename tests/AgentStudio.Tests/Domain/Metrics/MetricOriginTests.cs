using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Domain.Metrics;

/// <summary>
/// docs/14 D-03: every number says who gave it, with one of six origins. The creators that already existed keep their
/// meaning, and the three-level provenance the rest of the code reads is derived from the origin.
/// </summary>
public sealed class MetricOriginTests
{
    [Fact]
    public void The_six_origins_are_the_documented_ones_from_the_strongest_to_the_weakest()
    {
        Assert.Equal(
            ["ReportedByProvider", "MeasuredBySystem", "EstimatedByProvider", "EstimatedBySystem", "NotExposed", "Missing"],
            Enum.GetNames<MetricOrigin>());
    }

    [Fact]
    public void The_creators_that_already_existed_keep_their_meaning_and_their_provenance()
    {
        var exact = MetricValue<long>.Exact(5, "claude result.usage");
        var estimated = MetricValue<decimal>.Estimated(0.25m, "estimación del CLI");
        var unavailable = MetricValue<long>.Unavailable("codex exec --json does not report cost");

        Assert.Equal((MetricOrigin.ReportedByProvider, MetricProvenance.Exact, 5L), (exact.Origin, exact.Provenance, exact.Value!.Value));
        Assert.Equal((MetricOrigin.EstimatedByProvider, MetricProvenance.Estimated, 0.25m), (estimated.Origin, estimated.Provenance, estimated.Value!.Value));
        Assert.Equal((MetricOrigin.NotExposed, MetricProvenance.Unavailable), (unavailable.Origin, unavailable.Provenance));
        Assert.False(unavailable.HasValue);
    }

    [Fact]
    public void Measured_by_the_system_is_exact_and_keeps_its_value_and_source()
    {
        var measured = MetricValue<long>.Measured(1234, "agent studio: launch to exit");

        Assert.Equal((MetricOrigin.MeasuredBySystem, MetricProvenance.Exact, 1234L, "agent studio: launch to exit"), (measured.Origin, measured.Provenance, measured.Value!.Value, measured.Source));
    }

    [Fact]
    public void Estimated_by_the_system_is_an_estimate_and_never_exact()
    {
        var estimated = MetricValue<long>.EstimatedBySystem(250, "estimate: 4 chars/token");

        Assert.Equal((MetricOrigin.EstimatedBySystem, MetricProvenance.Estimated, 250L), (estimated.Origin, estimated.Provenance, estimated.Value!.Value));
    }

    [Fact]
    public void Not_exposed_and_missing_carry_no_value_and_are_told_apart()
    {
        var notExposed = MetricValue<long>.NotExposed("codex exec does not expose the context window");
        var missing = MetricValue<long>.Missing("the attempt was interrupted");

        Assert.Equal((MetricOrigin.NotExposed, MetricProvenance.Unavailable), (notExposed.Origin, notExposed.Provenance));
        Assert.Equal((MetricOrigin.Missing, MetricProvenance.Unavailable), (missing.Origin, missing.Provenance));
        Assert.False(notExposed.HasValue);
        Assert.False(missing.HasValue);
        Assert.NotEqual(MetricValue<long>.NotExposed("x"), MetricValue<long>.Missing("x"));
        Assert.Equal(MetricValue<long>.Unavailable("x"), MetricValue<long>.NotExposed("x"));
    }

    [Fact]
    public void The_new_creators_require_a_source_too()
    {
        Assert.ThrowsAny<ArgumentException>(() => MetricValue<long>.Measured(1, " "));
        Assert.ThrowsAny<ArgumentException>(() => MetricValue<long>.EstimatedBySystem(1, ""));
        Assert.ThrowsAny<ArgumentException>(() => MetricValue<long>.NotExposed(" "));
        Assert.ThrowsAny<ArgumentException>(() => MetricValue<long>.Missing(null!));
    }

    [Theory]
    [InlineData(MetricOrigin.ReportedByProvider, MetricProvenance.Exact)]
    [InlineData(MetricOrigin.MeasuredBySystem, MetricProvenance.Exact)]
    [InlineData(MetricOrigin.EstimatedByProvider, MetricProvenance.Estimated)]
    [InlineData(MetricOrigin.EstimatedBySystem, MetricProvenance.Estimated)]
    [InlineData(MetricOrigin.NotExposed, MetricProvenance.Unavailable)]
    [InlineData(MetricOrigin.Missing, MetricProvenance.Unavailable)]
    public void The_provenance_is_derived_from_the_origin(MetricOrigin origin, MetricProvenance expected)
    {
        Assert.Equal(expected, origin.ToProvenance());
        Assert.Equal(expected != MetricProvenance.Unavailable, origin.CarriesValue());
    }

    [Fact]
    public void An_origin_that_is_not_one_of_the_six_is_a_bug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((MetricOrigin)42).ToProvenance());
    }

    public static TheoryData<MetricValue<long>> EveryCreator() =>
    [
        MetricValue<long>.Exact(7, "provider"),
        MetricValue<long>.Measured(7, "system"),
        MetricValue<long>.Estimated(7, "provider estimate"),
        MetricValue<long>.EstimatedBySystem(7, "system estimate"),
        MetricValue<long>.NotExposed("not exposed"),
        MetricValue<long>.Missing("did not arrive"),
    ];

    /// <summary>What a record keeps (origin, value and source) gives back the very value it was written from.</summary>
    [Theory]
    [MemberData(nameof(EveryCreator))]
    public void A_value_rebuilt_from_its_origin_value_and_source_is_the_same_value(MetricValue<long> original)
    {
        var rebuilt = MetricValue<long>.Of(original.Origin, original.Value, original.Source);

        Assert.Equal(original, rebuilt);
    }

    [Theory]
    [InlineData(MetricOrigin.ReportedByProvider, null)]
    [InlineData(MetricOrigin.MeasuredBySystem, null)]
    [InlineData(MetricOrigin.EstimatedByProvider, null)]
    [InlineData(MetricOrigin.EstimatedBySystem, null)]
    [InlineData(MetricOrigin.NotExposed, 3L)]
    [InlineData(MetricOrigin.Missing, 3L)]
    public void Rebuilding_refuses_a_value_its_origin_cannot_have(MetricOrigin origin, long? value)
    {
        Assert.Throws<ArgumentException>(() => MetricValue<long>.Of(origin, value, "source"));
    }

    [Theory]
    [InlineData(MetricOrigin.ReportedByProvider, MetricOrigin.ReportedByProvider, MetricOrigin.ReportedByProvider)]
    [InlineData(MetricOrigin.ReportedByProvider, MetricOrigin.MeasuredBySystem, MetricOrigin.MeasuredBySystem)]
    [InlineData(MetricOrigin.ReportedByProvider, MetricOrigin.EstimatedBySystem, MetricOrigin.EstimatedBySystem)]
    [InlineData(MetricOrigin.EstimatedByProvider, MetricOrigin.EstimatedBySystem, MetricOrigin.EstimatedBySystem)]
    [InlineData(MetricOrigin.EstimatedBySystem, MetricOrigin.NotExposed, MetricOrigin.NotExposed)]
    [InlineData(MetricOrigin.NotExposed, MetricOrigin.Missing, MetricOrigin.Missing)]
    public void Of_several_origins_the_weakest_wins(MetricOrigin a, MetricOrigin b, MetricOrigin expected)
    {
        Assert.Equal(expected, MetricOrigins.Weakest(a, b));
        Assert.Equal(expected, MetricOrigins.Weakest(b, a));
    }

    [Fact]
    public void The_weakest_of_no_origin_is_missing()
    {
        Assert.Equal(MetricOrigin.Missing, MetricOrigins.Weakest());
        Assert.Equal(MetricOrigin.MeasuredBySystem, MetricOrigins.Weakest(MetricOrigin.MeasuredBySystem));
    }

    /// <summary>Combining origins never says more than combining their provenances would.</summary>
    [Fact]
    public void The_weakest_origin_and_the_combined_provenance_always_agree()
    {
        foreach (var a in Enum.GetValues<MetricOrigin>())
        {
            foreach (var b in Enum.GetValues<MetricOrigin>())
            {
                Assert.Equal(MetricValue<long>.Combine(a.ToProvenance(), b.ToProvenance()), MetricOrigins.Weakest(a, b).ToProvenance());
            }
        }
    }
}
