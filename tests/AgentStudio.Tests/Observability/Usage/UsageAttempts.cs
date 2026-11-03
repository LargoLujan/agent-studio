using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>Attempts built directly, so the aggregation and the export are tested without the projection.</summary>
internal static class UsageAttempts
{
    public const string Source = "claude result.usage";

    public static UsageFigures Tokens(long input, long output = 0, long read = 0, long write = 0, long? reasoning = null, decimal? cost = null, MetricOrigin origin = MetricOrigin.ReportedByProvider, string source = Source) =>
        new(
            MetricValue<long>.Of(origin, input, source),
            MetricValue<long>.Of(origin, read, source),
            MetricValue<long>.Of(origin, write, source),
            MetricValue<long>.Of(origin, output, source),
            reasoning is { } r ? MetricValue<long>.Of(origin, r, source) : MetricValue<long>.NotExposed("claude no informa del razonamiento"),
            cost is { } c ? MetricValue<decimal>.Estimated(c, "estimación del CLI") : MetricValue<decimal>.NotExposed("codex exec --json does not report cost"));

    public static UsageFigures NoFigures(string reason = "intento sin resultado") =>
        new(
            MetricValue<long>.Missing(reason),
            MetricValue<long>.Missing(reason),
            MetricValue<long>.Missing(reason),
            MetricValue<long>.Missing(reason),
            MetricValue<long>.Missing(reason),
            MetricValue<decimal>.Missing(reason));

    public static ContextPeakFigures Peak(string? model, long used, long window) =>
        new(model, MetricValue<long>.Exact(used, "claude assistant.message.usage"), MetricValue<long>.Exact(window, "claude result.modelUsage"));

    public static ContextPeakFigures NoPeak(MetricOrigin origin = MetricOrigin.NotExposed, string reason = "el CLI no informa de la ocupación del contexto") =>
        new(null, MetricValue<long>.Of(origin, null, reason), MetricValue<long>.Of(origin, null, reason));

    public static UsageAttempt Make(
        DateTimeOffset finishedAt,
        ProviderId? provider = null,
        string? configuredModel = "claude-sonnet-5-5",
        string? reportedModel = null,
        string run = "20261025-a1b2c3",
        string? task = "T1",
        int attempt = 1,
        AgentRunPurpose? purpose = AgentRunPurpose.Executor,
        string agent = "ejecutor",
        string? session = "s1",
        string projectId = UsageLogBuilder.ProjectId,
        UsageFigures? own = null,
        UsageFigures? sessionTotal = null,
        IReadOnlyList<ModelUsageFigures>? perModel = null,
        AttemptOutcome outcome = AttemptOutcome.Succeeded,
        int version = 2,
        TimeSpan? duration = null,
        TimeSpan? queued = null,
        bool launched = true,
        ContextPeakFigures? peak = null,
        int? compactions = null)
    {
        var on = provider ?? ProviderId.ClaudeCode;
        var length = duration ?? TimeSpan.FromSeconds(30);
        var launchedAt = finishedAt - length;
        DateTimeOffset? launch = version >= AgentRunFinishedPayload.UsageVersion && launched ? launchedAt : null;
        return new UsageAttempt(
            projectId,
            run,
            task,
            attempt,
            purpose,
            new AgentId(agent),
            on,
            UsageLogBuilder.VendorOf(on),
            configuredModel,
            reportedModel,
            session,
            launchedAt - (queued ?? TimeSpan.FromSeconds(2)),
            launch,
            finishedAt,
            outcome,
            own ?? Tokens(100, 10, cost: 0.01m),
            sessionTotal,
            perModel ?? [],
            peak ?? NoPeak(),
            compactions)
        {
            RecordedVersion = version,
        };
    }
}
