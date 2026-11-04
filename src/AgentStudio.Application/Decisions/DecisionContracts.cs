using System.Globalization;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Application.Decisions;

// Phase 3 contract (docs/13 section 3.2).

/// <summary>
/// The decision log of a project as read from storage, and whether its hash chain holds. Orchestration only starts or
/// resumes a run on a log whose <see cref="Verification"/> is valid.
/// </summary>
public sealed record DecisionLogState(IReadOnlyList<DecisionEntry> Entries, DecisionLogVerification Verification)
{
    /// <summary>The hash of the last entry (the genesis hash for an empty log). Verdicts quote it.</summary>
    public string HeadHash => Entries.Count == 0 ? DecisionLog.GenesisHash : Entries[^1].Hash;

    /// <summary>
    /// Set, with <see cref="Verification"/> not valid, when the only thing wrong is that no anchor outside the project
    /// recognises this history at the project's location: a person may accept it as a new origin
    /// (<see cref="IDecisionLogOrigin"/>). Null for any other log, trusted or not.
    /// </summary>
    public UnanchoredHistory? Unanchored { get; init; }
}

/// <summary>
/// A history whose chain holds but that no anchor outside the project recognises at the project's location (ADR-0010,
/// addendum of 2026-10-18): the folder was moved or copied there, the log comes from another machine or with the
/// repository, or (when <paramref name="PreviousProjectId"/> is set and the history is empty) the location's anchor is
/// that of a project whose database is no longer there. Nothing is recorded or run on it until a person accepts it.
/// </summary>
/// <param name="EntryCount">How many entries the history has.</param>
/// <param name="HeadHash">The hash of its last entry (<see cref="DecisionLog.GenesisHash"/> when it is empty).</param>
/// <param name="PreviousProjectId">The project the anchor of this location belongs to; null when the location has no anchor.</param>
/// <param name="PreviousEntryCount">How many entries that anchor recorded; null when the location has no anchor.</param>
public sealed record UnanchoredHistory(long EntryCount, string HeadHash, string? PreviousProjectId = null, long? PreviousEntryCount = null)
{
    /// <summary>The action of the approval that accepts a history as a new origin (<see cref="ApprovalRequest.Action"/>).</summary>
    public const string AcceptAction = "decision-log.accept-origin";

    /// <summary>
    /// What an approval to accept this history of <paramref name="projectId"/> answers (<see cref="ApprovalRequest.Binding"/>):
    /// the project, the head that was shown and the anchor that was found, so it cannot accept a history that changed since.
    /// </summary>
    public string Binding(string projectId) => string.Join(
        '|',
        AcceptAction,
        projectId,
        EntryCount.ToString(CultureInfo.InvariantCulture),
        HeadHash,
        PreviousProjectId ?? "-",
        PreviousEntryCount?.ToString(CultureInfo.InvariantCulture) ?? "-");
}

/// <summary>
/// The anchor kept for a project's location belongs to another project id (ADR-0010, addendum of 2026-10-18): the id in
/// the database was changed, or the folder now holds the database of another project. It is never "no anchor": whoever
/// does not know this exception sees an anchor that cannot be read, which is no trust either.
/// </summary>
public sealed class DecisionLogAnchorMismatchException : IOException
{
    public DecisionLogAnchorMismatchException(string message, string anchoredProjectId, long anchoredEntryCount)
        : base(message)
    {
        AnchoredProjectId = anchoredProjectId;
        AnchoredEntryCount = anchoredEntryCount;
    }

    public DecisionLogAnchorMismatchException()
    {
    }

    public DecisionLogAnchorMismatchException(string message)
        : base(message)
    {
    }

    public DecisionLogAnchorMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The project the anchor of the location belongs to.</summary>
    public string? AnchoredProjectId { get; }

    /// <summary>How many entries that anchor recorded.</summary>
    public long? AnchoredEntryCount { get; }
}

/// <summary>
/// Accepts, on a person's word, a history no anchor recognises at the project's location as the new origin of the
/// project's log (ADR-0010, addendum of 2026-10-18). The writer of the log does it, since it records the acceptance.
/// </summary>
public interface IDecisionLogOrigin
{
    /// <summary>
    /// Records, in this order, an <c>AuditAlertRaised</c> that says what was accepted and why, the
    /// <paramref name="request"/> as <c>ApprovalRequested</c> and the person's <paramref name="decision"/> as
    /// <c>ApprovalGranted</c>, and then anchors the log at its new head; returns the log as it is then.
    /// </summary>
    /// <param name="request">The question the person was asked: <see cref="UnanchoredHistory.AcceptAction"/>, bound to the history shown.</param>
    /// <param name="decision">The person's approval, which answers that binding.</param>
    /// <exception cref="InvalidOperationException">
    /// The log is not a history no anchor recognises (it is trusted, or it does not verify for another reason), it changed
    /// since it was shown, or the decision is not a person's approval of it. Nothing is written.
    /// </exception>
    Task<DecisionLogState> AcceptAsNewOriginAsync(string projectId, ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken);
}

/// <summary>The decision log of a project does not verify, so nothing more is written to it.</summary>
public sealed class DecisionLogIntegrityException : InvalidOperationException
{
    public DecisionLogIntegrityException(string projectId, DecisionLogVerification verification)
        : base($"The decision log of project '{projectId}' is not intact (entry {verification.FirstInvalidSequence}): {verification.Problem}")
    {
        Verification = verification;
    }

    public DecisionLogIntegrityException()
    {
        Verification = new DecisionLogVerification(false, null, null);
    }

    public DecisionLogIntegrityException(string message)
        : base(message)
    {
        Verification = new DecisionLogVerification(false, null, null);
    }

    public DecisionLogIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
        Verification = new DecisionLogVerification(false, null, null);
    }

    public DecisionLogVerification Verification { get; }
}

/// <summary>
/// Where the head of each project's decision log is kept outside the project (ADR-0010, addendum). The chain of hashes
/// shows any edit in the middle of the log, but not a log cut short or rewritten from some entry on with recomputed
/// hashes: compared with the anchor, both are evident.
/// </summary>
public interface IDecisionLogAnchorStore
{
    /// <summary>The anchor of the project, or null when none was written yet. Reading it changes nothing.</summary>
    /// <exception cref="InvalidDataException">There is an anchor, but it cannot be read as the anchor of this project.</exception>
    /// <exception cref="DecisionLogAnchorMismatchException">The anchor kept for the project's location is that of another project.</exception>
    Task<DecisionLogAnchor?> ReadAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>Replaces the anchor of the project atomically: a reader finds the old one or the new one, never a mix.</summary>
    Task WriteAsync(string projectId, DecisionLogAnchor anchor, CancellationToken cancellationToken);

    /// <summary>
    /// Says that the log of the project verified against <paramref name="anchor"/>, the anchor <see cref="ReadAsync"/>
    /// returned (ADR-0010, addendum P-04). Only then may a store move an anchor it keeps under an old name to the
    /// project's location, so a copy of the project whose log does not verify against that anchor never takes it. The
    /// default does nothing: a store with no old names has nothing to move.
    /// </summary>
    /// <exception cref="IOException">The anchor could not be kept at the project's location; nothing was moved.</exception>
    Task ConfirmAsync(string projectId, DecisionLogAnchor anchor, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>How much of the log a load reads from storage and verifies (ADR-0010, addendum of 2026-11-04).</summary>
public enum LogLoad
{
    /// <summary>
    /// What was appended since the writer last verified the log, chained to it, when the store can tell nothing else
    /// changed; otherwise, and at least every 5 minutes and every 256 loads, the whole log. For everything that is not a
    /// critical moment: a run or a debate going on, recording and recalling memory, listing.
    /// </summary>
    Incremental,

    /// <summary>
    /// The whole log, read from storage and verified from its first entry, whatever was verified before. For the critical
    /// moments, where a decision or an agent rests on the log, and everything a judge sees: a delivery, the dossier of a
    /// judge and an independent review, the start of a run and its resumes, a planning, the dossier, the verdict and the
    /// close of a debate, a project audit (its proposal, its dossier and its close) and a challenge (its preparation, its
    /// <c>VerdictChallenged</c> and the acceptance of its revision).
    /// </summary>
    Full,
}

/// <summary>
/// The only writer of a project's decision log. It keeps the chain consistent (sequence and time come from the domain
/// object, never from the database) and serializes the payloads in one fixed format, because their text is part of the hash.
/// </summary>
public interface IDecisionRecorder
{
    /// <summary>
    /// Reads the log from storage and verifies its hash chain and its anchor (<see cref="LogLoad.Incremental"/>). A broken
    /// chain is returned, not thrown.
    /// </summary>
    Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the log from storage and verifies it as <paramref name="load"/> says: <see cref="LogLoad.Full"/> at the critical
    /// moments, so a change no incremental check can see is caught there. A broken chain is returned, not thrown.
    /// </summary>
    Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one entry. Throws <see cref="DecisionLogIntegrityException"/> if the stored log does not verify. The entry
    /// is stamped with the later of "now" and the last entry, so a clock that goes back never breaks the chronology.
    /// </summary>
    Task<DecisionEntry> RecordAsync(
        string projectId,
        Actor actor,
        DecisionKind kind,
        string summary,
        DecisionPayload payload,
        long? corrects = null,
        CancellationToken cancellationToken = default);
}
