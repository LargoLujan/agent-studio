using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The context occupancy of a step as the monitor and the usage panel show it (docs/14 D-09 to D-12): a percentage with
/// its origin, the tokens and the window, the model and the level of the configured thresholds. An estimate is never
/// shown as exact, an unknown window gives tokens without a percentage, and what the CLI does not expose says so.
/// </summary>
public sealed class ContextTextTests
{
    private static readonly ContextHealthThresholds Defaults = new();

    [Fact]
    public void Exact_tokens_and_window_give_an_exact_percentage_and_its_level()
    {
        var reading = ContextText.Read("claude-sonnet-5-5", MetricValue<long>.Exact(30_822, "claude assistant.usage"), MetricValue<long>.Exact(1_000_000, "claude modelUsage"), Defaults);

        Assert.Equal("3 % · exacto · informado por el programa del agente · 30.822 de 1.000.000 tokens · claude-sonnet-5-5 · sano", reading.Text);
        Assert.Equal(ContextHealthLevel.Healthy, reading.Level);
    }

    [Fact]
    public void A_window_seen_in_an_earlier_run_makes_the_percentage_an_estimate()
    {
        var reading = ContextText.Read(
            "claude-sonnet-5-5",
            MetricValue<long>.Exact(140_000, "claude assistant.usage"),
            MetricValue<long>.EstimatedBySystem(192_000, "window seen in an earlier run"),
            Defaults);

        Assert.Equal("~73 % · estimado por Agent Studio · 140.000 de ~192.000 tokens · claude-sonnet-5-5 · aviso", reading.Text);
        Assert.DoesNotContain("exacto", reading.Occupancy.Text, StringComparison.Ordinal);
        Assert.Equal(ContextHealthLevel.Warning, reading.Level);
    }

    [Fact]
    public void Estimated_tokens_make_the_percentage_an_estimate_of_the_CLI()
    {
        var reading = ContextText.Read(null, MetricValue<long>.Estimated(900_000, "claude live estimate"), MetricValue<long>.Exact(1_000_000, "claude modelUsage"), Defaults);

        Assert.Equal("~90 % · estimado por el programa del agente · ~900.000 de 1.000.000 tokens · compresión recomendada", reading.Text);
    }

    [Fact]
    public void Without_a_window_the_occupancy_is_shown_in_tokens_without_a_percentage()
    {
        var reading = ContextText.Read("claude-sonnet-5-5", MetricValue<long>.Exact(30_822, "claude assistant.usage"), MetricValue<long>.Missing("no window known yet"), Defaults);

        Assert.Equal("30.822 tokens · exacto · informado por el programa del agente · sin porcentaje: aún no se conoce la memoria de trabajo del modelo · claude-sonnet-5-5 · sin evaluar", reading.Text);
        Assert.Equal(ContextHealthLevel.Unknown, reading.Level);
        Assert.DoesNotContain("sano", reading.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_CLI_does_not_expose_says_so_and_is_not_judged()
    {
        var reading = ContextText.Read(null, MetricValue<long>.NotExposed("codex exec"), MetricValue<long>.NotExposed("codex exec"), Defaults);

        Assert.Equal("no expuesto", reading.Text);
        Assert.Equal(ContextHealthLevel.Unknown, reading.Level);
    }

    [Fact]
    public void The_level_follows_the_configured_thresholds()
    {
        var thresholds = new ContextHealthThresholds(0.50, 0.60, 0.90);

        var reading = ContextText.Read(null, MetricValue<long>.Exact(55, "x"), MetricValue<long>.Exact(100, "y"), thresholds);

        Assert.Equal(ContextHealthLevel.Warning, reading.Level);
        Assert.EndsWith("· aviso", reading.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_window_is_critical()
    {
        var reading = ContextText.Read(null, MetricValue<long>.Exact(990, "x"), MetricValue<long>.Exact(1_000, "y"), Defaults);

        Assert.Equal(ContextHealthLevel.Critical, reading.Level);
        Assert.EndsWith("· crítico", reading.Text, StringComparison.Ordinal);
    }
}
