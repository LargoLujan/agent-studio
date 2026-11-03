using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/15 §5.5: what the events of one launch of an agent said about it (launch time, model of the last step, the step
/// with the largest context, compactions), shared by the auxiliary runner, the recorder of the judge and the turns of a
/// debate, which kept three copies of it.
/// </summary>
public sealed class AgentRunObservationsTests
{
    private static ContextObserved Step(int seconds, string? model, long tokens, long? window = null) =>
        new(T0.AddSeconds(seconds), model, MetricValue<long>.Exact(tokens, "claude assistant.message.usage"),
            window is { } w ? MetricValue<long>.EstimatedBySystem(w, "ventana vista antes") : MetricValue<long>.Missing("window not known yet"));

    private static TokenUsage UsageOf(params ModelUsage[] perModel) => new(
        MetricValue<long>.Exact(1_000, "claude result.usage"),
        MetricValue<long>.Exact(10, "claude result.usage"),
        MetricValue<long>.Exact(0, "claude result.usage"),
        MetricValue<long>.Unavailable("n/a"),
        MetricValue<decimal>.Unavailable("n/a"))
    {
        PerModel = perModel,
    };

    private static ModelUsage Model(string name, long window) => new(
        name,
        MetricValue<long>.Exact(1, "m"),
        MetricValue<long>.Exact(1, "m"),
        MetricValue<long>.Exact(0, "m"),
        MetricValue<long>.Exact(0, "m"),
        MetricValue<decimal>.Estimated(0.01m, "m"),
        MetricValue<long>.Exact(window, "claude result.modelUsage"),
        "list");

    [Fact]
    public void A_launch_time_given_stays_and_without_one_it_is_when_the_first_event_came()
    {
        var given = new AgentRunObservations(reportsContext: false, launchedAt: T0);
        var seen = new AgentRunObservations(reportsContext: false);

        given.Observe(new SessionStarted(T0.AddSeconds(5), "s1", null), T0.AddSeconds(5));
        seen.Observe(new SessionStarted(T0.AddSeconds(5), "s1", null), T0.AddSeconds(6));
        seen.Observe(new MessageEmitted(T0.AddSeconds(9), "hola", false), T0.AddSeconds(9));

        Assert.Equal(T0, given.LaunchedAt);
        Assert.Equal(T0.AddSeconds(6), seen.LaunchedAt);
        Assert.Null(new AgentRunObservations(reportsContext: false).LaunchedAt);
    }

    [Fact]
    public void The_session_and_the_usage_are_the_last_ones_the_events_reported()
    {
        var observations = new AgentRunObservations(reportsContext: true, resumedSession: "sess-0");
        var first = UsageOf();
        var last = UsageOf(Model("claude-haiku-4-5", 200_000));

        observations.Observe(new SessionStarted(T0, "sess-1", "claude-haiku-4-5"), T0);
        observations.Observe(new UsageReported(T0, first), T0);
        observations.Observe(new UsageReported(T0, last), T0);

        Assert.Equal(("sess-1", "sess-0"), (observations.SessionId, observations.ResumedSession));
        Assert.Same(last, observations.Usage);
    }

    /// <summary>docs/14 D-13: never the model of <see cref="SessionStarted"/>, which a CLI may announce for an auxiliary model.</summary>
    [Fact]
    public void The_reported_model_is_that_of_the_last_step_or_without_steps_the_only_one_of_the_breakdown()
    {
        var stepped = new AgentRunObservations(reportsContext: true);
        stepped.Observe(new SessionStarted(T0, "s", "claude-opus-4-1"), T0);
        stepped.Observe(Step(1, "claude-sonnet-5-5", 30_000), T0);
        stepped.Observe(Step(2, null, 31_000), T0);

        var unstepped = new AgentRunObservations(reportsContext: false);
        unstepped.Observe(new SessionStarted(T0, "s", "claude-opus-4-1"), T0);

        Assert.Equal("claude-sonnet-5-5", stepped.ReportedModel(null));
        Assert.Equal("gpt-5.5", unstepped.ReportedModel(UsageOf(Model("gpt-5.5", 0))));
        Assert.Null(unstepped.ReportedModel(UsageOf(Model("a", 0), Model("b", 0))));
        Assert.Null(unstepped.ReportedModel(null));
    }

    [Fact]
    public void The_peak_is_the_step_with_the_largest_prompt_with_the_window_its_model_reported_at_the_end()
    {
        var observations = new AgentRunObservations(reportsContext: true);
        observations.Observe(Step(1, "claude-sonnet-5-5", 30_000), T0);
        observations.Observe(Step(2, "claude-sonnet-5-5", 52_000, window: 200_000), T0);
        observations.Observe(Step(3, "claude-sonnet-5-5", 41_000), T0);

        var withBreakdown = observations.PeakSnapshot(UsageOf(Model("claude-sonnet-5-5-20260101", 1_000_000)));
        var withoutBreakdown = observations.PeakSnapshot(null);

        Assert.Equal(52_000, observations.Peak!.PromptTokens.Value);
        Assert.Equal(52_000m, withBreakdown!.UsedTokens.Value);
        Assert.Equal((1_000_000m, MetricOrigin.ReportedByProvider), (withBreakdown.ContextWindow.Value, withBreakdown.ContextWindow.Origin!.Value));
        Assert.Equal((200_000m, MetricOrigin.EstimatedBySystem), (withoutBreakdown!.ContextWindow.Value, withoutBreakdown.ContextWindow.Origin!.Value));
        Assert.Null(new AgentRunObservations(reportsContext: true).PeakSnapshot(null));
    }

    [Fact]
    public void Compactions_are_counted_when_the_cli_reports_its_context_and_not_exposed_otherwise()
    {
        var reporting = new AgentRunObservations(reportsContext: true);
        var silent = new AgentRunObservations(reportsContext: false);
        foreach (var observations in new[] { reporting, silent })
        {
            observations.Observe(new CompactionObserved(T0, "auto", MetricValue<long>.Exact(180_000, "claude compact_boundary")), T0);
            observations.Observe(new CompactionObserved(T0, "manual", MetricValue<long>.Exact(90_000, "claude compact_boundary")), T0);
        }

        Assert.Equal(2, reporting.Compactions);
        Assert.Null(silent.Compactions);
        Assert.Equal(0, new AgentRunObservations(reportsContext: true).Compactions);
    }

    [Fact]
    public void Events_observed_from_several_threads_are_all_counted()
    {
        var observations = new AgentRunObservations(reportsContext: true);

        Parallel.For(0, 500, i => observations.Observe(new CompactionObserved(T0, "auto", MetricValue<long>.Exact(i, "x")), T0.AddSeconds(i)));

        Assert.Equal(500, observations.Compactions);
    }
}
