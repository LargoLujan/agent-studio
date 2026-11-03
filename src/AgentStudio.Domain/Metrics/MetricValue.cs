namespace AgentStudio.Domain.Metrics;

/// <summary>
/// How sure a number is, in three levels. The UI must always show it: an estimate is never presented as a measurement,
/// and a missing value is shown as unavailable rather than as zero (master prompt, sections 10 and 11). It is derived from
/// <see cref="MetricOrigin"/>, which also says who gave the number.
/// </summary>
public enum MetricProvenance
{
    /// <summary>Reported by the provider or measured by Agent Studio.</summary>
    Exact,

    /// <summary>An estimate: the provider's (a list-price cost) or Agent Studio's (characters divided by an average).</summary>
    Estimated,

    /// <summary>There is no value: the provider does not expose it, or it did not arrive.</summary>
    Unavailable,
}

/// <summary>
/// Who gave a number, as the UI labels it (docs/14 D-03). The origins go from the strongest to the weakest, so the
/// weakest of several is the greatest (<see cref="MetricOrigins.Weakest"/>).
/// </summary>
public enum MetricOrigin
{
    /// <summary>«exacto · informado por el CLI»: the provider reported it (tokens, quota utilization).</summary>
    ReportedByProvider,

    /// <summary>«exacto · medido por Agent Studio»: Agent Studio measured it (attempts, durations, rate-limit errors).</summary>
    MeasuredBySystem,

    /// <summary>«estimado por el CLI»: the provider gave it as an estimate (Claude's list-price cost, live thinking tokens).</summary>
    EstimatedByProvider,

    /// <summary>«estimado por Agent Studio»: computed from partial data (characters divided by 4, a window seen in an earlier run).</summary>
    EstimatedBySystem,

    /// <summary>«no expuesto»: the provider does not offer it (cost and context window of Codex, everything of Gemini).</summary>
    NotExposed,

    /// <summary>«sin dato»: the provider offers it, but it did not arrive (an interrupted attempt).</summary>
    Missing,
}

/// <summary>What an origin implies, and how several origins combine.</summary>
public static class MetricOrigins
{
    /// <summary>The three-level provenance of an origin.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The origin is not one of the six.</exception>
    public static MetricProvenance ToProvenance(this MetricOrigin origin) => origin switch
    {
        MetricOrigin.ReportedByProvider or MetricOrigin.MeasuredBySystem => MetricProvenance.Exact,
        MetricOrigin.EstimatedByProvider or MetricOrigin.EstimatedBySystem => MetricProvenance.Estimated,
        MetricOrigin.NotExposed or MetricOrigin.Missing => MetricProvenance.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown metric origin."),
    };

    /// <summary>Whether a number with this origin has a value: reported, measured and estimated ones do; the others never.</summary>
    public static bool CarriesValue(this MetricOrigin origin) => origin.ToProvenance() != MetricProvenance.Unavailable;

    /// <summary>
    /// The origin of a number derived from several: the weakest one wins, so an estimate is never presented as exact.
    /// With no input there is no figure: <see cref="MetricOrigin.Missing"/>.
    /// </summary>
    public static MetricOrigin Weakest(params MetricOrigin[] origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        return origins.Length == 0 ? MetricOrigin.Missing : origins.Max();
    }
}

public readonly record struct MetricValue<T>
    where T : struct
{
    private MetricValue(T? value, MetricOrigin origin, string source)
    {
        Value = value;
        Origin = origin;
        Source = source;
    }

    public T? Value { get; }

    /// <summary>Who gave the number (docs/14 D-03).</summary>
    public MetricOrigin Origin { get; }

    /// <summary>The three levels of <see cref="Origin"/>: exact, estimated or unavailable.</summary>
    public MetricProvenance Provenance => Origin.ToProvenance();

    /// <summary>Human-readable origin, e.g. "claude result.usage" or "estimate: 4 chars/token".</summary>
    public string Source { get; }

    public bool HasValue => Value.HasValue;

    /// <summary>A figure the provider reported (<see cref="MetricOrigin.ReportedByProvider"/>).</summary>
    public static MetricValue<T> Exact(T value, string source) => new(value, MetricOrigin.ReportedByProvider, Require(source));

    /// <summary>A figure the provider gave as an estimate (<see cref="MetricOrigin.EstimatedByProvider"/>).</summary>
    public static MetricValue<T> Estimated(T value, string source) => new(value, MetricOrigin.EstimatedByProvider, Require(source));

    /// <summary>The provider does not expose it (<see cref="MetricOrigin.NotExposed"/>); the same as <see cref="NotExposed"/>.</summary>
    public static MetricValue<T> Unavailable(string reason) => new(null, MetricOrigin.NotExposed, Require(reason));

    /// <summary>A figure Agent Studio measured itself (<see cref="MetricOrigin.MeasuredBySystem"/>).</summary>
    public static MetricValue<T> Measured(T value, string source) => new(value, MetricOrigin.MeasuredBySystem, Require(source));

    /// <summary>A figure Agent Studio computed from partial data (<see cref="MetricOrigin.EstimatedBySystem"/>).</summary>
    public static MetricValue<T> EstimatedBySystem(T value, string source) => new(value, MetricOrigin.EstimatedBySystem, Require(source));

    /// <summary>The provider does not offer the figure (<see cref="MetricOrigin.NotExposed"/>).</summary>
    public static MetricValue<T> NotExposed(string reason) => new(null, MetricOrigin.NotExposed, Require(reason));

    /// <summary>The provider offers the figure, but it did not arrive (<see cref="MetricOrigin.Missing"/>).</summary>
    public static MetricValue<T> Missing(string reason) => new(null, MetricOrigin.Missing, Require(reason));

    /// <summary>
    /// The value a record kept (its origin, value and source), as it was written: an origin that carries a value
    /// (reported, measured or estimated) needs one, and the others must not have one.
    /// </summary>
    /// <exception cref="ArgumentException">The value does not fit the origin, or there is no source.</exception>
    public static MetricValue<T> Of(MetricOrigin origin, T? value, string source)
    {
        if (origin.CarriesValue() != value.HasValue)
        {
            throw new ArgumentException(
                value.HasValue ? $"A {origin} figure has no value." : $"A {origin} figure needs a value.",
                nameof(value));
        }

        return new(value, origin, Require(source));
    }

    /// <summary>Provenance of a value derived from several inputs: the weakest input wins.</summary>
    public static MetricProvenance Combine(params MetricProvenance[] inputs) => inputs.Length == 0 ? MetricProvenance.Unavailable : inputs.Max();

    private static string Require(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text;
    }
}
