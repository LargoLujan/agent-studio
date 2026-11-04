using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Semantic;

namespace AgentStudio.Memory.Semantic;

/// <summary>What an identifier of a search names.</summary>
public enum SearchIdentifierKind
{
    /// <summary>A run: <c>20261015-a1b2c3</c>, or a debate, an audit or an auxiliary run (<c>deb-</c>, <c>aud-</c>, <c>aux-</c>).</summary>
    Run,

    /// <summary>The number of an entry of the journal in its day: <c>P-0102</c>.</summary>
    Prompt,

    /// <summary>An entry of the journal with its day: <c>2026-10-15/P-0102</c>.</summary>
    Entry,

    /// <summary>A day: <c>2026-10-29</c>, <c>29/10/2026</c>, «el 29/10», «29 de octubre».</summary>
    Date,

    /// <summary>A task of a plan: <c>T2</c>, <c>T-12</c>. The plans number their tasks again, so it only orders what another identifier found.</summary>
    Task,

    /// <summary>An entry of the memory, <c>mem-0123456789abcdef</c>, or one of its fragments, <c>mem:mem-0123456789abcdef#2</c>.</summary>
    Memory,
}

/// <summary>
/// One identifier of a search (ADR-0047, addendum of 2026-11-04, second): what it names and how a fragment contains it
/// exactly, in its whole text (at the level of whole words, as an FTS5 phrase) or in its data (the day of its time, the id of
/// its entry).
/// </summary>
/// <param name="Kind">What it names.</param>
/// <param name="Text">How the search wrote it.</param>
/// <param name="Words">Its words (<see cref="FoldedWords"/>), for a run, a number, a task, an entry or a memory id.</param>
/// <param name="Year">The year of a day or of an entry; null when the search did not say it (any year).</param>
/// <param name="Month">The month of a day or of an entry; 0 for the rest.</param>
/// <param name="Day">The day of the month of a day or of an entry; 0 for the rest.</param>
/// <param name="Part">The fragment of a memory id that names one; null for the whole entry.</param>
public sealed record SearchIdentifier(SearchIdentifierKind Kind, string Text, IReadOnlyList<string> Words, int? Year = null, int Month = 0, int Day = 0, int? Part = null)
{
    /// <summary>Whether a fragment that contains it goes first by itself; a task only orders what another identifier found.</summary>
    public bool OpensTheHead => Kind != SearchIdentifierKind.Task;
}

/// <summary>
/// The identifiers of a search and whether a fragment contains them exactly (ADR-0047, addendum of 2026-11-04, second): the
/// runs, the numbers of the journal (<c>P-NNNN</c>, alone or with their day), the days (ISO, <c>dd/mm/aaaa</c>,
/// <c>dd-mm-aaaa</c>, «29 de octubre [de 2026]», and <c>dd/mm</c> only after «el», «del» or «día», since alone «3/5» is as
/// likely a share), the tasks and the ids of the memory. They are found in the text as the search wrote it, never inside a
/// longer word; an entry with its day counts once, not also as its day and its number.
/// <list type="bullet">
/// <item>A run, a number, a task and a memory id are in a fragment when their words come together in its text, as an FTS5
/// phrase would find them, so <c>P-0102</c> is not in <c>P-01023</c>. A memory id is also in the fragments of its entry.</item>
/// <item>A day is in a fragment that writes it (ISO, inside the date of a run, or «29 de octubre») or whose time is that day.
/// A day without a year is any year's.</item>
/// <item>An entry with its day is in a fragment that writes it whole (<c>entrada `2026-10-15/P-0102`</c>), or that writes its
/// number and is of that day.</item>
/// </list>
/// </summary>
public static partial class SearchIdentifiers
{
    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>The identifiers of <paramref name="text"/>, in the order it writes them, each once.</summary>
    public static IReadOnlyList<SearchIdentifier> In(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var found = new List<(int At, SearchIdentifier Identifier)>();
        var taken = new List<(int Start, int End)>();

        // The longest first, so an entry with its day is not also its day and its number, and a run is not a day.
        Add(QualifiedEntry(), m => DateOf(m, "y", "m", "d") is { } day
            ? new SearchIdentifier(SearchIdentifierKind.Entry, m.Value, FoldedWords.Of("P-" + m.Groups["n"].Value), day.Year, day.Month, day.Day)
            : null);
        Add(Run(), m => new SearchIdentifier(SearchIdentifierKind.Run, m.Value, FoldedWords.Of(m.Value)));
        Add(MemoryId(), m => new SearchIdentifier(
            SearchIdentifierKind.Memory,
            m.Value,
            FoldedWords.Of(m.Groups["entry"].Value),
            Part: m.Groups["part"].Success ? int.Parse(m.Groups["part"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null));
        Add(IsoDate(), m => DateOf(m, "y", "m", "d") is { } day ? Date(m.Value, day.Year, day.Month, day.Day) : null);
        Add(DayMonthYear(), m => DateOf(m, "y", "m", "d") is { } day ? Date(m.Value, day.Year, day.Month, day.Day) : null);
        // Without its year, «3/5» is a day only after «el», «del» or «día»: alone it is as likely a share («3/5 en verde»).
        Add(SlashDate(), m => DateOf(m, "y", "m", "d") is { } day && (day.Year is not null || DayWord().IsMatch(text.AsSpan(0, m.Index)))
            ? Date(m.Value, day.Year, day.Month, day.Day)
            : null);
        Add(WrittenDate(), m =>
        {
            var month = Array.IndexOf(Months, Normalized(m.Groups["month"].Value)) + 1;
            var day = int.Parse(m.Groups["d"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            int? year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null;
            return month > 0 && IsDay(year ?? 2000, month, day) ? Date(m.Value, year, month, day) : null;
        });
        Add(PromptNumber(), m => new SearchIdentifier(SearchIdentifierKind.Prompt, m.Value, FoldedWords.Of(m.Value)));
        Add(TaskId(), m => new SearchIdentifier(SearchIdentifierKind.Task, m.Value, FoldedWords.Of(m.Value)));

        return [.. found.OrderBy(f => f.At).Select(f => f.Identifier).DistinctBy(i => (i.Kind, string.Join(' ', i.Words), i.Year, i.Month, i.Day, i.Part))];

        void Add(Regex pattern, Func<Match, SearchIdentifier?> make)
        {
            foreach (Match match in pattern.Matches(text))
            {
                var (start, end) = (match.Index, match.Index + match.Length);
                if (taken.Any(t => start < t.End && t.Start < end) || make(match) is not { } identifier)
                {
                    continue;
                }

                taken.Add((start, end));
                found.Add((start, identifier));
            }
        }
    }

    /// <summary>
    /// Whether the fragment with these <paramref name="words"/> (<see cref="FoldedWords.Of"/> of its text), of
    /// <paramref name="fragment"/> and its entry <paramref name="entryId"/>, contains <paramref name="identifier"/> exactly.
    /// </summary>
    public static bool Contains(SearchIdentifier identifier, IReadOnlyList<string> words, SemanticFragment fragment, string entryId)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(entryId);
        return identifier.Kind switch
        {
            SearchIdentifierKind.Date => WritesDay(words, identifier) || IsOfDay(fragment.At, identifier),
            SearchIdentifierKind.Entry => FoldedWords.Contains(words, [.. DayWords(identifier), .. identifier.Words])
                || (FoldedWords.Contains(words, identifier.Words) && (WritesDay(words, identifier) || IsOfDay(fragment.At, identifier))),
            SearchIdentifierKind.Memory => (string.Equals(string.Join('-', identifier.Words), entryId, StringComparison.OrdinalIgnoreCase)
                    && (identifier.Part is null || identifier.Part == fragment.Part))
                || FoldedWords.Contains(words, identifier.Words),
            _ => FoldedWords.Contains(words, identifier.Words),
        };
    }

    private static SearchIdentifier Date(string text, int? year, int month, int day) =>
        new(SearchIdentifierKind.Date, text, [], year, month, day);

    /// <summary>The day of the groups <paramref name="y"/>, <paramref name="m"/> and <paramref name="d"/> of a match, when it is one (a two-digit year is of this century).</summary>
    private static (int? Year, int Month, int Day)? DateOf(Match match, string y, string m, string d)
    {
        int? year = match.Groups[y].Success ? int.Parse(match.Groups[y].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null;
        if (year is < 100)
        {
            year += 2000;
        }

        var month = int.Parse(match.Groups[m].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[d].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        return IsDay(year ?? 2000, month, day) ? (year, month, day) : null;
    }

    private static bool IsDay(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    /// <summary>The words of the ISO date of an identifier with a year; none without one.</summary>
    private static string[] DayWords(SearchIdentifier identifier) => identifier.Year is { } year
        ? [year.ToString("D4", CultureInfo.InvariantCulture), identifier.Month.ToString("D2", CultureInfo.InvariantCulture), identifier.Day.ToString("D2", CultureInfo.InvariantCulture)]
        : [];

    /// <summary>Whether a time is of the day of <paramref name="identifier"/> (in the offset it carries).</summary>
    private static bool IsOfDay(DateTimeOffset at, SearchIdentifier identifier) =>
        at.Month == identifier.Month && at.Day == identifier.Day && (identifier.Year is null || at.Year == identifier.Year);

    /// <summary>
    /// Whether <paramref name="words"/> write the day of <paramref name="identifier"/>: as an ISO date (a year of four
    /// digits, then the month and the day), as the date of a run (<c>yyyyMMdd</c>), or as «29 de octubre». The year must be
    /// one (from 1970 to 2999), so a number of the journal followed by a time (<c>P-0004 · 10:15</c>) is not a day.
    /// </summary>
    private static bool WritesDay(IReadOnlyList<string> words, SearchIdentifier identifier)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Length == 4 && i + 2 < words.Count && IsNumber(word, out var year) && IsYear(year)
                && words[i + 1].Length == 2 && IsNumber(words[i + 1], out var month)
                && words[i + 2].Length == 2 && IsNumber(words[i + 2], out var day)
                && Same(identifier, year, month, day))
            {
                return true;
            }

            if (word.Length == 8 && IsNumber(word, out var compact) && IsYear(compact / 10_000) && Same(identifier, compact / 10_000, compact / 100 % 100, compact % 100))
            {
                return true;
            }

            if (i + 2 < words.Count && word.Length <= 2 && IsNumber(word, out var written)
                && string.Equals(words[i + 1], "de", StringComparison.Ordinal)
                && Array.IndexOf(Months, words[i + 2]) + 1 is var writtenMonth and > 0
                && written == identifier.Day && writtenMonth == identifier.Month
                && (identifier.Year is null || (i + 4 < words.Count && string.Equals(words[i + 3], "de", StringComparison.Ordinal) && IsNumber(words[i + 4], out var writtenYear) && writtenYear == identifier.Year)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsYear(int year) => year is >= 1970 and <= 2999;

    private static bool Same(SearchIdentifier identifier, int year, int month, int day) =>
        month == identifier.Month && day == identifier.Day && (identifier.Year is null || year == identifier.Year);

    private static bool IsNumber(string word, out int value) =>
        int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>A month as it is compared: folded, so «Octubre» and «octubre» are the same.</summary>
    private static string Normalized(string month) => JournalTemplate.Fold(month);

    [GeneratedRegex(@"(?<![\w/-])(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})/[Pp]-(?<n>\d{4,9})(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedEntry();

    [GeneratedRegex(@"(?<![\w-])(?:(?:deb|aud|aux)-)?\d{8}-[0-9a-f]{6}(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Run();

    [GeneratedRegex(@"(?<![\w:-])(?:mem:)?(?<entry>mem-[0-9a-f]{16})(?:#(?<part>\d{1,6}))?(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MemoryId();

    [GeneratedRegex(@"(?<![\w/-])(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})(?![\w/-])", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![\w/-])(?<d>\d{1,2})-(?<m>\d{1,2})-(?<y>\d{4})(?![\w/-])", RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"(?<![\w/-])(?<d>\d{1,2})/(?<m>\d{1,2})(?:/(?<y>\d{4}|\d{2}))?(?![\w/-])", RegexOptions.CultureInvariant)]
    private static partial Regex SlashDate();

    /// <summary>The word a day without its year must follow, right before it: «el», «del» or «día».</summary>
    [GeneratedRegex(@"(?:^|[^\w])(?:el|del|d[ií]a)\s+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayWord();

    [GeneratedRegex(@"(?<!\w)(?<d>\d{1,2}) de (?<month>[a-zA-Z]+)(?: de (?<y>\d{4}))?(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex WrittenDate();

    [GeneratedRegex(@"(?<![\w/-])[Pp]-\d{4,9}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex PromptNumber();

    [GeneratedRegex(@"(?<![\w/-])T-?\d{1,4}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex TaskId();
}
