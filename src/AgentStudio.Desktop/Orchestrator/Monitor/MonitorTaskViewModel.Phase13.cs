using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Context;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

// Phase 13 (docs/17 D-12 to D-14, WP8): what a task card of the monitor adds: the context by parts, the consumption of its
// finished attempts, the question it waits to be answered and the live state the projection groups.

public sealed partial class MonitorTaskViewModel
{
    /// <summary>The usage of the last attempt that reported one and has not ended yet; it joins <see cref="_finishedAttempts"/> when the attempt ends.</summary>
    private TokenUsage? _attemptUsage;

    /// <summary>The usage of each attempt of the task that ended, as its CLI reported it (<c>UsageReported</c> before <c>RunFinished</c>).</summary>
    private readonly List<TokenUsage> _finishedAttempts = [];

    private IReadOnlyList<ContextPartSnapshot>? _parts;
    private bool _partsFailed;
    private int _partsAttempt;

    /// <summary>How the last attempt of the task ended, as its CLI classified it (docs/16 D-41); null until an attempt ends. The panel «Problemas» says it with the reason.</summary>
    public AttemptEndClass? EndClass { get; private set; }

    /// <summary>
    /// What the monitor gave the task to say that its bars changed, so a burst of steps is one repaint of its card and not one per event
    /// (docs/17 D-16): the figures are always current; only the notification waits for the monitor to send it. Without one, it is sent at once.
    /// </summary>
    internal Action<MonitorTaskViewModel>? NotifyScheduler { get; set; }

    private bool _touched;

    /// <summary>The bars of the card changed: the notification goes now, or when the monitor sends it (at most once for every change that came before).</summary>
    internal void Touch()
    {
        if (NotifyScheduler is not { } schedule)
        {
            RaiseBarsChanged();
        }
        else if (!_touched)
        {
            _touched = true;
            schedule(this);
        }
    }

    /// <summary>Tells the screen that the context, its level, the parts and the consumption may have changed: the one notification of a burst.</summary>
    internal void RaiseBarsChanged()
    {
        _touched = false;
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(ContextLevel));
        OnPropertyChanged(nameof(ContextTone));
        OnPropertyChanged(nameof(ContextBar));
        OnPropertyChanged(nameof(ContextDetail));
        OnPropertyChanged(nameof(PartsBar));
        OnPropertyChanged(nameof(ConsumptionBar));
    }

    /// <summary>The attempts of the task that ended with the tokens their CLI reported.</summary>
    internal IReadOnlyList<TokenUsage> FinishedAttempts => _finishedAttempts;

    /// <summary>The question the agent of the task asked and the person has not answered (docs/17 D-29); null otherwise.</summary>
    public PendingQuestion? Question
    {
        get => _question;
        internal set
        {
            if (!Equals(_question, value))
            {
                _question = value;
                OnPropertyChanged(nameof(Question));
                OnPropertyChanged(nameof(HasQuestion));
                OnPropertyChanged(nameof(IsBlocked));
                OnPropertyChanged(nameof(AwaitingAnswer));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    private PendingQuestion? _question;

    /// <summary>The task waits for the person to answer a question of its agent.</summary>
    public bool HasQuestion => Question is not null;

    /// <summary>
    /// The task is blocked because its agent asked: «Espera tu respuesta», not a block to retry. «Reintentar» would relaunch it
    /// without the answer, which is what the question is there to avoid (ADR-0051), so the monitor does not offer it.
    /// </summary>
    public bool AwaitingAnswer => Question is not null
        || (Status == WorkTaskStatus.Blocked && BlockReason is { } block && block.StartsWith(BlockReasonText.AwaitingAnswerReason, StringComparison.OrdinalIgnoreCase));

    /// <summary>The agent that runs the task as an id; null while it is not known.</summary>
    internal AgentId? LiveAgent => string.IsNullOrWhiteSpace(Agent) ? null : new AgentId(Agent);

    /// <summary>
    /// The bar of the context by parts (docs/17 D-13, F13-6): for a task that runs an agent and has started, the parts of its
    /// prompt as <c>AgentRunStarted</c> recorded them and what the CLI reports beyond them; null otherwise.
    /// </summary>
    public MetricBarReading? PartsBar
    {
        get
        {
            if (!RunsAnAgent || FromLog || (Status == WorkTaskStatus.Pending && _partsAttempt == 0 && _parts is null))
            {
                return null;
            }

            var (model, used, window) = ContextFigures();
            return MonitorBars.PartsBar(_parts, _partsFailed, model, used, window, _thresholds);
        }
    }

    /// <summary>The bar of the tokens of the finished attempts of the task (D-12); null for a task that runs no agent.</summary>
    public MetricBarReading? ConsumptionBar => RunsAnAgent ? MonitorBars.ConsumptionBar(_finishedAttempts) : null;

    /// <summary>The model, the use and the window of the step now, as the tracker of the attempt follows them.</summary>
    private (string? Model, MetricValue<long> Used, MetricValue<long> Window) ContextFigures()
    {
        if (_contextState is { } state && (state.Steps > 0 || state.UsedTokens.Origin != MetricOrigin.Missing))
        {
            return (state.Model, state.UsedTokens, state.ContextWindow);
        }

        return _lastContext is { } step
            ? (step.Model, step.PromptTokens, step.ContextWindow)
            : (null, MetricValue<long>.Missing("no step of the agent was reported yet"), MetricValue<long>.Missing("the window of the model is not known yet"));
    }

    /// <summary>The context of the step in progress as the projection says it (D-14): the figures and the parts before the launch.</summary>
    internal LiveContext? ReadLiveContext()
    {
        if (!RunsAnAgent || Status == WorkTaskStatus.Pending && _lastContext is null && _contextState is null)
        {
            return null;
        }

        var (model, used, window) = ContextFigures();
        return new LiveContext(model, used, window, _parts ?? []);
    }

    /// <summary>Notes the usage the CLI reported and when the attempt ended, so the consumption counts the finished attempts only.</summary>
    internal void NoteAttemptEvent(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case UsageReported reported:
                _attemptUsage = reported.Usage;
                break;
            case RunFinished finished:
                EndClass = finished.Outcome.Classified().Class;
                OnPropertyChanged(nameof(EndClass));
                FoldAttempt();
                break;
        }
    }

    /// <summary>The attempt that was following ends: its usage, if any was reported, counts in the consumption of the task.</summary>
    internal void FoldAttempt()
    {
        if (_attemptUsage is { } usage)
        {
            _finishedAttempts.Add(usage);
            _attemptUsage = null;
            Touch();
        }
    }

    /// <summary>The parts of the prompt of the attempt <paramref name="attempt"/>, read from the log; null when it recorded none.</summary>
    internal void SetParts(int attempt, IReadOnlyList<ContextPartSnapshot>? parts, bool failed)
    {
        // A later read that found nothing (or failed) does not take away what an earlier one found for the same attempt.
        if (attempt < _partsAttempt || (attempt == _partsAttempt && parts is null && _parts is { Count: > 0 }))
        {
            return;
        }

        _partsAttempt = attempt;
        _parts = parts;
        _partsFailed = failed;
        Touch();
    }

    /// <summary>A new attempt starts: the parts of the one before no longer say anything.</summary>
    internal void ForgetParts(int attempt)
    {
        _parts = null;
        _partsFailed = false;
        _partsAttempt = Math.Max(attempt, 1);
        Touch();
    }

    /// <summary>Whether the parts of the attempt in course were already read; one still missing is asked again later.</summary>
    internal bool HasParts => _parts is { Count: > 0 };

    /// <summary>The tokens of the finished attempts as input (the whole prompt), cache and output, each a figure with its origin.</summary>
    internal (MetricValue<long> Input, MetricValue<long> Cached, MetricValue<long> Output) FinishedTokens()
    {
        const string none = "no attempt of the task has finished yet";
        if (_finishedAttempts.Count == 0)
        {
            return (MetricValue<long>.Missing(none), MetricValue<long>.Missing(none), MetricValue<long>.Missing(none));
        }

        return (
            MonitorBars.ToValue(MetricAggregate.Sum(_finishedAttempts.Select(a => a.InputTokens)), none),
            MonitorBars.ToValue(MetricAggregate.Sum(_finishedAttempts.Select(a => a.CachedInputTokens)), none),
            MonitorBars.ToValue(MetricAggregate.Sum(_finishedAttempts.Select(a => a.OutputTokens)), none));
    }
}
