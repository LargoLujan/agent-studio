using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Memory.Semantic;

/// <summary>
/// How an anchored source becomes the fragments of the semantic index (docs/17 D-01; ADR-0047, decision 7). Deterministic:
/// the same text is always cut the same way, so rebuilding the index gives the same fragments with the same fingerprints
/// (F12-3), and a search can cut the source again to check a fragment against it (F12-2).
/// <list type="bullet">
/// <item>A text that fits in <see cref="SemanticFragment.MaxCharacters"/> is one fragment.</item>
/// <item>A longer one is cut greedily: at the last line break of the second half of the window, or else at its last white
/// space, or else at the window's end (never between the two halves of a surrogate pair). Each piece is trimmed.</item>
/// <item>A memory entry is <c>"topic: content"</c>, the same text the context pack quotes. When it is cut, every piece
/// keeps the topic in front (when the topic takes at most a third of the window), so a piece is never found without
/// knowing what it is about.</item>
/// <item>An entry of the journal (<see cref="MemoryKind.JournalEntry"/>) has two texts. Its fragments carry the whole
/// text, as every entry: the words of the index search it (dates, roles, numbers <c>P-NNNN</c>, runs, tags and states are
/// what a person asks for by name) and the planner's tool shows it. Its meaning is computed from the clean view of each
/// piece (<see cref="MeaningOf"/>, <see cref="JournalSearchText"/>), without the template every entry shares. The view is a
/// pure function of the fragment and of the category its anchored source has, so the fingerprint of the fragment, which
/// covers the text and the SHA-256 of the source, settles both; and the vectors carry the version of the view.</item>
/// </list>
/// </summary>
public static class FragmentCutter
{
    /// <summary>The pieces of <paramref name="text"/>, each at most <paramref name="max"/> characters, trimmed and not blank; none for a blank text.</summary>
    public static IReadOnlyList<string> Cut(string text, int max = SemanticFragment.MaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 2);

        var rest = text.Trim();
        var pieces = new List<string>();
        while (rest.Length > max)
        {
            var cut = BreakAt(rest, max);
            var piece = rest[..cut].TrimEnd();
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }

            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            pieces.Add(rest);
        }

        return pieces;
    }

    /// <summary>
    /// The fragments of a memory entry, in order, citing it by its id and the canonical SHA-256 its <c>MemoryRecorded</c>
    /// announced, with its time; none for an entry that cannot be cited (an id with white space or a hash that is not one).
    /// </summary>
    public static IReadOnlyList<SemanticFragment> Of(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Any(char.IsWhiteSpace) || !ContentHash.IsSha256Hex(entry.ContentSha256))
        {
            return [];
        }

        var source = FragmentSource.Memory(entry.Id, entry.ContentSha256);
        var whole = $"{entry.Topic}: {entry.Content}";
        var prefix = entry.Topic.Trim() + ": ";
        var pieces = whole.Length <= SemanticFragment.MaxCharacters ? Cut(whole)
            : prefix.Length <= SemanticFragment.MaxCharacters / 3 ? [.. Cut(entry.Content, SemanticFragment.MaxCharacters - prefix.Length).Select(p => prefix + p)]
            : Cut(whole);
        return [.. pieces.Select((piece, part) => new SemanticFragment(source, part, piece, entry.CreatedAt))];
    }

    /// <summary>
    /// The text the meaning of <paramref name="fragment"/>, a fragment of <paramref name="entry"/>, is computed from: its text,
    /// or for an entry of the journal the clean view of its piece of content (without the topic in front, which is the same
    /// in every entry of a day).
    /// </summary>
    public static string MeaningOf(MemoryEntry entry, SemanticFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(fragment);
        if (entry.Kind != MemoryKind.JournalEntry)
        {
            return fragment.Text;
        }

        var prefix = entry.Topic.Trim() + ": ";
        var piece = fragment.Text.StartsWith(prefix, StringComparison.Ordinal) ? fragment.Text[prefix.Length..] : fragment.Text;
        return JournalSearchText.Of(piece);
    }

    /// <summary>Where to cut <paramref name="text"/>, longer than <paramref name="max"/>: a position from 1 to <paramref name="max"/>.</summary>
    private static int BreakAt(string text, int max)
    {
        for (var i = max; i >= max / 2; i--)
        {
            if (text[i] == '\n')
            {
                return i;
            }
        }

        for (var i = max; i > 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
    }
}
