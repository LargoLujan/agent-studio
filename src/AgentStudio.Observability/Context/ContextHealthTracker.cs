using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Observability.Context;

/// <summary>
/// The health of the context of a run at one moment (docs/14 D-12): the model of the last step, the tokens of its prompt
/// and the window they are compared with (each with its origin), the utilization and level that follow, the compactions
/// so far, the step with the most tokens, how many steps were seen and whether the run ended.
/// </summary>
public sealed record ContextHealthState(
    string? Model,
    MetricValue<long> UsedTokens,
    MetricValue<long> ContextWindow,
    ContextHealthReport Health,
    MetricValue<long> Compactions,
    ContextPeakFigures? Peak,
    int Steps,
    bool Finished);

/// <summary>
/// Follows the context of one agent run, step by step, from its events (docs/14 D-09 and D-10, ADR-0021).
/// <list type="bullet">
/// <item>The occupancy is the prompt of the last step (<see cref="ContextObserved"/>, one per step), never the total of
/// the run: in <c>02-herramienta</c> <c>result.usage</c> would give twice the last step.</item>
/// <item>It is compared with the window of the model of that step: the one the step brings; else the last one the CLI
/// gave for that model in this run (the breakdown of its result included), which is exact; else the one learned from
/// an earlier run (<see cref="ModelWindowCatalog"/>), an estimate of Agent Studio; else, when the catalog has one, the
/// window of the base table (<see cref="ModelWindowTable"/>), an estimate too; else none, and then only the tokens are
/// known. The model announced when the session starts is never taken for the model of the steps (D-13).</item>
/// <item>The utilization takes the weakest origin of its two figures (<see cref="ContextHealthEvaluator"/>), so an
/// estimate is never shown as exact, and a figure that is not there gives the level <see cref="ContextHealthLevel.Unknown"/>,
/// never «sano».</item>
/// <item>A provider that does not report the context (<c>codex exec</c>, Gemini) is «no expuesto» throughout.</item>
/// </list>
/// Safe to call from the thread that reads the events while another reads <see cref="Current"/>.
/// </summary>
public sealed class ContextHealthTracker
{
    private const string CompactionSource = "compactaciones observadas por Agent Studio (compact_boundary)";

    private readonly Lock _gate = new();
    private readonly ContextHealthEvaluator _evaluator;
    private readonly ModelWindowCatalog _learned;
    private readonly bool _reportsContext;
    private readonly string _notExposed;
    private readonly Dictionary<string, MetricValue<long>> _windowsOfRun = new(StringComparer.Ordinal);
    private ContextObserved? _last;
    private ContextObserved? _peak;
    private int _steps;
    private int _compactions;
    private bool _finished;
    private ContextHealthState _current;

    /// <param name="thresholds">The levels (global settings, 70/85/95 % by default); they must hold, or the constructor throws.</param>
    /// <param name="learned">The windows seen in earlier runs of the project.</param>
    /// <param name="provider">The provider of the run, to say which CLI does not expose the context.</param>
    /// <param name="reportsContextOccupancy">The provider's <see cref="ProviderCapabilities.ReportsContextOccupancy"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The thresholds do not satisfy 0 &lt; aviso &lt; compresión &lt; crítico ≤ 1.</exception>
    public ContextHealthTracker(ContextHealthThresholds thresholds, ModelWindowCatalog learned, ProviderId provider, bool reportsContextOccupancy)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(learned);
        _evaluator = new ContextHealthEvaluator(thresholds);
        _learned = learned;
        _reportsContext = reportsContextOccupancy;
        _notExposed = provider == ProviderId.CodexCli ? "el CLI no informa de la ocupación del contexto (codex exec)"
            : provider == ProviderId.GeminiCli ? "el CLI no informa de la ocupación del contexto (Gemini CLI)"
            : "el CLI no informa de la ocupación del contexto";
        _current = Build();
    }

    /// <summary>The state after the last event observed.</summary>
    public ContextHealthState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Takes one event of the run into account and returns the new state.</summary>
    public ContextHealthState Observe(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        lock (_gate)
        {
            switch (agentEvent)
            {
                case ContextObserved step:
                    _steps++;
                    _last = step;
                    Remember(step.Model, step.ContextWindow);
                    if (step.PromptTokens.Value is { } tokens && (_peak?.PromptTokens.Value is not { } most || tokens > most))
                    {
                        _peak = step;
                    }

                    break;
                case CompactionObserved:
                    _compactions++;
                    break;
                case UsageReported reported:
                    foreach (var model in reported.Usage.PerModel)
                    {
                        Remember(model.Model, model.ContextWindow);
                    }

                    break;
                case RunFinished:
                    _finished = true;
                    break;
            }

            _current = Build();
            return _current;
        }
    }

    /// <summary>A window the CLI gave for a model in this run serves the later steps of that model.</summary>
    private void Remember(string? model, MetricValue<long> window)
    {
        if (model is not null && window.HasValue && window.Origin is MetricOrigin.ReportedByProvider or MetricOrigin.EstimatedByProvider)
        {
            if (window.Origin == MetricOrigin.ReportedByProvider || !_windowsOfRun.TryGetValue(model, out var known) || known.Origin != MetricOrigin.ReportedByProvider)
            {
                _windowsOfRun[model] = window;
            }
        }
    }

    private ContextHealthState Build()
    {
        var steps = _steps;
        if (!_reportsContext && _last is null)
        {
            var none = MetricValue<long>.NotExposed(_notExposed);
            return new ContextHealthState(null, none, none, _evaluator.Evaluate(none, none), none, null, 0, _finished);
        }

        var compactions = MetricValue<long>.Measured(_compactions, CompactionSource);
        if (_last is not { } last)
        {
            var notYet = MetricValue<long>.Missing("aún no ha llegado ningún paso");
            return new ContextHealthState(null, notYet, notYet, _evaluator.Evaluate(notYet, notYet), compactions, null, 0, _finished);
        }

        var window = WindowOf(last);
        var peak = _peak is { } largest ? new ContextPeakFigures(largest.Model, largest.PromptTokens, WindowOf(largest)) : null;
        return new ContextHealthState(last.Model, last.PromptTokens, window, _evaluator.Evaluate(last.PromptTokens, window), compactions, peak, steps, _finished);
    }

    /// <summary>The window of the model of a step: the CLI's for this step or this run, else the one learned, else the step's own reason.</summary>
    private MetricValue<long> WindowOf(ContextObserved step)
    {
        if (step.Model is { } model && _windowsOfRun.TryGetValue(model, out var ofRun) && (ofRun.Origin == MetricOrigin.ReportedByProvider || !step.ContextWindow.HasValue))
        {
            return ofRun;
        }

        if (step.ContextWindow.HasValue)
        {
            return step.ContextWindow;
        }

        var learned = _learned.WindowFor(step.Model);
        return learned.HasValue ? learned : step.ContextWindow;
    }
}
