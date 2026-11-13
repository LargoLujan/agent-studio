using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Application.Hosting;

/// <summary>
/// A remote entry of the log that the reading left out, and why: a stable code, never text of the remote. <paramref name="Run"/> is the run the entry names in its field when it
/// could be read that far (the entries of the remote belong to no run), or null when the payload could not even be read: what is not attributable to a run is not attributed.
/// </summary>
public sealed record IgnoredRemoteEntry(long Sequence, DecisionKind Kind, string Code, string? Run = null);

/// <summary>
/// A pull request as the log last saw it: its final observation (<see cref="RemoteSnapshotOrder"/>), the run it belongs to when an observation says so, when the final
/// observation was recorded and how many observations the log holds of it.
/// </summary>
public sealed record ObservedPullRequest(PullRequestRef PullRequest, string? Run, RemoteSnapshot Latest, DateTimeOffset ObservedAt, int Observations);

/// <summary>An operation Agent Studio did on the remote, as the log holds it (validated).</summary>
public sealed record PerformedRemoteOperation(long Sequence, DateTimeOffset At, HostingOperationPerformedPayload Payload);

/// <summary>
/// The remote entries of the verified log (docs/21 §5.3), read once and as data: <c>RemoteStateObserved</c> and <c>HostingOperationPerformed</c>. It is the one reading of those
/// entries, for everything that needs them (the projections of «Trabajo» and «Repositorio y cambios» in <c>Orchestration/Work</c>, the report of the milestone in
/// <c>Debate/Audit</c> and the mailbox of the remote): what counts, how it is validated, what is left out and which observation of a pull request is the final one are decided here
/// and nowhere else. Pure: no network, no git, no write.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Read as data.</b> Every entry is validated when it is read (<c>EnsureValid</c> and <c>ToSnapshot</c>); one that does not validate, or whose payload cannot be read, is left out
/// and listed in <see cref="Ignored"/> with its sequence, a code (<see cref="UnreadableCode"/> or <see cref="InvalidCode"/>) and, when the entry says it, its run. It never makes the
/// reading fail.</item>
/// <item><b>Idempotent and independent of order.</b> Of the observations of a pull request only the final one counts (<see cref="RemoteSnapshotOrder"/>), so a repeated or out-of-order
/// observation neither duplicates a pull request nor changes the final state. The run of a pull request is the smallest (ordinal) run named by the observations that are the final one
/// and, if none names it, by any observation; the moment is the latest of those. What depends on the order of operations uses their <c>Sequence</c>, which is data of the entry, and not the
/// position in the list.</item>
/// <item><b>The pull request is its exact identifier</b> (<see cref="PullRequestRef.TraceId"/>): the same repository written with another case is another pull request (the equality of
/// <see cref="RemoteRepository"/> is exact, by decision of the P0).</item>
/// </list>
/// </remarks>
public sealed class RemoteLogReading
{
    public const string UnreadableCode = "remote.entry.unreadable";
    public const string InvalidCode = "remote.entry.invalid";

    private readonly Dictionary<string, ObservedPullRequest> _byId;

    private RemoteLogReading(IReadOnlyList<ObservedPullRequest> pullRequests, IReadOnlyList<PerformedRemoteOperation> operations, IReadOnlyList<IgnoredRemoteEntry> ignored)
    {
        PullRequests = pullRequests;
        Operations = operations;
        Ignored = ignored;
        _byId = pullRequests.ToDictionary(p => p.PullRequest.TraceId, StringComparer.Ordinal);
    }

    /// <summary>The pull requests the log observed, by identifier.</summary>
    public IReadOnlyList<ObservedPullRequest> PullRequests { get; }

    /// <summary>The operations Agent Studio did on the remote that validate, by sequence.</summary>
    public IReadOnlyList<PerformedRemoteOperation> Operations { get; }

    /// <summary>The remote entries that were left out, by sequence.</summary>
    public IReadOnlyList<IgnoredRemoteEntry> Ignored { get; }

    public static RemoteLogReading Empty { get; } = new([], [], []);

    /// <summary>Reads the remote entries out of a log already parsed (each entry with its payload, or null when it could not be read); the entries of another kind are not touched.</summary>
    public static RemoteLogReading Read(IEnumerable<(DecisionEntry Entry, DecisionPayload? Payload)> parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var observations = new Dictionary<string, List<(RemoteSnapshot Snapshot, string? Run, DateTimeOffset At)>>(StringComparer.Ordinal);
        var refs = new Dictionary<string, PullRequestRef>(StringComparer.Ordinal);
        var operations = new List<PerformedRemoteOperation>();
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
                        ignored.Add(new IgnoredRemoteEntry(entry.Sequence, entry.Kind, InvalidCode, observed.Run));
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
                        operations.Add(new PerformedRemoteOperation(entry.Sequence, entry.RecordedAt, performed));
                    }
                    catch (Exception ex) when (IsInvalidData(ex))
                    {
                        ignored.Add(new IgnoredRemoteEntry(entry.Sequence, entry.Kind, InvalidCode, performed.Run));
                    }

                    break;
            }
        }

        var pullRequests = new List<ObservedPullRequest>();
        foreach (var (id, list) in observations.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            var latest = RemoteSnapshotOrder.Latest(list.Select(o => o.Snapshot));

            // The observations that are the final one (equal in every field that orders them): the run and the moment come from them, by a total order, so that two of them that
            // name another run do not leave the choice to the position in the list.
            var finals = list.Where(o => RemoteSnapshotOrder.Compare(o.Snapshot, latest) == 0).ToList();
            var run = finals.Select(o => o.Run).Where(r => r is not null).Order(StringComparer.Ordinal).FirstOrDefault()
                ?? list.Select(o => o.Run).Where(r => r is not null).Order(StringComparer.Ordinal).FirstOrDefault();
            pullRequests.Add(new ObservedPullRequest(refs[id], run, latest, finals.Max(o => o.At), list.Count));
        }

        return new RemoteLogReading(pullRequests, [.. operations.OrderBy(o => o.Sequence)], [.. ignored.OrderBy(i => i.Sequence)]);
    }

    /// <summary>
    /// As <see cref="Read"/> for a reader that has not parsed the log: only the entries of a remote kind are deserialized, so a project without a remote pays one pass over the kinds and
    /// nothing else. A payload that cannot be read is null, and is listed as left out.
    /// </summary>
    public static RemoteLogReading ReadEntries(IEnumerable<DecisionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Read(entries
            .Where(e => e.Kind is DecisionKind.RemoteStateObserved or DecisionKind.HostingOperationPerformed)
            .Select(e => (Entry: e, Payload: Parse(e))));
    }

    /// <summary>The final observation of a pull request, or null when the log has none.</summary>
    public ObservedPullRequest? Find(PullRequestRef pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return _byId.GetValueOrDefault(pullRequest.TraceId);
    }

    /// <summary>The pull requests that belong to a run: the ones an observation names with it.</summary>
    public IEnumerable<ObservedPullRequest> OfRun(string run) => PullRequests.Where(p => string.Equals(p.Run, run, StringComparison.Ordinal));

    /// <summary>The method with which Agent Studio itself merged the pull request, when the log has the operation done with <c>Ok</c> (the last by sequence); null otherwise.</summary>
    public MergeMethod? MergedByAppWith(PullRequestRef pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return Operations
            .Where(o => o.Payload.Operation == HostingOperationKind.MergePullRequest && o.Payload.Outcome == HostingOutcome.Ok
                && o.Payload.PullRequest == pullRequest.Number && string.Equals($"{o.Payload.Host}/{o.Payload.Owner}/{o.Payload.Repository}", pullRequest.Repository.Id, StringComparison.Ordinal))
            .Select(o => o.Payload.Method)
            .LastOrDefault(m => m is not null);
    }

    /// <summary>The succession of commits of the merged pull requests. <paramref name="judgedCommitOf"/> gives the commit the judge saw for the run of a pull request (null: the log does not say).</summary>
    public CommitSuccession Succession(Func<ObservedPullRequest, string?> judgedCommitOf)
    {
        ArgumentNullException.ThrowIfNull(judgedCommitOf);
        var judged = PullRequests.ToDictionary(p => p.PullRequest.TraceId, judgedCommitOf, StringComparer.Ordinal);
        return CommitSuccession.From(PullRequests.Select(p => p.Latest), pr => judged.GetValueOrDefault(pr.TraceId), MergedByAppWith);
    }

    /// <summary>Whether an exception is what reading an entry as data throws for what is not one: an identifier, a number, an enum or a list outside its rules, or a field the JSON left null.</summary>
    public static bool IsInvalidData(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or NullReferenceException or FormatException or OverflowException or JsonException or KeyNotFoundException;

    private static DecisionPayload? Parse(DecisionEntry entry)
    {
        try
        {
            return DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
