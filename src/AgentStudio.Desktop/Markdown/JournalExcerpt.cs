namespace AgentStudio.Desktop.Markdown;

/// <summary>
/// What the readable view of a day of the journal draws (V31): the whole day, or, when it is long, its title with the lines
/// under it and its latest entries, whole, within a budget of lines and characters. A document is laid out on the UI thread
/// and that grows with its length (a day of 5,000 lines took seconds), so opening a project or «Actualizar» never draws more
/// than this; the whole day is still there as plain text, on demand.
/// </summary>
/// <param name="Text">The Markdown to draw.</param>
/// <param name="IsTrimmed">Whether older entries were left out.</param>
/// <param name="ShownEntries">The entries drawn (the lines that start with <c>- </c>).</param>
/// <param name="TotalEntries">The entries of the day.</param>
/// <param name="ShownLines">The lines drawn.</param>
/// <param name="TotalLines">The lines of the day.</param>
public sealed record JournalExcerpt(string Text, bool IsTrimmed, int ShownEntries, int TotalEntries, int ShownLines, int TotalLines)
{
    /// <summary>The most lines of the title and what is under it that a trimmed view keeps before the entries.</summary>
    public const int MaxHeadLines = 6;

    public static JournalExcerpt Empty { get; } = new(string.Empty, false, 0, 0, 0, 0);

    /// <summary>What to draw of <paramref name="text"/> within <paramref name="maxLines"/> lines and <paramref name="maxCharacters"/> characters.</summary>
    public static JournalExcerpt Of(string? text, int maxLines, int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, MaxHeadLines + 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 1);
        if (string.IsNullOrEmpty(text))
        {
            return Empty;
        }

        var lines = text.Split('\n');
        var entries = lines.Count(IsEntry);
        if (lines.Length <= maxLines && text.Length <= maxCharacters)
        {
            return new JournalExcerpt(text, false, entries, entries, lines.Length, lines.Length);
        }

        // The title and the lines under it, up to the first entry.
        var head = lines.TakeWhile(line => !IsEntry(line)).Take(MaxHeadLines).ToList();
        var lineBudget = maxLines - head.Count;
        var characterBudget = maxCharacters - head.Sum(line => line.Length + 1);

        // The latest entries, whole: walking back from the end, the last place where an entry (or a title) starts and
        // everything below it still fits.
        var cut = lines.Length;
        var used = 0;
        var characters = 0;
        for (var i = lines.Length - 1; i >= head.Count; i--)
        {
            used++;
            characters += lines[i].Length + 1;
            if (used > lineBudget || characters > characterBudget)
            {
                break;
            }

            if (IsEntry(lines[i]) || lines[i].StartsWith('#'))
            {
                cut = i;
            }
        }

        // Not even the last entry fits whole: its last lines, and the start of a line too long cut off.
        var tail = cut < lines.Length ? lines[cut..] : LastLines(lines, head.Count, lineBudget, characterBudget);
        var shown = string.Join('\n', head.Concat(tail));
        return new JournalExcerpt(shown, true, tail.Count(IsEntry), entries, head.Count + tail.Length, lines.Length);
    }

    private static string[] LastLines(string[] lines, int from, int lineBudget, int characterBudget)
    {
        var tail = lines[Math.Max(from, lines.Length - lineBudget)..];
        var characters = 0;
        for (var i = tail.Length - 1; i >= 0; i--)
        {
            characters += tail[i].Length + 1;
            if (characters > characterBudget)
            {
                var keep = Math.Max(0, tail[i].Length - (characters - characterBudget));
                tail[i] = "…" + tail[i][^keep..];
                return tail[i..];
            }
        }

        return tail;
    }

    private static bool IsEntry(string line) => line.StartsWith("- ", StringComparison.Ordinal);
}
