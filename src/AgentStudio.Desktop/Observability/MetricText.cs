using System.Globalization;
using System.Numerics;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Desktop.Observability;

/// <summary>A number as a screen shows it: its value, when there is one, and always where it came from.</summary>
/// <param name="Value">The formatted value; null when there is none (never a zero).</param>
/// <param name="Origin">Who gave it (docs/14 D-03), with «parcial (n de m)» before it for a sum that misses values.</param>
public sealed record Figure(string? Value, string Origin)
{
    /// <summary>One line: the value and its origin, or only the origin when there is no value.</summary>
    public string Text => Value is null ? Origin : $"{Value} · {Origin}";

    /// <summary>What goes where the number would be: the value, or a dash.</summary>
    public string Shown => Value ?? "—";
}

/// <summary>
/// The texts of the figures of the Phase 4 screens (docs/14 D-03), from <c>Resources/ObservabilityStrings.resx</c> in the language
/// in use (docs/17 D-47). Every number goes through here, so none is shown without its origin: an estimate carries «~» and never
/// says «exacto», a figure that is not there says «no expuesto» or «sin dato» instead of a zero, and a sum that misses values
/// says «parcial (n de m)». Numbers are written in the culture of the language.
/// </summary>
public static class MetricText
{
    /// <summary>The culture of the numbers on screen, the one of the language in use: «1.234.567», «0,42», «47 %» in Spanish.</summary>
    public static CultureInfo Culture => Texts.Current.Culture;

    /// <summary>The label of an origin, as D-03 names it.</summary>
    public static string Origin(MetricOrigin origin) => origin switch
    {
        MetricOrigin.ReportedByProvider => Texts.Get("Observability_Origin_ReportedByProvider"),
        MetricOrigin.MeasuredBySystem => Texts.Get("Observability_Origin_MeasuredBySystem"),
        MetricOrigin.EstimatedByProvider => Texts.Get("Observability_Origin_EstimatedByProvider"),
        MetricOrigin.EstimatedBySystem => Texts.Get("Observability_Origin_EstimatedBySystem"),
        MetricOrigin.NotExposed => Texts.Get("Observability_Origin_NotExposed"),
        MetricOrigin.Missing => Texts.Get("Observability_Origin_Missing"),
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown metric origin."),
    };

    /// <summary>A count of tokens (or of anything else), with its origin.</summary>
    public static Figure Tokens(MetricValue<long> value) => Of(value.Value, value.Origin, Integer);

    /// <summary>A sum of tokens over several attempts, with its origin and how many values it misses.</summary>
    public static Figure Tokens(MetricAggregate<long> sum) => Of(sum, Integer);

    /// <summary>
    /// A cost (docs/14 D-06): what the CLI estimates with its own price table. <paramref name="costBasis"/> is the basis
    /// the CLI gives (<c>list</c>, <c>managed</c>, <c>unknown</c>); none, or <c>list</c>, is the list price.
    /// </summary>
    public static Figure Cost(MetricValue<decimal> value, string? costBasis = null) =>
        WithCostBasis(Of(value.Value, value.Origin, Money), value.Origin, costBasis);

    /// <summary>A sum of costs, with its origin and how many values it misses.</summary>
    public static Figure Cost(MetricAggregate<decimal> sum) => WithCostBasis(Of(sum, Money), sum.Origin, null);

    /// <summary>A duration in milliseconds, measured by Agent Studio from the start of the process.</summary>
    public static Figure Duration(MetricAggregate<long> milliseconds) => Of(milliseconds, DurationText);

    /// <summary>The duration of one attempt in milliseconds, with its origin.</summary>
    public static Figure Duration(MetricValue<long> milliseconds) => Of(milliseconds.Value, milliseconds.Origin, DurationText);

    /// <summary>Something Agent Studio counted itself: attempts, rate-limit and budget errors.</summary>
    public static Figure Measured(int count) => new(count.ToString("N0", Culture), Origin(MetricOrigin.MeasuredBySystem));

    /// <summary>A fraction (0..1) as a percentage, with its origin.</summary>
    public static Figure Percent(MetricValue<double> value) => Of(value.Value, value.Origin, PercentText);

    /// <summary>A whole number with its thousands, «1.234.567».</summary>
    public static string Integer(long value) => value.ToString("N0", Culture);

    /// <summary>A fraction (0..1) as a whole percentage, «47 %».</summary>
    public static string PercentText(double fraction) => fraction.ToString("P0", Culture);

    /// <summary>A duration in milliseconds as people read it: «45 s», «41 min», «1 h 5 min».</summary>
    public static string DurationText(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalMinutes < 1
            ? string.Create(Culture, $"{(int)time.TotalSeconds} s")
            : time.TotalHours < 1
                ? string.Create(Culture, $"{(int)time.TotalMinutes} min")
                : string.Create(Culture, $"{(int)time.TotalHours} h {time.Minutes} min");
    }

    /// <summary>A value with its origin; an estimate carries «~», and a figure without a value is only its origin.</summary>
    internal static Figure Of<T>(T? value, MetricOrigin origin, Func<T, string> format)
        where T : struct
    {
        if (value is not { } figure || !origin.CarriesValue())
        {
            // Without a value nothing is stronger than «sin dato», whatever the origin claims.
            return new Figure(null, Origin(origin.CarriesValue() ? MetricOrigin.Missing : origin));
        }

        var text = format(figure);
        return new Figure(origin.ToProvenance() == MetricProvenance.Estimated ? "~" + text : text, Origin(origin));
    }

    private static Figure Of<T>(MetricAggregate<T> sum, Func<T, string> format)
        where T : struct, INumber<T>
    {
        var figure = Of(sum.Sum, sum.Origin, format);
        return sum.IsPartial
            ? figure with { Origin = Texts.Format("Observability_Figure_Partial", sum.Reported, sum.Count, figure.Origin) }
            : figure;
    }

    private static string Money(decimal value) => value.ToString("N2", Culture) + " USD";

    private static Figure WithCostBasis(Figure figure, MetricOrigin origin, string? costBasis)
    {
        if (figure.Value is null || origin != MetricOrigin.EstimatedByProvider)
        {
            return figure;
        }

        var basis = string.IsNullOrWhiteSpace(costBasis) || string.Equals(costBasis, "list", StringComparison.OrdinalIgnoreCase)
            ? Texts.Get("Observability_Figure_PriceList")
            : Texts.Format("Observability_Figure_PriceBasis", costBasis.Trim());
        return figure with { Origin = $"{figure.Origin} {basis}" };
    }
}
