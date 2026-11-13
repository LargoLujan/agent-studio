using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Orchestration.Work;

/// <summary>A remote entry of the log the projections left out, and why (a stable code, never text of the remote).</summary>
internal sealed record IgnoredRemoteEntry(long Sequence, DecisionKind Kind, string Code);

/// <summary>
/// A pull request as the log last saw it: its final observation (<see cref="RemoteSnapshotOrder"/>), the run it belongs to when an observation says so, and when it
/// was recorded.
/// </summary>
internal sealed record ObservedPullRequest(PullRequestRef PullRequest, string? Run, RemoteSnapshot Latest, DateTimeOffset ObservedAt, int Observations);

/// <summary>An operation Agent Studio did on the remote, as the log holds it (validated).</summary>
internal sealed record PerformedOperation(long Sequence, DateTimeOffset At, HostingOperationPerformedPayload Payload);

/// <summary>
/// The remote entries of the verified log (docs/21 §5.3), read as data for the projections of «Trabajo» and «Repositorio y cambios» (D-28 to D-30): the board, the
/// chain, the participation, the annotator, the timeline and the view of the pull requests all read the log once and ask this. Pure: no network, no git, no write.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Read as data.</b> Every entry is validated when it is read (<c>EnsureValid</c> and <c>ToSnapshot</c>); one that does not validate, or whose payload cannot
/// be read, is left out and listed in <see cref="Ignored"/> with its sequence and a code, and never makes the projection fail.</item>
/// <item><b>Idempotent and independent of order.</b> Of the observations of a pull request only the final one counts (<see cref="RemoteSnapshotOrder"/>), so a repeated
/// or out-of-order observation neither duplicates a node nor changes the final state. What depends on the order of operations uses their sequence in the log, which is
/// data of the entry, and not the position in the list.</item>
/// </list>
/// </remarks>
internal sealed class RemoteLog
{
    public const string UnreadableCode = "remote.entry.unreadable";
    public const string InvalidCode = "remote.entry.invalid";

    private readonly Dictionary<string, ObservedPullRequest> _byId;

    private RemoteLog(IReadOnlyList<ObservedPullRequest> pullRequests, IReadOnlyList<PerformedOperation> operations, IReadOnlyList<IgnoredRemoteEntry> ignored)
    {
        PullRequests = pullRequests;
        Operations = operations;
        Ignored = ignored;
        _byId = pullRequests.ToDictionary(p => p.PullRequest.TraceId, StringComparer.Ordinal);
    }

    /// <summary>The pull requests the log observed, by identifier.</summary>
    public IReadOnlyList<ObservedPullRequest> PullRequests { get; }

    /// <summary>The operations Agent Studio did on the remote, by sequence.</summary>
    public IReadOnlyList<PerformedOperation> Operations { get; }

    /// <summary>The remote entries that were left out, by sequence.</summary>
    public IReadOnlyList<IgnoredRemoteEntry> Ignored { get; }

    public static RemoteLog Empty { get; } = new([], [], []);

    public bool IsEmpty => PullRequests.Count == 0 && Operations.Count == 0 && Ignored.Count == 0;

    /// <summary>Reads the remote entries out of a log already parsed (<see cref="WorkLog.Parse"/>): the entries without a remote kind are not touched.</summary>
    public static RemoteLog Read(IEnumerable<(DecisionEntry Entry, DecisionPayload? Payload)> parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var observations = new Dictionary<string, List<(RemoteSnapshot Snapshot, string? Run, DateTimeOffset At)>>(StringComparer.Ordinal);
        var refs = new Dictionary<string, PullRequestRef>(StringComparer.Ordinal);
        var operations = new List<PerformedOperation>();
        var ignored = new List<IgnoredRemoteEntry>();
        foreach (var (entry, payload) in parsed)
        {
            switch (entry.Kind)
            {
                case DecisionKind.RemoteStateObserved:
                    if (payload is not RemoteStateObservedPayload observed)
                    {
                        ignored.Add(new IgnoredRemoteEntry(entry.Sequence, entry.Kind, UnreadableCode));
                        break;
                    }

                    try
                    {
                        observed.EnsureValid();
                        var pullRequest = observed.ToPullRequest();
                        var snapshot = observed.ToSnapshot();
                        if (!observations.TryGetValue(pullRequest.TraceId, out var list))
                        {
                            observations[pullRequest.TraceId] = list = [];
                            refs[pullRequest.TraceId] = pullRequest;
                        }

                        list.Add((snapshot, observed.Run, entry.RecordedAt));
                    }
                    catch (Exception ex) when (IsInvalidData(ex))
                    {
                        ignored.Add(new IgnoredRemoteEntry(entry.Sequence, entry.Kind, InvalidCode));
                    }

                    break;
                case DecisionKind.HostingOperationPerformed:
                    if (payload is not HostingOperationPerformedPayload performed)
                    {
                        ignored.Add(new IgnoredRemoteEntry(entry.Sequence, entry.Kind, UnreadableCode));
                        break;
                    }

                    try
                    {
                        performed.EnsureValid();
                        operations.Add(new PerformedOperation(entry.Sequence, entry.RecordedAt, performed));
                    }
                    catch (Exception ex) when (IsInvalidData(ex))
                    {
                        ignored.Add(new IgnoredRemoteEntry(entry.Sequence, entry.Kind, InvalidCode));
                    }

                    break;
            }
        }

        var pullRequests = new List<ObservedPullRequest>();
        foreach (var (id, list) in observations.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            var latest = RemoteSnapshotOrder.Latest(list.Select(o => o.Snapshot));

            // The run an observation names; if the final one does not, any other that does, the same whatever the order they came in.
            var run = list.Where(o => ReferenceEquals(o.Snapshot, latest)).Select(o => o.Run).FirstOrDefault(r => r is not null)
                ?? list.Select(o => o.Run).Where(r => r is not null).Order(StringComparer.Ordinal).FirstOrDefault();
            var at = list.Where(o => ReferenceEquals(o.Snapshot, latest)).Select(o => o.At).Max();
            pullRequests.Add(new ObservedPullRequest(refs[id], run, latest, at, list.Count));
        }

        return new RemoteLog(
            pullRequests,
            [.. operations.OrderBy(o => o.Sequence)],
            [.. ignored.OrderBy(i => i.Sequence)]);
    }

    /// <summary>
    /// As <see cref="Read"/> for a reader that has not parsed the log: only the entries of a remote kind are
    /// deserialized, so a project without a remote pays one pass over the kinds and nothing else.
    /// </summary>
    public static RemoteLog ReadEntries(IEnumerable<DecisionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Read(entries
            .Where(e => e.Kind is DecisionKind.RemoteStateObserved or DecisionKind.HostingOperationPerformed)
            .Select(e => (Entry: e, Payload: Engine.RunLog.Payload(e))));
    }

    /// <summary>The final observation of a pull request, or null when the log has none.</summary>
    public ObservedPullRequest? Find(PullRequestRef pullRequest) => _byId.GetValueOrDefault(pullRequest.TraceId);

    /// <summary>The pull requests that belong to a run: the ones an observation names with it.</summary>
    public IEnumerable<ObservedPullRequest> OfRun(string run) => PullRequests.Where(p => string.Equals(p.Run, run, StringComparison.Ordinal));

    /// <summary>The method with which Agent Studio itself merged the pull request, when the log has the operation done (the last by sequence); null otherwise.</summary>
    public MergeMethod? MergedByAppWith(PullRequestRef pullRequest) =>
        Operations
            .Where(o => o.Payload.Operation == HostingOperationKind.MergePullRequest && o.Payload.Outcome == HostingOutcome.Ok
                && o.Payload.PullRequest == pullRequest.Number && string.Equals($"{o.Payload.Host}/{o.Payload.Owner}/{o.Payload.Repository}", pullRequest.Repository.Id, StringComparison.Ordinal))
            .Select(o => o.Payload.Method)
            .LastOrDefault(m => m is not null);

    /// <summary>The succession of commits of the merged pull requests of the log. <paramref name="judgedCommitOf"/> gives the commit the judge saw for the run of a pull request.</summary>
    public CommitSuccession Succession(Func<ObservedPullRequest, string?> judgedCommitOf)
    {
        ArgumentNullException.ThrowIfNull(judgedCommitOf);
        var judged = PullRequests.ToDictionary(p => p.PullRequest.TraceId, judgedCommitOf, StringComparer.Ordinal);
        return CommitSuccession.From(PullRequests.Select(p => p.Latest), pr => judged.GetValueOrDefault(pr.TraceId), MergedByAppWith);
    }

    /// <summary>An entry that is not what a remote entry holds: an identifier, a number, an enum or a list outside its rules, or a field the JSON left null.</summary>
    internal static bool IsInvalidData(Exception ex) => ex is ArgumentException or InvalidOperationException or NullReferenceException or FormatException or OverflowException or JsonException or KeyNotFoundException;
}
