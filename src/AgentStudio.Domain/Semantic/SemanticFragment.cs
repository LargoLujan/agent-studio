using System.Globalization;
using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Semantic;

// Phase 12 (docs/17 sections 3.1 and 3.3, ADR-0047): the fragments of the local semantic index. The index is a projection
// of what is anchored in the decision log, outside the project and rebuilt at will; it never keeps anything it cannot
// cite, and a fragment is only a way to rank its source.

/// <summary>What a fragment of the semantic index cites (docs/17 D-01): only what the decision log anchors.</summary>
public enum FragmentSourceKind
{
    /// <summary>
    /// A memory entry, the journal entries (<c>JournalEntry</c>) included: its id and the canonical SHA-256 that its
    /// <c>MemoryRecorded</c> announced (<c>MemoryContent.Sha256</c>).
    /// </summary>
    Memory,

    /// <summary>
    /// An entry of the decision log whose text the system generates from its payload (the approved plan, a verdict, a
    /// delivery, an alert of the auditor): its sequence in the project's log and its hash in the chain.
    /// </summary>
    LogEntry,
}

/// <summary>
/// The anchored source a fragment cites (docs/17 D-01): a memory entry by its id and the SHA-256 its announcement carries,
/// or an entry of the decision log by its sequence and its hash in the chain. A search drops every fragment whose source
/// no longer verifies (retracted, superseded, changed or not anchored), so the index can never make anything trusted.
/// </summary>
public sealed record FragmentSource
{
    /// <exception cref="ArgumentException">The reference is empty or has white space, or the hash is not 64 lowercase hex characters.</exception>
    public FragmentSource(FragmentSourceKind kind, string reference, string sha256)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of fragment source.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The reference of a fragment source has no white space.", nameof(reference));
        }

        if (!ContentHash.IsSha256Hex(sha256))
        {
            throw new ArgumentException("The anchored hash of a fragment source must be 64 lowercase hex characters.", nameof(sha256));
        }

        Kind = kind;
        Reference = reference;
        Sha256 = sha256;
    }

    public FragmentSourceKind Kind { get; }

    /// <summary>The id of the memory entry, or the sequence of the log entry in invariant digits.</summary>
    public string Reference { get; }

    /// <summary>The SHA-256 the log anchors for the source: the canonical one of the memory entry, or the hash of the log entry.</summary>
    public string Sha256 { get; }

    /// <summary>A memory entry, by its id and the canonical SHA-256 its <c>MemoryRecorded</c> announced.</summary>
    public static FragmentSource Memory(string entryId, string contentSha256) => new(FragmentSourceKind.Memory, entryId, contentSha256);

    /// <summary>An entry of the decision log, by its sequence and its hash in the chain.</summary>
    public static FragmentSource LogEntry(long sequence, string entryHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        return new FragmentSource(FragmentSourceKind.LogEntry, sequence.ToString(CultureInfo.InvariantCulture), entryHash);
    }
}

/// <summary>
/// One fragment of the semantic index (docs/17 D-01 and D-04): a piece of the text of an anchored source, already redacted
/// (<c>ISecretRedactor</c>) and at most <see cref="MaxCharacters"/> long, with the time of its source, which breaks the ties
/// of a search (the newest first). <see cref="Part"/> numbers the pieces of one source from 0; how a text is cut into
/// pieces is the indexer's (WP3), and the same text must always be cut the same way.
/// <list type="bullet">
/// <item><see cref="Id"/> depends only on the source and the part: <c>mem:&lt;entry&gt;#&lt;part&gt;</c> or
/// <c>log:&lt;sequence&gt;#&lt;part&gt;</c>.</item>
/// <item><see cref="Sha256"/>, its fingerprint, depends on everything it says (the source with its hash, the part and the
/// text) and not on the time, so rebuilding the index gives the same fragments with the same fingerprints (F12-3), and a
/// fragment whose fingerprint changed is embedded again.</item>
/// </list>
/// </summary>
public sealed record SemanticFragment
{
    /// <summary>The longest text a fragment keeps, in characters (docs/17 D-01).</summary>
    public const int MaxCharacters = 1500;

    /// <summary>The prefix of the id of a fragment of a memory entry.</summary>
    public const string MemoryPrefix = "mem:";

    /// <summary>The prefix of the id of a fragment of a log entry.</summary>
    public const string LogPrefix = "log:";

    private const string FingerprintFormat = "agent-studio/semantic-fragment/1";

    /// <exception cref="ArgumentException">The text is blank or longer than <see cref="MaxCharacters"/>, or the part is negative.</exception>
    public SemanticFragment(FragmentSource source, int part, string text, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaxCharacters)
        {
            throw new ArgumentException($"A fragment keeps at most {MaxCharacters} characters; this one has {text.Length}.", nameof(text));
        }

        Source = source;
        Part = part;
        Text = text;
        At = at;
        Id = IdOf(source, part);
        Sha256 = ContentHash.Sha256Hex(ContentHash.Canonical(
            FingerprintFormat,
            source.Kind.ToString(),
            source.Reference,
            source.Sha256,
            part.ToString(CultureInfo.InvariantCulture),
            text));
    }

    public FragmentSource Source { get; }

    public int Part { get; }

    public string Text { get; }

    /// <summary>The time of the source: when the memory entry was recorded, or when the log entry was.</summary>
    public DateTimeOffset At { get; }

    public string Id { get; }

    /// <summary>The fingerprint of the fragment: SHA-256 of its source, part and text, with length prefixes.</summary>
    public string Sha256 { get; }

    /// <summary>The id of the piece <paramref name="part"/> of <paramref name="source"/>.</summary>
    public static string IdOf(FragmentSource source, int part)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        var prefix = source.Kind == FragmentSourceKind.Memory ? MemoryPrefix : LogPrefix;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{source.Reference}#{part}");
    }
}
