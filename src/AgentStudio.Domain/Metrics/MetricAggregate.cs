using System.Numerics;

namespace AgentStudio.Domain.Metrics;

/// <summary>
/// A figure added up over several values (docs/14 D-04): the sum of the values that are there, how many were there
/// (<paramref name="Reported"/>) and how many were not (<paramref name="Missing"/>), and the weakest origin among them,
/// so origins are never mixed silently. With <paramref name="Missing"/> &gt; 0 and something reported the figure is
/// partial and is shown as «parcial (<paramref name="Reported"/> de <see cref="Count"/>)»; with nothing reported there is
/// no sum, never a zero.
/// </summary>
/// <param name="Sum">The sum of the values that are there; null when none is.</param>
/// <param name="Origin">
/// The weakest origin of the values that are there or, when none is, the weakest reason why not
/// (<see cref="MetricOrigin.NotExposed"/> or <see cref="MetricOrigin.Missing"/>).
/// </param>
/// <param name="Reported">How many values had a figure.</param>
/// <param name="Missing">How many values had none.</param>
/// <param name="Sources">The distinct sources of every value, in ordinal order.</param>
public sealed record MetricAggregate<T>(T? Sum, MetricOrigin Origin, int Reported, int Missing, IReadOnlyList<string> Sources)
    where T : struct
{
    public T? Sum { get; init; } = Consistent(Sum, Origin, Reported);

    public int Reported { get; init; } = Reported >= 0 ? Reported : throw new ArgumentOutOfRangeException(nameof(Reported), Reported, "A count cannot be negative.");

    public int Missing { get; init; } = Missing >= 0 ? Missing : throw new ArgumentOutOfRangeException(nameof(Missing), Missing, "A count cannot be negative.");

    public IReadOnlyList<string> Sources { get; init; } = Sources ?? throw new ArgumentNullException(nameof(Sources));

    /// <summary>The three levels of <see cref="Origin"/>.</summary>
    public MetricProvenance Provenance => Origin.ToProvenance();

    /// <summary>How many values were added up, with a figure or without one.</summary>
    public int Count => Reported + Missing;

    /// <summary>Some values had a figure and some did not: «parcial (n de m)».</summary>
    public bool IsPartial => Reported > 0 && Missing > 0;

    private static T? Consistent(T? sum, MetricOrigin origin, int reported)
    {
        if (sum.HasValue != reported > 0)
        {
            throw new ArgumentException(sum.HasValue ? "A sum needs at least one reported value." : "Reported values add up to a sum.", nameof(sum));
        }

        if (origin.CarriesValue() != sum.HasValue)
        {
            throw new ArgumentException($"A {origin} aggregate {(sum.HasValue ? "has no sum" : "needs a sum")}.", nameof(origin));
        }

        return sum;
    }
}

/// <summary>Builds <see cref="MetricAggregate{T}"/> values.</summary>
public static class MetricAggregate
{
    /// <summary>
    /// Adds up the values that have a figure and counts those that do not. The origin is the weakest of the values that
    /// have a figure or, when none has, the weakest reason why not. The sum is checked: one that does not fit throws.
    /// </summary>
    /// <exception cref="OverflowException">The sum does not fit in <typeparamref name="T"/>.</exception>
    public static MetricAggregate<T> Sum<T>(IEnumerable<MetricValue<T>> values)
        where T : struct, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(values);

        T? sum = null;
        var reported = new List<MetricOrigin>();
        var missing = new List<MetricOrigin>();
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value.Source))
            {
                sources.Add(value.Source);
            }

            if (value.Value is { } figure)
            {
                sum = checked((sum ?? T.Zero) + figure);
                reported.Add(value.Origin);
            }
            else
            {
                // A value without a figure is never stronger than "missing", whatever its origin claims.
                missing.Add(value.Origin.CarriesValue() ? MetricOrigin.Missing : value.Origin);
            }
        }

        var origin = reported.Count > 0 ? MetricOrigins.Weakest([.. reported]) : MetricOrigins.Weakest([.. missing]);
        return new MetricAggregate<T>(sum, origin, reported.Count, missing.Count, [.. sources]);
    }
}
