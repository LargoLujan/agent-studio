using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;

namespace AgentStudio.Application.Knowledge;

// Phase 4 contracts of the project memory (docs/14 sections 4.4 and 5.4). They replace the Phase 0 IProjectMemory and the
// IUsageLedger, whose RecordAsync contradicted the usage being a projection of the log (D-01).

/// <summary>What every writer and reader of memory agrees on about the content of an entry (docs/14 D-18 and D-19).</summary>
public static class MemoryContent
{
    /// <summary>The longest content an entry keeps, in characters, after redacting secrets (D-18).</summary>
    public const int MaxCharacters = 4000;

    /// <summary>
    /// The canonical SHA-256 that <c>MemoryRecorded</c> announces and that recall checks the row against: the category, the
    /// topic and the content, each with a length prefix, so two different entries never share a hash.
    /// </summary>
    public static string Sha256(MemoryKind kind, string topic, string content)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(content);
        return ContentHash.Sha256Hex(ContentHash.Canonical(kind.ToString(), topic, content));
    }
}

/// <summary>Categories of project memory (master prompt, section 14). Stored by name: new ones are appended.</summary>
public enum MemoryKind
{
    Architecture,
    Convention,
    Decision,
    KnownError,
    Solution,
    PendingTask,
    Change,
    DebateSummary,
    Verdict,
    DailySummary,

    /// <summary>
    /// One entry of the journal (<c>.agentstudio/bitacora/</c>), with its result and its «Hecho» lines, anchored as memory
    /// (docs/17 D-02, Phase 12) so the journal never reaches a prompt as a raw file of the repository. Its source is
    /// <see cref="MemorySources.Journal"/>. What Agent Studio wrote, and matches the <c>AgentRunStarted</c> its result line
    /// names (<see cref="JournalResultAnchor"/>), has <see cref="MemoryOrigin.System"/> and is trusted; anything else (the
    /// development hook, an entry that does not match, a third party's) has <see cref="MemoryOrigin.Agent"/> and is in
    /// quarantine until a person accepts it, by entry or by day, or trusts the journal of the project
    /// (<see cref="IJournalTrust"/>). It enters a prompt last, by relevance, and it is not exported to Markdown
    /// (<see cref="MemoryKinds.IsExported"/>): its source already is Markdown in the repository.
    /// </summary>
    JournalEntry,
}

/// <summary>What the categories of memory imply beyond their name.</summary>
public static class MemoryKinds
{
    /// <summary>
    /// Whether the entries of <paramref name="kind"/> go to the Markdown export (docs/14 D-22): every category but
    /// <see cref="MemoryKind.JournalEntry"/>, whose source is the journal itself, already Markdown and versioned in the
    /// repository (docs/17 D-02): exporting it would write the journal back into the repository a second time.
    /// </summary>
    public static bool IsExported(MemoryKind kind) => kind != MemoryKind.JournalEntry;
}

/// <summary>
/// Who wrote the text of a memory entry (docs/14 D-20), which decides how far it is trusted: the system with its own
/// text and the user are trusted; an agent's text is a proposal until the user accepts it.
/// </summary>
public enum MemoryOrigin
{
    /// <summary>Agent Studio, with text of its own (a fact taken from the verified log).</summary>
    System,

    /// <summary>The user wrote it.</summary>
    User,

    /// <summary>
    /// It quotes text an agent wrote (a plan summary, the open issues of a report, a judge's summary). For a
    /// <see cref="MemoryKind.JournalEntry"/> it means text whose author cannot be checked, because no receipt proves Agent
    /// Studio wrote it (the development hook, a person, a third party, or Agent Studio before Phase 12): never trusted by
    /// itself, whatever the rules of the journal say (docs/17 D-02).
    /// </summary>
    Agent,
}

/// <summary>What a person decided about a memory entry (<c>MemoryReviewed</c>, docs/14 D-19).</summary>
public enum MemoryReview
{
    /// <summary>An agent's proposal, or an entry in quarantine, is accepted: it may reach a prompt and the export.</summary>
    Accepted,

    /// <summary>A proposal is rejected: it never reaches a prompt nor the export.</summary>
    Rejected,

    /// <summary>An entry is withdrawn: it is hidden from recall and the export (purging is Phase 7).</summary>
    Retracted,
}

/// <summary>
/// How far an entry is trusted, derived from the log (docs/14 D-20): what its origin and the reviews say, and whether it
/// was anchored before the last history accepted as a new origin (<c>decision-log.origin-accepted</c>), which puts it in
/// quarantine until a person reviews it.
/// </summary>
public enum MemoryTrust
{
    /// <summary>System or user text, or a proposal a person accepted: it may reach a prompt (<c>ReadSelected</c>) and the export.</summary>
    Trusted,

    /// <summary>Text of an agent nobody reviewed yet: neither prompts nor the export.</summary>
    Proposed,

    /// <summary>
    /// Inherited from a history accepted as a new origin: neither prompts nor the export until it is reviewed. Also a
    /// journal entry whose author cannot be checked (<see cref="MemoryKind.JournalEntry"/> with <see cref="MemoryOrigin.Agent"/>)
    /// until a person accepts it or trusts the journal of the project or of its day (docs/17 D-02).
    /// </summary>
    Quarantined,

    /// <summary>A person rejected it.</summary>
    Rejected,

    /// <summary>A person withdrew it.</summary>
    Retracted,
}

/// <summary>
/// What a writer asks to remember (D-18): the category, a topic, the content (redacted and at most
/// <see cref="MemoryContent.MaxCharacters"/>), who wrote the text, the source it cites (a run and an entry of the log, or
/// an evidence and its SHA-256: deriving twice never duplicates) and the entry it supersedes.
/// </summary>
public sealed record MemoryDraft(MemoryKind Kind, string Topic, string Content, MemoryOrigin Origin, string Source, string? Supersedes = null);

/// <summary>
/// A memory entry as recall gives it: an anchored row (announced by <c>MemoryRecorded</c> with the same
/// <paramref name="ContentSha256"/>) with the trust the log gives it. <paramref name="SupersededBy"/> is the entry that
/// supersedes it, from the log; the row itself never changes.
/// </summary>
public sealed record MemoryEntry(
    string Id,
    MemoryKind Kind,
    string Topic,
    string Content,
    MemoryOrigin Origin,
    string Source,
    DateTimeOffset CreatedAt,
    string ContentSha256,
    MemoryTrust Trust,
    string? Supersedes,
    string? SupersededBy);

/// <summary>
/// Selective recall (D-21): FTS5 over topic and content for <paramref name="Text"/>, filtered by categories and topic,
/// without what was superseded, within <paramref name="MaxEntries"/> and <paramref name="MaxCharacters"/>. Only trusted
/// entries unless <paramref name="TrustedOnly"/> is false (the memory screen shows the rest with their trust).
/// </summary>
public sealed record MemoryQuery(
    string? Text = null,
    IReadOnlyList<MemoryKind>? Kinds = null,
    string? Topic = null,
    int MaxEntries = 20,
    int? MaxCharacters = null,
    bool TrustedOnly = true);

/// <summary>
/// The memory of a project (D-19): rows append-only in SQLite, each one announced in the decision log with its SHA-256,
/// and every state (anchoring, review, supersession, quarantine) derived from the log. Writing fails closed on a log that
/// does not verify (<c>DecisionLogIntegrityException</c>) and waits while a run or a planning of the project goes on
/// (<c>IRunActivity</c>).
/// </summary>
public interface IProjectMemory
{
    /// <summary><c>MemoryRecorded</c> first, then the row: an announcement without a row is completed by deriving again.</summary>
    Task<MemoryEntry> RecordAsync(ProjectContext project, MemoryDraft draft, CancellationToken ct);

    /// <summary>Records <c>MemoryReviewed</c> with the person's decision.</summary>
    Task ReviewAsync(ProjectContext project, string entry, MemoryReview decision, string decidedBy, string? comment, CancellationToken ct);

    /// <summary>Anchored rows only: a row never announced, or changed since, is left out and raises an integrity alert.</summary>
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(ProjectContext project, MemoryQuery query, CancellationToken ct);
}

/// <summary>An immutable row of <c>memory_entries</c> (migration 0004): content only, no state.</summary>
public sealed record MemoryRow(
    string Id,
    MemoryKind Kind,
    string Topic,
    string Content,
    MemoryOrigin Origin,
    string Source,
    DateTimeOffset CreatedAt,
    string ContentSha256,
    string? Supersedes);

/// <summary>
/// A search of the store (D-21): ranked by <c>bm25</c> over topic and content when there is <paramref name="Text"/>, each
/// of its terms quoted as a phrase so no FTS5 operator ever gets through; ties by date (newest first) and then by id.
/// <paramref name="Limit"/> null returns every match.
/// </summary>
public sealed record MemorySearch(string? Text = null, IReadOnlyList<MemoryKind>? Kinds = null, string? Topic = null, int? Limit = null);

/// <summary>The rows and the full-text index of the memory (Infrastructure). It appends and searches; it keeps no state.</summary>
public interface IMemoryStore
{
    Task AppendAsync(string projectId, MemoryRow row, CancellationToken ct);

    Task<IReadOnlyList<MemoryRow>> SearchAsync(string projectId, MemorySearch search, CancellationToken ct);

    /// <summary>
    /// The rows with these ids, in no particular order; an id with no row is left out. The memory reads only the rows of
    /// what the log announced since it last checked them (2026-11-05, ADR-0010 addendum of 2026-11-04).
    /// </summary>
    Task<IReadOnlyList<MemoryRow>> FindAsync(string projectId, IReadOnlyCollection<string> ids, CancellationToken ct);
}

/// <summary>What an export of the memory to Markdown did, or why it was refused.</summary>
public sealed record MemoryExportResult(bool Exported, IReadOnlyList<string> Files, int Entries, string? Reason = null)
{
    /// <summary>An export that did not happen (a run or a planning goes on), with the reason in Spanish; nothing was written.</summary>
    public static MemoryExportResult Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new MemoryExportResult(false, [], 0, reason);
    }
}

/// <summary>
/// Exports the trusted and accepted memory to <c>.agentstudio/memory/</c> (D-22): flat names, whole files replaced in a
/// fixed order, a header saying they are generated. The export is never read back, and it is refused while the project
/// is busy (<c>IRunActivity</c>).
/// </summary>
public interface IMemoryExporter
{
    Task<MemoryExportResult> ExportAsync(ProjectContext project, CancellationToken ct);
}

/// <summary>What a decision about the trust of the journal covers (docs/17 D-02).</summary>
public enum JournalTrustScope
{
    /// <summary>The whole journal of the project, the days to come included, until it is withdrawn («Confiar en la bitácora de este proyecto»).</summary>
    Project,

    /// <summary>One day of the journal: its entries, those derived later included.</summary>
    Day,
}

/// <summary>
/// How far the journal of a project is trusted now: whether the whole project is, the days that are (in order), and how
/// many journal entries remain in quarantine.
/// </summary>
public sealed record JournalTrustState(bool ProjectTrusted, IReadOnlyList<DateOnly> TrustedDays, int QuarantinedEntries);

/// <summary>
/// The decision «Confiar en la bitácora de este proyecto», and the same by days (docs/17 D-02; WP1 implements it and WP4
/// puts it on screen). Each change is a <c>JournalTrustChanged</c> entry of the log, out of every run and by a person;
/// withdrawing it is another one, and the journal entries it covered go back to quarantine. It only covers the journal
/// entries Agent Studio did not write (<see cref="MemoryOrigin.Agent"/>): what Agent Studio wrote is trusted by its match,
/// and an entry inherited from a history accepted as a new origin is still reviewed one by one. Accepting a single entry
/// is a review (<see cref="IProjectMemory.ReviewAsync"/>). Writing follows the rules of the memory: it fails closed on a
/// log that does not verify and waits while a run or a planning of the project goes on.
/// </summary>
public interface IJournalTrust
{
    Task<JournalTrustState> ReadAsync(ProjectContext project, CancellationToken ct);

    /// <param name="day">The day, for <see cref="JournalTrustScope.Day"/>; null for the whole project.</param>
    /// <param name="trusted">True to trust, false to withdraw the trust.</param>
    /// <exception cref="ArgumentException">A day for the project, or none for a day.</exception>
    Task SetAsync(ProjectContext project, JournalTrustScope scope, DateOnly? day, bool trusted, string decidedBy, string? comment, CancellationToken ct);
}
