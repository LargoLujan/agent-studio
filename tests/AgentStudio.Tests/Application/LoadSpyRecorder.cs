using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Tests.Application;

/// <summary>A recorder that tells which loads of the log a caller asked for (<see cref="LogLoad"/>), around a real one.</summary>
internal sealed class LoadSpyRecorder(IDecisionRecorder inner) : IDecisionRecorder
{
    private readonly ConcurrentQueue<LogLoad> _loads = new();
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>Every load asked for, in order.</summary>
    public IReadOnlyList<LogLoad> Loads => [.. _loads];

    /// <summary>Every call, in order: the <see cref="LogLoad"/> of a load, or <c>Record:</c> and the kind of the entry recorded.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) =>
        LoadAsync(projectId, LogLoad.Incremental, cancellationToken);

    public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken)
    {
        _loads.Enqueue(load);
        _calls.Enqueue(load.ToString());
        return inner.LoadAsync(projectId, load, cancellationToken);
    }

    public Task<DecisionEntry> RecordAsync(
        string projectId,
        Actor actor,
        DecisionKind kind,
        string summary,
        DecisionPayload payload,
        long? corrects = null,
        CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("Record:" + kind);
        return inner.RecordAsync(projectId, actor, kind, summary, payload, corrects, cancellationToken);
    }
}
