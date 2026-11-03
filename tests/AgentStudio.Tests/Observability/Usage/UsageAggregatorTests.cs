using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Usage;
using static AgentStudio.Tests.Observability.Usage.UsageAttempts;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// docs/14 D-04: the attempts of a period are filtered, grouped by the dimensions of the query in the user's zone and
/// added up with <see cref="MetricAggregate{T}"/>: origins are never mixed silently and a sum that misses values says so.
/// </summary>
public sealed class UsageAggregatorTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static readonly DateTimeOffset Day = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static UsageQuery Query(params UsageDimension[] groupBy) => new(Day.AddDays(-30), Day.AddDays(30), Madrid, groupBy);

    private static IReadOnlyList<string> Keys(UsageGroup group) => [.. group.Key.Select(k => k.Value)];

    // ---- selection ----

    [Fact]
    public void Only_the_attempts_that_ended_within_the_period_are_kept()
    {
        var from = Day;
        var to = Day.AddDays(1);
        var attempts = new[] { Make(from.AddSeconds(-1), run: "before"), Make(from, run: "first"), Make(to.AddSeconds(-1), run: "last"), Make(to, run: "after") };

        var kept = UsageAggregator.Select(attempts, new UsageQuery(from, to, Madrid, []));

        Assert.Equal(["first", "last"], kept.Select(a => a.RunId));
    }

    [Fact]
    public void A_filter_keeps_only_the_values_it_lists_and_an_empty_list_keeps_everything()
    {
        var attempts = new[]
        {
            Make(Day, run: "r1", agent: "ejecutor", purpose: AgentRunPurpose.Executor),
            Make(Day, run: "r2", provider: ProviderId.CodexCli, configuredModel: "gpt-6", agent: "juez", purpose: AgentRunPurpose.Judge),
            Make(Day, run: "r3", reportedModel: "claude-haiku-4-5", agent: "orquestador", purpose: AgentRunPurpose.Planner),
        };

        IReadOnlyList<string> Runs(UsageFilter filter) => [.. UsageAggregator.Select(attempts, Query() with { Filter = filter }).Select(a => a.RunId)];

        Assert.Equal(["r1", "r2", "r3"], Runs(new UsageFilter(Providers: [], Models: [])));
        Assert.Equal(["r2"], Runs(new UsageFilter(Providers: [ProviderId.CodexCli])));
        Assert.Equal(["r1", "r3"], Runs(new UsageFilter(Vendors: [VendorId.Anthropic])));
        Assert.Equal(["r3"], Runs(new UsageFilter(Models: ["claude-haiku-4-5"])));
        Assert.Equal(["r2"], Runs(new UsageFilter(Agents: [new AgentId("juez")])));
        Assert.Equal(["r1", "r3"], Runs(new UsageFilter(Purposes: [AgentRunPurpose.Executor, AgentRunPurpose.Planner])));
        Assert.Equal(["r1"], Runs(new UsageFilter(Runs: ["r1"])));
    }

    // ---- groups ----

    [Fact]
    public void Without_dimensions_there_is_only_the_grand_total()
    {
        var group = Assert.Single(UsageAggregator.Group([Make(Day), Make(Day, run: "r2")], Query()));

        Assert.Empty(group.Key);
        Assert.Equal(2, group.Totals.Attempts);
    }

    [Fact]
    public void The_grand_total_comes_first_and_then_one_group_per_key_in_the_order_of_the_dimensions()
    {
        var attempts = new[]
        {
            Make(Day, provider: ProviderId.CodexCli, configuredModel: "gpt-6"),
            Make(Day, reportedModel: "claude-sonnet-5-5"),
            Make(Day, reportedModel: "claude-haiku-4-5"),
            Make(Day, reportedModel: "claude-sonnet-5-5"),
        };

        var groups = UsageAggregator.Group(attempts, Query(UsageDimension.Provider, UsageDimension.Model));

        Assert.Empty(groups[0].Key);
        Assert.Equal(4, groups[0].Totals.Attempts);
        Assert.Equal(
            [["claude-code", "claude-haiku-4-5"], ["claude-code", "claude-sonnet-5-5"], ["codex-cli", "gpt-6"]],
            groups.Skip(1).Select(Keys));
        Assert.Equal([UsageDimension.Provider, UsageDimension.Model], groups[1].Key.Select(k => k.Dimension));
        Assert.Equal([1, 2, 1], groups.Skip(1).Select(g => g.Totals.Attempts));
    }

    [Fact]
    public void The_model_is_the_reported_one_else_the_configured_one_else_the_default_of_the_cli()
    {
        Assert.Equal("claude-sonnet-5-5-20261001", Make(Day, configuredModel: "claude-sonnet-5-5", reportedModel: "claude-sonnet-5-5-20261001").Model());
        Assert.Equal("claude-sonnet-5-5", Make(Day, configuredModel: "claude-sonnet-5-5").Model());
        Assert.Equal("predeterminado del CLI (no expuesto)", Make(Day, configuredModel: null).Model());
    }

    [Fact]
    public void Days_weeks_and_months_are_those_of_the_zone_across_the_end_of_summer_time_in_madrid()
    {
        // 2026-10-25 at 03:00 CEST the clock goes back to 02:00 CET: a fixed +02:00 would put the third attempt on the 26th.
        var attempts = new[]
        {
            Make(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero), run: "a"),
            Make(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), run: "b"),
            Make(new DateTimeOffset(2026, 10, 25, 22, 30, 0, TimeSpan.Zero), run: "c"),
            Make(new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero), run: "d"),
        };

        var days = UsageAggregator.Group(attempts, Query(UsageDimension.Day)).Skip(1).ToList();
        var weeks = UsageAggregator.Group(attempts, Query(UsageDimension.Week)).Skip(1).ToList();
        var months = UsageAggregator.Group(attempts, Query(UsageDimension.Month)).Skip(1).ToList();

        Assert.Equal([["2026-10-25"], ["2026-10-26"]], days.Select(Keys));
        Assert.Equal([3, 1], days.Select(g => g.Totals.Attempts));
        Assert.Equal([["2026-W43"], ["2026-W44"]], weeks.Select(Keys));
        Assert.Equal([["2026-10"]], months.Select(Keys));
    }

    [Fact]
    public void Days_are_those_of_the_zone_across_the_start_of_summer_time_in_madrid()
    {
        var attempts = new[]
        {
            Make(new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero)),
            Make(new DateTimeOffset(2026, 3, 29, 22, 30, 0, TimeSpan.Zero)),
        };

        var days = UsageAggregator.Group(attempts, new UsageQuery(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), Madrid, [UsageDimension.Day]));

        Assert.Equal([["2026-03-29"], ["2026-03-30"]], days.Skip(1).Select(Keys));
    }

    [Fact]
    public void An_iso_week_belongs_to_its_iso_year()
    {
        var utc = TimeZoneInfo.Utc;

        Assert.Equal("2026-W53", UsageAggregator.KeyOf(Make(new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero)), UsageDimension.Week, utc));
        Assert.Equal("2026-W53", UsageAggregator.KeyOf(Make(new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero)), UsageDimension.Week, utc));
        Assert.Equal("2027-W01", UsageAggregator.KeyOf(Make(new DateTimeOffset(2027, 1, 4, 12, 0, 0, TimeSpan.Zero)), UsageDimension.Week, utc));
    }

    [Fact]
    public void A_session_is_its_provider_and_its_id_and_values_that_are_not_there_have_a_label()
    {
        var utc = TimeZoneInfo.Utc;
        var planner = Make(Day, task: null, purpose: null, session: null);

        Assert.Equal("claude-code/s1", UsageAggregator.KeyOf(Make(Day, session: "s1"), UsageDimension.Session, utc));
        Assert.Equal("codex-cli/s1", UsageAggregator.KeyOf(Make(Day, provider: ProviderId.CodexCli, session: "s1"), UsageDimension.Session, utc));
        Assert.Equal("sin sesión", UsageAggregator.KeyOf(planner, UsageDimension.Session, utc));
        Assert.Equal("sin tarea", UsageAggregator.KeyOf(planner, UsageDimension.Task, utc));
        Assert.Equal("sin propósito (registro anterior a la Fase 4)", UsageAggregator.KeyOf(planner, UsageDimension.Purpose, utc));
        Assert.Equal("Judge", UsageAggregator.KeyOf(Make(Day, purpose: AgentRunPurpose.Judge), UsageDimension.Purpose, utc));
        Assert.Equal(UsageLogBuilder.ProjectId, UsageAggregator.KeyOf(planner, UsageDimension.Project, utc));
        Assert.Equal("anthropic", UsageAggregator.KeyOf(planner, UsageDimension.Vendor, utc));
        Assert.Equal("20261025-a1b2c3", UsageAggregator.KeyOf(planner, UsageDimension.Run, utc));
        Assert.Equal("ejecutor", UsageAggregator.KeyOf(planner, UsageDimension.Agent, utc));
    }

    // ---- totals ----

    [Fact]
    public void A_sum_with_values_missing_says_how_many_it_has()
    {
        var totals = UsageAggregator.Total([Make(Day, own: Tokens(100, 10)), Make(Day, own: Tokens(50, 5)), Make(Day, own: NoFigures(), outcome: AttemptOutcome.Interrupted)]);

        Assert.Equal(150L, totals.InputTokens.Sum);
        Assert.Equal((2, 1), (totals.InputTokens.Reported, totals.InputTokens.Missing));
        Assert.True(totals.InputTokens.IsPartial);
        Assert.Equal(MetricOrigin.ReportedByProvider, totals.InputTokens.Origin);
        Assert.Equal(3, totals.Attempts);
    }

    [Fact]
    public void A_sum_never_mixes_origins_silently_the_weakest_wins()
    {
        var totals = UsageAggregator.Total([Make(Day, own: Tokens(100)), Make(Day, own: Tokens(40, origin: MetricOrigin.EstimatedBySystem))]);

        Assert.Equal(140L, totals.InputTokens.Sum);
        Assert.Equal(MetricOrigin.EstimatedBySystem, totals.InputTokens.Origin);
        Assert.False(totals.InputTokens.IsPartial);
    }

    [Fact]
    public void A_sum_without_any_value_is_no_sum_and_never_a_zero()
    {
        var totals = UsageAggregator.Total([Make(Day, provider: ProviderId.CodexCli, own: Tokens(100, cost: null))]);

        Assert.Null(totals.CostUsd.Sum);
        Assert.Equal(MetricOrigin.NotExposed, totals.CostUsd.Origin);
        Assert.Equal((0, 1), (totals.CostUsd.Reported, totals.CostUsd.Missing));
    }

    [Fact]
    public void The_cost_of_claude_stays_an_estimate_of_the_cli()
    {
        var totals = UsageAggregator.Total([Make(Day, own: Tokens(1, cost: 0.25m)), Make(Day, own: Tokens(1, cost: 0.5m))]);

        Assert.Equal(0.75m, totals.CostUsd.Sum);
        Assert.Equal(MetricOrigin.EstimatedByProvider, totals.CostUsd.Origin);
    }

    [Fact]
    public void The_duration_is_measured_from_the_launch_and_an_older_record_estimates_it_from_the_start()
    {
        var measured = Make(Day, duration: TimeSpan.FromSeconds(30));
        var older = Make(Day, version: 1, duration: TimeSpan.FromSeconds(30), queued: TimeSpan.FromSeconds(5));
        var interrupted = Make(Day, outcome: AttemptOutcome.Interrupted, version: 1, own: NoFigures());
        var neverLaunched = Make(Day, outcome: AttemptOutcome.Failed, launched: false, own: NoFigures());

        Assert.Equal(MetricValue<long>.Measured(30_000, "medido por Agent Studio desde el arranque del proceso"), measured.Duration());
        Assert.Equal(35_000L, older.Duration().Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, older.Duration().Origin);
        Assert.Contains("espera en la cola", older.Duration().Source, StringComparison.Ordinal);
        Assert.Equal(MetricOrigin.Missing, interrupted.Duration().Origin);
        Assert.Equal(MetricOrigin.Missing, neverLaunched.Duration().Origin);
        Assert.Contains("no llegó a arrancar", neverLaunched.Duration().Source, StringComparison.Ordinal);

        var totals = UsageAggregator.Total([measured, older, interrupted]);
        Assert.Equal(65_000L, totals.DurationMilliseconds.Sum);
        Assert.Equal((MetricOrigin.EstimatedBySystem, 2, 1), (totals.DurationMilliseconds.Origin, totals.DurationMilliseconds.Reported, totals.DurationMilliseconds.Missing));
    }

    [Fact]
    public void Rate_limits_and_budget_limits_are_counted()
    {
        var totals = UsageAggregator.Total(
        [
            Make(Day, outcome: AttemptOutcome.RateLimited),
            Make(Day, outcome: AttemptOutcome.RateLimited),
            Make(Day, outcome: AttemptOutcome.LimitReached),
            Make(Day),
        ]);

        Assert.Equal((4, 2, 1), (totals.Attempts, totals.RateLimited, totals.LimitReached));
    }

    [Fact]
    public void The_context_peak_of_a_group_is_its_largest_step()
    {
        var totals = UsageAggregator.Total(
        [
            Make(Day, peak: Peak("claude-sonnet-5-5", 30_822, 1_000_000)),
            Make(Day, peak: Peak("claude-haiku-4-5", 150_000, 200_000)),
            Make(Day, provider: ProviderId.CodexCli, peak: NoPeak()),
        ]);

        Assert.Equal("claude-haiku-4-5", totals.ContextPeak?.Model);
        Assert.Equal(150_000L, totals.ContextPeak?.UsedTokens.Value);
    }

    [Fact]
    public void A_group_without_any_known_step_has_the_weakest_reason_as_its_peak()
    {
        var totals = UsageAggregator.Total([Make(Day, provider: ProviderId.CodexCli, peak: NoPeak()), Make(Day, peak: NoPeak(MetricOrigin.Missing, "no llegó ningún paso"))]);

        Assert.Null(totals.ContextPeak?.UsedTokens.Value);
        Assert.Equal(MetricOrigin.Missing, totals.ContextPeak?.UsedTokens.Origin);
    }

    [Fact]
    public void Compactions_that_a_cli_does_not_expose_are_never_zero()
    {
        var claude = Make(Day, compactions: 2);
        var codex = Make(Day, provider: ProviderId.CodexCli, compactions: null);
        var older = Make(Day, version: 1, compactions: null);

        Assert.Equal(MetricValue<long>.Measured(2, "compactaciones observadas por Agent Studio (compact_boundary)"), claude.CompactionCount());
        Assert.Equal(MetricOrigin.NotExposed, codex.CompactionCount().Origin);
        Assert.Equal(MetricOrigin.Missing, older.CompactionCount().Origin);

        var codexOnly = UsageAggregator.Total([codex]);
        Assert.Null(codexOnly.Compactions.Sum);
        Assert.Equal(MetricOrigin.NotExposed, codexOnly.Compactions.Origin);
        Assert.Equal(2L, UsageAggregator.Total([claude, codex]).Compactions.Sum);
    }
}
