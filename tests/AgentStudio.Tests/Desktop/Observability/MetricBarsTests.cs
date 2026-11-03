using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Context;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The readings of the bars (docs/18 P3): the value in words with its origin and level, «~» and «estimado» for an estimate,
/// an empty track that says why when there is no value (never a bar at zero), and the thresholds as marks.
/// </summary>
public sealed class MetricBarsTests
{
    private static readonly ContextHealthThresholds Thresholds = new();

    [Fact]
    public void An_exact_context_is_a_bar_with_its_percentage_origin_level_and_the_thresholds_as_marks()
    {
        var bar = MetricBars.Context("Contexto", "claude-sonnet-5-5", MetricValue<long>.Exact(470_000, "claude assistant.usage"), MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), Thresholds);

        Assert.Equal(47, bar.Value!.Value, 6);
        Assert.Equal(100, bar.Maximum);
        Assert.Equal(0.47, bar.Fraction!.Value, 6);
        Assert.Equal("47 %", bar.ValueText);
        Assert.Equal("exacto · informado por el CLI", bar.Origin);
        Assert.Equal(MetricBarLevel.Healthy, bar.Level);
        Assert.False(bar.IsEstimate);
        Assert.Equal("47 % · exacto · informado por el CLI · sano", bar.Text);
        Assert.Equal("47 % exacto · sano", bar.CompactText);
        Assert.Equal([0.70, 0.85, 0.95], bar.Marks);
        Assert.Contains("470.000 de 1.000.000 tokens", bar.Detail, StringComparison.Ordinal);
        Assert.StartsWith("Contexto: 47 % · exacto", bar.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_window_of_the_base_table_makes_an_estimate_that_never_says_exacto_and_names_where_it_comes_from()
    {
        var window = ModelWindowTable.Current.WindowFor("claude-sonnet-5-5");

        var bar = MetricBars.Context("Contexto", "claude-sonnet-5-5", MetricValue<long>.Exact(870_000, "claude assistant.usage"), window, Thresholds);

        Assert.Equal("~87 %", bar.ValueText);
        Assert.Equal("estimado por Agent Studio", bar.Origin);
        Assert.True(bar.IsEstimate);
        Assert.Equal(MetricBarLevel.Compression, bar.Level);
        Assert.Equal("compresión recomendada", bar.LevelText);
        Assert.Equal("~87 % estimado · compresión recomendada", bar.CompactText);
        Assert.DoesNotContain("exacto", bar.Text, StringComparison.Ordinal);
        Assert.Contains("tabla base", bar.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_without_a_window_are_an_empty_track_with_the_tokens_and_nothing_judged()
    {
        var bar = MetricBars.Context("Contexto", "modelo-desconocido", MetricValue<long>.Exact(30_822, "claude assistant.usage"), MetricValue<long>.Missing("no window"), Thresholds);

        Assert.False(bar.HasValue);
        Assert.Equal("30.822 tokens", bar.ValueText);
        Assert.Equal("sin evaluar", bar.LevelText);
        Assert.Equal(MetricBarLevel.Neutral, bar.Level);
        Assert.Contains("sin porcentaje", bar.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_cli_does_not_expose_is_an_empty_track_that_says_no_expuesto()
    {
        var none = MetricValue<long>.NotExposed("codex exec");

        var bar = MetricBars.Context("Contexto", null, none, none, Thresholds);

        Assert.False(bar.HasValue);
        Assert.Null(bar.Fraction);
        Assert.Equal("no expuesto", bar.ValueText);
        Assert.Equal("no expuesto", bar.Text);
        Assert.Equal("no expuesto", bar.CompactText);
        Assert.Equal(MetricOrigin.NotExposed, bar.OriginKind);
    }

    [Fact]
    public void A_tracker_before_its_first_step_says_there_is_no_observation_yet()
    {
        var tracker = new ContextHealthTracker(Thresholds, ModelWindowCatalog.Baseline, AgentStudio.Domain.Providers.ProviderId.ClaudeCode, reportsContextOccupancy: true);

        var bar = MetricBars.Context("Contexto", tracker.Current, Thresholds);

        Assert.Equal(MetricBars.NoObservationYet, bar.ValueText);
        Assert.Equal(MetricBars.NoObservationYet, bar.Text);
        Assert.False(bar.HasValue);
    }

    [Fact]
    public void A_codex_tracker_is_not_exposed_from_the_start()
    {
        var tracker = new ContextHealthTracker(Thresholds, ModelWindowCatalog.Baseline, AgentStudio.Domain.Providers.ProviderId.CodexCli, reportsContextOccupancy: false);

        Assert.Equal("no expuesto", MetricBars.Context("Contexto", tracker.Current, Thresholds).ValueText);
    }

    [Fact]
    public void A_quota_window_is_the_providers_own_figure_and_a_limit_reached_is_critical_in_words()
    {
        var quota = MetricBars.Quota("Claude · 5 horas", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), "vista hace 12 min", "se reinicia hoy a las 15:00");
        var reached = MetricBars.Quota("Claude · 7 días", MetricValue<double>.Exact(1, "claude rate_limit_event"), reached: "límite alcanzado: seven_day");
        var missing = MetricBars.Quota("Codex", MetricValue<double>.Missing("sin leer"));

        Assert.Equal("47 %", quota.ValueText);
        Assert.Equal(MetricBarLevel.Neutral, quota.Level);
        Assert.Equal("se reinicia hoy a las 15:00", quota.Detail);
        Assert.Equal(MetricBarLevel.Critical, reached.Level);
        Assert.Equal("límite alcanzado: seven_day", reached.LevelText);
        Assert.False(missing.HasValue);
        Assert.Equal("sin dato", missing.ValueText);
    }

    [Fact]
    public void The_age_of_a_quota_goes_next_to_its_figure_also_in_the_short_form_of_the_status_bar()
    {
        // docs/14 D-14: the quota is what the provider said at a moment; how old it is goes with the figure.
        var quota = MetricBars.Quota("Claude 5 h", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), "vista hace 12 min", "se reinicia hoy a las 15:00");

        Assert.Equal("47 % · exacto · informado por el CLI · vista hace 12 min", quota.Text);
        Assert.Equal("47 % exacto · vista hace 12 min", quota.CompactText);
    }

    [Fact]
    public void A_window_that_reset_since_it_was_seen_is_an_empty_track_that_says_so_and_keeps_the_old_figure_apart()
    {
        var after = MetricBars.QuotaAfterReset("Claude 5 h", "a las 15:00", MetricValue<double>.Exact(0.92, "claude rate_limit_event"), "vista hace 3 h");

        Assert.False(after.HasValue);
        Assert.Equal("se reinició a las 15:00, sin dato nuevo", after.ValueText);
        Assert.Equal("se reinició a las 15:00, sin dato nuevo", after.CompactText);
        Assert.Equal("antes del reinicio: 92 % · exacto · informado por el CLI · vista hace 3 h", after.Detail);
        Assert.DoesNotContain("exacto", after.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void In_the_short_form_the_level_is_written_too_never_said_by_the_colour_alone()
    {
        var spent = MetricBars.Budget("Presupuesto", 12, 12);
        var critical = MetricBars.Context("Contexto", "claude-sonnet-5-5", MetricValue<long>.Exact(980_000, "claude assistant.usage"), MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), Thresholds);
        var neutral = MetricBars.Budget("Presupuesto", 3, 12);

        Assert.Equal("12 de 12 exacto · presupuesto agotado", spent.CompactText);
        Assert.Equal("98 % exacto · crítico", critical.CompactText);
        Assert.Equal("3 de 12 exacto", neutral.CompactText);
    }

    [Fact]
    public void The_budget_is_measured_by_agent_studio_and_without_the_maximum_says_so()
    {
        var budget = MetricBars.Budget("Presupuesto", 3, 12);
        var spent = MetricBars.Budget("Presupuesto", 12, 12);
        var unknown = MetricBars.Budget("Presupuesto", 3, null);

        Assert.Equal("3 de 12", budget.ValueText);
        Assert.Equal(0.25, budget.Fraction!.Value, 6);
        Assert.Equal("exacto · medido por Agent Studio", budget.Origin);
        Assert.Equal(MetricBarLevel.Critical, spent.Level);
        Assert.Equal("presupuesto agotado", spent.LevelText);
        Assert.False(unknown.HasValue);
        Assert.Equal("3 ejecuciones de agente usadas · máximo sin dato", unknown.Text);
    }

    [Fact]
    public void Tokens_against_a_limit_are_a_bar_and_without_a_limit_only_the_tokens()
    {
        var limited = MetricBars.Tokens("Tokens del debate", MetricValue<long>.Exact(100_000, "turnos"), 400_000);
        var unlimited = MetricBars.Tokens("Tokens del debate", MetricValue<long>.Exact(100_000, "turnos"), null);
        var estimated = MetricBars.Tokens("Tokens", MetricValue<long>.EstimatedBySystem(50, "caracteres entre 4"), 100);

        Assert.Equal(0.25, limited.Fraction!.Value, 6);
        Assert.Equal("100.000 de 400.000 tokens", limited.ValueText);
        Assert.False(unlimited.HasValue);
        Assert.Equal("100.000 tokens · exacto · informado por el CLI · sin límite", unlimited.Text);
        Assert.True(estimated.IsEstimate);
        Assert.StartsWith("~50", estimated.ValueText, StringComparison.Ordinal);
    }
}
