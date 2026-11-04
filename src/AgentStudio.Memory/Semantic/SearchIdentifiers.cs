using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;

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

    /// <summary>A day: <c>2026-10-29</c>, <c>2026/10/29</c>, <c>29/10/2026</c>, <c>29.10.2026</c>, «el 29/10», «29 de octubre», «29 de oct.».</summary>
    Date,

    /// <summary>A task of a plan: <c>T2</c>, <c>T-12</c>. The plans number their tasks again, so it only orders what another identifier found.</summary>
    Task,

    /// <summary>An entry of the memory, <c>mem-0123456789abcdef</c>, or one of its fragments, <c>mem:mem-0123456789abcdef#2</c>.</summary>
    Memory,
}

/// <summary>
/// One identifier of a search (ADR-0047, addenda of 2026-11-04): what it names and how a fragment contains it exactly, in its
/// whole text (at the level of whole words, as an FTS5 phrase) or in its data (the day of its source, the id of its entry).
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
/// The identifiers of a search and whether a fragment contains them exactly (ADR-0047, addenda of 2026-11-04). They are found
/// in the text as the search wrote it, never inside a longer word; an entry with its day counts once, not also as its day and
/// its number.
/// <list type="bullet">
/// <item>The runs, the numbers of the journal (<c>P-NNNN</c>, alone or with their day), the tasks and the ids of the memory.</item>
/// <item>The days with their year: ISO (<c>2026-10-29</c>), <c>2026/10/29</c>, <c>29/10/2026</c>, <c>29-10-2026</c>,
/// <c>29.10.2026</c>, and «29 de octubre de 2026». With the name of the month, the year may go: «29 de octubre», «29 de
/// oct.».</item>
/// <item>A <c>dd/mm</c> without its year only after «el», «del», «al», «día» or «días», or right after another day of the
/// same list («los días 3/5 y 7/5», «el 3/5, 4/5 y 7/5»): alone, «3/5» is as likely a share. Nor when «de las», «de los» or a
/// number follows it («el 24/10 de las pruebas»). A day and a month are always a real day of the calendar.</item>
/// </list>
/// A fragment contains a run, a number, a task or a memory id when their words come together in its text, as an FTS5 phrase
/// would find them, so <c>P-0102</c> is not in <c>P-01023</c>; a memory id is also in the fragments of its entry. It contains
/// a day when it writes it (ISO, inside the date of a run, or «29 de octubre») or when it is the day of its source: for an
/// entry of the journal, the day of its file; for the rest, the day it was anchored in the time zone of the person. An entry
/// with its day is in a fragment that writes it whole (<c>entrada `2026-10-15/P-0102`</c>), or that writes its number and
/// is of that day.
/// </summary>
public static partial class SearchIdentifiers
{
    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>The short names of the months a person writes, folded, with or without their dot: «oct.», «sept», «set».</summary>
    private static readonly Dictionary<string, int> ShortMonths = new(StringComparer.Ordinal)
    {
        ["ene"] = 1, ["feb"] = 2, ["mar"] = 3, ["abr"] = 4, ["may"] = 5, ["jun"] = 6, ["jul"] = 7, ["ago"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["set"] = 9, ["setiembre"] = 9, ["oct"] = 10, ["nov"] = 11, ["dic"] = 12,
    };

    /// <summary>The identifiers of <paramref name="text"/>, in the order it writes them, each once.</summary>
    public static IReadOnlyList<SearchIdentifier> In(string? text) =>
        [.. Found(text).Select(f => f.Identifier).DistinctBy(i => (i.Kind, string.Join(' ', i.Words), i.Year, i.Month, i.Day, i.Part))];

    /// <summary><paramref name="text"/> with each of its identifiers blanked out: what the search asks besides them.</summary>
    public static string Without(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var rest = new StringBuilder(text);
        foreach (var (start, end, _) in Found(text))
        {
            for (var i = start; i < end; i++)
            {
                rest[i] = ' ';
            }
        }

        return rest.ToString();
    }

    /// <summary>
    /// Whether a fragment with these <paramref name="words"/> (<see cref="FoldedWords.Of"/> of its text), the
    /// <paramref name="day"/> of its source (null when it has none), of the entry <paramref name="entryId"/> and its
    /// <paramref name="part"/>, contains <paramref name="identifier"/> exactly.
    /// </summary>
    public static bool Contains(SearchIdentifier identifier, IReadOnlyList<string> words, DateOnly? day, string entryId, int part)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(entryId);
        return identifier.Kind switch
        {
            SearchIdentifierKind.Date => WritesDay(words, identifier) || IsOfDay(day, identifier),
            SearchIdentifierKind.Entry => FoldedWords.Contains(words, [.. DayWords(identifier), .. identifier.Words])
                || (FoldedWords.Contains(words, identifier.Words) && (WritesDay(words, identifier) || IsOfDay(day, identifier))),
            SearchIdentifierKind.Memory => (string.Equals(string.Join('-', identifier.Words), entryId, StringComparison.OrdinalIgnoreCase)
                    && (identifier.Part is null || identifier.Part == part))
                || FoldedWords.Contains(words, identifier.Words),
            _ => FoldedWords.Contains(words, identifier.Words),
        };
    }

    /// <summary>Every identifier of <paramref name="text"/> where it writes it, in its order; the same one may come twice.</summary>
    private static List<(int Start, int End, SearchIdentifier Identifier)> Found(string? text)
    {
        var found = new List<(int Start, int End, SearchIdentifier Identifier)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return found;
        }

        // The longest first, so an entry with its day is not also its day and its number, and a run is not a day.
        Add(QualifiedEntry(), m => DateOf(m) is { } day
            ? new SearchIdentifier(SearchIdentifierKind.Entry, m.Value, FoldedWords.Of("P-" + m.Groups["n"].Value), day.Year, day.Month, day.Day)
            : null);
        Add(Run(), m => new SearchIdentifier(SearchIdentifierKind.Run, m.Value, FoldedWords.Of(m.Value)));
        Add(MemoryId(), m => new SearchIdentifier(
            SearchIdentifierKind.Memory,
            m.Value,
            FoldedWords.Of(m.Groups["entry"].Value),
            Part: m.Groups["part"].Success ? int.Parse(m.Groups["part"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null));
        Add(IsoDate(), DayWithItsYear);
        Add(YearSlashDate(), DayWithItsYear);
        Add(DayMonthYear(), DayWithItsYear);
        Add(DottedDate(), DayWithItsYear);
        Add(SlashDate(), m => DateOf(m) is not { } day
            ? null
            : day.Year is not null
                ? Date(m.Value, day.Year, day.Month, day.Day)
                : (DayWord().IsMatch(text.AsSpan(0, m.Index)) || FollowsADay(m.Index)) && !NotADay().IsMatch(text.AsSpan(m.Index + m.Length))
                    ? Date(m.Value, null, day.Month, day.Day)
                    : null);
        Add(WrittenDate(), m =>
        {
            var month = MonthOf(JournalTemplate.Fold(m.Groups["month"].Value));
            var day = int.Parse(m.Groups["d"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            int? year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null;
            return month > 0 && IsDay(year ?? 2000, month, day) ? Date(m.Value, year, month, day) : null;
        });
        Add(PromptNumber(), m => new SearchIdentifier(SearchIdentifierKind.Prompt, m.Value, FoldedWords.Of(m.Value)));
        Add(TaskId(), m => new SearchIdentifier(SearchIdentifierKind.Task, m.Value, FoldedWords.Of(m.Value)));

        found.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return found;

        void Add(Regex pattern, Func<Match, SearchIdentifier?> make)
        {
            foreach (Match match in pattern.Matches(text))
            {
                var (start, end) = (match.Index, match.Index + match.Length);
                if (found.Any(t => start < t.End && t.Start < end) || make(match) is not { } identifier)
                {
                    continue;
                }

                found.Add((start, end, identifier));
            }
        }

        // A day of a list after another one: «los días 3/5 y 7/5», «el 3/5, 4/5 y 7/5».
        bool FollowsADay(int start) => found.Any(f => f.Identifier.Kind == SearchIdentifierKind.Date && f.End <= start && ListSeparator().IsMatch(text.AsSpan(f.End, start - f.End)));
    }

    private static SearchIdentifier? DayWithItsYear(Match match) =>
        DateOf(match) is { Year: not null } day ? Date(match.Value, day.Year, day.Month, day.Day) : null;

    private static SearchIdentifier Date(string text, int? year, int month, int day) =>
        new(SearchIdentifierKind.Date, text, [], year, month, day);

    /// <summary>The day of the groups <c>y</c>, <c>m</c> and <c>d</c> of a match, when it is a real one (a two-digit year is of this century).</summary>
    private static (int? Year, int Month, int Day)? DateOf(Match match)
    {
        int? year = match.Groups["y"].Success ? int.Parse(match.Groups["y"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null;
        if (year is < 100)
        {
            year += 2000;
        }

        var month = int.Parse(match.Groups["m"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["d"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        return IsDay(year ?? 2000, month, day) ? (year, month, day) : null;
    }

    private static bool IsDay(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    /// <summary>The month of a name, folded: the whole one or the short one; 0 for none.</summary>
    private static int MonthOf(string folded) =>
        Array.IndexOf(Months, folded) + 1 is var month and > 0 ? month : ShortMonths.GetValueOrDefault(folded);

    /// <summary>The words of the ISO date of an identifier with a year; none without one.</summary>
    private static string[] DayWords(SearchIdentifier identifier) => identifier.Year is { } year
        ? [year.ToString("D4", CultureInfo.InvariantCulture), identifier.Month.ToString("D2", CultureInfo.InvariantCulture), identifier.Day.ToString("D2", CultureInfo.InvariantCulture)]
        : [];

    /// <summary>Whether <paramref name="day"/> is the day of <paramref name="identifier"/>.</summary>
    private static bool IsOfDay(DateOnly? day, SearchIdentifier identifier) =>
        day is { } of && of.Month == identifier.Month && of.Day == identifier.Day && (identifier.Year is null || of.Year == identifier.Year);

    /// <summary>
    /// Whether <paramref name="words"/> write the day of <paramref name="identifier"/>: as an ISO date (a year of four
    /// digits, then the month and the day), as the date of a run (<c>yyyyMMdd</c>), or as «29 de octubre» («29 de oct.»).
    /// The year must be one (from 1970 to 2999), so a number of the journal followed by a time (<c>P-0004 · 10:15</c>) is
    /// not a day.
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
                && MonthOf(words[i + 2]) is var writtenMonth and > 0
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

    [GeneratedRegex(@"(?<![\w/-])(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})/[Pp]-(?<n>\d{4,9})(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedEntry();

    [GeneratedRegex(@"(?<![\w-])(?:(?:deb|aud|aux)-)?\d{8}-[0-9a-f]{6}(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Run();

    [GeneratedRegex(@"(?<![\w:-])(?:mem:)?(?<entry>mem-[0-9a-f]{16})(?:#(?<part>\d{1,6}))?(?![\w-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MemoryId();

    [GeneratedRegex(@"(?<![\w/.-])(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})(?![\w/-])", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![\w/.-])(?<y>\d{4})/(?<m>\d{1,2})/(?<d>\d{1,2})(?![\w/-])", RegexOptions.CultureInvariant)]
    private static partial Regex YearSlashDate();

    [GeneratedRegex(@"(?<![\w/.-])(?<d>\d{1,2})-(?<m>\d{1,2})-(?<y>\d{4})(?![\w/-])", RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"(?<![\w/.,-])(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?<y>\d{4})(?![\w-]|[.,]\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DottedDate();

    [GeneratedRegex(@"(?<![\w/.-])(?<d>\d{1,2})/(?<m>\d{1,2})(?:/(?<y>\d{4}|\d{2}))?(?![\w/-])", RegexOptions.CultureInvariant)]
    private static partial Regex SlashDate();

    /// <summary>The word a day without its year must follow, right before it: «el», «del», «al», «día» or «días».</summary>
    [GeneratedRegex(@"(?:^|[^\w])(?:el|del|al|d[ií]as?)\s+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayWord();

    /// <summary>What may stand between two days of a list: a comma, «y», «e», or a comma and «y».</summary>
    [GeneratedRegex(@"^\s*(?:,\s*(?:y\s+|e\s+)?|y\s+|e\s+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ListSeparator();

    /// <summary>What after a <c>dd/mm</c> makes it a share and not a day: «de las», «de los» or a number.</summary>
    [GeneratedRegex(@"^\s*(?:de\s+l[ao]s(?!\w)|\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NotADay();

    [GeneratedRegex(@"(?<!\w)(?<d>\d{1,2}) de (?<month>[a-zA-Z]+)\.?(?: de (?<y>\d{4}))?(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex WrittenDate();

    [GeneratedRegex(@"(?<![\w/-])[Pp]-\d{4,9}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex PromptNumber();

    [GeneratedRegex(@"(?<![\w/-])T-?\d{1,4}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex TaskId();
}
