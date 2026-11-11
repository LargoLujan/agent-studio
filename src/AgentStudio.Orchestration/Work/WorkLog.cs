using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Decisions;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Waits for the project to be idle and takes its gate, as the memory does before it writes to the log (ADR-0022, decision 3): the
/// head of the log must not move between showing a verdict or an approval and using it, except for the entries of a milestone and
/// its runs themselves. One gate per project serializes the writes of the work services among themselves.
/// </summary>
public sealed class WorkWriteGate(IRunActivity activity, TimeProvider time)
{
    /// <summary>How often a write that waits for a run, a planning, a debate or an audit asks again whether the project is busy.</summary>
    public static readonly TimeSpan BusyPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Runs <paramref name="write"/> with the project idle and the gate taken; idleness is asked again with the gate taken, since a run may have started meanwhile.</summary>
    public async Task<T> RunAsync<T>(string projectId, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(write);
        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        while (true)
        {
            while (activity.IsBusy(projectId))
            {
                await Task.Delay(BusyPollInterval, time, cancellationToken).ConfigureAwait(false);
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!activity.IsBusy(projectId))
                {
                    return await write().ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

/// <summary>Reads the verified log for the work services: one that does not verify is never read nor extended.</summary>
internal static class WorkLog
{
    /// <exception cref="DecisionLogIntegrityException">The log of the project does not verify.</exception>
    public static async Task<IReadOnlyList<DecisionEntry>> LoadAsync(IDecisionRecorder recorder, string projectId, CancellationToken cancellationToken)
    {
        var log = await recorder.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(projectId, log.Verification);
        }

        return log.Entries;
    }

    /// <summary>The entries outside every run (<see cref="DecisionPayload.NoRun"/>) with their payloads, in the order of the log.</summary>
    public static IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> OutsideRuns(IEnumerable<DecisionEntry> entries) =>
        [.. entries.Select(e => (Entry: e, Payload: RunLog.Payload(e))).Where(x => x.Payload is { RunId: DecisionPayload.NoRun }).Select(x => (x.Entry, x.Payload!))];
}
