using System.Collections.Concurrent;
using System.Threading.Channels;
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
/// <item><b>From memory.</b> What recall selects from (<see cref="SnapshotAsync"/>) is kept per project, verified, and
/// served again while the log only grows (ADR-0010, addenda of 2026-11-04 and 2026-11-05): a recall with nothing new
/// reads no row, and one after the log grew reads and checks only the rows of what it announced since. At each whole
/// verification of the log (<see cref="DecisionLogState.WholeVerification"/>: a critical moment, five minutes or 256
/// loads, a store whose stamp changed, an opening) every row is read and checked again, so a row changed or planted
/// behind the store is found then, and until then it is never served. An entry announced whose row is not there is
/// remembered as missing until then, and raises its alert, unless this process stores its row meanwhile. One reader per
/// project reads the log and builds the ledger and the snapshot (a queue, <see cref="ProjectState"/>), and publishes
/// them whole, never older than the ones before; recalls and writes take what it published. A project closed is
/// forgotten (<see cref="Forget"/>).</item>
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

    /// <summary>The alert of an entry the log announced whose row is not in the store.</summary>
    public const string MissingRowAlert = "memory.entry-missing-row";

    /// <summary>How often a write that waits for a run or a planning asks again whether the project is busy.</summary>
    public static readonly TimeSpan BusyPollInterval = TimeSpan.FromMilliseconds(500);

    private const string Ellipsis = "…";

    /// <summary>Agent Studio announces every row; who wrote its text is its origin.</summary>
    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>What the memory of each open project rests on, kept by the one reader of the project's queue (<see cref="ProjectState"/>).</summary>
    private readonly ConcurrentDictionary<string, ProjectState> _projects = new(StringComparer.Ordinal);

    /// <summary>
    /// Forgets what the memory keeps of <paramref name="projectId"/> (its ledger and its snapshot): the next recall reads
    /// and checks everything again. The composition calls it when the project is closed (<see cref="IProjectClosedSignal"/>).
    /// </summary>
    public void Forget(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_projects.TryRemove(projectId, out var state))
        {
            state.Queue.Writer.TryComplete();
        }
    }

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

        // The store only ranks by the text; the category and the topic are those of the verified entry, never of the row.
        var candidates = !string.IsNullOrWhiteSpace(query.Text)
            ? await store.SearchAsync(project.ProjectId, new MemorySearch(query.Text), ct).ConfigureAwait(false)
            : snapshot.Rows;
        var recalled = new List<MemoryEntry>();
        var characters = 0;
        foreach (var candidate in candidates)
        {
            if (snapshot.Entry(candidate.Id) is not { SupersededBy: null } entry
                || (query.TrustedOnly && entry.Trust != MemoryTrust.Trusted)
                || (query.Kinds is { } kinds && !kinds.Contains(entry.Kind))
                || (query.Topic is { } topic && !SameTopic(entry.Topic, topic)))
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

    /// <summary>Whether two topics are the same as the store compares them (<c>COLLATE NOCASE</c>: only the letters of ASCII fold).</summary>
    private static bool SameTopic(string held, string asked)
    {
        if (held.Length != asked.Length)
        {
            return false;
        }

        for (var i = 0; i < held.Length; i++)
        {
            if (Fold(held[i]) != Fold(asked[i]))
            {
                return false;
            }
        }

        return true;

        static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
    }

    /// <summary>
    /// Every anchored entry of the project with the trust and the supersession the log gives it, newest first: what recall
    /// selects from and what the Markdown export writes. It raises the integrity alerts of the rows left out, unless the
    /// project is busy (then the next recall that finds it idle does). Kept per project and served again while the log only
    /// grows: see the class.
    /// </summary>
    internal async Task<MemorySnapshot> SnapshotAsync(string projectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var loaded = await RequestAsync(projectId, snapshot: true, ct).ConfigureAwait(false);
        var view = loaded.View!;
        if (Unraised(loaded.Ledger, view.Problems).Any())
        {
            await RaiseAsync(projectId, view.Problems, ct).ConfigureAwait(false);
        }

        return view.Snapshot;
    }

    /// <summary>
    /// The problems whose alert the log does not hold yet. A row left out as never announced whose entry the log announces
    /// by now is not one: it was read while it was being recorded, and the next recall checks it again.
    /// </summary>
    private static IEnumerable<MemoryIntegrityProblem> Unraised(MemoryLedger ledger, IEnumerable<MemoryIntegrityProblem> problems) =>
        problems.Where(p => !ledger.HasAlert(p.Message) && !(p.Code == NotAnchoredAlert && ledger.Announcement(p.Entry) is not null));

    /// <summary>
    /// What the memory of <paramref name="projectId"/> rests on now, as the reader of its queue loads it (with the snapshot
    /// when <paramref name="snapshot"/>). The reader goes on whatever the caller does; the caller stops waiting when
    /// <paramref name="ct"/> says so.
    /// </summary>
    private async Task<Loaded> RequestAsync(string projectId, bool snapshot, CancellationToken ct)
    {
        var done = new TaskCompletionSource<Loaded>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(projectId, new Request(snapshot, done));
        return await done.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Puts <paramref name="request"/> in the queue of the project, starting its reader the first time.</summary>
    private void Enqueue(string projectId, Request request)
    {
        while (true)
        {
            var state = _projects.GetOrAdd(projectId, _ => new ProjectState());
            if (state.Queue.Writer.TryWrite(request))
            {
                state.Start(() => ConsumeAsync(projectId, state));
                return;
            }

            // Forgotten meanwhile (the project was closed): a new state takes the request.
            _projects.TryRemove(new KeyValuePair<string, ProjectState>(projectId, state));
        }
    }

    /// <summary>
    /// The one reader of the queue of a project: it takes every request waiting, loads the log once for all of them and
    /// answers them with what it published. Only it reads the log for the memory and builds the ledger and the snapshot, so
    /// what it publishes is never older than what it published before.
    /// </summary>
    private async Task ConsumeAsync(string projectId, ProjectState state)
    {
        var reader = state.Queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var waiting = new List<Request>();
            var snapshot = false;
            while (reader.TryRead(out var request))
            {
                if (request.Landed is { } landed)
                {
                    state.Landed.Add(landed);
                }

                if (request.Done is not null)
                {
                    waiting.Add(request);
                    snapshot |= request.Snapshot;
                }
            }

            if (waiting.Count == 0)
            {
                continue;
            }

            try
            {
                var loaded = await RefreshAsync(projectId, state, snapshot).ConfigureAwait(false);
                foreach (var request in waiting)
                {
                    request.Done!.TrySetResult(loaded);
                }
            }
            catch (Exception ex)
            {
                foreach (var request in waiting)
                {
                    request.Done!.TrySetException(ex);
                }
            }
        }
    }

    /// <summary>
    /// The log of the project, which must verify: memory never trusts, nor extends, one that does not. The ledger is the
    /// one published, extended with what the log grew since (<see cref="MemoryLedger.Extend"/>: the last entry it covers
    /// still has its hash, so by the chain the log up to there is the one it was read from), or read again whole. With
    /// <paramref name="snapshot"/>, the snapshot too: built anew in a whole verification it was not built in, or extended.
    /// Only the reader of the queue calls it.
    /// </summary>
    private async Task<Loaded> RefreshAsync(string projectId, ProjectState state, bool snapshot)
    {
        var current = state.Current;
        var log = await recorder.LoadAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(projectId, log.Verification);
        }

        var entries = log.Entries;
        if (current is not null && log.WholeVerification == current.WholeVerification && entries.Count < current.Count && (!snapshot || current.View is not null))
        {
            // Within one whole verification the log only grows: what was published is never put back by an older load.
            return current;
        }

        var head = entries.Count == 0 ? DecisionLog.GenesisHash : entries[^1].Hash;
        var ledger = current is not null && current.Count == entries.Count && string.Equals(current.Head, head, StringComparison.Ordinal)
            ? current.Ledger
            : current is { Count: > 0 } && current.Count < entries.Count && string.Equals(entries[current.Count - 1].Hash, current.Head, StringComparison.Ordinal)
                ? current.Ledger.Extend(entries, current.Count)
                : MemoryLedger.From(entries);
        var view = current?.View;
        if (snapshot)
        {
            view = view is null || view.WholeVerification != log.WholeVerification
                ? await BuildAsync(projectId, ledger, log.WholeVerification).ConfigureAwait(false)
                : await ExtendAsync(projectId, view, ledger, state.Landed).ConfigureAwait(false);
            state.Landed.Clear();
        }

        var loaded = new Loaded(log.WholeVerification, entries.Count, head, ledger, view);
        state.Current = loaded;
        return loaded;
    }

    /// <summary>Every row of the project read and checked against <paramref name="ledger"/>.</summary>
    private async Task<SnapshotView> BuildAsync(string projectId, MemoryLedger ledger, long whole)
    {
        var rows = await store.SearchAsync(projectId, new MemorySearch(), CancellationToken.None).ConfigureAwait(false);
        var problems = new List<MemoryIntegrityProblem>();
        var intact = new List<MemoryRow>();
        var stored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            stored.Add(row.Id);
            if (ledger.Check(row) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                intact.Add(row);
            }
        }

        var announced = ledger.Announcements;
        var missing = announced.Select(a => a.Payload.Entry).Where(id => !stored.Contains(id)).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(missing.Select(MissingRow));
        return new SnapshotView(whole, ledger, announced.Count, intact, missing, problems, MemorySnapshot.Of(ledger, intact));
    }

    /// <summary>
    /// <paramref name="known"/> with what the log grew since: the rows of the entries it announced since read and checked,
    /// and those of the entries remembered as missing whose row this process stored since (<paramref name="landed"/>); the
    /// others stay missing until the next whole verification. The trust and the supersession of every entry come again
    /// from <paramref name="ledger"/>, which may have changed any of them (a review, the trust of the journal, a history
    /// accepted as a new origin). Within one whole verification the log only grew, so its announcements are those of
    /// <paramref name="known"/> followed by the new ones.
    /// </summary>
    private async Task<SnapshotView> ExtendAsync(string projectId, SnapshotView known, MemoryLedger ledger, IReadOnlySet<string> landed)
    {
        var announced = ledger.Announcements;
        var wanted = new HashSet<string>(known.Missing.Where(landed.Contains), StringComparer.Ordinal);
        for (var i = known.Announced; i < announced.Count; i++)
        {
            wanted.Add(announced[i].Payload.Entry);
        }

        // A row left out as never announced is checked again once the log announces it (it was read in between).
        foreach (var problem in known.Problems.Where(p => p.Code == NotAnchoredAlert && ledger.Announcement(p.Entry) is not null))
        {
            wanted.Add(problem.Entry);
        }

        if (wanted.Count == 0)
        {
            return ReferenceEquals(known.Ledger, ledger) ? known : known with { Ledger = ledger, Announced = announced.Count, Snapshot = MemorySnapshot.Of(ledger, known.Intact) };
        }

        var missing = new HashSet<string>(known.Missing, StringComparer.Ordinal);
        missing.UnionWith(wanted);
        var found = new List<MemoryIntegrityProblem>();
        var added = new List<MemoryRow>();
        foreach (var row in await store.FindAsync(projectId, wanted, CancellationToken.None).ConfigureAwait(false))
        {
            if (!wanted.Contains(row.Id) || !missing.Remove(row.Id))
            {
                continue;
            }

            if (ledger.Check(row) is { } problem)
            {
                found.Add(problem);
            }
            else
            {
                added.Add(row);
            }
        }

        // What was looked for is judged again: its old problems go, its new ones (a row missing still, or not as announced) come.
        var problems = known.Problems.Where(p => !wanted.Contains(p.Entry)).Concat(found).Concat(missing.Where(wanted.Contains).Select(MissingRow)).ToList();
        var intact = added.Count == 0
            ? known.Intact
            : [.. known.Intact.Concat(added).OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)]; // the order of the store
        var snapshot = ReferenceEquals(known.Ledger, ledger) && added.Count == 0 ? known.Snapshot : MemorySnapshot.Of(ledger, intact);
        return new SnapshotView(known.WholeVerification, ledger, announced.Count, intact, missing, problems, snapshot);
    }

    /// <summary>The problem of an entry announced whose row is not in the store.</summary>
    private static MemoryIntegrityProblem MissingRow(string entry) => new(
        MissingRowAlert,
        entry,
        $"Memory entry '{entry}' was announced in the decision log, but its row is not in the store: it is left out of recall, prompts and the export until it is stored and the log is verified whole again.");

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
                Enqueue(projectId, new Request(false, null, existing.Id));
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

        // A recall between the announcement and the row found it missing: the reader looks for it again.
        Enqueue(projectId, new Request(false, null, row.Id));

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

    /// <summary>
    /// The ledger of the log of the project as it is now, which must verify: memory never trusts, nor extends, one that
    /// does not (see <see cref="RefreshAsync"/>). Ledgers never change, so concurrent recalls and writes share them.
    /// </summary>
    private async Task<MemoryLedger> LoadAsync(string projectId, CancellationToken ct) =>
        (await RequestAsync(projectId, snapshot: false, ct).ConfigureAwait(false)).Ledger;

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
    /// Records an alert for each problem the log does not hold yet, when the project is idle; while it is busy, or a memory
    /// write holds the project (an entry is being recorded: its row may be the one found missing), nothing is written, and
    /// the next recall that finds the problem with the project idle raises it. A recall never waits for it. If the log
    /// stopped verifying meanwhile, the next load reports it.
    /// </summary>
    private async Task RaiseAsync(string projectId, IReadOnlyList<MemoryIntegrityProblem> problems, CancellationToken ct)
    {
        if (problems.Count == 0 || activity.IsBusy(projectId))
        {
            return;
        }

        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (activity.IsBusy(projectId))
            {
                return;
            }

            // Read again under the gate: another recall may have raised the same alerts meanwhile.
            var ledger = await LoadAsync(projectId, ct).ConfigureAwait(false);

            // A row found missing while it was being recorded is there by now (the write held this gate): no alert.
            var missing = Unraised(ledger, problems).Where(p => p.Code == MissingRowAlert).Select(p => p.Entry).ToList();
            var stored = missing.Count == 0
                ? []
                : (await store.FindAsync(projectId, missing, ct).ConfigureAwait(false)).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var problem in Unraised(ledger, problems).Where(p => !(p.Code == MissingRowAlert && stored.Contains(p.Entry))))
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

    /// <summary>
    /// What the memory of a project rests on, as the reader of its queue last published it: the whole verification of the
    /// log and how far it covers it (how many entries, the hash of the last one), the ledger and, once a recall asked for it,
    /// the snapshot (built on this ledger, or on an older one of the log when only writes asked since).
    /// </summary>
    private sealed record Loaded(long WholeVerification, int Count, string Head, MemoryLedger Ledger, SnapshotView? View);

    /// <summary>
    /// A request to the reader of a project's queue: what the memory rests on now (with the snapshot when
    /// <paramref name="Snapshot"/>), answered on <paramref name="Done"/>; or, with <paramref name="Landed"/> and no
    /// answer, that this process stored the row of that entry.
    /// </summary>
    private sealed record Request(bool Snapshot, TaskCompletionSource<Loaded>? Done, string? Landed = null);

    /// <summary>The queue of a project, its one reader, and what that reader alone keeps and publishes.</summary>
    private sealed class ProjectState
    {
        private Loaded? _current;
        private int _started;

        public Channel<Request> Queue { get; } = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>What the reader published last; only the reader writes it.</summary>
        public Loaded? Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }

        /// <summary>The entries whose row this process stored since the snapshot was last built or extended; only the reader touches it.</summary>
        public HashSet<string> Landed { get; } = new(StringComparer.Ordinal);

        /// <summary>Starts the reader, once.</summary>
        public void Start(Func<Task> consume)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = Task.Run(consume);
            }
        }
    }

    /// <summary>
    /// What recall selects from, built on <paramref name="Ledger"/> within the whole verification
    /// <paramref name="WholeVerification"/> of the log: the rows found intact (<paramref name="Intact"/>, newest first), how
    /// many announcements of the ledger were looked for (<paramref name="Announced"/>), the announced entries whose row was
    /// not there (<paramref name="Missing"/>), the rows left out with their alert (<paramref name="Problems"/>) and the
    /// snapshot itself.
    /// </summary>
    private sealed record SnapshotView(
        long WholeVerification,
        MemoryLedger Ledger,
        int Announced,
        IReadOnlyList<MemoryRow> Intact,
        IReadOnlySet<string> Missing,
        IReadOnlyList<MemoryIntegrityProblem> Problems,
        MemorySnapshot Snapshot);

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
