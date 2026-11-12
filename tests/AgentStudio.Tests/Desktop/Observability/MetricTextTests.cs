using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// Every number of the Phase 4 screens carries its origin (docs/14 D-03): an estimate is never shown as exact, and a
/// figure that is not there is «no expuesto» or «sin dato», never a zero.
/// </summary>
public sealed class MetricTextTests
{
    [Theory]
    [InlineData(MetricOrigin.ReportedByProvider, "exacto · informado por el programa del agente")]
    [InlineData(MetricOrigin.MeasuredBySystem, "exacto · medido por Agent Studio")]
    [InlineData(MetricOrigin.EstimatedByProvider, "estimado por el programa del agente")]
    [InlineData(MetricOrigin.EstimatedBySystem, "estimado por Agent Studio")]
    [InlineData(MetricOrigin.NotExposed, "no expuesto")]
    [InlineData(MetricOrigin.Missing, "sin dato")]
    public void Each_origin_has_the_label_of_D03(MetricOrigin origin, string label) => Assert.Equal(label, MetricText.Origin(origin));

    [Fact]
    public void A_reported_count_shows_its_value_and_that_the_CLI_gave_it()
    {
        var figure = MetricText.Tokens(MetricValue<long>.Exact(1_234_567, "claude result.usage"));

        Assert.Equal("1.234.567", figure.Value);
        Assert.Equal("1.234.567 · exacto · informado por el programa del agente", figure.Text);
    }

    [Fact]
    public void An_estimate_is_never_labelled_exact()
    {
        var byCli = MetricText.Tokens(MetricValue<long>.Estimated(800, "claude thinking_tokens"));
        var bySystem = MetricText.Tokens(MetricValue<long>.EstimatedBySystem(2_500, "10000 chars / 4"));

        Assert.Equal("~800 · estimado por el programa del agente", byCli.Text);
        Assert.Equal("~2.500 · estimado por Agent Studio", bySystem.Text);
        Assert.DoesNotContain("exacto", byCli.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("exacto", bySystem.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_figure_that_is_not_there_is_never_zero()
    {
        var notExposed = MetricText.Tokens(MetricValue<long>.NotExposed("codex exec does not report it"));
        var missing = MetricText.Tokens(MetricValue<long>.Missing("the attempt was interrupted"));

        Assert.Null(notExposed.Value);
        Assert.Equal("no expuesto", notExposed.Text);
        Assert.Equal("—", notExposed.Shown);
        Assert.Null(missing.Value);
        Assert.Equal("sin dato", missing.Text);
    }

    [Fact]
    public void A_partial_sum_says_how_many_values_it_has()
    {
        var aggregate = new MetricAggregate<long>(300, MetricOrigin.ReportedByProvider, 3, 2, ["claude result.usage"]);

        var figure = MetricText.Tokens(aggregate);

        Assert.Equal("300 · parcial (3 de 5) · exacto · informado por el programa del agente", figure.Text);
    }

    [Fact]
    public void A_sum_with_nothing_reported_says_why_and_shows_no_number()
    {
        var notExposed = MetricAggregate.Sum([MetricValue<long>.NotExposed("codex exec"), MetricValue<long>.NotExposed("codex exec")]);
        var missing = MetricAggregate.Sum([MetricValue<long>.Missing("interrupted"), MetricValue<long>.NotExposed("codex exec")]);

        Assert.Equal("no expuesto", MetricText.Tokens(notExposed).Text);
        Assert.Equal("sin dato", MetricText.Tokens(missing).Text);
        Assert.Null(MetricText.Tokens(missing).Value);
    }

    [Fact]
    public void A_sum_takes_the_weakest_origin_of_what_it_adds()
    {
        var mixed = MetricAggregate.Sum([MetricValue<long>.Exact(100, "claude result.usage"), MetricValue<long>.EstimatedBySystem(50, "chars / 4")]);

        Assert.Equal("~150 · estimado por Agent Studio", MetricText.Tokens(mixed).Text);
    }

    [Fact]
    public void A_cost_is_the_CLIs_list_price_estimate_and_Codex_does_not_expose_it()
    {
        Assert.Equal("~0,42 USD · estimado por el programa del agente (precio de lista)", MetricText.Cost(MetricValue<decimal>.Estimated(0.4215m, "claude total_cost_usd")).Text);
        Assert.Equal("~1,05 USD · estimado por el programa del agente (precio de lista)", MetricText.Cost(new MetricAggregate<decimal>(1.05m, MetricOrigin.EstimatedByProvider, 2, 0, ["claude total_cost_usd"])).Text);
        Assert.Equal("no expuesto", MetricText.Cost(MetricValue<decimal>.NotExposed("codex has no cost")).Text);
    }

    [Fact]
    public void A_cost_on_another_basis_than_the_list_price_says_which()
    {
        Assert.Equal("~0,42 USD · estimado por el programa del agente (base «managed»)", MetricText.Cost(MetricValue<decimal>.Estimated(0.42m, "claude costUSD"), "managed").Text);
        Assert.Equal("~0,42 USD · estimado por el programa del agente (precio de lista)", MetricText.Cost(MetricValue<decimal>.Estimated(0.42m, "claude costUSD"), "list").Text);
    }

    [Theory]
    [InlineData(0L, "0 s")]
    [InlineData(45_000L, "45 s")]
    [InlineData(2_460_000L, "41 min")]
    [InlineData(3_900_000L, "1 h 5 min")]
    public void A_duration_is_measured_by_Agent_Studio(long milliseconds, string shown)
    {
        var figure = MetricText.Duration(new MetricAggregate<long>(milliseconds, MetricOrigin.MeasuredBySystem, 1, 0, ["process start to finish"]));

        Assert.Equal($"{shown} · exacto · medido por Agent Studio", figure.Text);
    }

    [Fact]
    public void A_count_of_attempts_is_measured_by_Agent_Studio()
    {
        Assert.Equal("14 · exacto · medido por Agent Studio", MetricText.Measured(14).Text);
    }

    [Fact]
    public void A_utilization_is_a_percentage_with_its_origin()
    {
        Assert.Equal("47 % · exacto · informado por el programa del agente", MetricText.Percent(MetricValue<double>.Exact(0.47, "claude rate_limit_event")).Text);
        Assert.Equal("~68 % · estimado por Agent Studio", MetricText.Percent(MetricValue<double>.EstimatedBySystem(0.68, "learned window")).Text);
        Assert.Equal("no expuesto", MetricText.Percent(MetricValue<double>.NotExposed("gemini")).Text);
    }
}
