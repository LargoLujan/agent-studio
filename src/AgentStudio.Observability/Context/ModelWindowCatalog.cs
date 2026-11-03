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
/// </summary>
public sealed class ModelWindowCatalog
{
    private readonly Dictionary<string, LearnedWindow> _windows;
    private readonly ModelWindowTable? _baseline;

    private ModelWindowCatalog(Dictionary<string, LearnedWindow> windows, ModelWindowTable? baseline = null)
    {
        _windows = windows;
        _baseline = baseline;
    }

    /// <summary>No window learned and no base table: a model has a window only once the CLI reports it in the attempt.</summary>
    public static ModelWindowCatalog Empty { get; } = new(new Dictionary<string, LearnedWindow>(StringComparer.Ordinal));

    /// <summary>
    /// No window learned, with the base table of this version (<see cref="ModelWindowTable.Current"/>): what a screen uses
    /// until the log of its project was read, so the first run of a new project has a percentage from its first step.
    /// </summary>
    public static ModelWindowCatalog Baseline { get; } = Empty.WithBaseline(ModelWindowTable.Current);

    /// <summary>The base table a model never seen in the log falls back to; null when there is none.</summary>
    public ModelWindowTable? BaselineTable => _baseline;

    /// <summary>
    /// The same windows learned, falling back to <paramref name="table"/> (the base table of this version by default) for a
    /// model the log has not seen.
    /// </summary>
    public ModelWindowCatalog WithBaseline(ModelWindowTable? table = null) => new(_windows, table ?? ModelWindowTable.Current);

    /// <summary>The last window seen for each model.</summary>
    public IReadOnlyCollection<LearnedWindow> Windows => _windows.Values;

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
    /// The window learned for <paramref name="model"/>, as an estimate of Agent Studio; else, with a base table, the window of
    /// the table (an estimate too, or «no expuesto» for a model of Codex); missing when none is known.
    /// </summary>
    public MetricValue<long> WindowFor(string? model)
    {
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

        return new ModelWindowCatalog(windows);
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
