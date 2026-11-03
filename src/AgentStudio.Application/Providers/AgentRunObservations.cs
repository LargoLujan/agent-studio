using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Application.Providers;

/// <summary>
/// What the events of one launch of an agent said about it, as <c>AgentRunFinished</c> version 2 records it (docs/14 section
/// 5.2 and docs/15 section 5.5): when it was launched, the session it reported, the last usage, the model of the last step,
/// the step with the largest context and the compactions. One instance per launch; it is safe to observe from several
/// threads. The auxiliary runner, the recorder of the judge and the turns of a debate share it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The launch time is the one given (the moment the provider started the run, after any wait in its queue) or, without
/// one, when the first event was observed: a caller that is not told when the queue ended sees the run running at most
/// the start-up of the CLI later.</item>
/// <item>The reported model is that of the last step (<see cref="ContextObserved.Model"/>) or, without steps, the only
/// model of the usage breakdown; never the model of <see cref="SessionStarted"/>, which a CLI may announce for an
/// auxiliary model (docs/14 D-13).</item>
/// <item>Compactions are counted only when the CLI reports its context (<see cref="ProviderCapabilities.ReportsContextOccupancy"/>);
/// otherwise they are not exposed, never zero.</item>
/// </list>
/// </remarks>
/// <param name="reportsContext">Whether the CLI reports the context of each step and its compactions.</param>
/// <param name="resumedSession">The session this launch resumes, as the caller wants it recorded (null: a new session).</param>
/// <param name="launchedAt">When the provider started the run, if the caller knows it.</param>
public sealed class AgentRunObservations(bool reportsContext, string? resumedSession = null, DateTimeOffset? launchedAt = null)
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _launchedAt = launchedAt;
    private string? _sessionId;
    private TokenUsage? _usage;
    private ContextObserved? _peak;
    private string? _lastStepModel;
    private int _compactions;

    public bool ReportsContext { get; } = reportsContext;

    public string? ResumedSession { get; } = resumedSession;

    /// <summary>When the run was launched, or when its first event came; null while nothing was seen.</summary>
    public DateTimeOffset? LaunchedAt
    {
        get
        {
            lock (_gate)
            {
                return _launchedAt;
            }
        }
    }

    /// <summary>The session the last <see cref="SessionStarted"/> named.</summary>
    public string? SessionId
    {
        get
        {
            lock (_gate)
            {
                return _sessionId;
            }
        }
    }

    /// <summary>The last usage the run reported (<see cref="UsageReported"/>).</summary>
    public TokenUsage? Usage
    {
        get
        {
            lock (_gate)
            {
                return _usage;
            }
        }
    }

    /// <summary>The step with the largest prompt.</summary>
    public ContextObserved? Peak
    {
        get
        {
            lock (_gate)
            {
                return _peak;
            }
        }
    }

    /// <summary>The compactions; null when the CLI does not report its context (not exposed, never zero).</summary>
    public int? Compactions
    {
        get
        {
            lock (_gate)
            {
                return ReportsContext ? _compactions : null;
            }
        }
    }

    /// <summary>Takes in one event, seen at <paramref name="at"/> (the launch time, when none was given, is that of the first one).</summary>
    public void Observe(AgentEvent agentEvent, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        lock (_gate)
        {
            _launchedAt ??= at;
            switch (agentEvent)
            {
                case SessionStarted started:
                    _sessionId = started.SessionId;
                    break;
                case UsageReported reported:
                    _usage = reported.Usage;
                    break;
                case ContextObserved step:
                    _lastStepModel = step.Model ?? _lastStepModel;
                    if (step.PromptTokens.Value is { } tokens && (_peak?.PromptTokens.Value is not { } largest || tokens > largest))
                    {
                        _peak = step;
                    }

                    break;
                case CompactionObserved:
                    _compactions++;
                    break;
            }
        }
    }

    /// <summary>The model of the last step or, without steps, the only model of the breakdown of <paramref name="usage"/>.</summary>
    public string? ReportedModel(TokenUsage? usage)
    {
        lock (_gate)
        {
            return _lastStepModel ?? (usage?.PerModel is [var only] ? only.Model : null);
        }
    }

    /// <summary>The largest step, with the window of its model from the breakdown of <paramref name="usage"/> when it has one; null without steps.</summary>
    public ContextPeakSnapshot? PeakSnapshot(TokenUsage? usage)
    {
        var peak = Peak;
        return peak is null ? null : ContextPeakSnapshot.From(peak, usage);
    }
}
