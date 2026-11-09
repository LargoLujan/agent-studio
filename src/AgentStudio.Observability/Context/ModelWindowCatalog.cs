using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Observability.Context;

/// <summary>A context window the CLI reported for a model, and when the log recorded it.</summary>
public sealed record LearnedWindow(string Model, long Tokens, DateTimeOffset SeenAt);

/// <summary>
/// The context windows seen for each model in a project's log (docs/14 D-10). Until a run reports its windows, the
/// window of a step is the last one seen for its model here, labelled «estimado por Agent Studio (ventana vista en una
/// ejecución anterior)»: the model may have changed its window since. Only windows the CLI reported (exact) for a named
/// model are learned, from the breakdown by model and the context peak of <c>AgentRunFinished</c> version 2. A version 1
/// window is never learned: the parser of Phase 3 paired it with the model announced by <c>init</c>, not with the model
/// of the steps (D-13). A log that does not verify, or that no anchor recognises, teaches nothing.
/// <para>
/// With a base table (<see cref="WithBaseline"/>, tanda 2), a model never seen in the log takes the window of the table,
/// also «estimado por Agent Studio» and naming the table and its version: what the log learned always wins over it.
/// </para>
/// <para>
/// <b>Learned live</b> (docs/17 §0.2 and F13-20, ADR-0052): a catalog read from a log also learns, while a run goes on, the
/// windows the CLI reports in it (<see cref="LearnLive"/>, which <see cref="ContextHealthTracker"/> calls for every window
/// the CLI reported exactly). Every tracker that shares the catalog (the attempts of a monitor and its planning) then judges
/// the steps of that model with it, from the first step of its next attempt, without waiting for the run to end and the log
/// to be read again: Claude Code reports the window only in the result of an attempt. A window learned live is «estimado por
/// Agent Studio» for another attempt and wins over the log and the base table, being the newest. The shared catalogs
/// <see cref="Empty"/> and <see cref="Baseline"/> never learn, so nothing passes from one project to another through them.
/// </para>
/// </summary>
public sealed class ModelWindowCatalog
{
    private readonly Dictionary<string, LearnedWindow> _windows;
    private readonly ModelWindowTable? _baseline;
    private readonly ConcurrentDictionary<string, LearnedWindow>? _live;

    private ModelWindowCatalog(Dictionary<string, LearnedWindow> windows, ModelWindowTable? baseline, ConcurrentDictionary<string, LearnedWindow>? live)
    {
        _windows = windows;
        _baseline = baseline;
        _live = live;
    }

    /// <summary>No window learned and no base table: a model has a window only once the CLI reports it in the attempt. It never learns live.</summary>
    public static ModelWindowCatalog Empty { get; } = new(new Dictionary<string, LearnedWindow>(StringComparer.Ordinal), null, null);

    /// <summary>
    /// No window learned, with the base table of this version (<see cref="ModelWindowTable.Current"/>): what a screen uses
    /// until the log of its project was read, so the first run of a new project has a percentage from its first step. It is
    /// shared, so it never learns live: a catalog read from the log of the project does (<see cref="FromLog(ProjectLogSnapshot)"/>).
    /// </summary>
    public static ModelWindowCatalog Baseline { get; } = Empty.WithBaseline(ModelWindowTable.Current);

    /// <summary>A catalog of its own that learns live from the start, with the base table of this version under it (or <paramref name="table"/>).</summary>
    public static ModelWindowCatalog Learning(ModelWindowTable? table = null) =>
        new(new Dictionary<string, LearnedWindow>(StringComparer.Ordinal), table ?? ModelWindowTable.Current, NewLive());

    /// <summary>The base table a model never seen in the log falls back to; null when there is none.</summary>
    public ModelWindowTable? BaselineTable => _baseline;

    /// <summary>Whether this catalog learns the windows of the run going on (<see cref="LearnLive"/>).</summary>
    public bool LearnsLive => _live is not null;

    /// <summary>
    /// The same windows learned, falling back to <paramref name="table"/> (the base table of this version by default) for a
    /// model the log has not seen. It learns live with this catalog, sharing what it learned, when this one does.
    /// </summary>
    public ModelWindowCatalog WithBaseline(ModelWindowTable? table = null) => new(_windows, table ?? ModelWindowTable.Current, _live);

    /// <summary>The last window seen for each model in the log.</summary>
    public IReadOnlyCollection<LearnedWindow> Windows => _windows.Values;

    /// <summary>The windows learned live, in the run going on; empty for a catalog that does not learn live.</summary>
    public IReadOnlyCollection<LearnedWindow> LiveWindows => _live is { } live ? [.. live.Values] : [];

    /// <summary>
    /// Learns live the window <paramref name="tokens"/> the CLI reported, exactly, for <paramref name="model"/> in the run going
    /// on (F13-20); the last one reported for a model wins. Nothing for a catalog that does not learn live, a blank model or a
    /// window that is not positive. Safe from any thread.
    /// </summary>
    public void LearnLive(string? model, long tokens, DateTimeOffset seenAt)
    {
        if (_live is { } live && !string.IsNullOrWhiteSpace(model) && tokens > 0)
        {
            live[model] = new LearnedWindow(model, tokens, seenAt);
        }
    }

    private static ConcurrentDictionary<string, LearnedWindow> NewLive() => new(StringComparer.Ordinal);

    /// <summary>The windows of the open project's log; none when it does not verify.</summary>
    public static ModelWindowCatalog FromLog(DecisionLogState log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Verification.IsValid ? Learn(log.Entries) : Empty;
    }

    /// <summary>The windows of another project's log; none unless it is trusted (it verifies against a recognised anchor).</summary>
    public static ModelWindowCatalog FromLog(ProjectLogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.IsTrusted ? Learn(snapshot.Entries) : Empty;
    }

    /// <summary>
    /// The window learned live for <paramref name="model"/> in the run going on, else the one learned from the log, both as an
    /// estimate of Agent Studio; else, with a base table, the window of
    /// the table (an estimate too, or «no expuesto» for a model of Codex); missing when none is known.
    /// </summary>
    public MetricValue<long> WindowFor(string? model)
    {
        if (model is not null && _live is { } live && live.TryGetValue(model, out var seen))
        {
            return MetricValue<long>.EstimatedBySystem(
                seen.Tokens,
                string.Create(CultureInfo.InvariantCulture, $"ventana informada por el CLI en esta ejecución ({seen.Model})"));
        }

        if (model is null || !_windows.TryGetValue(model, out var window))
        {
            if (_baseline?.WindowFor(model) is { } fromTable && (fromTable.HasValue || fromTable.Origin == MetricOrigin.NotExposed))
            {
                return fromTable;
            }

            return MetricValue<long>.Missing(model is null ? "sin modelo del paso: no se conoce la ventana" : $"no se ha visto ninguna ventana de {model}");
        }

        return MetricValue<long>.EstimatedBySystem(
            window.Tokens,
            string.Create(CultureInfo.InvariantCulture, $"ventana vista en una ejecución anterior ({window.Model}, {window.SeenAt.UtcDateTime:yyyy-MM-dd})"));
    }

    private static ModelWindowCatalog Learn(IReadOnlyList<DecisionEntry> entries)
    {
        var windows = new Dictionary<string, LearnedWindow>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Kind == DecisionKind.AgentRunFinished))
        {
            AgentRunFinishedPayload finished;
            try
            {
                finished = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(entry.PayloadJson);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                continue;
            }

            if (!finished.RecordsUsageWithOrigins())
            {
                continue;
            }

            foreach (var model in finished.Usage?.PerModel ?? [])
            {
                Remember(windows, model.Model, model.ContextWindow, entry.RecordedAt);
            }

            if (finished.ContextPeak is { Model: { } peakModel } peak)
            {
                Remember(windows, peakModel, peak.ContextWindow, entry.RecordedAt);
            }
        }

        return new ModelWindowCatalog(windows, null, NewLive());
    }

    private static void Remember(Dictionary<string, LearnedWindow> windows, string model, MetricSnapshot window, DateTimeOffset seenAt)
    {
        if (string.IsNullOrWhiteSpace(model) || window.ResolvedOrigin() != MetricOrigin.ReportedByProvider || window.Value is not { } tokens || tokens <= 0 || decimal.Truncate(tokens) != tokens || tokens > long.MaxValue)
        {
            return;
        }

        windows[model] = new LearnedWindow(model, decimal.ToInt64(tokens), seenAt);
    }
}
