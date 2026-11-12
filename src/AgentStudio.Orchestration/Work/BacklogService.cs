using System.Globalization;
using System.Security.Cryptography;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// The backlog of a project (docs/16 D-18, ADR-0038): rows append-only in the store, each version announced in the log, and every
/// state derived from the log, as the memory is (ADR-0022).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Writing a version.</b> <c>BacklogItemRecorded</c> goes to the log first, with the canonical SHA-256 of the content and no
/// text; then the row, stamped with the time of the announcement (so the time of a version is the log's). Writing the version again with
/// the same content completes its row if the app died between the two steps; with other content it is refused.</item>
/// <item><b>Reading</b> returns only the rows the log announced with the same hash. A row that differs from its announcement (content or
/// time) is a log or a store that was tampered with and fails closed with <see cref="InvalidOperationException"/>; so does a log the
/// domain does not replay (<see cref="BacklogReplay.Problems"/>), and one that does not verify (<see cref="DecisionLogIntegrityException"/>).
/// An announcement without its row is not an item yet; a row nobody announced is not read.</item>
/// <item>Retiring and reordering are entries of the log, decisions of the person; no row is ever deleted.</item>
/// <item>Writing waits until no run, debate or audit has the project (<see cref="WorkWriteGate"/>).</item>
/// </list>
/// </remarks>
public sealed class BacklogService(IDecisionRecorder recorder, IBacklogStore store, WorkWriteGate gate, TimeProvider time) : IBacklogService
{
    public Task<BacklogItem> AddAsync(ProjectContext project, BacklogDraft draft, string addedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(addedBy);
        return gate.RunAsync(
            project.ProjectId,
            async () =>
            {
                var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
                var item = Draft(await OrphanIdOrNewAsync(project.ProjectId, draft, entries, cancellationToken).ConfigureAwait(false), 1, draft);
                return await PutAsync(project.ProjectId, item, addedBy, entries, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<BacklogItem> ReviseAsync(ProjectContext project, BacklogItemId item, BacklogDraft draft, string revisedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisedBy);
        return gate.RunAsync(
            project.ProjectId,
            async () =>
            {
                var (state, entries, _) = await ReadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
                var history = state.History(item);
                if (history.Count == 0)
                {
                    throw new ArgumentException($"'{item.Value}' is not in the backlog.", nameof(item));
                }

                var next = Draft(item, history[^1].Version + 1, draft);

                // The domain says whether this version can follow (a retired item takes none).
                state.Record(next with { CreatedAt = DateTimeOffset.MaxValue });
                return await PutAsync(project.ProjectId, next, revisedBy, entries, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task RetireAsync(ProjectContext project, BacklogItemId item, string retiredBy, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(retiredBy);
        return gate.RunAsync(
            project.ProjectId,
            async () =>
            {
                var (state, _, _) = await ReadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
                var history = state.History(item);
                var version = history.Count == 0 ? 0 : history[^1].Version;
                state.Retire(item, version, DateTimeOffset.MaxValue);
                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(ActorKind.User, retiredBy),
                    DecisionKind.BacklogItemRetired,
                    $"Retira {item.Value} del backlog",
                    BacklogItemRetiredPayload.Retiring(item, version, retiredBy, reason),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task ReorderAsync(ProjectContext project, IReadOnlyList<BacklogItemId> items, string decidedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        return gate.RunAsync(
            project.ProjectId,
            async () =>
            {
                var (state, _, _) = await ReadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
                state.Reorder(items, DateTimeOffset.MaxValue);
                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(ActorKind.User, decidedBy),
                    DecisionKind.BacklogOrderChanged,
                    "Cambia el orden del backlog",
                    BacklogOrderChangedPayload.Ordering(items, decidedBy),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<BacklogEntry>> ListAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var (state, _, _) = await ReadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        return [.. state.Items.Select(i => new BacklogEntry(i, state.History(i.Id).Count))];
    }

    public async Task<IReadOnlyList<BacklogItem>> HistoryAsync(ProjectContext project, BacklogItemId item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var (state, _, _) = await ReadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        return [.. state.History(item)];
    }

    /// <summary>
    /// The backlog as the verified log and the rows say it, with the log it was read from. Fails closed: see the class.
    /// </summary>
    private async Task<(BacklogState State, IReadOnlyList<DecisionEntry> Entries, IReadOnlyDictionary<(string Item, int Version), BacklogItemRecordedPayload> Announced)> ReadAsync(string projectId, CancellationToken cancellationToken)
    {
        var entries = await WorkLog.LoadAsync(recorder, projectId, cancellationToken).ConfigureAwait(false);
        var rows = (await store.ListAsync(projectId, cancellationToken).ConfigureAwait(false)).ToDictionary(i => (i.Id.Value, i.Version));
        var announced = new Dictionary<(string Item, int Version), BacklogItemRecordedPayload>();
        var events = new List<BacklogEvent>();
        foreach (var (entry, payload) in WorkLog.OutsideRuns(entries))
        {
            switch (payload)
            {
                case BacklogItemRecordedPayload recorded when announced.TryAdd((recorded.Item, recorded.Version), recorded):
                    if (rows.TryGetValue((recorded.Item, recorded.Version), out var row))
                    {
                        if (!string.Equals(row.Sha256(), recorded.Sha256, StringComparison.Ordinal) || row.Priority != recorded.Priority || row.CreatedAt != entry.RecordedAt)
                        {
                            throw new InvalidOperationException($"The row of version {recorded.Version} of backlog item '{recorded.Item}' does not match its announcement in the decision log (its content hashes to {row.Sha256()}, the log announced {recorded.Sha256}): the backlog is not read.");
                        }

                        events.Add(new ItemRecorded(row));
                    }

                    // An announcement without its row is not an item yet: writing the version again completes it.
                    break;
                case BacklogItemRetiredPayload retired:
                    events.Add(new ItemRetired(entry.RecordedAt, new BacklogItemId(retired.Item), retired.Version));
                    break;
                case BacklogOrderChangedPayload ordered:
                    events.Add(new OrderChanged(entry.RecordedAt, [.. ordered.Items.Select(i => new BacklogItemId(i))]));
                    break;
            }
        }

        var replay = BacklogReplayer.Replay(events);
        if (!replay.IsConsistent)
        {
            throw new InvalidOperationException($"The decision log does not describe a backlog the domain allows: {string.Join(" ", replay.Problems)}");
        }

        return (replay.Backlog, entries, announced);
    }

    /// <summary>Announces a version, then stores its row stamped with the time of the announcement; or completes the row of an announcement already in the log.</summary>
    private async Task<BacklogItem> PutAsync(string projectId, BacklogItem item, string by, IReadOnlyList<DecisionEntry> entries, CancellationToken cancellationToken)
    {
        var announcement = entries
            .Where(e => e.Kind == DecisionKind.BacklogItemRecorded)
            .Select(e => (Entry: e, Payload: RunLog.Payload(e) as BacklogItemRecordedPayload))
            .FirstOrDefault(x => x.Payload is { RunId: DecisionPayload.NoRun } p && p.Item == item.Id.Value && p.Version == item.Version);
        if (announcement.Payload is { } announced)
        {
            if (!string.Equals(announced.Sha256, item.Sha256(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Version {item.Version} of backlog item '{item.Id.Value}' was announced with other content: nothing was written.");
            }

            var completed = item with { CreatedAt = announcement.Entry.RecordedAt };
            await store.AppendAsync(projectId, completed, cancellationToken).ConfigureAwait(false);
            return completed;
        }

        var entry = await recorder.RecordAsync(
            projectId,
            new Actor(ActorKind.User, by),
            DecisionKind.BacklogItemRecorded,
            $"Backlog: versión {item.Version} de {item.Id.Value}",
            BacklogItemRecordedPayload.Announcing(item),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = item with { CreatedAt = entry.RecordedAt };
        await store.AppendAsync(projectId, row, cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// The id of an item announced as version 1 whose row was never stored and whose announced hash is this draft's: adding the same thing
    /// again after the app died between the two steps completes that item instead of leaving it orphaned and making another. Otherwise a new id.
    /// </summary>
    private async Task<BacklogItemId> OrphanIdOrNewAsync(string projectId, BacklogDraft draft, IReadOnlyList<DecisionEntry> entries, CancellationToken cancellationToken)
    {
        var rows = (await store.ListAsync(projectId, cancellationToken).ConfigureAwait(false)).Select(i => (i.Id.Value, i.Version)).ToHashSet();
        foreach (var announced in WorkLog.OutsideRuns(entries).Select(e => e.Payload).OfType<BacklogItemRecordedPayload>().Where(p => p.Version == 1 && !rows.Contains((p.Item, 1))))
        {
            try
            {
                var id = new BacklogItemId(announced.Item);
                if (string.Equals(Draft(id, 1, draft).Sha256(), announced.Sha256, StringComparison.Ordinal))
                {
                    return id;
                }
            }
            catch (ArgumentException)
            {
                // An id of the log that is not one of ours is not an item to complete.
            }
        }

        return NewId(entries);
    }

    private BacklogItem Draft(BacklogItemId id, int version, BacklogDraft draft)
    {
        var item = new BacklogItem(id, version, draft.Statement?.Trim() ?? string.Empty, draft.Priority, [.. draft.Criteria ?? []], time.GetUtcNow());
        var problems = item.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"The backlog item is not valid: {string.Join(", ", problems)}.", nameof(draft));
        }

        return item;
    }

    private BacklogItemId NewId(IReadOnlyList<DecisionEntry> entries)
    {
        var day = time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var taken = WorkLog.OutsideRuns(entries).Select(e => e.Payload).OfType<BacklogItemRecordedPayload>().Select(p => p.Item).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < 64; i++)
        {
            var id = $"{BacklogItemId.Prefix}{day}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            if (!taken.Contains(id))
            {
                return new BacklogItemId(id);
            }
        }

        throw new InvalidOperationException("Could not allocate a free backlog item id.");
    }
}
