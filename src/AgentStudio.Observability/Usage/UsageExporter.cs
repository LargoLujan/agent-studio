using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Observability.Usage;

/// <summary>
/// Exports a usage report (docs/14 D-07, F4-2) as CSV or as JSON <c>agentstudio-usage/1</c>.
/// <list type="bullet">
/// <item>CSV: RFC 4180 (commas, CRLF, quotes doubled inside quoted fields), UTF-8 with BOM for Excel, numbers in the
/// invariant culture and every instant twice, in UTC and in local time with its offset. Each figure has three columns
/// (value, <c>_origin</c>, <c>_source</c>), with the value empty when there is none. An attempt is one <c>intento</c> row
/// with its own figures, followed by one <c>modelo</c> row per model of its breakdown (the attempt's figures that are not
/// per model are left empty there, so summing the rows of one kind never counts twice).</item>
/// <item>JSON: every figure is <c>{ value, origin, source }</c> (<c>value: null</c> when there is none) and every sum
/// <c>{ value, origin, reported, missing, sources }</c>.</item>
/// <item>A text field of the CSV that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return
/// gets an apostrophe first: errors and names come from CLIs and agents.</item>
/// <item>No prompt nor agent output is ever written: the report holds only identifiers and figures.</item>
/// </list>
/// <see cref="UsageReport"/> does not carry its query, so through <see cref="IUsageExporter"/> the local times are those
/// of <paramref name="clock"/>'s zone (the zone the panel queries in) and the JSON's <c>filters</c> are null; the
/// overloads that take the query write both.
/// </summary>
public sealed class UsageExporter(TimeProvider clock) : IUsageExporter
{
    /// <summary>The schema of the JSON export.</summary>
    public const string JsonSchema = "agentstudio-usage/1";

    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] FigureColumns =
        ["duration_ms", "input_tokens", "cache_read_input_tokens", "cache_write_input_tokens", "output_tokens", "reasoning_output_tokens", "cost_usd"];

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Task WriteCsvAsync(UsageReport report, Stream destination, CancellationToken ct) => WriteCsvAsync(report, null, destination, ct);

    public Task WriteJsonAsync(UsageReport report, Stream destination, CancellationToken ct) => WriteJsonAsync(report, null, destination, ct);

    /// <summary>The CSV of <paramref name="report"/>, with local times in the zone of <paramref name="query"/> (else the clock's).</summary>
    public async Task WriteCsvAsync(UsageReport report, UsageQuery? query, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(destination);
        var zone = query?.Zone ?? _clock.LocalTimeZone;
        var writer = new StreamWriter(destination, Utf8WithBom, bufferSize: 16 * 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await WriteRecordAsync(writer, Header(), ct).ConfigureAwait(false);
            foreach (var attempt in report.Attempts)
            {
                ct.ThrowIfCancellationRequested();
                await WriteRecordAsync(writer, AttemptRow(attempt, zone), ct).ConfigureAwait(false);
                foreach (var model in attempt.PerModel)
                {
                    await WriteRecordAsync(writer, ModelRow(attempt, model, zone), ct).ConfigureAwait(false);
                }
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The JSON of <paramref name="report"/>, with the filters and the zone of <paramref name="query"/> when it is given.</summary>
    public async Task WriteJsonAsync(UsageReport report, UsageQuery? query, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(destination);
        var zone = query?.Zone ?? _clock.LocalTimeZone;
        var document = new UsageExportDocument(
            JsonSchema,
            Utc(_clock.GetUtcNow()),
            zone.Id,
            query is null ? null : FiltersOf(query),
            ProjectsOf(report),
            new JournalDto(report.Journal.JournalPrompts, report.Journal.RecordedAttempts, report.Journal.Difference, report.Journal.Warnings),
            [.. report.Attempts.Select(a => AttemptOf(a, zone))],
            [.. report.Groups.Select(GroupOf)]);
        await JsonSerializer.SerializeAsync(destination, document, JsonOptions, ct).ConfigureAwait(false);
        await destination.WriteAsync(Utf8.GetBytes("\n"), ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    // ---- CSV ----

    private static List<string> Header()
    {
        List<string> header =
        [
            "row", "project_id", "run_id", "task_id", "attempt", "purpose", "agent", "provider", "vendor", "configured_model",
            "reported_model", "model", "breakdown", "session_id", "recorded_version", "outcome", "recorded_at_utc",
            "recorded_at_local", "launched_at_utc", "launched_at_local", "finished_at_utc", "finished_at_local",
        ];
        foreach (var column in FigureColumns)
        {
            header.AddRange([column, column + "_origin", column + "_source"]);
        }

        header.Add("cost_basis");
        header.AddRange(["model_context_window", "model_context_window_origin", "model_context_window_source"]);
        header.Add("context_peak_model");
        header.AddRange(["context_peak_tokens", "context_peak_tokens_origin", "context_peak_tokens_source"]);
        header.AddRange(["context_peak_window", "context_peak_window_origin", "context_peak_window_source"]);
        header.AddRange(["compactions", "compactions_origin", "compactions_source"]);
        return header;
    }

    private static List<string> AttemptRow(UsageAttempt attempt, TimeZoneInfo zone)
    {
        var row = Identity("intento", attempt, attempt.Model(), Breakdown(attempt), zone);
        AddFigure(row, attempt.Duration());
        AddFigures(row, attempt.Own);
        row.Add(string.Empty);
        AddNotApplicable(row);
        row.Add(Text(attempt.ContextPeak?.Model));
        if (attempt.ContextPeak is { } peak)
        {
            AddFigure(row, peak.UsedTokens);
            AddFigure(row, peak.ContextWindow);
        }
        else
        {
            AddNotApplicable(row);
            AddNotApplicable(row);
        }

        AddFigure(row, attempt.CompactionCount());
        return row;
    }

    private static List<string> ModelRow(UsageAttempt attempt, ModelUsageFigures model, TimeZoneInfo zone)
    {
        var row = Identity("modelo", attempt, model.Model, "modelo", zone);
        AddNotApplicable(row);
        AddFigures(row, model.Figures);
        row.Add(Text(model.CostBasis));
        AddFigure(row, model.ContextWindow);
        row.Add(string.Empty);
        AddNotApplicable(row);
        AddNotApplicable(row);
        AddNotApplicable(row);
        return row;
    }

    private static List<string> Identity(string kind, UsageAttempt attempt, string model, string breakdown, TimeZoneInfo zone) =>
    [
        kind,
        Text(attempt.ProjectId),
        Text(attempt.RunId),
        Text(attempt.TaskId),
        attempt.Attempt.ToString(CultureInfo.InvariantCulture),
        attempt.Purpose?.ToString() ?? string.Empty,
        Text(attempt.Agent.Value),
        Text(attempt.Provider.Value),
        Text(attempt.Vendor.Value),
        Text(attempt.ConfiguredModel),
        Text(attempt.ReportedModel),
        Text(model),
        Text(breakdown),
        Text(attempt.SessionId),
        attempt.RecordedVersion.ToString(CultureInfo.InvariantCulture),
        attempt.Outcome.ToString(),
        Utc(attempt.RecordedAt),
        Local(attempt.RecordedAt, zone),
        attempt.LaunchedAt is { } launched ? Utc(launched) : string.Empty,
        attempt.LaunchedAt is { } local ? Local(local, zone) : string.Empty,
        Utc(attempt.FinishedAt),
        Local(attempt.FinishedAt, zone),
    ];

    private static void AddFigures(List<string> row, UsageFigures figures)
    {
        AddFigure(row, figures.InputTokens);
        AddFigure(row, figures.CacheReadInputTokens);
        AddFigure(row, figures.CacheWriteInputTokens);
        AddFigure(row, figures.OutputTokens);
        AddFigure(row, figures.ReasoningOutputTokens);
        AddFigure(row, figures.CostUsd);
    }

    private static void AddFigure(List<string> row, MetricValue<long> figure) =>
        row.AddRange([figure.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, figure.Origin.ToString(), Text(figure.Source)]);

    private static void AddFigure(List<string> row, MetricValue<decimal> figure) =>
        row.AddRange([figure.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, figure.Origin.ToString(), Text(figure.Source)]);

    /// <summary>A figure that does not belong to this row: value, origin and source all empty.</summary>
    private static void AddNotApplicable(List<string> row) => row.AddRange([string.Empty, string.Empty, string.Empty]);

    /// <summary>Whether an attempt's figures are broken down by model, and why not (D-04).</summary>
    private static string Breakdown(UsageAttempt attempt) =>
        attempt.PerModel.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"por modelo ({attempt.PerModel.Count})")
        : attempt.RecordedVersion < AgentRunFinishedPayload.UsageVersion ? "sin desglose (registro anterior a la Fase 4)"
        : "sin desglose (el CLI no lo da)";

    /// <summary>A text field: an apostrophe before anything a spreadsheet would take for a formula.</summary>
    private static string Text(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty
        : text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text
        : text;

    private static async Task WriteRecordAsync(StreamWriter writer, List<string> fields, CancellationToken ct)
    {
        var line = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            var field = fields[i];
            if (field.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            {
                line.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                line.Append(field);
            }
        }

        line.Append("\r\n");
        await writer.WriteAsync(line, ct).ConfigureAwait(false);
    }

    private static string Utc(DateTimeOffset instant) => instant.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);

    private static string Local(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).ToString(LocalFormat, CultureInfo.InvariantCulture);

    // ---- JSON ----

    private static FiltersDto FiltersOf(UsageQuery query) => new(
        Utc(query.From),
        Utc(query.To),
        [.. query.GroupBy.Select(d => d.ToString())],
        query.Filter?.Providers?.Select(p => p.Value).ToList(),
        query.Filter?.Vendors?.Select(v => v.Value).ToList(),
        query.Filter?.Models?.ToList(),
        query.Filter?.Agents?.Select(a => a.Value).ToList(),
        query.Filter?.Purposes?.Select(p => p.ToString()).ToList(),
        query.Filter?.Runs?.ToList());

    /// <summary>The projects with attempts in the report, in their order, and then those left out with their reason.</summary>
    private static List<ProjectDto> ProjectsOf(UsageReport report)
    {
        var projects = report.Attempts
            .GroupBy(a => a.ProjectId, StringComparer.Ordinal)
            .Select(g => new ProjectDto(g.Key, null, true, g.Count(), null))
            .ToList();
        projects.AddRange(report.Excluded.Select(e => new ProjectDto(e.ProjectId, e.Root, false, 0, e.Reason)));
        return projects;
    }

    private static AttemptDto AttemptOf(UsageAttempt attempt, TimeZoneInfo zone) => new(
        attempt.ProjectId,
        attempt.RunId,
        attempt.TaskId,
        attempt.Attempt,
        attempt.Purpose?.ToString(),
        attempt.Agent.Value,
        attempt.Provider.Value,
        attempt.Vendor.Value,
        attempt.ConfiguredModel,
        attempt.ReportedModel,
        attempt.Model(),
        Breakdown(attempt),
        attempt.SessionId,
        attempt.RecordedVersion,
        attempt.Outcome.ToString(),
        Utc(attempt.RecordedAt),
        Local(attempt.RecordedAt, zone),
        attempt.LaunchedAt is { } launched ? Utc(launched) : null,
        attempt.LaunchedAt is { } local ? Local(local, zone) : null,
        Utc(attempt.FinishedAt),
        Local(attempt.FinishedAt, zone),
        Figure(attempt.Duration()),
        FiguresOf(attempt.Own),
        attempt.SessionTotal is { } total ? FiguresOf(total) : null,
        [.. attempt.PerModel.Select(m => new ModelDto(m.Model, m.CostBasis, Figure(m.ContextWindow), FiguresOf(m.Figures)))],
        PeakOf(attempt.ContextPeak),
        Figure(attempt.CompactionCount()));

    private static GroupDto GroupOf(UsageGroup group)
    {
        var totals = group.Totals;
        return new GroupDto(
            [.. group.Key.Select(k => new KeyDto(k.Dimension.ToString(), k.Value))],
            new TotalsDto(
                totals.Attempts,
                totals.RateLimited,
                totals.LimitReached,
                Aggregate(totals.DurationMilliseconds),
                Aggregate(totals.InputTokens),
                Aggregate(totals.CacheReadInputTokens),
                Aggregate(totals.CacheWriteInputTokens),
                Aggregate(totals.OutputTokens),
                Aggregate(totals.ReasoningOutputTokens),
                Aggregate(totals.CostUsd),
                PeakOf(totals.ContextPeak),
                Aggregate(totals.Compactions)));
    }

    private static FiguresDto FiguresOf(UsageFigures figures) => new(
        Figure(figures.InputTokens),
        Figure(figures.CacheReadInputTokens),
        Figure(figures.CacheWriteInputTokens),
        Figure(figures.OutputTokens),
        Figure(figures.ReasoningOutputTokens),
        Figure(figures.CostUsd));

    private static PeakDto? PeakOf(ContextPeakFigures? peak) =>
        peak is null ? null : new PeakDto(peak.Model, Figure(peak.UsedTokens), Figure(peak.ContextWindow));

    private static FigureDto<T> Figure<T>(MetricValue<T> figure)
        where T : struct => new(figure.Value, figure.Origin.ToString(), figure.Source);

    private static AggregateDto<T> Aggregate<T>(MetricAggregate<T> aggregate)
        where T : struct => new(aggregate.Sum, aggregate.Origin.ToString(), aggregate.Reported, aggregate.Missing, aggregate.Sources);

    private sealed record UsageExportDocument(
        string Schema,
        string GeneratedAt,
        string Zone,
        FiltersDto? Filters,
        IReadOnlyList<ProjectDto> Projects,
        JournalDto Journal,
        IReadOnlyList<AttemptDto> Attempts,
        IReadOnlyList<GroupDto> Aggregates);

    private sealed record FiltersDto(
        string From,
        string To,
        IReadOnlyList<string> GroupBy,
        IReadOnlyList<string>? Providers,
        IReadOnlyList<string>? Vendors,
        IReadOnlyList<string>? Models,
        IReadOnlyList<string>? Agents,
        IReadOnlyList<string>? Purposes,
        IReadOnlyList<string>? Runs);

    private sealed record ProjectDto(string? ProjectId, string? Root, bool Included, int Attempts, string? Reason);

    private sealed record JournalDto(int? JournalPrompts, int RecordedAttempts, int? Difference, IReadOnlyList<string> Warnings);

    private sealed record AttemptDto(
        string ProjectId,
        string RunId,
        string? TaskId,
        int Attempt,
        string? Purpose,
        string Agent,
        string Provider,
        string Vendor,
        string? ConfiguredModel,
        string? ReportedModel,
        string Model,
        string Breakdown,
        string? SessionId,
        int RecordedVersion,
        string Outcome,
        string RecordedAt,
        string RecordedAtLocal,
        string? LaunchedAt,
        string? LaunchedAtLocal,
        string FinishedAt,
        string FinishedAtLocal,
        FigureDto<long> DurationMilliseconds,
        FiguresDto Own,
        FiguresDto? SessionTotal,
        IReadOnlyList<ModelDto> PerModel,
        PeakDto? ContextPeak,
        FigureDto<long> Compactions);

    private sealed record FiguresDto(
        FigureDto<long> InputTokens,
        FigureDto<long> CacheReadInputTokens,
        FigureDto<long> CacheWriteInputTokens,
        FigureDto<long> OutputTokens,
        FigureDto<long> ReasoningOutputTokens,
        FigureDto<decimal> CostUsd);

    private sealed record ModelDto(string Model, string? CostBasis, FigureDto<long> ContextWindow, FiguresDto Figures);

    private sealed record PeakDto(string? Model, FigureDto<long> UsedTokens, FigureDto<long> ContextWindow);

    private sealed record GroupDto(IReadOnlyList<KeyDto> Key, TotalsDto Totals);

    private sealed record KeyDto(string Dimension, string Value);

    private sealed record TotalsDto(
        int Attempts,
        int RateLimited,
        int LimitReached,
        AggregateDto<long> DurationMilliseconds,
        AggregateDto<long> InputTokens,
        AggregateDto<long> CacheReadInputTokens,
        AggregateDto<long> CacheWriteInputTokens,
        AggregateDto<long> OutputTokens,
        AggregateDto<long> ReasoningOutputTokens,
        AggregateDto<decimal> CostUsd,
        PeakDto? ContextPeak,
        AggregateDto<long> Compactions);

    private sealed record FigureDto<T>(T? Value, string Origin, string Source)
        where T : struct;

    private sealed record AggregateDto<T>(T? Value, string Origin, int Reported, int Missing, IReadOnlyList<string> Sources)
        where T : struct;
}
