using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Desktop.Observability;

/// <summary>
/// The context occupancy of one step as a screen shows it (docs/14 D-12): the percentage with its origin
/// (<see cref="Occupancy"/>), the tokens and the window, the model of the step and the level against the thresholds.
/// </summary>
/// <param name="Occupancy">The percentage with its origin; the tokens alone when the window is not known yet (D-10).</param>
/// <param name="Tokens">«30.822 de 1.000.000 tokens», or why there is no percentage; null when the CLI does not expose it.</param>
/// <param name="Model">The model that served the step, when known.</param>
/// <param name="Level">The level of the thresholds; <see cref="ContextHealthLevel.Unknown"/> without a percentage.</param>
public sealed record ContextReading(Figure Occupancy, string? Tokens, string? Model, ContextHealthLevel Level)
{
    /// <summary>The level in the language in use; an unknown level is never «sano».</summary>
    public string LevelText => ContextText.Level(Level);

    /// <summary>
    /// One line with everything. What the CLI does not expose is only «no expuesto»: there is nothing to judge.
    /// </summary>
    public string Text => Tokens is null
        ? Occupancy.Text
        : string.Join(" · ", new[] { Occupancy.Text, Tokens, Model, LevelText }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>Reads the occupancy of a step against the configured thresholds (docs/14 D-09 to D-12).</summary>
public static class ContextText
{
    public static string Level(ContextHealthLevel level) => level switch
    {
        ContextHealthLevel.Healthy => Texts.Get("Observability_Level_Healthy"),
        ContextHealthLevel.Warning => Texts.Get("Observability_Level_Warning"),
        ContextHealthLevel.CompressionRecommended => Texts.Get("Observability_Level_Compression"),
        ContextHealthLevel.Critical => Texts.Get("Observability_Level_Critical"),
        _ => Texts.Get("Observability_Level_Unknown"),
    };

    /// <summary>
    /// The occupancy of a step: <paramref name="used"/> tokens of the prompt against the <paramref name="window"/> of
    /// its model. The percentage takes the weakest origin of the two (a window seen in an earlier run makes it an estimate
    /// of Agent Studio). Without a window the tokens are shown without a percentage; what the CLI does not expose says so.
    /// </summary>
    public static ContextReading Read(string? model, MetricValue<long> used, MetricValue<long> window, ContextHealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        var report = new ContextHealthEvaluator(thresholds).Evaluate(used, window);
        if (report.Utilization.HasValue)
        {
            var tokens = Texts.Format("Observability_Figure_TokensOf", Shown(used), Shown(window));
            return new ContextReading(MetricText.Percent(report.Utilization), tokens, model, report.Level);
        }

        if (used.HasValue)
        {
            // D-10: until a window of that model is known, the occupancy goes in tokens, without a percentage.
            var inTokens = MetricText.Of(used.Value, used.Origin, value => MetricText.Integer(value) + " tokens");
            return new ContextReading(inTokens, Texts.Get("Observability_Context_NoPercent"), model, ContextHealthLevel.Unknown);
        }

        return new ContextReading(MetricText.Tokens(used), null, model, ContextHealthLevel.Unknown);
    }

    /// <summary>A figure inside a sentence: its value, with «~» when it is an estimate.</summary>
    private static string Shown(MetricValue<long> value) => MetricText.Tokens(value).Shown;
}
