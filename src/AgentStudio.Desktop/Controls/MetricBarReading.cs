using AgentStudio.Domain.Metrics;

namespace AgentStudio.Desktop.Controls;

/// <summary>
/// The level a bar is drawn with: the thresholds of the context (docs/14 D-11) or nothing to judge. The colour of the fill
/// follows it, and the words always say it too (<see cref="MetricBarReading.LevelText"/>): nothing is said with colour alone.
/// </summary>
public enum MetricBarLevel
{
    /// <summary>Nothing to judge (a budget, the tokens, the quota of an account): the accent.</summary>
    Neutral,

    /// <summary>«sano»: under the threshold of warning.</summary>
    Healthy,

    /// <summary>«aviso».</summary>
    Warning,

    /// <summary>«compresión recomendada».</summary>
    Compression,

    /// <summary>«crítico», or a limit the provider says was reached.</summary>
    Critical,
}

/// <summary>
/// What a <see cref="MetricBar"/> shows (docs/18 P3, tanda 2): a value against its maximum, with the text of the value
/// («62 %», «~62 %», «3 de 12»), where it comes from (docs/14 D-03), the level of the thresholds when there is one, and the
/// detail. Without a value (<see cref="Value"/> null) the bar is an empty track and <see cref="ValueText"/> says why: «no
/// expuesto», «sin dato», «sin observaciones todavía»; it is never a bar at zero.
/// </summary>
public sealed record MetricBarReading
{
    /// <summary>What the bar measures: «Contexto», «Claude · 5 horas», «Presupuesto».</summary>
    public required string Label { get; init; }

    /// <summary>The value against <see cref="Maximum"/>; null when there is none (the track is drawn empty).</summary>
    public double? Value { get; init; }

    /// <summary>The maximum of the value: 100 for a percentage, the budget for a count.</summary>
    public double Maximum { get; init; } = 100;

    /// <summary>The value as it is read: «62 %», «~62 %» for an estimate, «3 de 12»; without a value, why there is none.</summary>
    public required string ValueText { get; init; }

    /// <summary>Where the value comes from, in the words of docs/14 D-03: «exacto · informado por el CLI», «estimado por Agent Studio»…</summary>
    public required string Origin { get; init; }

    /// <summary>The kind of the origin, for its short form in the status bar.</summary>
    public MetricOrigin OriginKind { get; init; } = MetricOrigin.Missing;

    /// <summary>The value is an estimate: the fill carries a pattern, and the text «~» and «estimado».</summary>
    public bool IsEstimate { get; init; }

    public MetricBarLevel Level { get; init; }

    /// <summary>The level in words («sano», «aviso», «compresión recomendada», «crítico», «sin evaluar»); null when nothing is judged.</summary>
    public string? LevelText { get; init; }

    /// <summary>What the line does not say: «30.822 de 1.000.000 tokens · claude-sonnet-5-5», «se reinicia hoy a las 15:00».</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// How old the figure is, said next to it in every form of the bar, the short one included: «vista hace 12 min» for the
    /// quota of an account, which the provider announced at some moment (docs/14 D-14). Null for a figure of now.
    /// </summary>
    public string? Age { get; init; }

    /// <summary>Where the thresholds are, as fractions of the maximum (0 to 1): small marks on the track.</summary>
    public IReadOnlyList<double> Marks { get; init; } = [];

    /// <summary>The share of the maximum, from 0 to 1; null without a value. A value over the maximum fills the track.</summary>
    public double? Fraction => Value is { } value && Maximum > 0 ? Math.Clamp(value / Maximum, 0, 1) : null;

    public bool HasValue => Fraction is not null;

    /// <summary>The origin in one word, for the status bar: «exacto», «estimado», «no expuesto», «sin dato».</summary>
    public string ShortOrigin => OriginKind switch
    {
        MetricOrigin.ReportedByProvider or MetricOrigin.MeasuredBySystem => "exacto",
        MetricOrigin.EstimatedByProvider or MetricOrigin.EstimatedBySystem => "estimado",
        MetricOrigin.NotExposed => "no expuesto",
        _ => "sin dato",
    };

    /// <summary>The line of the bar: the value, its origin, its level and its age, without repeating what is already said.</summary>
    public string Text
    {
        get
        {
            var parts = new List<string> { ValueText };
            if (!string.IsNullOrWhiteSpace(Origin) && !string.Equals(Origin, ValueText, StringComparison.Ordinal))
            {
                parts.Add(Origin);
            }

            if (!string.IsNullOrWhiteSpace(LevelText))
            {
                parts.Add(LevelText);
            }

            if (!string.IsNullOrWhiteSpace(Age))
            {
                parts.Add(Age);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The short line of the status bar: the value with the origin in one word, the level written when the colour says one
    /// («sano», «crítico», «presupuesto agotado»: never the colour alone) and the age of the figure; without a value, only why.
    /// </summary>
    public string CompactText
    {
        get
        {
            if (!HasValue)
            {
                return ValueText;
            }

            var parts = new List<string> { $"{ValueText} {ShortOrigin}" };
            if (Level != MetricBarLevel.Neutral && !string.IsNullOrWhiteSpace(LevelText))
            {
                parts.Add(LevelText);
            }

            if (!string.IsNullOrWhiteSpace(Age))
            {
                parts.Add(Age);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>What a screen reader says: what is measured, the value, its origin, its level, its age and the detail.</summary>
    public string AccessibleName => Detail is { Length: > 0 } detail ? $"{Label}: {Text}. {detail}" : $"{Label}: {Text}";

    /// <summary>A reading shown as an item of a list (the bars of the status bar) is named by UI Automation with this: its whole line.</summary>
    public override string ToString() => AccessibleName;

    /// <summary>Two readings are equal when they say the same, marks included: a screen that builds an equal one changes nothing.</summary>
    public bool Equals(MetricBarReading? other) =>
        other is not null
        && string.Equals(Label, other.Label, StringComparison.Ordinal)
        && Value == other.Value
        && Maximum.Equals(other.Maximum)
        && string.Equals(ValueText, other.ValueText, StringComparison.Ordinal)
        && string.Equals(Origin, other.Origin, StringComparison.Ordinal)
        && OriginKind == other.OriginKind
        && IsEstimate == other.IsEstimate
        && Level == other.Level
        && string.Equals(LevelText, other.LevelText, StringComparison.Ordinal)
        && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
        && string.Equals(Age, other.Age, StringComparison.Ordinal)
        && Marks.SequenceEqual(other.Marks);

    public override int GetHashCode() => HashCode.Combine(Label, Value, ValueText, Origin, Level, IsEstimate, Detail, Age);
}
