using System.Collections.Concurrent;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Security;

namespace AgentStudio.Application.Decisions;

/// <summary>
/// The single writer of each project's decision log. It keeps the log of a project in memory, loaded from the store and
/// verified, and a lock per project, so entries get contiguous sequence numbers and a valid chain whatever the number
/// of concurrent callers. Projects do not wait for each other.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The sequence number and the hash come from <see cref="DecisionLog"/>, never from the database.</item>
/// <item>The time of an entry is the later of "now" and the last entry, because the log refuses a chronology that goes back.</item>
/// <item>If the store fails, what the recorder believes may be ahead of (or, after a lost reply, behind) the store. It
/// forgets the in-memory log and reads it again from the store on the next call, so the failed entry's sequence is reused
/// if it was not stored and continued after if it was.</item>
/// <item>A stored log that does not verify is never extended.</item>
/// <item>With an <see cref="IDecisionLogAnchorStore"/> (ADR-0010, addendum), the head of the log is also kept outside the
/// project and every load is checked against it, so a log cut short, rewritten with recomputed hashes or extended by
/// someone else no longer verifies, nor does one whose anchor cannot be read. Each entry is announced in the anchor
/// before it is stored (<see cref="DecisionLogAnchor.NextHash"/>), and the anchor moves to it once it is stored: an entry
/// that reached the store just before the process died is still recognised, and one whose announcement cannot be
/// written is not stored at all. Without an anchor store only the chain is checked.</item>
/// <item>The anchor belongs to the project's location (ADR-0010, addendum of 2026-10-18). A log with entries and no anchor
/// for its location (the folder was moved or copied, or the log written elsewhere) is not trusted either:
/// <see cref="DecisionLogState.Unanchored"/> says so, and only a person can accept it as a new origin
/// (<see cref="AcceptAsNewOriginAsync"/>). An empty one is a new project, which its first entry anchors. An anchor of the
/// location that belongs to another project id is no trust: it is accepted only when the log is empty (nobody's entries
/// to accept: the history that lived there is gone).</item>
/// <item>The store is told of every anchor the log verified against (<see cref="IDecisionLogAnchorStore.ConfirmAsync"/>),
/// and only then may it move one kept under an old name to the project's location (ADR-0010, addendum P-04): a copy whose
/// log does not verify never takes the anchor of the original. If it cannot be moved, the log is not trusted on that load.</item>
/// <item><b>Incremental.</b> Every load reads the store, but with a store that reads its tail (<see cref="IDecisionLogTailReader"/>)
/// only what was appended since this writer last verified the log is read and verified, chained to the head it verified:
/// in the same read the store says that the entries up to there are still the ones verified (as many, the last with the
/// same hash) and that nothing else about it changed (its stamp: the opening of the project, the schema of the database,
/// its file). If anything does not match, the whole log is read and verified, as with a store that cannot read its tail,
/// and the anchor is checked on every load either way. So recording and recalling memory, which load the log each time,
/// cost what the log grew, not what it holds. The verified log is this process's and per project, kept by the single
/// writer of the project (the gate), like the log it appends to, and forgotten when the project is closed
/// (<see cref="Forget"/>).</item>
/// <item><b>What the tail cannot see.</b> A change in place that leaves the stamp as it was (bytes written into the file or
/// through SQLite's blob interface, an entry changed with SQL and the schema version forged back, the file replaced by a
/// crafted copy, or a row replaced through its rowid with <c>INSERT OR REPLACE</c> by a connection without recursive
/// triggers) is not seen by a load from the tail. It is seen by the next whole verification: every load at the
/// critical moments asks for one (<see cref="LogLoad.Full"/>: a delivery, what a judge sees, the start of a run, a
/// planning, the close of a debate, an audit, a challenge), and an incremental load makes one when the last was at least
/// <see cref="IncrementalAgeBeforeFullVerification"/> ago or after <see cref="IncrementalLoadsBeforeFullVerification"/>
/// loads from the tail, and at the next opening of the project. Meanwhile nobody in this process is given the changed
/// entries: what was verified is served from memory.</item>
/// </list>
/// </remarks>
public sealed class DecisionRecorder(IDecisionLogStore store, TimeProvider timeProvider, IDecisionLogAnchorStore? anchors = null) : IDecisionRecorder, IDecisionLogOrigin
{
    /// <summary>The code of the alert that records a history accepted as a new origin.</summary>
    public const string OriginAcceptedAlert = "decision-log.origin-accepted";

    /// <summary>How many loads verify only the tail of the log before one verifies the whole log again.</summary>
    public const int IncrementalLoadsBeforeFullVerification = 256;

    /// <summary>How long after a whole verification loads may still verify only the tail of the log.</summary>
    public static readonly TimeSpan IncrementalAgeBeforeFullVerification = TimeSpan.FromMinutes(5);

    /// <summary>Who signs the alert and the question of an acceptance: the system, as the approval gate does.</summary>
    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private readonly ConcurrentDictionary<string, ProjectWriter> _writers = new(StringComparer.Ordinal);

    /// <summary>The last value given to a whole verification (<see cref="DecisionLogState.WholeVerification"/>), for every project of this recorder.</summary>
    private long _wholeVerifications;

    public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) =>
        LoadAsync(projectId, LogLoad.Incremental, cancellationToken);

    public async Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!Enum.IsDefined(load))
        {
            throw new ArgumentOutOfRangeException(nameof(load), load, "Unknown kind of load of the decision log.");
        }

        var writer = _writers.GetOrAdd(projectId, _ => new ProjectWriter());
        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Always from the store: this is the check made when a project is opened, and it also catches changes made
            // behind our back. Only what is new since the last verification is verified again, unless the load is a full
            // one or the last full one is too old (ReadVerifiedAsync).
            var (log, verification, unanchored) = await ReadVerifiedAsync(projectId, writer, load, cancellationToken).ConfigureAwait(false);
            return new DecisionLogState(log.Entries, verification) { Unanchored = unanchored, WholeVerification = writer.WholeVerification };
        }
        finally
        {
            writer.Gate.Release();
        }
    }

    /// <summary>
    /// Accepts the history the person was shown as the new origin of the log. The entries go first and the anchor last,
    /// with no announcement: if the process dies in between, the history is still without an anchor and the person is
    /// asked again, so no history is ever anchored without its acceptance in the log.
    /// </summary>
    public async Task<DecisionLogState> AcceptAsNewOriginAsync(string projectId, ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        if (anchors is null)
        {
            throw new InvalidOperationException("There is no anchor store: every log is checked by its chain alone, so no history waits to be accepted as a new origin.");
        }

        if (!string.Equals(request.Action, UnanchoredHistory.AcceptAction, StringComparison.Ordinal) || request.Binding is null)
        {
            throw new InvalidOperationException($"The request is not one to accept a history as a new origin ({UnanchoredHistory.AcceptAction}, bound to the history shown).");
        }

        if (!decision.Approved || string.IsNullOrWhiteSpace(decision.DecidedBy) || !string.Equals(decision.Answers, request.Binding, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the approval a person gave to this very history accepts it as a new origin, and this decision is not that: nothing was written.");
        }

        // The fixed barrier of Windows Hello (docs/17 D-37, ADR-0053): accepting a history changes what is trusted, so the
        // approval must have confirmed the person. A backup of the desktop's check, by the one rule of the domain, with the
        // floor no setting lowers.
        if (!PresencePolicy.IsSatisfiedBy(PresenceAction.AcceptHistoryAsOrigin, importantApprovalsSetting: false, helloAvailable: false, PresenceProofs.Claimed(decision.Verification)))
        {
            throw new InvalidOperationException(
                $"Accepting a history as a new origin always asks for Windows Hello (docs/17 D-37), and this approval was given with '{decision.Verification}': nothing was written.");
        }

        var writer = _writers.GetOrAdd(projectId, _ => new ProjectWriter());
        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var log = await ReadLogAsync(projectId, cancellationToken).ConfigureAwait(false);
            var (verification, unanchored) = await VerifyAsync(projectId, log, log.Verify(), cancellationToken).ConfigureAwait(false);
            if (unanchored is null)
            {
                throw new InvalidOperationException(verification.IsValid
                    ? $"The decision log of project '{projectId}' is anchored at its location: there is nothing to accept."
                    : $"The decision log of project '{projectId}' cannot be accepted as a new origin: {verification.Problem}");
            }

            if (!string.Equals(unanchored.Binding(projectId), request.Binding, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The decision log of project '{projectId}' changed since it was shown to the person: nothing was accepted, it has to be asked again.");
            }

            var now = timeProvider.GetUtcNow();
            var at = log.Entries is [.., var last] && now < last.RecordedAt ? last.RecordedAt : now;
            var run = request.RunId ?? string.Empty;
            try
            {
                await store.AppendAsync(projectId, log.Append(
                    at,
                    SystemActor,
                    DecisionKind.AuditAlertRaised,
                    "Historial aceptado como nuevo origen",
                    DecisionPayloadSerializer.Serialize(new AuditAlertPayload(OriginAcceptedAlert, [], AcceptanceMessage(projectId, unanchored, decision.DecidedBy)) { RunId = run })), cancellationToken).ConfigureAwait(false);
                await store.AppendAsync(projectId, log.Append(
                    at,
                    SystemActor,
                    DecisionKind.ApprovalRequested,
                    $"Aprobación pedida ({request.Risk}): {request.Action}",
                    DecisionPayloadSerializer.Serialize(new ApprovalRequestedPayload(request.Action, request.Description, request.Risk, null, request.AffectedPaths ?? []) { RunId = run })), cancellationToken).ConfigureAwait(false);
                var granted = log.Append(
                    at,
                    new Actor(ActorKind.User, decision.DecidedBy),
                    DecisionKind.ApprovalGranted,
                    $"Aprobado por {decision.DecidedBy}: {request.Action}",
                    DecisionPayloadSerializer.Serialize(ApprovalAnswers.Payload(request.Action, true, decision.DecidedBy, decision.Comment, null, decision.Verification, run)));
                await store.AppendAsync(projectId, granted, cancellationToken).ConfigureAwait(false);

                var anchor = new DecisionLogAnchor(granted.Sequence, granted.Hash);
                await anchors.WriteAsync(projectId, anchor, cancellationToken).ConfigureAwait(false);
                writer.Adopt(log, NextWholeVerification());
                return new DecisionLogState(log.Entries, log.Verify(anchor)) { WholeVerification = writer.WholeVerification };
            }
            catch
            {
                writer.Adopt(null, 0);
                throw;
            }
        }
        finally
        {
            writer.Gate.Release();
        }
    }

    public async Task<DecisionEntry> RecordAsync(
        string projectId,
        Actor actor,
        DecisionKind kind,
        string summary,
        DecisionPayload payload,
        long? corrects = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(payload);
        var payloadJson = DecisionPayloadSerializer.Serialize(payload);

        var writer = _writers.GetOrAdd(projectId, _ => new ProjectWriter());
        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (writer.Log is null)
            {
                var (_, verification, _) = await ReadVerifiedAsync(projectId, writer, LogLoad.Incremental, cancellationToken).ConfigureAwait(false);
                if (!verification.IsValid)
                {
                    throw new DecisionLogIntegrityException(projectId, verification);
                }
            }

            var log = writer.Log!;
            var now = timeProvider.GetUtcNow();
            var at = writer.LastRecordedAt is { } last && now < last ? last : now;

            // Append validates before it changes anything, so a refused entry (a bad correction) leaves the log as it was.
            var entry = log.Append(at, actor, kind, summary, payloadJson, corrects);
            try
            {
                if (anchors is not null)
                {
                    // Announced first: if the process dies once the entry is stored, the anchor still recognises it.
                    await anchors.WriteAsync(projectId, new DecisionLogAnchor(entry.Sequence - 1, entry.PreviousHash, entry.Hash), cancellationToken).ConfigureAwait(false);
                }

                await store.AppendAsync(projectId, entry, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                writer.Adopt(null, 0);
                throw;
            }

            writer.LastRecordedAt = entry.RecordedAt;
            await SettleAnchorAsync(projectId, entry).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            writer.Gate.Release();
        }
    }

    /// <summary>A value no whole verification of this recorder had before: the logs of a project closed and opened again never share one.</summary>
    private long NextWholeVerification() => Interlocked.Increment(ref _wholeVerifications);

    private async Task<DecisionLog> ReadLogAsync(string projectId, CancellationToken cancellationToken) =>
        DecisionLog.Load(await store.ReadAllAsync(projectId, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Forgets what the writer of <paramref name="projectId"/> verified and holds: its next load reads and verifies the whole
    /// log. The composition calls it when the project is closed, so a closed project keeps nothing in memory.
    /// </summary>
    public void Forget(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        _writers.TryRemove(projectId, out _);
    }

    /// <summary>
    /// The log as the store holds it now, verified against its anchor, which the writer adopts when it verifies (and
    /// forgets when it does not). With a store that reads its tail, a log the writer verified before, an incremental load
    /// and a whole verification recent enough, only the entries after that log are read and verified; anything else reads
    /// and verifies the whole log (see the remarks of the class).
    /// </summary>
    private async Task<(DecisionLog Log, DecisionLogVerification Verification, UnanchoredHistory? Unanchored)> ReadVerifiedAsync(
        string projectId,
        ProjectWriter writer,
        LogLoad load,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        DecisionLog log;
        DecisionLogVerification chain;
        string? stamp = null;
        var incremental = false;
        if (store is IDecisionLogTailReader tails)
        {
            var recent = writer.VerifiedWholeAt is { } whole && now - whole < IncrementalAgeBeforeFullVerification;
            var tail = load == LogLoad.Incremental && recent && writer is { Log: { } verified, Stamp: { } known, IncrementalLoads: < IncrementalLoadsBeforeFullVerification }
                ? await tails.ReadTailAsync(projectId, new DecisionLogMark(verified.Count, verified.HeadHash, known), cancellationToken).ConfigureAwait(false)
                : null;
            if (tail is { Continues: true } && writer.Log is { } prefix)
            {
                (log, chain) = prefix.Extend(tail.Entries);
                stamp = tail.Stamp;
                incremental = true;
            }
            else
            {
                var all = await tails.ReadTailAsync(projectId, null, cancellationToken).ConfigureAwait(false);
                log = DecisionLog.Load(all.Entries);
                chain = log.Verify();
                stamp = all.Stamp;
            }
        }
        else
        {
            log = await ReadLogAsync(projectId, cancellationToken).ConfigureAwait(false);
            chain = log.Verify();
        }

        var (verification, unanchored) = await VerifyAsync(projectId, log, chain, cancellationToken).ConfigureAwait(false);
        writer.Adopt(verification.IsValid ? log : null, incremental ? writer.WholeVerification : NextWholeVerification(), stamp, incremental, now);
        return (log, verification, unanchored);
    }

    /// <summary>
    /// The chain (<paramref name="chain"/>, already checked), and the log against the anchor of its location when there is
    /// one: a log that verifies against it is confirmed to the store. An anchor that cannot be read or confirmed, or that is
    /// another project's, is no trust. With no anchor for the location, an empty log is a new project and a log with
    /// entries is not trusted, which only a person can change. The second value is set when a person may accept the log as
    /// a new origin: its chain holds and no anchor recognises it here.
    /// </summary>
    private async Task<(DecisionLogVerification Verification, UnanchoredHistory? Unanchored)> VerifyAsync(string projectId, DecisionLog log, DecisionLogVerification chain, CancellationToken cancellationToken)
    {
        if (anchors is null)
        {
            return (chain, null);
        }

        DecisionLogAnchor? anchor;
        try
        {
            anchor = await anchors.ReadAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (DecisionLogAnchorMismatchException ex)
        {
            if (!chain.IsValid)
            {
                return (chain, null);
            }

            // The detected attack when the log has entries (its id was rewritten); when it has none, there is nobody's
            // history to accept: what lived here is gone, and a person may start a new one.
            var empty = log.Count == 0;
            var problem = $"{ex.Message} The log of this database can only be trusted against the anchor of its location."
                + (empty ? " It is empty: only a person can accept starting a new history here." : string.Empty);
            return (new DecisionLogVerification(false, null, problem), empty && ex.AnchoredProjectId is { } previous ? new UnanchoredHistory(0, DecisionLog.GenesisHash, previous, ex.AnchoredEntryCount) : null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return chain.IsValid
                ? (new DecisionLogVerification(false, null, $"The anchor of the decision log outside the project cannot be read, so the log cannot be trusted: {ex.Message}"), null)
                : (chain, null);
        }

        if (anchor is not null)
        {
            var verification = chain.IsValid ? log.VerifyHead(anchor) : chain;
            if (verification.IsValid)
            {
                try
                {
                    // Only a log that verified against the anchor lets the store move it to the location (P-04).
                    await anchors.ConfirmAsync(projectId, anchor, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return (new DecisionLogVerification(false, null, $"The anchor of the decision log outside the project cannot be kept at the project's location, so the log cannot be trusted: {ex.Message}"), null);
                }
            }

            return (verification, null);
        }

        var count = log.Count;
        if (!chain.IsValid || count == 0)
        {
            return (chain, null);
        }

        return (
            new DecisionLogVerification(
                false,
                null,
                $"The decision log has {count} entries, but there is no anchor outside the project for its location: the project was moved or copied there, or its log was written elsewhere. Only a person can accept it as the new origin of the log."),
            new UnanchoredHistory(count, log.HeadHash));
    }

    /// <summary>What the alert of an acceptance says: what was accepted, why it had no anchor, and who accepted it.</summary>
    private static string AcceptanceMessage(string projectId, UnanchoredHistory history, string decidedBy) =>
        history.PreviousProjectId is { } previous
            ? $"The anchor of this location belonged to project '{previous}' ({history.PreviousEntryCount} entries), and the log of project '{projectId}' was empty: that history is no longer here. {decidedBy} accepted starting a new history as the origin of the log."
            : $"The decision log of project '{projectId}' had {history.EntryCount} entries, head {history.HeadHash}, and no anchor outside the project for its location. {decidedBy} accepted it as the new origin of the log.";

    /// <summary>
    /// Moves the anchor to the entry just stored. If that fails the entry stays recognised by its announcement, and the
    /// next append writes the anchor again (or fails before storing anything), so the failure is not the caller's: the
    /// entry is stored.
    /// </summary>
    private async Task SettleAnchorAsync(string projectId, DecisionEntry entry)
    {
        if (anchors is null)
        {
            return;
        }

        try
        {
            await anchors.WriteAsync(projectId, new DecisionLogAnchor(entry.Sequence, entry.Hash), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The announcement written before the entry still covers it (see above).
        }
    }

    private sealed class ProjectWriter
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>The log as this writer believes it is stored, verified; null when it must be read again.</summary>
        public DecisionLog? Log { get; private set; }

        /// <summary>The stamp of the store <see cref="Log"/> was read and verified with (<see cref="IDecisionLogTailReader"/>); null when it has none.</summary>
        public string? Stamp { get; private set; }

        /// <summary>How many loads verified only the tail since the whole log was last verified.</summary>
        public int IncrementalLoads { get; private set; }

        /// <summary>When <see cref="Log"/> was last read and verified whole; null when it was not (or there is no log).</summary>
        public DateTimeOffset? VerifiedWholeAt { get; private set; }

        /// <summary>The whole verification <see cref="Log"/> rests on (<see cref="DecisionLogState.WholeVerification"/>); zero without a log.</summary>
        public long WholeVerification { get; private set; }

        public DateTimeOffset? LastRecordedAt { get; set; }

        /// <param name="wholeVerification">The whole verification the log rests on: a new one unless the load was incremental.</param>
        /// <param name="incremental">Whether this load verified only the tail; a whole verification starts the count again.</param>
        /// <param name="at">When the log was read; for a whole verification, the time the next ones are counted from.</param>
        public void Adopt(DecisionLog? log, long wholeVerification, string? stamp = null, bool incremental = false, DateTimeOffset? at = null)
        {
            Log = log;
            WholeVerification = log is null ? 0 : wholeVerification;
            Stamp = log is null ? null : stamp;
            IncrementalLoads = log is not null && incremental ? IncrementalLoads + 1 : 0;
            VerifiedWholeAt = log is null ? null : incremental ? VerifiedWholeAt : at;
            LastRecordedAt = log is { Entries: [.., var last] } ? last.RecordedAt : null;
        }
    }
}
