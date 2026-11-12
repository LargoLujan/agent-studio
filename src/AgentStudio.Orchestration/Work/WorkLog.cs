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

    /// <summary>
    /// The runs linked to a milestone by <c>MilestoneLinked</c> and those that follow them (<c>RunStarted.Follows</c>, transitively), in the order they
    /// started; a linked run that never started comes after.
    /// </summary>
    public static List<string> RunsOfMilestone(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> parsed, string milestone)
    {
        var linked = parsed
            .Select(p => p.Payload)
            .OfType<MilestoneLinkedPayload>()
            .Where(p => string.Equals(p.RunId, milestone, StringComparison.Ordinal) && p.Kind == AgentStudio.Domain.Work.Milestones.MilestoneTargetKind.Run)
            .Select(p => p.Target)
            .ToHashSet(StringComparer.Ordinal);
        return Followed(parsed, linked);
    }

    /// <summary>Each entry with its payload (null when it has none), deserialized once: what a reader that asks the log several things passes around.</summary>
    public static IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> Parse(IEnumerable<DecisionEntry> entries) =>
        [.. entries.Select(e => (Entry: e, Payload: RunLog.Payload(e)))];

    /// <summary>The runs in <paramref name="linked"/> and those that follow them, in the order they started, then the linked ones that never started.</summary>
    public static List<string> Followed(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> parsed, HashSet<string> linked)
    {
        var starts = parsed
            .Select(p => p.Payload as RunStartedPayload)
            .Where(p => p is not null && !string.IsNullOrEmpty(p.RunId))
            .Select(p => p!)
            .DistinctBy(p => p.RunId)
            .ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var start in starts.Where(s => s.Follows is { } follows && linked.Contains(follows) && !linked.Contains(s.RunId)))
            {
                linked.Add(start.RunId);
                changed = true;
            }
        }

        return [.. starts.Where(s => linked.Contains(s.RunId)).Select(s => s.RunId), .. linked.Except(starts.Select(s => s.RunId))];
    }

    /// <summary>The entries outside every run (<see cref="DecisionPayload.NoRun"/>) with their payloads, in the order of the log.</summary>
    public static IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> OutsideRuns(IEnumerable<DecisionEntry> entries) =>
        [.. entries.Select(e => (Entry: e, Payload: RunLog.Payload(e))).Where(x => x.Payload is { RunId: DecisionPayload.NoRun }).Select(x => (x.Entry, x.Payload!))];
}
