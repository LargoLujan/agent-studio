using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Context;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>
/// The bars of the cards of the monitor that Phase 13 adds (docs/17 D-12 and D-13, F13-6), built with the common bar
/// (<see cref="MetricBarReading"/>): the context by parts and the consumption of the finished attempts. Their words come from
/// the resources. A figure that is not there is an empty track that says why («no expuesto», «sin intentos todavía»): never a
/// bar at zero, and an estimate says so.
/// </summary>
public static class MonitorBars
{
    /// <summary>
    /// The bar of the context by parts (D-13): the parts of the prompt, estimated by Agent Studio from the characters that
    /// <c>AgentRunStarted</c> version 3 recorded, and, once the CLI reports the occupancy of the step, the conversation and the
    /// tools as what it reports beyond their sum.
    /// <list type="bullet">
    /// <item>Without parts (a start of before version 3, or a log that could not be read) it is an empty track that says why.</item>
    /// <item>Before the CLI reports the occupancy, or when it does not expose it (<c>codex exec</c>), the track is empty and the
    /// words give the tokens of the parts «antes de empezar»; in Codex they say that the occupancy in progress is not exposed.</item>
    /// <item>With the occupancy and the window of the model, it is the bar of the context with the parts as segments.</item>
    /// </list>
    /// </summary>
    public static MetricBarReading PartsBar(
        IReadOnlyList<ContextPartSnapshot>? parts,
        bool readFailed,
        string? model,
        MetricValue<long> used,
        MetricValue<long> window,
        ContextHealthThresholds thresholds,
        IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        var t = texts ?? Texts.Current;
        var label = t.Get("Monitor_Bar_Parts");
        if (parts is not { Count: > 0 })
        {
            return MetricBars.Empty(label, t.Get(readFailed ? "Monitor_Parts_Read" : "Monitor_Parts_NoBreakdown"));
        }

        var breakdown = ContextBreakdown.Of(parts, used);
        long? windowTokens = window.Value is > 0 ? window.Value : null;
        double InUnit(long tokens) => windowTokens is { } w ? tokens * 100d / w : tokens;
        string Tokens(long tokens) => t.Format("Monitor_Parts_Tokens", MetricText.Integer(tokens));

        var segments = new List<MetricBarSegment>();
        foreach (var part in breakdown.Parts)
        {
            var tokens = part.Tokens.Value ?? 0;
            segments.Add(new MetricBarSegment(ContextPartTexts.Label(part.Kind, t), InUnit(tokens), Tokens(tokens), IsEstimate: true));
        }

        if (breakdown.Conversation.Value is { } conversation)
        {
            segments.Add(new MetricBarSegment(t.Get(ContextPartTexts.ConversationKey), InUnit(conversation), Tokens(conversation), IsEstimate: true));
        }

        var origin = t.Get("Monitor_Parts_Origin");
        if (used.HasValue)
        {
            // The CLI reported the occupancy: the common bar of the context, with the parts as its segments.
            var context = MetricBars.Context(label, model, used, window, thresholds);
            var unreliable = breakdown.Conversation.Origin == MetricOrigin.Missing && breakdown.Preamble.HasValue ? t.Get("Monitor_Parts_Unreliable") : null;
            return context with { Segments = segments, Detail = Join(context.Detail, origin, unreliable) };
        }

        // Before the first step, or a CLI that does not expose it: only what was in the prompt before the launch is known.
        var preamble = breakdown.Preamble.Value ?? 0;
        var notExposed = used.Origin == MetricOrigin.NotExposed ? t.Get("Monitor_Parts_NotExposed") : null;
        return new MetricBarReading
        {
            Label = label,
            ValueText = t.Format("Monitor_Parts_BeforeStart", MetricText.Integer(preamble)),
            Origin = origin,
            OriginKind = MetricOrigin.EstimatedBySystem,
            IsEstimate = true,
            Marks = [thresholds.Warning, thresholds.Compress, thresholds.Critical],
            Segments = segments,
            Detail = Join(notExposed, windowTokens is null ? t.Get("Monitor_Parts_NoWindow") : null),
        };
    }

    /// <summary>
    /// The consumption of the finished attempts (D-12): the tokens of their prompts and their outputs, each attempt as its CLI
    /// reported it, with the weakest origin and «parcial» when one is missing. There is no maximum to draw against, so the track
    /// is empty and the words carry the figure. Without finished attempts: «sin intentos todavía».
    /// </summary>
    public static MetricBarReading ConsumptionBar(IReadOnlyList<TokenUsage> attempts, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var t = texts ?? Texts.Current;
        var label = t.Get("Monitor_Bar_Consumption");
        if (attempts.Count == 0)
        {
            return MetricBars.Empty(label, t.Get("Monitor_Bar_NoAttempts"), MetricOrigin.Missing, t.Get("Monitor_Bar_NoAttemptsDetail"));
        }

        var totals = attempts.Select(Total).ToList();
        var detail = Join(
            t.Format(
                "Monitor_Consumption_Detail",
                MetricText.Tokens(MetricAggregate.Sum(attempts.Select(a => a.InputTokens))).Shown,
                MetricText.Tokens(MetricAggregate.Sum(attempts.Select(a => a.CachedInputTokens))).Shown,
                MetricText.Tokens(MetricAggregate.Sum(attempts.Select(a => a.OutputTokens))).Shown),
            t.Format("Monitor_Consumption_Attempts", MetricText.Integer(attempts.Count)));
        return MetricBars.Tokens(label, MetricAggregate.Sum(totals), null, detail);
    }

    /// <summary>The tokens of one attempt: its prompt (with the cache) and its output; a figure that is missing leaves the total missing.</summary>
    internal static MetricValue<long> Total(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.InputTokens.Value is { } input && usage.OutputTokens.Value is { } output)
        {
            var origin = MetricOrigins.Weakest(usage.InputTokens.Origin, usage.OutputTokens.Origin);
            return MetricValue<long>.Of(origin, input + output, usage.InputTokens.Source);
        }

        return usage.InputTokens.HasValue ? usage.OutputTokens : usage.InputTokens;
    }

    /// <summary>A sum of tokens as a figure again: the sum with its origin, or why there is none.</summary>
    internal static MetricValue<long> ToValue(MetricAggregate<long> sum, string source) =>
        MetricValue<long>.Of(sum.Origin, sum.Sum, sum.Sources.Count > 0 ? sum.Sources[0] : source);

    private static string? Join(params string?[] parts)
    {
        var shown = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return shown.Count == 0 ? null : string.Join(" · ", shown);
    }
}
