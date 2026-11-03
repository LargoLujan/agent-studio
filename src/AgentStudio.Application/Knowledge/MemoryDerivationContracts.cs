using System.Globalization;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;

namespace AgentStudio.Application.Knowledge;

// Phase 4 contract of the memory derived from the decision log (docs/14 D-18 to D-20).

/// <summary>
/// How a memory entry derived from the decision log cites what it says (docs/14 D-18). Every derived fact has exactly one
/// source, and a source never changes (the log and the evidence are append-only), so deriving twice never duplicates an
/// entry. Three forms:
/// <list type="bullet">
/// <item><c>registro:&lt;run&gt;#&lt;sequence&gt;</c>: an entry of the decision log, by the run it belongs to and its
/// sequence in the project's log (for example <c>registro:20261015-a1b2c3#42</c>).</item>
/// <item><c>evidencia:&lt;id&gt;#sha256:&lt;hash&gt;</c>: a piece of evidence, by its id and the SHA-256 that its
/// <c>EvidenceRegistered</c> entry anchored. It is the source of every text an agent wrote.</item>
/// <item><c>bitacora:&lt;day&gt;#sha256:&lt;hash&gt;</c>: an entry of the journal (Phase 12, docs/17 D-02), by its day and
/// the SHA-256 of its text (<see cref="Journal"/>).</item>
/// </list>
/// </summary>
public static class MemorySources
{
    /// <summary>The start of a source that is an entry of the decision log.</summary>
    public const string LogEntryPrefix = "registro:";

    /// <summary>The start of a source that is a piece of evidence.</summary>
    public const string EvidencePrefix = "evidencia:";

    /// <summary>The source that cites the entry <paramref name="sequence"/> of the log, which belongs to <paramref name="runId"/>.</summary>
    public static string LogEntry(string runId, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        return string.Create(CultureInfo.InvariantCulture, $"{LogEntryPrefix}{runId}#{sequence}");
    }

    /// <summary>The start of a source that is an entry of the journal (docs/17 D-02).</summary>
    public const string JournalPrefix = "bitacora:";

    /// <summary>
    /// The source of a journal entry (<see cref="MemoryKind.JournalEntry"/>): <c>bitacora:&lt;yyyy-MM-dd&gt;#sha256:&lt;hash&gt;</c>,
    /// the local day of its file and the SHA-256 of the text of the entry as the derivation reads it (its lines, with
    /// their result and «Hecho» lines). The hash is the identity: deriving the same entry twice records nothing the second
    /// time, and an entry changed in the file afterwards is another entry, which never changes what was anchored.
    /// </summary>
    public static string Journal(DateOnly day, string entrySha256)
    {
        if (!ContentHash.IsSha256Hex(entrySha256))
        {
            throw new ArgumentException("The hash of a journal entry must be 64 lowercase hex characters.", nameof(entrySha256));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{JournalPrefix}{day:yyyy-MM-dd}#sha256:{entrySha256}");
    }

    /// <summary>The source that cites the evidence <paramref name="evidence"/> with the SHA-256 the log anchored for it.</summary>
    public static string Evidence(EvidenceId evidence, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Value, nameof(evidence));
        if (!ContentHash.IsSha256Hex(sha256))
        {
            throw new ArgumentException("The anchored hash of the evidence must be 64 lowercase hex characters.", nameof(sha256));
        }

        return $"{EvidencePrefix}{evidence.Value}#sha256:{sha256}";
    }
}

/// <summary>
/// What one derivation of the memory did (docs/14 D-18). <paramref name="Recorded"/> holds the entries it recorded now
/// (including a row it completed for an announcement the app had left without one); <paramref name="AlreadyRecorded"/>,
/// the sources of the facts that were already in the memory, which it left as they were; <paramref name="Problems"/>, in
/// Spanish, each text of an agent it did not quote because its evidence is missing or is not the content the log anchored
/// (each one also leaves an integrity alert in the log, once). <paramref name="Reason"/>, in Spanish, says why it wrote
/// nothing or why it stopped before the end; it is null when it went through the whole log.
/// </summary>
public sealed record MemoryDerivationResult(
    IReadOnlyList<MemoryEntry> Recorded,
    IReadOnlyList<string> AlreadyRecorded,
    IReadOnlyList<string> Problems,
    string? Reason = null)
{
    /// <summary>Whether the derivation went through the whole log (it may still have <see cref="Problems"/>).</summary>
    public bool Completed => Reason is null;

    /// <summary>A derivation that wrote nothing, with the reason in Spanish.</summary>
    public static MemoryDerivationResult NotDerived(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new MemoryDerivationResult([], [], [], reason);
    }
}

/// <summary>
/// Derives the memory of a project from its verified decision log (docs/14 D-18 to D-20): the decisions on the plan and
/// the delivery, the tasks left pending, the known errors, the changes and the verdicts, each entry with its source
/// (<see cref="MemorySources"/>). The desktop calls it when a run ends and when the project is opened.
/// <list type="bullet">
/// <item>Only from a log that verifies: on any other it writes nothing and says why.</item>
/// <item>Never while a run or a planning of the project goes on (<c>IRunActivity</c>): it does not wait, it says why.</item>
/// <item>Idempotent: a fact already in the memory is not recorded again, so deriving twice in a row records nothing the
/// second time.</item>
/// <item>The text of an agent is quoted only from evidence the log anchored, checked against its SHA-256, and starts as
/// a proposal (<see cref="MemoryTrust.Proposed"/>); the system's own text is trusted.</item>
/// <item>The daily summary is not derived here (<see cref="IDailySummaryService"/>).</item>
/// </list>
/// </summary>
public interface IMemoryDerivation
{
    Task<MemoryDerivationResult> DeriveAsync(ProjectContext project, CancellationToken ct);
}
