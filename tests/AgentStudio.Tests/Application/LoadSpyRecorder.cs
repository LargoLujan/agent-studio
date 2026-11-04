using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Tests.Application;

/// <summary>A recorder that tells which loads of the log a caller asked for (<see cref="LogLoad"/>), around a real one.</summary>
internal sealed class LoadSpyRecorder(IDecisionRecorder inner) : IDecisionRecorder
{
    private readonly ConcurrentQueue<LogLoad> _loads = new();

    /// <summary>Every load asked for, in order.</summary>
    public IReadOnlyList<LogLoad> Loads => [.. _loads];

    public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken)
    {
        _loads.Enqueue(LogLoad.Incremental);
        return inner.LoadAsync(projectId, cancellationToken);
    }

    public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken)
    {
        _loads.Enqueue(load);
        return inner.LoadAsync(projectId, load, cancellationToken);
    }

    public Task<DecisionEntry> RecordAsync(
        string projectId,
        Actor actor,
        DecisionKind kind,
        string summary,
        DecisionPayload payload,
        long? corrects = null,
        CancellationToken cancellationToken = default) =>
        inner.RecordAsync(projectId, actor, kind, summary, payload, corrects, cancellationToken);
}
