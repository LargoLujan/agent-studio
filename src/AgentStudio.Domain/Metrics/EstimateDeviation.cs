namespace AgentStudio.Domain.Metrics;

/// <summary>
/// How what was spent compares with what the system estimated (docs/16 D-20, F9-7: «real frente a estimado al cerrar»).
/// <see cref="Unknown"/> when either side has no figure: an estimate «desconocida», or a total that did not arrive. Nothing is
/// compared against a number made up.
/// </summary>
public enum EstimateDeviation
{
    Unknown,

    /// <summary>The actual figure is inside the interval of the estimate (the 25th to the 75th percentile, both ends included).</summary>
    Within,

    /// <summary>Under the interval: it took less than estimated.</summary>
    Below,

    /// <summary>Over the interval: it took more than estimated.</summary>
    Above,
}

/// <summary>The comparison of an actual figure with an estimated interval.</summary>
public static class EstimateComparison
{
    /// <summary>Where <paramref name="actual"/> falls against the interval from <paramref name="low"/> to <paramref name="high"/>; <see cref="EstimateDeviation.Unknown"/> without any of the three, or with the ends reversed.</summary>
    public static EstimateDeviation Compare<T>(T? low, T? high, T? actual)
        where T : struct, IComparable<T>
    {
        if (low is not { } from || high is not { } to || actual is not { } value || from.CompareTo(to) > 0)
        {
            return EstimateDeviation.Unknown;
        }

        if (value.CompareTo(from) < 0)
        {
            return EstimateDeviation.Below;
        }

        return value.CompareTo(to) > 0 ? EstimateDeviation.Above : EstimateDeviation.Within;
    }
}
