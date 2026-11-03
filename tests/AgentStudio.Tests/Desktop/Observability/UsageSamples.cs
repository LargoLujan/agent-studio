using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// Usage reports as the projection of WP2 gives them (docs/04 section 16.4), built by hand: a day of Claude Code and
/// Codex, a partial sum (an interrupted attempt), what Codex does not expose, a v1 attempt without breakdown, a project
/// left out and the reconciliation with the journal.
/// </summary>
internal static class UsageSamples
{
    public static readonly DateTimeOffset Day = new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(2));

    public static UsageReport Empty { get; } = new([], [], [], new UsageReconciliation(null, 0, []));

    public static MetricAggregate<long> Tokens(long sum, int reported = 1, int missing = 0) =>
        new(sum, MetricOrigin.ReportedByProvider, reported, missing, ["claude result.usage"]);

    public static MetricAggregate<long> NotExposed(int count) => new(null, MetricOrigin.NotExposed, 0, count, ["codex exec does not report it"]);

    public static MetricAggregate<long> Measured(long sum, int count = 1) => new(sum, MetricOrigin.MeasuredBySystem, count, 0, ["process start to finish"]);

    public static UsageTotals ClaudeTotals { get; } = new(
        Attempts: 14,
        DurationMilliseconds: Measured(2_460_000, 14),
        InputTokens: Tokens(1_200_000, 14),
        CacheReadInputTokens: Tokens(900_000, 14),
        CacheWriteInputTokens: Tokens(250_000, 14),
        OutputTokens: Tokens(96_000, 14),
        ReasoningOutputTokens: NotExposed(14),
        CostUsd: new MetricAggregate<decimal>(3.25m, MetricOrigin.EstimatedByProvider, 14, 0, ["claude total_cost_usd"]),
        RateLimited: 0,
        LimitReached: 0,
        ContextPeak: new ContextPeakFigures("claude-sonnet-5-5", MetricValue<long>.Exact(300_000, "claude assistant.usage"), MetricValue<long>.Exact(1_000_000, "claude modelUsage")),
        Compactions: new MetricAggregate<long>(0, MetricOrigin.ReportedByProvider, 14, 0, ["claude compact_boundary"]));

    public static UsageTotals CodexTotals { get; } = new(
        Attempts: 9,
        DurationMilliseconds: Measured(1_320_000, 9),
        InputTokens: Tokens(640_000, 8, 1),
        CacheReadInputTokens: Tokens(400_000, 8, 1),
        CacheWriteInputTokens: Tokens(0, 8, 1),
        OutputTokens: Tokens(51_000, 8, 1),
        ReasoningOutputTokens: Tokens(12_000, 8, 1),
        CostUsd: new MetricAggregate<decimal>(null, MetricOrigin.NotExposed, 0, 9, ["codex has no cost"]),
        RateLimited: 1,
        LimitReached: 0,
        ContextPeak: new ContextPeakFigures(null, MetricValue<long>.NotExposed("codex exec"), MetricValue<long>.NotExposed("codex exec")),
        Compactions: NotExposed(9));

    public static UsageGroup Group(UsageTotals totals, params (UsageDimension Dimension, string Value)[] key) =>
        new([.. key.Select(k => new UsageGroupKey(k.Dimension, k.Value))], totals);

    public static UsageFigures Figures(long input, long output, decimal? cost) => new(
        MetricValue<long>.Exact(input, "claude result.usage"),
        MetricValue<long>.Exact(0, "claude result.usage"),
        MetricValue<long>.Exact(0, "claude result.usage"),
        MetricValue<long>.Exact(output, "claude result.usage"),
        MetricValue<long>.NotExposed("claude does not split reasoning"),
        cost is { } usd ? MetricValue<decimal>.Estimated(usd, "claude total_cost_usd") : MetricValue<decimal>.NotExposed("codex has no cost"));

    /// <summary>A v2 attempt of the executor on Claude, with its breakdown by model and a peak judged against a learned window.</summary>
    public static UsageAttempt ClaudeAttempt { get; } = new(
        "p-1",
        "20261018-c4a1f2",
        "T1",
        1,
        AgentRunPurpose.Executor,
        new AgentId("ejecutor"),
        ProviderId.ClaudeCode,
        VendorId.Anthropic,
        ConfiguredModel: null,
        ReportedModel: "claude-sonnet-5-5",
        SessionId: "s-1",
        RecordedAt: Day,
        LaunchedAt: Day.AddSeconds(5),
        FinishedAt: Day.AddSeconds(5).AddMinutes(3),
        AttemptOutcome.Succeeded,
        Own: Figures(31_000, 1_200, 0.12m),
        SessionTotal: null,
        PerModel:
        [
            new ModelUsageFigures("claude-sonnet-5-5", Figures(30_000, 1_100, 0.11m), MetricValue<long>.Exact(1_000_000, "claude modelUsage"), "list"),
            new ModelUsageFigures("claude-haiku-4-5-20251001", Figures(1_000, 100, 0.01m), MetricValue<long>.Exact(200_000, "claude modelUsage"), "list"),
        ],
        ContextPeak: new ContextPeakFigures("claude-sonnet-5-5", MetricValue<long>.Exact(300_000, "claude assistant.usage"), MetricValue<long>.EstimatedBySystem(1_000_000, "window seen in an earlier run")),
        Compactions: 0)
    { RecordedVersion = 2 };

    /// <summary>A v1 attempt of the judge on Codex, recorded before Phase 4: no launch time, no breakdown, no model.</summary>
    public static UsageAttempt CodexV1Attempt { get; } = new(
        "p-1",
        "20261018-c4a1f2",
        "R1",
        1,
        Purpose: null,
        new AgentId("juez"),
        ProviderId.CodexCli,
        VendorId.OpenAI,
        ConfiguredModel: null,
        ReportedModel: null,
        SessionId: "s-2",
        RecordedAt: Day.AddMinutes(10),
        LaunchedAt: null,
        FinishedAt: Day.AddMinutes(14),
        AttemptOutcome.RateLimited,
        Own: Figures(640_000, 51_000, null),
        SessionTotal: null,
        PerModel: [],
        ContextPeak: null,
        Compactions: null)
    { RecordedVersion = 1 };

    public static UsageReport Day1 { get; } = new(
        [ClaudeAttempt, CodexV1Attempt],
        [
            Group(ClaudeTotals, (UsageDimension.Provider, "claude-code"), (UsageDimension.Model, "claude-sonnet-5-5")),
            Group(CodexTotals, (UsageDimension.Provider, "codex-cli"), (UsageDimension.Model, "predeterminado del CLI (no expuesto)")),
        ],
        [new ExcludedProject(@"C:\otros\viejo", null, "su registro no verifica: la cadena se rompe en la entrada 12")],
        new UsageReconciliation(25, 23, ["2 prompts de la bitácora no tienen intento en el registro: pruebas en vivo o una versión anterior."]));
}
