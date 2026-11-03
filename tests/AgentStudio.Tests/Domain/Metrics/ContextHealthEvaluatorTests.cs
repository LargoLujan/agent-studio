using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Domain.Metrics;

public sealed class ContextHealthEvaluatorTests
{
    private static readonly ContextHealthEvaluator Evaluator = new(new ContextHealthThresholds());

    private static MetricValue<long> Exact(long value, string source = "provider usage") => MetricValue<long>.Exact(value, source);

    private static MetricValue<long> Estimated(long value, string source = "estimate: 4 chars/token") => MetricValue<long>.Estimated(value, source);

    private static MetricValue<long> Missing => MetricValue<long>.Unavailable("not exposed");

    [Theory]
    [InlineData(0, ContextHealthLevel.Healthy)]
    [InlineData(699, ContextHealthLevel.Healthy)]
    [InlineData(700, ContextHealthLevel.Warning)]
    [InlineData(849, ContextHealthLevel.Warning)]
    [InlineData(850, ContextHealthLevel.CompressionRecommended)]
    [InlineData(949, ContextHealthLevel.CompressionRecommended)]
    [InlineData(950, ContextHealthLevel.Critical)]
    [InlineData(1000, ContextHealthLevel.Critical)]
    [InlineData(1500, ContextHealthLevel.Critical)] // over the window is worse than critical, not "unknown"
    public void The_level_follows_the_default_thresholds(long used, ContextHealthLevel expected)
    {
        var report = Evaluator.Evaluate(Exact(used), Exact(1000));

        Assert.Equal(expected, report.Level);
        Assert.Equal(used / 1000.0, report.Utilization.Value!.Value, 10);
    }

    [Fact]
    public void Custom_thresholds_are_honoured()
    {
        var evaluator = new ContextHealthEvaluator(new ContextHealthThresholds(0.5, 0.6, 0.7));

        Assert.Equal(ContextHealthLevel.Healthy, evaluator.Evaluate(Exact(49), Exact(100)).Level);
        Assert.Equal(ContextHealthLevel.Warning, evaluator.Evaluate(Exact(50), Exact(100)).Level);
        Assert.Equal(ContextHealthLevel.CompressionRecommended, evaluator.Evaluate(Exact(60), Exact(100)).Level);
        Assert.Equal(ContextHealthLevel.Critical, evaluator.Evaluate(Exact(70), Exact(100)).Level);
    }

    [Fact]
    public void Exact_inputs_give_an_exact_utilization_citing_both_sources()
    {
        var report = Evaluator.Evaluate(Exact(500, "claude result.usage"), Exact(1000, "model card"));

        Assert.Equal(MetricProvenance.Exact, report.Utilization.Provenance);
        Assert.Contains("claude result.usage", report.Utilization.Source);
        Assert.Contains("model card", report.Utilization.Source);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void An_estimated_input_never_produces_an_exact_utilization(bool usedEstimated, bool windowEstimated)
    {
        var used = usedEstimated ? Estimated(500, "used-src") : Exact(500, "used-src");
        var window = windowEstimated ? Estimated(1000, "window-src") : Exact(1000, "window-src");

        var report = Evaluator.Evaluate(used, window);

        Assert.Equal(MetricProvenance.Estimated, report.Utilization.Provenance);
        Assert.Contains("used-src", report.Utilization.Source);
        Assert.Contains("window-src", report.Utilization.Source);
        Assert.Equal(ContextHealthLevel.Healthy, report.Level);
    }

    [Fact]
    public void An_estimated_input_still_yields_a_level()
    {
        Assert.Equal(ContextHealthLevel.Critical, Evaluator.Evaluate(Estimated(960), Exact(1000)).Level);
    }

    [Fact]
    public void Unavailable_usage_gives_unknown_and_unavailable()
    {
        var report = Evaluator.Evaluate(Missing, Exact(1000));

        Assert.Equal(ContextHealthLevel.Unknown, report.Level);
        Assert.Equal(MetricProvenance.Unavailable, report.Utilization.Provenance);
        Assert.False(report.Utilization.HasValue);
    }

    [Fact]
    public void Unavailable_window_gives_unknown_and_unavailable()
    {
        var report = Evaluator.Evaluate(Exact(10), Missing);

        Assert.Equal(ContextHealthLevel.Unknown, report.Level);
        Assert.False(report.Utilization.HasValue);
        Assert.Equal(MetricProvenance.Unavailable, report.Utilization.Provenance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_window_gives_unknown_without_dividing_by_zero(long window)
    {
        var report = Evaluator.Evaluate(Exact(10), Exact(window));

        Assert.Equal(ContextHealthLevel.Unknown, report.Level);
        Assert.Equal(MetricProvenance.Unavailable, report.Utilization.Provenance);
        Assert.False(report.Utilization.HasValue);
    }

    [Fact]
    public void Negative_usage_is_treated_as_unknown()
    {
        var report = Evaluator.Evaluate(Exact(-1), Exact(1000));

        Assert.Equal(ContextHealthLevel.Unknown, report.Level);
        Assert.False(report.Utilization.HasValue);
    }

    [Fact]
    public void Thresholds_changed_with_a_with_expression_are_still_validated_by_the_evaluator()
    {
        var broken = new ContextHealthThresholds() with { Warning = 0.99 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextHealthEvaluator(broken));
    }

    [Fact]
    public void Zero_used_tokens_on_a_known_window_is_healthy_not_unknown()
    {
        var report = Evaluator.Evaluate(Exact(0), Exact(1000));

        Assert.Equal(ContextHealthLevel.Healthy, report.Level);
        Assert.Equal(0.0, report.Utilization.Value);
    }

    [Fact]
    public void The_level_never_decreases_as_usage_grows()
    {
        var previous = ContextHealthLevel.Healthy;
        for (long used = 0; used <= 1200; used += 7)
        {
            var level = Evaluator.Evaluate(Exact(used), Exact(1000)).Level;
            Assert.True(level >= previous, $"{used}: {level} < {previous}");
            previous = level;
        }
    }

    // ---- origin of the utilization (docs/14 D-03 and D-10) ----

    [Fact]
    public void Two_figures_reported_by_the_provider_give_a_utilization_reported_by_the_provider()
    {
        var report = Evaluator.Evaluate(Exact(30_822), Exact(1_000_000));

        Assert.Equal(MetricOrigin.ReportedByProvider, report.Utilization.Origin);
    }

    [Fact]
    public void A_window_seen_in_an_earlier_run_makes_the_utilization_an_estimate_of_agent_studio_not_of_the_cli()
    {
        var report = Evaluator.Evaluate(Exact(500), MetricValue<long>.EstimatedBySystem(1000, "ventana vista en una ejecución anterior"));

        Assert.Equal((MetricOrigin.EstimatedBySystem, MetricProvenance.Estimated), (report.Utilization.Origin, report.Utilization.Provenance));
        Assert.Equal(0.5, report.Utilization.Value);
        Assert.Equal(ContextHealthLevel.Healthy, report.Level);
    }

    [Fact]
    public void Usage_that_did_not_arrive_gives_a_missing_utilization_rather_than_a_not_exposed_one()
    {
        var report = Evaluator.Evaluate(MetricValue<long>.Missing("the attempt was interrupted"), Exact(1000));

        Assert.Equal((MetricOrigin.Missing, ContextHealthLevel.Unknown), (report.Utilization.Origin, report.Level));
        Assert.False(report.Utilization.HasValue);
    }

    [Fact]
    public void A_window_the_cli_does_not_expose_gives_a_not_exposed_utilization()
    {
        var report = Evaluator.Evaluate(Exact(10), MetricValue<long>.NotExposed("codex exec does not expose the context window"));

        Assert.Equal((MetricOrigin.NotExposed, ContextHealthLevel.Unknown), (report.Utilization.Origin, report.Level));
    }

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(10, 0)]
    [InlineData(10, -5)]
    public void Figures_that_arrived_but_cannot_be_used_give_a_missing_utilization(long used, long window)
    {
        var report = Evaluator.Evaluate(Exact(used), Exact(window));

        Assert.Equal((MetricOrigin.Missing, ContextHealthLevel.Unknown), (report.Utilization.Origin, report.Level));
    }

    /// <summary>The utilization is never stronger than the weaker of its two figures, whatever they are.</summary>
    [Fact]
    public void The_utilization_takes_the_weakest_origin_of_its_two_figures()
    {
        foreach (var usedOrigin in Enum.GetValues<MetricOrigin>())
        {
            foreach (var windowOrigin in Enum.GetValues<MetricOrigin>())
            {
                var used = MetricValue<long>.Of(usedOrigin, usedOrigin.CarriesValue() ? 500 : null, "used");
                var window = MetricValue<long>.Of(windowOrigin, windowOrigin.CarriesValue() ? 1000 : null, "window");

                var report = Evaluator.Evaluate(used, window);

                Assert.Equal(MetricOrigins.Weakest(usedOrigin, windowOrigin), report.Utilization.Origin);
                Assert.Equal(MetricValue<long>.Combine(used.Provenance, window.Provenance), report.Utilization.Provenance);
            }
        }
    }

    [Fact]
    public void A_figure_without_a_value_counts_as_missing_even_if_it_claims_another_origin()
    {
        var report = Evaluator.Evaluate(default, Exact(1000));

        Assert.Equal((MetricOrigin.Missing, ContextHealthLevel.Unknown), (report.Utilization.Origin, report.Level));
    }

    // ---- thresholds ----

    [Fact]
    public void Default_thresholds_are_70_85_95_percent()
    {
        var t = new ContextHealthThresholds();

        Assert.Equal((0.70, 0.85, 0.95), (t.Warning, t.Compress, t.Critical));
    }

    [Theory]
    [InlineData(0.0, 0.5, 0.9)] // warning must be > 0
    [InlineData(-0.1, 0.5, 0.9)]
    [InlineData(0.5, 0.5, 0.9)] // warning < compress
    [InlineData(0.6, 0.5, 0.9)]
    [InlineData(0.3, 0.9, 0.9)] // compress < critical
    [InlineData(0.3, 0.9, 0.8)]
    [InlineData(0.3, 0.9, 1.01)] // critical <= 1
    public void Invalid_thresholds_are_rejected(double warning, double compress, double critical)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextHealthThresholds(warning, compress, critical));
    }

    [Fact]
    public void A_critical_threshold_of_exactly_one_is_valid()
    {
        Assert.Equal(1.0, new ContextHealthThresholds(0.5, 0.9, 1.0).Critical);
    }

    [Theory]
    [InlineData(double.NaN, 0.5, 0.9)]
    [InlineData(0.3, double.NaN, 0.9)]
    [InlineData(0.3, 0.5, double.NaN)]
    public void NaN_thresholds_are_rejected(double warning, double compress, double critical)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextHealthThresholds(warning, compress, critical));
    }

    [Fact]
    public void The_evaluator_requires_thresholds()
    {
        Assert.Throws<ArgumentNullException>(() => new ContextHealthEvaluator(null!));
    }
}
