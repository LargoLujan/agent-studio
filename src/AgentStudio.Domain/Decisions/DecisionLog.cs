using System.Globalization;
using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Decisions;

public enum ActorKind
{
    User,
    Orchestrator,
    Agent,
    Judge,
    Auditor,
    System,
}

public sealed record Actor(ActorKind Kind, string Id);

public enum DecisionKind
{
    PlanCreated,
    PlanRevised,
    TaskCreated,
    TaskAssigned,
    TaskStatusChanged,
    InstructionsIssued,
    ReviewRequested,
    ReviewSkipped,
    ApprovalRequested,
    ApprovalGranted,
    ApprovalDenied,
    PermissionDenied,
    EvidenceRegistered,
    IntegrationPerformed,
    VerdictIssued,
    AuditAlertRaised,
    ReplanRequested,

    /// <summary>Supersedes an earlier entry. The earlier entry stays in the log: corrections never erase.</summary>
    Correction,

    // Phase 3 (orchestration). Appended, never inserted: the hash covers kind.ToString(), so the names of the kinds
    // above must not change either.

    /// <summary>An orchestration run starts: hashes of plan and requirements, base commit, profile and roster.</summary>
    RunStarted,

    /// <summary>An orchestration run reaches a final phase.</summary>
    RunFinished,

    /// <summary>The system is about to launch an agent for one attempt (recorded before the launch).</summary>
    AgentRunStarted,

    /// <summary>An agent attempt ended (any outcome, including an interrupted one).</summary>
    AgentRunFinished,

    /// <summary>A condition node was evaluated by the system and a branch was chosen.</summary>
    ConditionEvaluated,

    /// <summary>A task worktree was created or adopted.</summary>
    WorktreeCreated,

    /// <summary>A task worktree was removed.</summary>
    WorktreeRemoved,

    // Phase 4 (project memory, docs/14 D-19). Appended after the Phase 3 kinds, so no stored hash changes. Both belong to
    // no run: their payloads carry an empty run id, so replay and the judge's dossier never select them.

    /// <summary>A memory entry is announced (category, origin, canonical SHA-256, source) before its row is stored.</summary>
    MemoryRecorded,

    /// <summary>A person accepted, rejected or retracted a memory entry.</summary>
    MemoryReviewed,

    // Phase 5 (debates, audits and challenges, docs/15 D-03). Appended after the Phase 4 kinds, so no stored hash changes.
    // A debate (deb-…) and a whole-project audit (aud-…) are units of the log like a run: their payloads carry their id
    // as the run id. ReplanRequested, above, gains its payload in this phase and keeps its place.

    /// <summary>A debate opens: its mode, the hash of its immutable header and of the proposal the user approved, its participants, judges and limits.</summary>
    DebateOpened,

    /// <summary>A contribution of a participant is published when its round closes, by reference to its evidence.</summary>
    DebateContributionRecorded,

    /// <summary>A round of a debate closes: the next phase, the tokens used with their origin and, when it goes to judgment, why.</summary>
    DebateRoundClosed,

    /// <summary>A debate ends: its outcome, its verdicts and the rule that combined them (one judge, a panel, a person, aborted).</summary>
    DebateClosed,

    /// <summary>A person challenges a stored verdict: the reasons, by reference, and the revision debate it opens.</summary>
    VerdictChallenged,

    /// <summary>An audit of the whole project starts: its anchored header (commit, requirements, profile, judge, producers and declared authors).</summary>
    ProjectAuditStarted,

    /// <summary>An audit of the whole project ends: its verdicts, disposition and whether the report can be trusted.</summary>
    ProjectAuditFinished,

    // Phase 8 (unattended work, docs/16 D-16, D-38 and D-39). Appended after the Phase 5 kinds, so no stored hash changes.

    /// <summary>
    /// The app was left with the run going: what the person chose (pause and exit when the attempts going end, or exit now)
    /// or that Windows ended the session. It decides whether the run may resume by itself afterwards.
    /// </summary>
    RunExitChosen,

    /// <summary>A person paused or resumed the dispatch of the run. A run left paused never resumes by itself after a restart.</summary>
    RunDispatchChanged,

    /// <summary>The check before going on after a wait for a limit: its allowed source (a session probe, or the read of the Codex quota), its result and its time.</summary>
    AccessChecked,

    /// <summary>
    /// The run was resumed after a restart, before anything is launched: because a person asked, or by the consent of its
    /// header and why (it was waiting for quota, a close nobody asked for cut it, or Windows ended the session).
    /// </summary>
    RunResumed,

    // Phase 12 (the semantic index, docs/17 D-02). Appended after the Phase 8 kinds, so no stored hash changes. It belongs
    // to no run: its payload carries an empty run id, like the entries of the memory.

    /// <summary>
    /// A person trusted the journal of the project, or of one day of it, or withdrew that trust: the journal entries Agent
    /// Studio did not write leave quarantine, or go back to it.
    /// </summary>
    JournalTrustChanged,

    // Phase 13 (the visual builder, docs/17 D-29 and D-27). Appended after the Phase 12 kind, so no stored hash changes.

    /// <summary>
    /// An agent asked the person a question with options in its report (D-28 and D-29): the task, the attempt, the evidence of
    /// the whole question with its SHA-256, the ids of its options, the one the agent recommends and what the system estimates
    /// each would cost. No text of the agent goes in the payload: the question is evidence.
    /// </summary>
    QuestionAsked,

    /// <summary>The person answered a question of an agent (D-29): the option chosen, or the evidence of their own text, who and how.</summary>
    QuestionAnswered,

    /// <summary>
    /// The configurator of a project applied what the person reviewed (D-27): the hashes of everything it wrote through the
    /// usual gates (the template, the brief, the verification profile, the skills and the options). It belongs to no run.
    /// </summary>
    ProjectSetupApplied,

    // Phase 9 (work and traceability, docs/16 D-17, D-18 and D-29). Appended after the Phase 13 kinds, so no stored hash changes.
    // A milestone (mil-…) is a unit of the log like a debate: its payloads carry its id as the run id, so the entries of the
    // run it is linked to and its own never mix. The entries of the backlog and the trace links span runs: their payloads
    // carry an empty run id, like those of the memory, so replay and the dossier of the judge never select them.

    /// <summary>
    /// A milestone opens (D-17): the canonical hash of its immutable header in <c>milestones</c> (name, objective, first set of
    /// requirements and, when there is one, its budget), who opened it. The objective is the person's text: the header holds it.
    /// </summary>
    MilestoneOpened,

    /// <summary>The requirements of a milestone are revised (D-17): the hash of the new frozen set and of the one it replaces. The earlier set stays in its history.</summary>
    MilestoneRequirementsRevised,

    /// <summary>A run (with its follow-ups) or a debate is linked to a milestone (D-17 and D-29): by the system when it is launched from the milestone, or by a person.</summary>
    MilestoneLinked,

    /// <summary>A milestone is closed by auditing it (D-49): the audit, the evidence of the deterministic report with its SHA-256 and the commit audited.</summary>
    MilestoneClosed,

    /// <summary>A closed milestone is reopened (D-50): why (a revert, a check that fails, a new challenge or a person) and its evidence. The report it had stays.</summary>
    MilestoneReopened,

    /// <summary>
    /// An item of the backlog is announced (D-18), as <c>MemoryRecorded</c> announces a memory entry: its id, its version, its
    /// priority and the canonical SHA-256 of its content, before its row is stored. A new version is the item changed.
    /// </summary>
    BacklogItemRecorded,

    /// <summary>A person retired an item of the backlog (D-18): the item and the last version it had. Its rows stay.</summary>
    BacklogItemRetired,

    /// <summary>A person changed the order of the backlog (D-18): the ids of the items in their new order.</summary>
    BacklogOrderChanged,

    /// <summary>
    /// An explicit link between two nodes of the trace is recorded (D-29): a requirement, an item of the backlog, a milestone,
    /// a run, a task, an attempt, a commit, evidence, a verdict, a debate, an audit or a document; who asserts it and why. A
    /// link is never edited.
    /// </summary>
    TraceLinkRecorded,

    /// <summary>A link of the trace is withdrawn (D-29): who and why. The link stays in the log; recording another is how it changes.</summary>
    TraceLinkRetracted,
}

public sealed record DecisionEntry(
    long Sequence,
    DateTimeOffset RecordedAt,
    Actor Actor,
    DecisionKind Kind,
    string Summary,
    string PayloadJson,
    long? Corrects,
    string PreviousHash,
    string Hash);

public sealed record DecisionLogVerification(bool IsValid, long? FirstInvalidSequence, string? Problem)
{
    public static readonly DecisionLogVerification Valid = new(true, null, null);
}

/// <summary>
/// The head of a decision log as its writer recorded it outside the project (ADR-0010, addendum): how many entries the
/// log had and the hash of the last one (<see cref="DecisionLog.GenesisHash"/> for an empty log). The writer records
/// <see cref="NextHash"/>, the hash of the entry it is about to append, before it stores that entry, so an entry that
/// reached the store just before the process died is still recognised as its own.
/// </summary>
public sealed record DecisionLogAnchor(long EntryCount, string HeadHash, string? NextHash = null);

/// <summary>
/// Append-only, hash-chained log of every decision taken by the orchestrator, agents, judges and users
/// (master prompt, section 9). Each entry hashes the previous one, so editing or deleting any past entry breaks
/// <see cref="Verify()"/>. All members are safe to call concurrently (one lock). This makes tampering evident, not
/// impossible, and the chain alone cannot see a log cut short or rewritten with recomputed hashes: the head is also
/// anchored outside the project (<see cref="Verify(DecisionLogAnchor?)"/>), and every verdict records the head it examined.
/// </summary>
public sealed class DecisionLog
{
    public static readonly string GenesisHash = new('0', ContentHash.HexLength);

    private readonly object _gate = new();
    private readonly List<DecisionEntry> _entries;

    public DecisionLog()
    {
        _entries = [];
    }

    private DecisionLog(List<DecisionEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>A snapshot of the entries at the time of the call; later appends do not alter it.</summary>
    public IReadOnlyList<DecisionEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public string HeadHash
    {
        get
        {
            lock (_gate)
            {
                return HeadHashUnsafe();
            }
        }
    }

    /// <summary>How many entries the log has, without copying them (<see cref="Entries"/> does).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Rebuilds a log read from storage. Call <see cref="Verify"/> before trusting it.</summary>
    public static DecisionLog Load(IEnumerable<DecisionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new DecisionLog(entries.OrderBy(e => e.Sequence).ToList());
    }

    public DecisionEntry Append(DateTimeOffset at, Actor actor, DecisionKind kind, string summary, string payloadJson = "{}", long? corrects = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(payloadJson);

        lock (_gate)
        {
            var sequence = _entries.Count + 1L;
            var correctionProblem = CheckCorrection(kind, corrects, sequence);
            if (correctionProblem is not null)
            {
                throw new DomainRuleViolationException("decision-log.correction", correctionProblem);
            }

            if (_entries.Count > 0 && at < _entries[^1].RecordedAt)
            {
                throw new DomainRuleViolationException("decision-log.time-order", "Entries must be appended in chronological order.");
            }

            var previous = HeadHashUnsafe();
            var hash = ComputeHash(sequence, at, actor, kind, summary, payloadJson, corrects, previous);
            var entry = new DecisionEntry(sequence, at, actor, kind, summary, payloadJson, corrects, previous, hash);
            _entries.Add(entry);
            return entry;
        }
    }

    public DecisionLogVerification Verify()
    {
        lock (_gate)
        {
            return VerifyUnsafe();
        }
    }

    /// <summary>
    /// This log followed by <paramref name="more"/>, the entries a store holds after its end, with the check of those
    /// alone: their sequence, their chain from this log's head, their time and their hash, exactly as <see cref="Verify()"/>
    /// checks each entry. It is for a log that already verified (the writer of the log keeps the one it verified last):
    /// what it held is not checked again, so verifying a log as it grows costs what it grew, not what it holds. This log
    /// does not change; with nothing more, the log returned is this one.
    /// </summary>
    public (DecisionLog Log, DecisionLogVerification Verification) Extend(IReadOnlyList<DecisionEntry> more)
    {
        ArgumentNullException.ThrowIfNull(more);
        if (more.Count == 0)
        {
            return (this, DecisionLogVerification.Valid);
        }

        DecisionLog extended;
        int from;
        lock (_gate)
        {
            from = _entries.Count;
            extended = new DecisionLog([.. _entries, .. more.OrderBy(e => e.Sequence)]);
        }

        lock (extended._gate)
        {
            return (extended, extended.VerifyFromUnsafe(from));
        }
    }

    /// <summary>
    /// The log against the head anchored outside the project (<see cref="Verify(DecisionLogAnchor?)"/>) without checking
    /// the chain again: for a log whose chain verified already (by <see cref="Verify()"/> or <see cref="Extend"/>).
    /// </summary>
    public DecisionLogVerification VerifyHead(DecisionLogAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        lock (_gate)
        {
            return VerifyAgainstUnsafe(anchor);
        }
    }

    /// <summary>
    /// <see cref="Verify()"/>, and then the log against the head anchored outside the project. The chain alone cannot
    /// tell an honest log from one whose end was cut off, or one rewritten with recomputed hashes; the anchor can. Without
    /// an anchor (none was written yet) only the chain is checked.
    /// </summary>
    public DecisionLogVerification Verify(DecisionLogAnchor? anchor)
    {
        lock (_gate)
        {
            var chain = VerifyUnsafe();
            return !chain.IsValid || anchor is null ? chain : VerifyAgainstUnsafe(anchor);
        }
    }

    /// <summary>
    /// The anchored head must still be in the log, at its place: a shorter log lost its end, and a different hash there
    /// means the log was rewritten from some entry on. The log may hold one entry more only if it is the one its writer
    /// announced before storing it (<see cref="DecisionLogAnchor.NextHash"/>); anything else beyond the anchor was
    /// added by someone else.
    /// </summary>
    private DecisionLogVerification VerifyAgainstUnsafe(DecisionLogAnchor anchor)
    {
        var anchored = anchor.EntryCount;
        if (anchored < 0 || anchor.HeadHash is null || !ContentHash.IsSha256Hex(anchor.HeadHash) || (anchored == 0 && anchor.HeadHash != GenesisHash))
        {
            return new DecisionLogVerification(false, null, $"The anchor of the log outside the project ({anchored} entries, head '{anchor.HeadHash}') is not a head this log could have had.");
        }

        var count = _entries.Count;
        if (count < anchored)
        {
            return new DecisionLogVerification(false, count + 1, $"The log ends at entry {count}, but its head was anchored outside the project at entry {anchored}: entries were removed from its end.");
        }

        if (anchored > 0 && _entries[(int)anchored - 1].Hash != anchor.HeadHash)
        {
            return new DecisionLogVerification(false, anchored, $"Entry {anchored} does not match the head anchored outside the project: the log was rewritten.");
        }

        if (count == anchored || (count == anchored + 1 && _entries[^1].Hash == anchor.NextHash))
        {
            return DecisionLogVerification.Valid;
        }

        return new DecisionLogVerification(false, anchored + 1, $"Entries {anchored + 1} to {count} were added after the head anchored outside the project (entry {anchored}): its writer did not write them.");
    }

    private DecisionLogVerification VerifyUnsafe() => VerifyFromUnsafe(0);

    /// <summary>The checks of <see cref="Verify()"/> for the entries from index <paramref name="from"/> on, chained to the one before it.</summary>
    private DecisionLogVerification VerifyFromUnsafe(int from)
    {
        var expectedPrevious = from == 0 ? GenesisHash : _entries[from - 1].Hash;
        for (var i = from; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Sequence != i + 1)
            {
                return new DecisionLogVerification(false, entry.Sequence, "Sequence numbers are not contiguous (an entry was removed or inserted).");
            }

            if (entry.PreviousHash != expectedPrevious)
            {
                return new DecisionLogVerification(false, entry.Sequence, "The chain is broken: previous hash does not match.");
            }

            if (i > 0 && entry.RecordedAt < _entries[i - 1].RecordedAt)
            {
                return new DecisionLogVerification(false, entry.Sequence, "Entries are not in chronological order (the chain was rewritten).");
            }

            var recomputed = ComputeHash(entry.Sequence, entry.RecordedAt, entry.Actor, entry.Kind, entry.Summary, entry.PayloadJson, entry.Corrects, entry.PreviousHash);
            if (recomputed != entry.Hash)
            {
                return new DecisionLogVerification(false, entry.Sequence, "The entry content does not match its hash (it was modified).");
            }

            var correctionProblem = CheckCorrection(entry.Kind, entry.Corrects, entry.Sequence);
            if (correctionProblem is not null)
            {
                return new DecisionLogVerification(false, entry.Sequence, correctionProblem);
            }

            expectedPrevious = entry.Hash;
        }

        return DecisionLogVerification.Valid;
    }

    public static string ComputeHash(
        long sequence,
        DateTimeOffset at,
        Actor actor,
        DecisionKind kind,
        string summary,
        string payloadJson,
        long? corrects,
        string previousHash)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var canonical = ContentHash.Canonical(
            sequence.ToString(CultureInfo.InvariantCulture),
            at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            actor.Kind.ToString(),
            actor.Id,
            kind.ToString(),
            summary,
            payloadJson,
            corrects?.ToString(CultureInfo.InvariantCulture),
            previousHash);
        return ContentHash.Sha256Hex(canonical);
    }

    private string HeadHashUnsafe() => _entries.Count == 0 ? GenesisHash : _entries[^1].Hash;

    private static string? CheckCorrection(DecisionKind kind, long? corrects, long sequence)
    {
        if (kind == DecisionKind.Correction)
        {
            if (corrects is null || corrects < 1 || corrects >= sequence)
            {
                return "A correction must reference an earlier entry.";
            }
        }
        else if (corrects is not null)
        {
            return "Only correction entries may reference another entry.";
        }

        return null;
    }
}
