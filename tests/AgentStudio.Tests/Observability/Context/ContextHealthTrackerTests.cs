using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Context;
using AgentStudio.Tests.Observability.Usage;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Context;

/// <summary>
/// docs/14 D-09 to D-12 (F4-3, ADR-0021): the health of the context of a run in progress, step by step. The occupancy is
/// the prompt of the last step, against the window of the model of that step: the window the CLI gave, else the last one
/// it gave for that model in this run, else the one learned from an earlier run (an estimate of Agent Studio), else
/// none, and then only the tokens are shown. An estimate is never shown as exact, and the total of the run is never the
/// occupancy. <c>codex exec</c> does not expose it.
/// </summary>
public sealed class ContextHealthTrackerTests
{
    private const string Sonnet = "claude-sonnet-5-5";
    private const string Haiku = "claude-haiku-4-5";
    private const string StepSource = "claude assistant.message.usage";

    private static readonly MetricValue<long> NotYet = MetricValue<long>.Missing("window not known yet");

    private static ContextHealthTracker Claude(ModelWindowCatalog? learned = null, ContextHealthThresholds? thresholds = null) =>
        new(thresholds ?? new ContextHealthThresholds(), learned ?? ModelWindowCatalog.Empty, ProviderId.ClaudeCode, reportsContextOccupancy: true);

    private static ContextObserved Step(long tokens, string? model = Sonnet, MetricValue<long>? window = null) =>
        new(T0, model, MetricValue<long>.Exact(tokens, StepSource), window ?? NotYet);

    private static ModelWindowCatalog Learned(params (string Model, long Window)[] windows)
    {
        var log = new UsageLogBuilder();
        var attempt = 1;
        foreach (var (model, window) in windows)
        {
            log.Start("20261020-a1b2c3", "T1", attempt).Finish("20261020-a1b2c3", "T1", attempt, session: "s" + attempt, usage: Snapshots.Claude(10, 0, 0, 1, 0m, Snapshots.Model(model, 10, 1, 0, 0, 0m, window: window)));
            attempt++;
        }

        return ModelWindowCatalog.FromLog(new DecisionLogState(log.Entries, DecisionLogVerification.Valid));
    }

    private static UsageReported Result(params (string Model, long Window)[] windows) =>
        new(T0, new TokenUsage(
            MetricValue<long>.Exact(61_222, "claude result.usage"),
            MetricValue<long>.Exact(50, "claude result.usage"),
            MetricValue<long>.Exact(30_391, "claude result.usage"),
            MetricValue<long>.NotExposed("x"),
            MetricValue<decimal>.Estimated(0.01m, "estimación del CLI"))
        {
            PerModel = [.. windows.Select(w => new ModelUsage(w.Model, MetricValue<long>.Exact(1, "m"), MetricValue<long>.Exact(1, "m"), MetricValue<long>.Exact(0, "m"), MetricValue<long>.Exact(0, "m"), MetricValue<decimal>.Estimated(0m, "m"), MetricValue<long>.Exact(w.Window, "claude result.modelUsage"), "list"))],
        });

    [Fact]
    public void Before_any_step_the_occupancy_is_missing_and_the_level_unknown_never_healthy()
    {
        var state = Claude().Current;

        Assert.Equal(MetricOrigin.Missing, state.UsedTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, state.Health.Utilization.Origin);
        Assert.Equal(ContextHealthLevel.Unknown, state.Health.Level);
        Assert.Equal(MetricValue<long>.Measured(0, "compactaciones observadas por Agent Studio (compact_boundary)"), state.Compactions);
        Assert.Equal(0, state.Steps);
        Assert.False(state.Finished);
    }

    [Fact]
    public void A_step_with_the_window_of_its_model_gives_an_exact_utilization()
    {
        var state = Claude().Observe(Step(30_822, window: MetricValue<long>.Exact(1_000_000, "claude result.modelUsage")));

        Assert.Equal(Sonnet, state.Model);
        Assert.Equal(MetricValue<long>.Exact(30_822, StepSource), state.UsedTokens);
        Assert.Equal(MetricOrigin.ReportedByProvider, state.Health.Utilization.Origin);
        Assert.Equal(0.030822, state.Health.Utilization.Value!.Value, 6);
        Assert.Equal(ContextHealthLevel.Healthy, state.Health.Level);
        Assert.Equal(1, state.Steps);
    }

    [Fact]
    public void A_step_without_window_takes_the_one_learned_for_its_model_and_is_an_estimate()
    {
        var state = Claude(Learned((Sonnet, 1_000_000), (Haiku, 200_000))).Observe(Step(750_000));

        Assert.Equal(1_000_000L, state.ContextWindow.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, state.ContextWindow.Origin);
        Assert.Equal(MetricOrigin.EstimatedBySystem, state.Health.Utilization.Origin);
        Assert.Equal(ContextHealthLevel.Warning, state.Health.Level);
        Assert.NotEqual(MetricProvenance.Exact, state.Health.Utilization.Provenance);
    }

    [Fact]
    public void Without_any_known_window_only_the_tokens_are_shown()
    {
        var state = Claude().Observe(Step(30_822));

        Assert.Equal(30_822L, state.UsedTokens.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, state.UsedTokens.Origin);
        Assert.False(state.ContextWindow.HasValue);
        Assert.False(state.Health.Utilization.HasValue);
        Assert.Equal(ContextHealthLevel.Unknown, state.Health.Level);
    }

    [Fact]
    public void The_result_of_the_run_makes_the_last_step_exact_with_the_window_of_its_model()
    {
        var tracker = Claude(Learned((Sonnet, 200_000)));
        tracker.Observe(Step(30_822));

        var state = tracker.Observe(Result((Haiku, 200_000), (Sonnet, 1_000_000)));

        Assert.Equal(MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), state.ContextWindow);
        Assert.Equal(MetricOrigin.ReportedByProvider, state.Health.Utilization.Origin);
        Assert.Equal(0.030822, state.Health.Utilization.Value!.Value, 6);
    }

    [Fact]
    public void The_occupancy_is_never_the_total_of_the_run()
    {
        // 02-herramienta: result.usage would give 61 222 tokens, twice the 30 822 of the last step (docs/14 D-09).
        var tracker = Claude();
        tracker.Observe(Step(30_822));

        var state = tracker.Observe(Result((Sonnet, 1_000_000)));

        Assert.Equal(30_822L, state.UsedTokens.Value);
        Assert.Equal(1, state.Steps);
    }

    [Fact]
    public void A_window_given_for_a_model_in_this_run_serves_its_later_steps_and_never_another_model()
    {
        var tracker = Claude(Learned((Haiku, 200_000)));
        tracker.Observe(Step(1000, window: MetricValue<long>.Exact(1_000_000, "claude result.modelUsage")));

        var sonnet = tracker.Observe(Step(2000));
        var haiku = tracker.Observe(Step(3000, model: Haiku));

        Assert.Equal((1_000_000L, MetricOrigin.ReportedByProvider), (sonnet.ContextWindow.Value!.Value, sonnet.ContextWindow.Origin));
        Assert.Equal((200_000L, MetricOrigin.EstimatedBySystem), (haiku.ContextWindow.Value!.Value, haiku.ContextWindow.Origin));
        Assert.Equal(Haiku, haiku.Model);
    }

    [Fact]
    public void A_result_for_another_model_does_not_fix_the_window_of_the_step()
    {
        var tracker = Claude();
        tracker.Observe(Step(30_822));

        var state = tracker.Observe(Result((Haiku, 200_000)));

        Assert.False(state.ContextWindow.HasValue);
        Assert.Equal(ContextHealthLevel.Unknown, state.Health.Level);
    }

    [Fact]
    public void An_estimate_of_the_cli_stays_an_estimate()
    {
        var state = Claude().Observe(Step(100_000, window: MetricValue<long>.Estimated(200_000, "estimación del CLI")));

        Assert.Equal(MetricOrigin.EstimatedByProvider, state.Health.Utilization.Origin);
        Assert.Equal(MetricProvenance.Estimated, state.Health.Utilization.Provenance);
    }

    [Fact]
    public void Compactions_are_counted_and_the_peak_is_the_largest_step()
    {
        var tracker = Claude();
        tracker.Observe(Step(150_000));
        tracker.Observe(new CompactionObserved(T0, "auto", MetricValue<long>.Exact(180_000, "claude compact_boundary")));
        tracker.Observe(Step(20_000));
        tracker.Observe(new CompactionObserved(T0, "manual", MetricValue<long>.Exact(90_000, "claude compact_boundary")));

        var state = tracker.Observe(Step(40_000));

        Assert.Equal(2L, state.Compactions.Value);
        Assert.Equal(MetricOrigin.MeasuredBySystem, state.Compactions.Origin);
        Assert.Equal(40_000L, state.UsedTokens.Value);
        Assert.Equal(150_000L, state.Peak?.UsedTokens.Value);
        Assert.Equal(3, state.Steps);
    }

    [Fact]
    public void The_thresholds_decide_the_level()
    {
        var tracker = Claude(thresholds: new ContextHealthThresholds(0.5, 0.6, 0.7));
        var window = MetricValue<long>.Exact(1000, "claude result.modelUsage");

        Assert.Equal(ContextHealthLevel.Healthy, tracker.Observe(Step(499, window: window)).Health.Level);
        Assert.Equal(ContextHealthLevel.Warning, tracker.Observe(Step(500, window: window)).Health.Level);
        Assert.Equal(ContextHealthLevel.CompressionRecommended, tracker.Observe(Step(650, window: window)).Health.Level);
        Assert.Equal(ContextHealthLevel.Critical, tracker.Observe(Step(700, window: window)).Health.Level);
    }

    [Fact]
    public void Thresholds_that_do_not_hold_are_refused()
    {
        var broken = new ContextHealthThresholds() with { Warning = 0.9 };

        Assert.Throws<ArgumentOutOfRangeException>(() => Claude(thresholds: broken));
    }

    [Fact]
    public void Codex_exec_does_not_expose_the_context()
    {
        var tracker = new ContextHealthTracker(new ContextHealthThresholds(), Learned((Sonnet, 1_000_000)), ProviderId.CodexCli, reportsContextOccupancy: false);

        tracker.Observe(Result((Sonnet, 1_000_000)));
        var state = tracker.Observe(new RunFinished(T0, new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "ok", null, "th1", null)));

        Assert.Equal(MetricOrigin.NotExposed, state.UsedTokens.Origin);
        Assert.Equal(MetricOrigin.NotExposed, state.ContextWindow.Origin);
        Assert.Equal(MetricOrigin.NotExposed, state.Health.Utilization.Origin);
        Assert.Equal(MetricOrigin.NotExposed, state.Compactions.Origin);
        Assert.Contains("codex exec", state.UsedTokens.Source, StringComparison.Ordinal);
        Assert.Equal(ContextHealthLevel.Unknown, state.Health.Level);
        Assert.True(state.Finished);
    }

    [Fact]
    public void The_end_of_the_run_is_seen()
    {
        var tracker = Claude();
        tracker.Observe(Step(10));

        var state = tracker.Observe(new RunFinished(T0, new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "ok", null, "s1", null)));

        Assert.True(state.Finished);
        Assert.Equal(10L, state.UsedTokens.Value);
    }

    [Fact]
    public void The_model_announced_at_the_start_is_never_taken_for_the_model_of_the_steps()
    {
        // 01-texto: init announces claude-haiku-4-5, but the main loop ran on claude-sonnet-5-5 (docs/14 D-13).
        var tracker = Claude(Learned((Haiku, 200_000)));

        var state = tracker.Observe(new SessionStarted(T0, "s1", Haiku));

        Assert.Null(state.Model);
        Assert.False(state.ContextWindow.HasValue);
    }

    [Fact]
    public void With_the_base_table_a_first_run_has_an_estimated_percentage_from_its_first_step_and_the_result_makes_it_exact()
    {
        // P36: a new project has learned nothing; Claude reports the window only with the result of the attempt.
        var tracker = Claude(ModelWindowCatalog.Baseline);

        var live = tracker.Observe(Step(250_000));

        Assert.Equal(1_000_000L, live.ContextWindow.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, live.ContextWindow.Origin);
        Assert.Equal(0.25, live.Health.Utilization.Value!.Value, 6);
        Assert.Equal(MetricOrigin.EstimatedBySystem, live.Health.Utilization.Origin);
        Assert.Equal(ContextHealthLevel.Healthy, live.Health.Level);

        var final = tracker.Observe(Result((Sonnet, 1_000_000)));

        Assert.Equal(MetricOrigin.ReportedByProvider, final.Health.Utilization.Origin);
        Assert.Equal(0.25, final.Health.Utilization.Value!.Value, 6);
    }

    [Fact]
    public void A_window_learned_from_the_log_wins_over_the_base_table()
    {
        var tracker = Claude(Learned((Sonnet, 200_000)).WithBaseline());

        var state = tracker.Observe(Step(150_000));

        Assert.Equal(200_000L, state.ContextWindow.Value);
        Assert.Contains("ventana vista en una ejecución anterior", state.ContextWindow.Source, StringComparison.Ordinal);
        Assert.Equal(ContextHealthLevel.Warning, state.Health.Level);
    }
}
