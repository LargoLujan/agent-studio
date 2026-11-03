using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Context;
using AgentStudio.Tests.Observability.Usage;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Observability.Usage.Snapshots;

namespace AgentStudio.Tests.Observability.Context;

/// <summary>
/// docs/14 D-10: until a run reports its windows, the window of a model is the last one seen for it in the project's
/// log, labelled «estimado por Agent Studio (ventana vista en una ejecución anterior)». Only windows the CLI reported
/// for a known model are learned, and only from a log that verifies.
/// </summary>
public sealed class ModelWindowCatalogTests
{
    private const string Run = "20261025-a1b2c3";

    private static DecisionLogState Verified(UsageLogBuilder log) => new(log.Entries, DecisionLogVerification.Valid);

    [Fact]
    public void The_windows_are_learned_per_model_from_the_breakdown_and_the_context_peak_of_version_2_ends()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(10, 0, 0, 1, 0m, Model("claude-sonnet-5-5", 10, 1, 0, 0, 0m, window: 1_000_000)))
            .Start(Run, "T2", 1).Finish(Run, "T2", 1, session: "s2", usage: Claude(10, 0, 0, 1, 0m), peak: Peak("claude-haiku-4-5", 1000, 200_000), launchedAt: T0);

        var catalog = ModelWindowCatalog.FromLog(Verified(log));

        Assert.Equal(1_000_000L, catalog.WindowFor("claude-sonnet-5-5").Value);
        Assert.Equal(200_000L, catalog.WindowFor("claude-haiku-4-5").Value);
        Assert.Equal(["claude-haiku-4-5", "claude-sonnet-5-5"], catalog.Windows.Select(w => w.Model).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_learned_window_is_an_estimate_of_agent_studio_and_says_where_it_comes_from()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(10, 0, 0, 1, 0m, Model("claude-sonnet-5-5", 10, 1, 0, 0, 0m)));

        var window = ModelWindowCatalog.FromLog(Verified(log)).WindowFor("claude-sonnet-5-5");

        Assert.Equal(MetricOrigin.EstimatedBySystem, window.Origin);
        Assert.Contains("ventana vista en una ejecución anterior", window.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_window_seen_for_a_model_wins()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(10, 0, 0, 1, 0m, Model("claude-sonnet-5-5", 10, 1, 0, 0, 0m, window: 200_000)))
            .Start(Run, "T2", 1).Finish(Run, "T2", 1, session: "s2", usage: Claude(10, 0, 0, 1, 0m, Model("claude-sonnet-5-5", 10, 1, 0, 0, 0m, window: 1_000_000)));

        Assert.Equal(1_000_000L, ModelWindowCatalog.FromLog(Verified(log)).WindowFor("claude-sonnet-5-5").Value);
    }

    [Fact]
    public void A_version_1_window_is_never_learned_because_it_was_paired_with_the_model_of_init()
    {
        // D-13: the parser of Phase 3 took the window of the model announced by init, not of the model of the steps.
        var log = new UsageLogBuilder().Start(Run, "T1", 1, version: 1).Finish(Run, "T1", 1, session: "s1", usage: ClaudeV1(10, 0, 1, 0m), version: 1);

        Assert.Empty(ModelWindowCatalog.FromLog(Verified(log)).Windows);
    }

    [Fact]
    public void A_window_that_the_cli_did_not_report_is_not_learned()
    {
        var estimated = new ModelUsageSnapshot("claude-sonnet-5-5", Reported(1), Reported(1), Reported(0), Reported(0), Cost(0m), MetricSnapshot.WithOrigin(MetricValue<long>.EstimatedBySystem(500_000, "guess")), null);
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(10, 0, 0, 1, 0m, estimated));

        Assert.Empty(ModelWindowCatalog.FromLog(Verified(log)).Windows);
    }

    [Fact]
    public void A_log_that_does_not_verify_teaches_nothing()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(10, 0, 0, 1, 0m, Model("claude-sonnet-5-5", 10, 1, 0, 0, 0m)));

        Assert.Empty(ModelWindowCatalog.FromLog(new DecisionLogState(log.Entries, new DecisionLogVerification(false, 1, "broken"))).Windows);
        Assert.Empty(ModelWindowCatalog.FromLog(new ProjectLogSnapshot("p", "C:\\r", log.Entries, DecisionLogVerification.Valid, AnchorStatus.Unanchored)).Windows);
        Assert.NotEmpty(ModelWindowCatalog.FromLog(new ProjectLogSnapshot("p", "C:\\r", log.Entries, DecisionLogVerification.Valid, AnchorStatus.Anchored)).Windows);
    }

    [Fact]
    public void A_model_never_seen_has_no_window()
    {
        var window = ModelWindowCatalog.Empty.WindowFor("claude-opus-5-5");

        Assert.Equal(MetricOrigin.Missing, window.Origin);
        Assert.Contains("claude-opus-5-5", window.Source, StringComparison.Ordinal);
        Assert.Equal(MetricOrigin.Missing, ModelWindowCatalog.Empty.WindowFor(null).Origin);
    }
}
