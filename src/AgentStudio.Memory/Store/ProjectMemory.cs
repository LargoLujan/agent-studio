using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Memory.Store;

/// <summary>
/// The memory of a project (docs/14 D-18 to D-21; ADR-0022): rows append-only in the store, each one announced in the
/// decision log, and every state derived from the log.
/// <list type="bullet">
/// <item><b>Recording.</b> The topic (trimmed) and the content are redacted (<see cref="ISecretRedactor"/>) and the content
/// is cut to <see cref="MemoryContent.MaxCharacters"/>; nothing else changes it, and the canonical SHA-256 covers exactly
/// that. <c>MemoryRecorded</c> goes to
/// the log first, with no text and outside every run; then the row, stamped with the time of its announcement. The id
/// comes from the origin, the source, the hash and the entry it supersedes, so recording the same draft again gives the
/// same entry, and completes its row if the app died between the two steps.</item>
/// <item><b>Trust.</b> The system and the user are trusted; an agent's text is a proposal until a person accepts it; what
/// was announced before the last history accepted as a new origin is in quarantine until a person reviews it; a rejected
/// or retracted entry is never trusted again unless a person accepts it later.</item>
/// <item><b>Recall.</b> Only rows announced in the log with what they hold: a row never announced, or changed since, is
/// left out and raises an integrity alert (<c>AuditAlertRaised</c>, outside every run, once per problem). An entry that a
/// trusted entry supersedes is left out. With text, by relevance (<see cref="IMemoryStore"/>); without, newest first.
/// Entries go in that order while they fit in the budgets; one that does not fit in the characters left (topic and
/// content) is skipped.</item>
/// <item><b>Fail closed.</b> On a log that does not verify, nothing is recorded, reviewed or recalled
/// (<see cref="DecisionLogIntegrityException"/>).</item>
/// <item><b>Never while a run or a planning goes on.</b> Every write to the log (recording, reviews and the integrity
/// alerts) waits until <see cref="IRunActivity"/> says the project is idle, asking again every
/// <see cref="BusyPollInterval"/>; recall itself never waits, since the context packer recalls when an agent is launched.
/// An alert found while the project is busy is raised by the next recall that finds it idle.</item>
/// </list>
/// </summary>
/// <remarks>
/// <see cref="IRunActivity"/> only answers whether the project is busy now: a run that starts between that answer and the
/// write, in this process, still sees the write. The window is a few milliseconds; a reservation would need the port to
/// change.
/// </remarks>
public sealed class ProjectMemory(IDecisionRecorder recorder, IMemoryStore store, IRunActivity activity, ISecretRedactor redactor, TimeProvider timeProvider) : IProjectMemory
{
    /// <summary>The alert of a row the log never announced.</summary>
    public const string NotAnchoredAlert = "memory.entry-not-anchored";

    /// <summary>The alert of a row that no longer matches its announcement.</summary>
    public const string ModifiedAlert = "memory.entry-modified";

    /// <summary>How often a write that waits for a run or a planning asks again whether the project is busy.</summary>
    public static readonly TimeSpan BusyPollInterval = TimeSpan.FromMilliseconds(500);

    private const string Ellipsis = "…";

    /// <summary>Agent Studio announces every row; who wrote its text is its origin.</summary>
    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<MemoryEntry> RecordAsync(ProjectContext project, MemoryDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var prepared = Prepare(draft);
        return await WriteAsync(project.ProjectId, () => RecordPreparedAsync(project.ProjectId, prepared, ct), ct).ConfigureAwait(false);
    }

    public async Task ReviewAsync(ProjectContext project, string entry, MemoryReview decision, string decidedBy, string? comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown memory review.");
        }

        var note = string.IsNullOrWhiteSpace(comment) ? null : redactor.Redact(comment.Trim());
        await WriteAsync(
            project.ProjectId,
            async () =>
            {
                var ledger = await LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
                if (ledger.Announcement(entry) is null)
                {
                    throw new ArgumentException($"Memory entry '{entry}' was never announced in the decision log, so there is nothing to review.", nameof(entry));
                }

                if (decision == MemoryReview.Accepted
                    && (!(await RowsAsync(project.ProjectId, ct).ConfigureAwait(false)).TryGetValue(entry, out var row) || ledger.Check(row) is not null))
                {
                    throw new InvalidOperationException($"Memory entry '{entry}' has no row that matches its announcement in the decision log: what a person would accept is not what was announced.");
                }

                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(ActorKind.User, decidedBy),
                    DecisionKind.MemoryReviewed,
                    $"Memoria {MemoryLabels.Review(decision)} por {decidedBy}",
                    MemoryReviewedPayload.Deciding(entry, decision, decidedBy, note),
                    cancellationToken: ct).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(ProjectContext project, MemoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await SnapshotAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (query.MaxEntries <= 0)
        {
            return [];
        }

        var filtered = !string.IsNullOrWhiteSpace(query.Text) || query.Kinds is not null || query.Topic is not null;
        var candidates = filtered
            ? await store.SearchAsync(project.ProjectId, new MemorySearch(query.Text, query.Kinds, query.Topic), ct).ConfigureAwait(false)
            : snapshot.Rows;
        var recalled = new List<MemoryEntry>();
        var characters = 0;
        foreach (var candidate in candidates)
        {
            if (snapshot.Entry(candidate.Id) is not { SupersededBy: null } entry || (query.TrustedOnly && entry.Trust != MemoryTrust.Trusted))
            {
                continue;
            }

            var size = entry.Topic.Length + entry.Content.Length;
            if (query.MaxCharacters is { } budget && characters + size > budget)
            {
                continue;
            }

            recalled.Add(entry);
            characters += size;
            if (recalled.Count == query.MaxEntries)
            {
                break;
            }
        }

        return recalled;
    }

    /// <summary>
    /// Every anchored entry of the project with the trust and the supersession the log gives it, newest first: what recall
    /// selects from and what the Markdown export writes. It raises the integrity alerts of the rows left out, unless the
    /// project is busy.
    /// </summary>
    internal async Task<MemorySnapshot> SnapshotAsync(string projectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var ledger = await LoadAsync(projectId, ct).ConfigureAwait(false);
        var rows = await store.SearchAsync(projectId, new MemorySearch(), ct).ConfigureAwait(false);
        var problems = new List<MemoryIntegrityProblem>();
        var intact = new List<MemoryRow>();
        foreach (var row in rows)
        {
            if (ledger.Check(row) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                intact.Add(row);
            }
        }

        await RaiseAsync(projectId, problems, ct).ConfigureAwait(false);
        return MemorySnapshot.Of(ledger, intact);
    }

    private async Task<MemoryEntry> RecordPreparedAsync(string projectId, PreparedEntry prepared, CancellationToken ct)
    {
        var ledger = await LoadAsync(projectId, ct).ConfigureAwait(false);
        if (ledger.Announcement(prepared.Id) is { } announced)
        {
            // Deriving again: the announcement is there, and its row too unless the app died between the two steps.
            var rows = await RowsAsync(projectId, ct).ConfigureAwait(false);
            if (!rows.TryGetValue(prepared.Id, out var existing))
            {
                existing = prepared.ToRow(announced.At);
                if (ledger.Check(existing) is not null)
                {
                    throw new InvalidOperationException($"Memory entry '{prepared.Id}' was announced with something else than this draft: nothing was written.");
                }

                await store.AppendAsync(projectId, existing, ct).ConfigureAwait(false);
                rows[existing.Id] = existing;
            }
            else if (ledger.Check(existing) is not null)
            {
                throw new InvalidOperationException($"The row of memory entry '{prepared.Id}' no longer matches its announcement in the decision log: it cannot be recorded again.");
            }

            return MemorySnapshot.Of(ledger, [.. rows.Values.Where(r => ledger.Check(r) is null)]).Entry(existing.Id)!;
        }

        if (prepared.Supersedes is { } superseded
            && (ledger.Announcement(superseded) is null
                || !(await RowsAsync(projectId, ct).ConfigureAwait(false)).TryGetValue(superseded, out var old)
                || ledger.Check(old) is not null))
        {
            throw new ArgumentException($"Memory entry '{superseded}' cannot be superseded: the decision log never announced it, or its row is missing or changed.", nameof(prepared));
        }

        var payload = MemoryRecordedPayload.Announcing(prepared.Id, prepared.Kind, prepared.Origin, prepared.Sha256, prepared.Source, prepared.Supersedes);
        var announcement = await recorder.RecordAsync(
            projectId,
            SystemActor,
            DecisionKind.MemoryRecorded,
            $"Memoria registrada: {MemoryLabels.Kind(prepared.Kind)} ({MemoryLabels.Origin(prepared.Origin)})",
            payload,
            cancellationToken: ct).ConfigureAwait(false);
        var row = prepared.ToRow(announcement.RecordedAt);
        await store.AppendAsync(projectId, row, ct).ConfigureAwait(false);

        // The trust the log gives a new announcement: a journal entry Agent Studio did not write is in quarantine unless
        // the journal of the project or of its day is trusted (docs/17 D-02), not a proposal.
        return new MemoryEntry(
            row.Id,
            row.Kind,
            row.Topic,
            row.Content,
            row.Origin,
            row.Source,
            row.CreatedAt,
            row.ContentSha256,
            ledger.TrustOf(new MemoryAnnouncement(announcement.Sequence, announcement.RecordedAt, payload)),
            row.Supersedes,
            null);
    }

    /// <summary>The draft as it will be stored: checked, redacted, cut to the limit and hashed, with its id.</summary>
    private PreparedEntry Prepare(MemoryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Enum.IsDefined(draft.Kind) || !Enum.IsDefined(draft.Origin))
        {
            throw new ArgumentException("The draft has an unknown category or origin.", nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.Topic) || string.IsNullOrWhiteSpace(draft.Content) || string.IsNullOrWhiteSpace(draft.Source))
        {
            throw new ArgumentException("A memory entry needs a topic, a content and the source it cites.", nameof(draft));
        }

        if (draft.Supersedes is not null && string.IsNullOrWhiteSpace(draft.Supersedes))
        {
            throw new ArgumentException("The entry a draft supersedes must be named.", nameof(draft));
        }

        // The topic is a label, so it is trimmed; the content is kept as written, only redacted and cut to the limit.
        var topic = redactor.Redact(draft.Topic.Trim());
        var content = Limit(redactor.Redact(draft.Content));
        var sha = MemoryContent.Sha256(draft.Kind, topic, content);
        var id = "mem-" + ContentHash.Sha256Hex(ContentHash.Canonical("memory-entry", draft.Origin.ToString(), draft.Source, sha, draft.Supersedes))[..16];
        return new PreparedEntry(id, draft.Kind, topic, content, draft.Origin, draft.Source, sha, draft.Supersedes);
    }

    /// <summary>The content cut to <see cref="MemoryContent.MaxCharacters"/>, with an ellipsis that says so; never half a character.</summary>
    private static string Limit(string content)
    {
        if (content.Length <= MemoryContent.MaxCharacters)
        {
            return content;
        }

        var cut = MemoryContent.MaxCharacters - Ellipsis.Length;
        if (char.IsHighSurrogate(content[cut - 1]))
        {
            cut--;
        }

        return content[..cut] + Ellipsis;
    }

    /// <summary>The log of the project, which must verify: memory never trusts, nor extends, one that does not.</summary>
    private async Task<MemoryLedger> LoadAsync(string projectId, CancellationToken ct)
    {
        var state = await recorder.LoadAsync(projectId, ct).ConfigureAwait(false);
        return state.Verification.IsValid
            ? MemoryLedger.From(state.Entries)
            : throw new DecisionLogIntegrityException(projectId, state.Verification);
    }

    private async Task<Dictionary<string, MemoryRow>> RowsAsync(string projectId, CancellationToken ct) =>
        (await store.SearchAsync(projectId, new MemorySearch(), ct).ConfigureAwait(false)).ToDictionary(r => r.Id, StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="write"/> once the project is idle, one memory write of the project at a time. The wait is
    /// outside the gate, so a recall (the packer, inside the run being waited for) is never held up by it; the activity
    /// is asked again with the gate taken, since a run may have started meanwhile.
    /// </summary>
    private async Task<T> WriteAsync<T>(string projectId, Func<Task<T>> write, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        while (true)
        {
            while (activity.IsBusy(projectId))
            {
                await Task.Delay(BusyPollInterval, timeProvider, ct).ConfigureAwait(false);
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Records an alert for each problem the log does not hold yet, when the project is idle; while it is busy nothing is
    /// written, and the next recall that finds the problem with the project idle raises it. If the log stopped verifying
    /// meanwhile, the next load reports it.
    /// </summary>
    private async Task RaiseAsync(string projectId, IReadOnlyList<MemoryIntegrityProblem> problems, CancellationToken ct)
    {
        if (problems.Count == 0 || activity.IsBusy(projectId))
        {
            return;
        }

        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (activity.IsBusy(projectId))
            {
                return;
            }

            // Read again under the gate: another recall may have raised the same alerts meanwhile.
            var ledger = await LoadAsync(projectId, ct).ConfigureAwait(false);
            foreach (var problem in problems.Where(p => !ledger.HasAlert(p.Message)))
            {
                await recorder.RecordAsync(
                    projectId,
                    SystemActor,
                    DecisionKind.AuditAlertRaised,
                    "Alerta de integridad de la memoria",
                    new AuditAlertPayload(problem.Code, [], problem.Message) { RunId = DecisionPayload.NoRun },
                    cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (DecisionLogIntegrityException)
        {
            // The log stopped verifying since it was loaded: whatever reads it next reports that.
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A draft ready to be stored.</summary>
    private sealed record PreparedEntry(string Id, MemoryKind Kind, string Topic, string Content, MemoryOrigin Origin, string Source, string Sha256, string? Supersedes)
    {
        public MemoryRow ToRow(DateTimeOffset at) => new(Id, Kind, Topic, Content, Origin, Source, at, Sha256, Supersedes);
    }
}

/// <summary>
/// The anchored entries of a project at one read of its log and its rows: each with its trust, and the trusted entry
/// that supersedes it (the last one of the log), if any. <see cref="Rows"/> keeps the order of the store (newest first).
/// <see cref="Announced"/> is every entry the log ever announced, anchored or not, which is what tells the export which
/// files it wrote before.
/// </summary>
internal sealed class MemorySnapshot
{
    private readonly Dictionary<string, MemoryEntry> _entries;

    private MemorySnapshot(IReadOnlyList<MemoryRow> rows, Dictionary<string, MemoryEntry> entries, IReadOnlyList<MemoryAnnouncement> announced)
    {
        Rows = rows;
        _entries = entries;
        Announced = announced;
    }

    public IReadOnlyList<MemoryRow> Rows { get; }

    public IEnumerable<MemoryEntry> Entries => Rows.Select(r => _entries[r.Id]);

    public IReadOnlyList<MemoryAnnouncement> Announced { get; }

    public static MemorySnapshot Of(MemoryLedger ledger, IReadOnlyList<MemoryRow> intact)
    {
        var anchored = intact.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var trust = intact.ToDictionary(r => r.Id, r => ledger.TrustOf(ledger.Announcement(r.Id)!), StringComparer.Ordinal);
        var entries = new Dictionary<string, MemoryEntry>(StringComparer.Ordinal);
        foreach (var row in intact)
        {
            var supersededBy = ledger.SupersedersOf(row.Id)
                .Where(s => anchored.ContainsKey(s.Payload.Entry) && trust[s.Payload.Entry] == MemoryTrust.Trusted)
                .Select(s => s.Payload.Entry)
                .LastOrDefault();
            entries.Add(row.Id, new MemoryEntry(row.Id, row.Kind, row.Topic, row.Content, row.Origin, row.Source, row.CreatedAt, row.ContentSha256, trust[row.Id], row.Supersedes, supersededBy));
        }

        return new MemorySnapshot(intact, entries, ledger.Announcements);
    }

    /// <summary>The anchored entry with that id, or null when its row is missing or not what the log announced.</summary>
    public MemoryEntry? Entry(string id) => _entries.GetValueOrDefault(id);
}
