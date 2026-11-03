using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Observability.Usage;

/// <summary>
/// Selects, groups and adds up attempts (docs/14 D-04). The periods are those of the zone of the query, so a day across
/// a change of time is still that day. Sums never mix origins silently and say how many values they miss
/// (<see cref="MetricAggregate{T}"/>): with a gap they are «parcial (n de m)», and without any value there is no sum.
/// </summary>
public static class UsageAggregator
{
    /// <summary>The label of an attempt without a session id.</summary>
    public const string NoSession = "sin sesión";

    /// <summary>The label of an attempt without a task (the planner, the judge, the auxiliary runs).</summary>
    public const string NoTask = "sin tarea";

    /// <summary>The label of an attempt whose start did not record its purpose (version 1).</summary>
    public const string NoPurpose = "sin propósito (registro anterior a la Fase 4)";

    /// <summary>Separates the values of a key while grouping; it sorts before any printable character.</summary>
    private const char KeySeparator = '\u001F';

    /// <summary>The attempts that ended in [<see cref="UsageQuery.From"/>, <see cref="UsageQuery.To"/>) and pass its filter.</summary>
    public static IReadOnlyList<UsageAttempt> Select(IEnumerable<UsageAttempt> attempts, UsageQuery query)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(query);
        return [.. attempts.Where(a => a.FinishedAt >= query.From && a.FinishedAt < query.To && Passes(a, query.Filter))];
    }

    /// <summary>
    /// The grand total first, with an empty key, and then one group per key of the query's dimensions, in that order of
    /// dimensions and sorted by their values (ordinal; the periods sort by time).
    /// </summary>
    public static IReadOnlyList<UsageGroup> Group(IReadOnlyList<UsageAttempt> attempts, UsageQuery query)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Zone);

        var groups = new List<UsageGroup> { new([], Total(attempts)) };
        if (query.GroupBy is not { Count: > 0 } dimensions)
        {
            return groups;
        }

        var keyed = new Dictionary<string, (string[] Values, List<UsageAttempt> Attempts)>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            var values = dimensions.Select(d => KeyOf(attempt, d, query.Zone)).ToArray();
            var joined = string.Join(KeySeparator, values);
            if (!keyed.TryGetValue(joined, out var group))
            {
                keyed[joined] = group = (values, []);
            }

            group.Attempts.Add(attempt);
        }

        foreach (var (_, (values, members)) in keyed.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            groups.Add(new UsageGroup([.. dimensions.Select((d, i) => new UsageGroupKey(d, values[i]))], Total(members)));
        }

        return groups;
    }

    /// <summary>The figures of a set of attempts: counts measured by Agent Studio and sums with their coverage.</summary>
    public static UsageTotals Total(IEnumerable<UsageAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var all = attempts as IReadOnlyList<UsageAttempt> ?? [.. attempts];
        return new UsageTotals(
            all.Count,
            MetricAggregate.Sum(all.Select(a => a.Duration())),
            MetricAggregate.Sum(all.Select(a => a.Own.InputTokens)),
            MetricAggregate.Sum(all.Select(a => a.Own.CacheReadInputTokens)),
            MetricAggregate.Sum(all.Select(a => a.Own.CacheWriteInputTokens)),
            MetricAggregate.Sum(all.Select(a => a.Own.OutputTokens)),
            MetricAggregate.Sum(all.Select(a => a.Own.ReasoningOutputTokens)),
            MetricAggregate.Sum(all.Select(a => a.Own.CostUsd)),
            all.Count(a => a.Outcome == AttemptOutcome.RateLimited),
            all.Count(a => a.Outcome == AttemptOutcome.LimitReached),
            PeakOf(all),
            MetricAggregate.Sum(all.Select(a => a.CompactionCount())));
    }

    /// <summary>The value of one dimension for an attempt, as the key of its group shows it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The dimension is not one of <see cref="UsageDimension"/>.</exception>
    public static string KeyOf(UsageAttempt attempt, UsageDimension dimension, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(zone);
        return dimension switch
        {
            UsageDimension.Provider => attempt.Provider.Value,
            UsageDimension.Vendor => attempt.Vendor.Value,
            UsageDimension.Model => attempt.Model(),
            UsageDimension.Session => attempt.SessionId is { Length: > 0 } session ? $"{attempt.Provider.Value}/{session}" : NoSession,
            UsageDimension.Run => attempt.RunId,
            UsageDimension.Task => attempt.TaskId ?? NoTask,
            UsageDimension.Agent => attempt.Agent.Value,
            UsageDimension.Purpose => attempt.Purpose?.ToString() ?? NoPurpose,
            UsageDimension.Project => attempt.ProjectId,
            UsageDimension.Day => LocalDay(attempt, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            UsageDimension.Week => IsoWeek(LocalDay(attempt, zone)),
            UsageDimension.Month => LocalDay(attempt, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unknown usage dimension."),
        };
    }

    private static bool Passes(UsageAttempt attempt, UsageFilter? filter) =>
        filter is null
        || (Allows(filter.Providers, attempt.Provider)
            && Allows(filter.Vendors, attempt.Vendor)
            && Allows(filter.Models, attempt.Model())
            && Allows(filter.Agents, attempt.Agent)
            && (filter.Purposes is not { Count: > 0 } purposes || (attempt.Purpose is { } purpose && purposes.Contains(purpose)))
            && Allows(filter.Runs, attempt.RunId));

    private static bool Allows<T>(IReadOnlyList<T>? allowed, T value) => allowed is not { Count: > 0 } || allowed.Contains(value);

    private static DateTime LocalDay(UsageAttempt attempt, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(attempt.FinishedAt, zone).DateTime.Date;

    private static string IsoWeek(DateTime day) =>
        string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(day):D4}-W{ISOWeek.GetWeekOfYear(day):D2}");

    /// <summary>The step with the most tokens of the group; when no attempt knows one, the weakest reason why not.</summary>
    private static ContextPeakFigures? PeakOf(IReadOnlyList<UsageAttempt> attempts)
    {
        ContextPeakFigures? largest = null;
        var reasons = new List<ContextPeakFigures>();
        foreach (var peak in attempts.Select(a => a.ContextPeak).OfType<ContextPeakFigures>())
        {
            if (peak.UsedTokens.Value is { } used)
            {
                if (largest?.UsedTokens.Value is not { } most || used > most)
                {
                    largest = peak;
                }
            }
            else
            {
                reasons.Add(peak);
            }
        }

        if (largest is not null || reasons.Count == 0)
        {
            return largest;
        }

        var origin = MetricOrigins.Weakest([.. reasons.Select(r => r.UsedTokens.Origin.CarriesValue() ? MetricOrigin.Missing : r.UsedTokens.Origin)]);
        var source = string.Join("; ", reasons.Select(r => r.UsedTokens.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var none = MetricValue<long>.Of(origin, null, source);
        return new ContextPeakFigures(null, none, none);
    }
}

/// <summary>What an attempt says beyond its recorded fields: its model, its duration and its compactions, each with its origin.</summary>
public static class UsageAttemptFacts
{
    /// <summary>The model of an attempt when the CLI reported none and the agent configured none (D-04).</summary>
    public const string DefaultModel = "predeterminado del CLI (no expuesto)";

    /// <summary>The model the CLI reported; else the one the agent configured; else <see cref="DefaultModel"/>.</summary>
    public static string Model(this UsageAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return !string.IsNullOrWhiteSpace(attempt.ReportedModel) ? attempt.ReportedModel
            : !string.IsNullOrWhiteSpace(attempt.ConfiguredModel) ? attempt.ConfiguredModel
            : DefaultModel;
    }

    /// <summary>
    /// How long the process ran, in milliseconds (D-04): from <see cref="UsageAttempt.LaunchedAt"/>, after any wait in
    /// the provider's queue. A version 1 end has no launch time, so its duration is estimated from
    /// <c>AgentRunStarted</c> and includes that wait. An interrupted attempt, or one whose process never started, has none.
    /// </summary>
    public static MetricValue<long> Duration(this UsageAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Outcome == AttemptOutcome.Interrupted)
        {
            return MetricValue<long>.Missing("intento interrumpido: no se sabe cuándo terminó");
        }

        if (attempt.LaunchedAt is { } launched)
        {
            return attempt.FinishedAt >= launched
                ? MetricValue<long>.Measured(Milliseconds(attempt.FinishedAt - launched), "medido por Agent Studio desde el arranque del proceso")
                : MetricValue<long>.Missing("el final del intento es anterior a su arranque");
        }

        if (attempt.RecordedVersion < AgentRunFinishedPayload.UsageVersion)
        {
            return attempt.FinishedAt >= attempt.RecordedAt
                ? MetricValue<long>.EstimatedBySystem(Milliseconds(attempt.FinishedAt - attempt.RecordedAt), "estimado por Agent Studio desde AgentRunStarted: incluye la espera en la cola (registro v1)")
                : MetricValue<long>.Missing("el final del intento es anterior a su arranque");
        }

        return MetricValue<long>.Missing("el proceso no llegó a arrancar");
    }

    /// <summary>
    /// The compactions of an attempt, counted by Agent Studio from the CLI's events; not exposed when the CLI does not
    /// report the context (null in a version 2 end), and missing when the end is of version 1 or nothing ran. Never zero
    /// for lack of a figure.
    /// </summary>
    public static MetricValue<long> CompactionCount(this UsageAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return attempt.Compactions is { } count ? MetricValue<long>.Measured(count, "compactaciones observadas por Agent Studio (compact_boundary)")
            : attempt.Outcome == AttemptOutcome.Interrupted ? MetricValue<long>.Missing("intento interrumpido: no se sabe")
            : attempt.RecordedVersion < AgentRunFinishedPayload.UsageVersion ? MetricValue<long>.Missing(UsageProjector.Version1Reason)
            : attempt.LaunchedAt is null ? MetricValue<long>.Missing("el proceso no llegó a arrancar")
            : MetricValue<long>.NotExposed("el CLI no informa de compactaciones");
    }

    private static long Milliseconds(TimeSpan span) => span.Ticks / TimeSpan.TicksPerMillisecond;
}
