using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Context;
using AgentStudio.Tests.Observability.Usage;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Context;

/// <summary>
/// F13-20 and docs/17 §0.2 (ADR-0052): the window learned live. A window the CLI reports exactly in one attempt of a run serves
/// the next attempts that share the catalog (the tasks of a monitor and its planning) from their first step, as an estimate of
/// Agent Studio, without waiting for the run to end and the log to be read again; the shared catalogs never learn.
/// </summary>
public sealed class ModelWindowLiveTests
{
    private const string NewModel = "claude-nuevo-6";
    private const string StepSource = "claude assistant.message.usage";

    private static readonly ContextHealthThresholds Thresholds = new();

    private static ContextObserved Step(long tokens, string? model = NewModel, MetricValue<long>? window = null) =>
        new(T0, model, MetricValue<long>.Exact(tokens, StepSource), window ?? MetricValue<long>.Missing("window not known yet"));

    private static UsageReported Result(string model, long window) =>
        new(T0, new TokenUsage(
            MetricValue<long>.Exact(1_000, "claude result.usage"),
            MetricValue<long>.Exact(50, "claude result.usage"),
            MetricValue<long>.Exact(0, "claude result.usage"),
            MetricValue<long>.NotExposed("x"),
            MetricValue<decimal>.Estimated(0.01m, "estimación del CLI"))
        {
            PerModel = [new ModelUsage(model, MetricValue<long>.Exact(1, "m"), MetricValue<long>.Exact(1, "m"), MetricValue<long>.Exact(0, "m"), MetricValue<long>.Exact(0, "m"), MetricValue<decimal>.Estimated(0m, "m"), MetricValue<long>.Exact(window, "claude result.modelUsage"), "list")],
        });

    private static ModelWindowCatalog FromEmptyLog() =>
        ModelWindowCatalog.FromLog(new DecisionLogState([], DecisionLogVerification.Valid)).WithBaseline();

    [Fact]
    public void A_window_reported_by_one_attempt_judges_the_first_step_of_the_next_attempt()
    {
        var clock = new FakeTimeProvider(T0);
        var catalog = FromEmptyLog();
        var first = new ContextHealthTracker(Thresholds, catalog, ProviderId.ClaudeCode, reportsContextOccupancy: true, clock);
        Assert.Equal(MetricOrigin.Missing, first.Observe(Step(40_000)).ContextWindow.Origin);

        first.Observe(Result(NewModel, 400_000));
        var second = new ContextHealthTracker(Thresholds, catalog, ProviderId.ClaudeCode, reportsContextOccupancy: true, clock);
        var state = second.Observe(Step(100_000));

        Assert.Equal(400_000, state.ContextWindow.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, state.ContextWindow.Origin);
        Assert.Contains("en esta ejecución", state.ContextWindow.Source, StringComparison.Ordinal);
        Assert.Equal(0.25, state.Health.Utilization.Value!.Value, 6);
        Assert.Equal(MetricOrigin.EstimatedBySystem, state.Health.Utilization.Origin);
        Assert.Equal([(NewModel, 400_000L, T0)], catalog.LiveWindows.Select(w => (w.Model, w.Tokens, w.SeenAt)));
    }

    [Fact]
    public void What_is_learned_live_wins_over_the_log_and_the_base_table_and_the_attempt_s_own_window_wins_over_it()
    {
        var log = new UsageLogBuilder();
        log.Start("20261020-a1b2c3", "T1", 1).Finish("20261020-a1b2c3", "T1", 1, session: "s1", usage: Snapshots.Claude(10, 0, 0, 1, 0m, Snapshots.Model("claude-haiku-4-5", 10, 1, 0, 0, 0m, window: 150_000)));
        var catalog = ModelWindowCatalog.FromLog(new DecisionLogState(log.Entries, DecisionLogVerification.Valid)).WithBaseline();
        Assert.Equal(150_000, catalog.WindowFor("claude-haiku-4-5").Value);
        Assert.Equal(1_000_000, catalog.WindowFor("claude-opus-5-5").Value);

        catalog.LearnLive("claude-haiku-4-5", 180_000, T0);
        catalog.LearnLive("claude-opus-5-5", 900_000, T0);

        Assert.Equal(180_000, catalog.WindowFor("claude-haiku-4-5").Value);
        Assert.Equal(900_000, catalog.WindowFor("claude-opus-5-5").Value);
        var tracker = new ContextHealthTracker(Thresholds, catalog, ProviderId.ClaudeCode, reportsContextOccupancy: true);
        var own = tracker.Observe(Step(90_000, "claude-opus-5-5", MetricValue<long>.Exact(1_000_000, "claude result.modelUsage")));
        Assert.Equal((1_000_000L, MetricOrigin.ReportedByProvider), (own.ContextWindow.Value!.Value, own.ContextWindow.Origin));
    }

    [Fact]
    public void Only_an_exact_window_of_a_named_model_is_learned()
    {
        var catalog = ModelWindowCatalog.Learning();
        var tracker = new ContextHealthTracker(Thresholds, catalog, ProviderId.ClaudeCode, reportsContextOccupancy: true);

        tracker.Observe(Step(1_000, NewModel, MetricValue<long>.Estimated(300_000, "estimate of the CLI")));
        tracker.Observe(Step(1_000, null, MetricValue<long>.Exact(300_000, "claude result.modelUsage")));
        catalog.LearnLive(NewModel, 0, T0);

        Assert.Empty(catalog.LiveWindows);
        Assert.True(catalog.LearnsLive);
    }

    [Fact]
    public void The_shared_catalogs_never_learn_so_nothing_passes_between_projects()
    {
        var tracker = new ContextHealthTracker(Thresholds, ModelWindowCatalog.Baseline, ProviderId.ClaudeCode, reportsContextOccupancy: true);

        tracker.Observe(Result(NewModel, 400_000));
        ModelWindowCatalog.Empty.LearnLive(NewModel, 400_000, T0);

        Assert.False(ModelWindowCatalog.Baseline.LearnsLive);
        Assert.False(ModelWindowCatalog.Empty.LearnsLive);
        Assert.Empty(ModelWindowCatalog.Baseline.LiveWindows);
        Assert.Equal(MetricOrigin.Missing, ModelWindowCatalog.Baseline.WindowFor(NewModel).Origin);
        Assert.False(ModelWindowCatalog.FromLog(new DecisionLogState([], new DecisionLogVerification(false, 1, "hash"))).LearnsLive);
    }

    [Fact]
    public void A_catalog_with_its_base_table_shares_what_it_learns_live()
    {
        var read = ModelWindowCatalog.FromLog(new DecisionLogState([], DecisionLogVerification.Valid));
        var withTable = read.WithBaseline();

        read.LearnLive(NewModel, 400_000, T0);

        Assert.Equal(400_000, withTable.WindowFor(NewModel).Value);
    }

    [Fact]
    public void Codex_reports_no_window_and_teaches_nothing()
    {
        var catalog = FromEmptyLog();
        var tracker = new ContextHealthTracker(Thresholds, catalog, ProviderId.CodexCli, reportsContextOccupancy: false);

        tracker.Observe(new UsageReported(T0, new TokenUsage(
            MetricValue<long>.Exact(1, "codex"),
            MetricValue<long>.Exact(1, "codex"),
            MetricValue<long>.Exact(0, "codex"),
            MetricValue<long>.NotExposed("codex exec --json does not report the context window"),
            MetricValue<decimal>.NotExposed("codex"))));

        Assert.Empty(catalog.LiveWindows);
        Assert.Equal(MetricOrigin.NotExposed, catalog.WindowFor("gpt-6-luna").Origin);
    }
}
