using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Common;
using AgentStudio.Memory.Journal;

namespace AgentStudio.Memory.Derivation;

/// <summary>What wrote an entry of the journal, by its format.</summary>
internal enum JournalWriter
{
    /// <summary>
    /// <c>MarkdownDailyJournal</c>: <c>- **HH:mm:ss** · P-NNNN · …</c>. Its lines are its own line and its result lines, the
    /// only lines Agent Studio writes.
    /// </summary>
    Product,

    /// <summary>The development hook, or a person in its format: <c>- **HH:mm:ss** · sesión `xxxxxxxx` · …</c>, with the lines under it.</summary>
    Hook,

    /// <summary>The lines under an entry of the product that are not its results (a «Hecho» line, a note): never the product's.</summary>
    Notes,
}

/// <summary>
/// One entry of the journal as the derivation anchors it (docs/17 D-02): its lines, in order, and what its line says.
/// <see cref="Text"/> is the lines joined by <c>\n</c>, as normalized by <see cref="JournalEntries.Lines"/>; its SHA-256 is the
/// identity of the entry (<see cref="Source"/>) and, for an entry of the product with its result, what the receipt of its
/// launch names (<see cref="JournalReceipt"/>), so the same text gives the same source on any machine.
/// </summary>
/// <param name="Day">The day of the file of its line.</param>
/// <param name="Line">The index of its first line among the lines of its day: the order of the entries of a day.</param>
/// <param name="Key">What names it in its day (its number, or its time and session, <c>(2)</c>… for a repeated one), for its topic.</param>
/// <param name="Sequence">The number of an entry of the product (<c>P-NNNN</c>); 0 otherwise.</param>
/// <param name="Results">The result lines of an entry of the product, by its number or by the entry they name.</param>
internal sealed record JournalEntryText(DateOnly Day, int Line, JournalWriter Writer, string Key, int Sequence, IReadOnlyList<string> Lines, IReadOnlyList<string> Results)
{
    /// <summary>The text of the entry: its lines joined by <c>\n</c>.</summary>
    public string Text { get; } = string.Join('\n', Lines);

    /// <summary>The SHA-256 of <see cref="Text"/>.</summary>
    public string Sha256 => ContentHash.Sha256Hex(Text);

    /// <summary>Its source as memory: its day and the SHA-256 of its text (<see cref="MemorySources.Journal"/>).</summary>
    public string Source => MemorySources.Journal(Day, Sha256);
}

/// <summary>
/// Reads the days of the journal into their entries (docs/17 D-02 and the addendum of 2026-11-03 to ADR-0047; docs/11), with
/// the formats of its two writers that <c>DailySummaryBuilder</c> also reads: the product (<c>MarkdownDailyJournal</c>) and
/// the development hook (<c>.claude/hooks/bitacora.ps1</c>), whose entries share the files.
/// <list type="bullet">
/// <item><b>Normalized.</b> The byte order mark goes, and each line loses its end of line and its trailing blanks, so a file
/// checked out with CRLF or LF, or saved by an editor that trims, gives the same entries and the same hashes.</item>
/// <item><b>An entry of the product</b> has its line and its result lines (<c>  - Resultado P-NNNN · …</c>), and nothing
/// else. A result that names its entry (<c>· entrada `2026-11-03/P-0001`</c>) belongs to that entry, even in the file of
/// another day: after midnight the product writes it in the file of the day its agent ended. One that names none belongs
/// to the last line of its number above it, in its file, as in the daily summary.</item>
/// <item><b>An entry of the hook</b> has its line and the lines indented under it (and the «Hecho» lines, indented or not),
/// up to a heading or a line of neither format.</item>
/// <item><b>The notes of an entry of the product</b>: the lines under it that are not its results (a «Hecho» line, a note,
/// a result of no entry) are an entry of their own, <see cref="JournalWriter.Notes"/>, never part of what Agent Studio
/// wrote: they follow the trust of the journal, never <c>System</c>.</item>
/// <item><b>Not entries:</b> headings, the two lines under the title of either writer, blank lines and any other line (a
/// broken line, a note out of an entry). A line longer than <see cref="MaxLineLength"/> is not read at all: the journal is
/// content of the repository, and the patterns must not be fed a planted giant line.</item>
/// </list>
/// Nothing here depends on the clock, the zone or the culture of the machine: the day is the one of the file.
/// </summary>
internal static partial class JournalEntries
{
    /// <summary>The longest line read as part of the journal; a longer one is skipped as an unknown line.</summary>
    public const int MaxLineLength = 4000;

    /// <summary>The longest time or session of an entry of the hook quoted in its key.</summary>
    private const int MaxKeyPart = 40;

    /// <summary>The lines under the title of a day, of both writers: format, not entries.</summary>
    private static readonly HashSet<string> Preambles = new(StringComparer.Ordinal)
    {
        "Registro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.",
        "Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.",
        MarkdownDailyJournal.Preamble,
        MarkdownDailyJournal.ResultPreamble,
    };

    /// <summary>The statuses a result line of the product says (<c>AgentRunStatus</c>).</summary>
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "Succeeded", "Failed", "Cancelled", "LimitReached", "RateLimited" };

    /// <summary>The lines of <paramref name="text"/>, normalized: no byte order mark, no end of line, no trailing blanks.</summary>
    public static IReadOnlyList<string> Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        return [.. body.Split('\n').Select(l => l.TrimEnd())];
    }

    /// <summary>
    /// The entries of the days of the journal (<paramref name="days"/>: each day and the text of its file), each day in the
    /// order of its lines; a result written in the file of a later day joins the entry it names.
    /// </summary>
    public static IReadOnlyList<JournalEntryText> Parse(IEnumerable<(DateOnly Day, string Text)> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var read = days.OrderBy(d => d.Day).Select(d => ReadDay(d.Day, Lines(d.Text))).ToList();
        var byDay = read.ToDictionary(r => r.Day);

        // A result in a later file that names its entry: it joins that entry when its day has a line of that number;
        // otherwise it stays where it is, as a line of no entry.
        foreach (var (late, owner, host) in read.SelectMany(r => r.Later))
        {
            if (byDay.TryGetValue(owner.Day, out var earlier) && earlier.Products.TryGetValue(owner.Sequence, out var entry))
            {
                entry.Add(late, result: true);
            }
            else
            {
                host?.AddUnder(late);
            }
        }

        return [.. read.SelectMany(r => r.Build())];
    }

    /// <summary>The number of an entry of the product (<c>P-0007</c> is 7), or 0 when <paramref name="id"/> is not one.</summary>
    public static int SequenceOf(string id) =>
        id.StartsWith("P-", StringComparison.Ordinal) && int.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;

    /// <summary>The number of the entry of a result line of the product (<c>  - Resultado P-NNNN · …</c> with a status it writes), or 0.</summary>
    public static int ResultSequence(string line) =>
        Matched(Result(), line) is { } result && Statuses.Contains(result.Groups["status"].Value) ? SequenceOf(result.Groups["id"].Value) : 0;

    private static DayRead ReadDay(DateOnly day, IReadOnlyList<string> lines)
    {
        var read = new DayRead(day);
        Draft? host = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || Preambles.Contains(line))
            {
                continue;
            }

            if (line.Length > MaxLineLength || Matched(Heading(), line) is not null)
            {
                host = null;
                continue;
            }

            if (Matched(ProductHeader(), line) is { } product)
            {
                var sequence = SequenceOf(product.Groups["id"].Value);
                host = read.Add(new Draft(day, i, JournalWriter.Product, product.Groups["id"].Value, sequence, line));
                read.Products[sequence] = host;
            }
            else if (Matched(HookHeader(), line) is { } development)
            {
                var session = development.Groups["session"];
                var name = KeyPart(development.Groups["time"].Value) + (session.Success ? " · sesión " + KeyPart(session.Value) : string.Empty);
                host = read.Add(new Draft(day, i, JournalWriter.Hook, name, 0, line));
            }
            else if (ResultSequence(line) is > 0 and var sequence)
            {
                // A result names its entry with its day since the addendum of 2026-11-03; before, it only had its number.
                JournalResultAnchor.TryParse(line, out _, out var named);
                var owner = named ?? new JournalEntryId(day, sequence);
                if (owner.Sequence != sequence)
                {
                    host?.AddUnder((day, i, line));
                }
                else if (owner.Day == day)
                {
                    if (read.Products.TryGetValue(sequence, out var entry))
                    {
                        entry.Add((day, i, line), result: true);
                    }
                    else
                    {
                        host?.AddUnder((day, i, line));
                    }
                }
                else if (owner.Day < day)
                {
                    read.Later.Add(((day, i, line), owner, host));
                }
                else
                {
                    host?.AddUnder((day, i, line));
                }
            }
            else if (char.IsWhiteSpace(line[0]) || Matched(Done(), line) is not null)
            {
                host?.AddUnder((day, i, line));
            }
            else
            {
                host = null;
            }
        }

        return read;
    }

    /// <summary>A free piece of a line of the hook (its time may be a word, its session anything) as a short label.</summary>
    private static string KeyPart(string text)
    {
        var collapsed = Blanks().Replace(text, " ").Trim();
        return collapsed.Length <= MaxKeyPart ? collapsed : collapsed[..(MaxKeyPart - 1)] + "…";
    }

    /// <summary>The match of <paramref name="pattern"/>, or null; a pattern that runs out of time is no match.</summary>
    private static Match? Matched(Regex pattern, string line)
    {
        try
        {
            return pattern.Match(line) is { Success: true } match ? match : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^#{1,6} ", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Heading();

    // The lines of the two writers, as DailySummaryBuilder reads them.
    [GeneratedRegex(@"^- \*\*(?<time>\d{2}:\d{2}:\d{2})\*\* · (?<id>P-\d{4,}) · (?<actor>.+?) · (?<model>\S+) · (?<tags>#\S+(?: #\S+)*) · «(?<summary>.*)»(?: \(.*\))?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ProductHeader();

    [GeneratedRegex(@"^- \*\*(?<time>[^*]+)\*\* · (?:sesión `(?<session>[^`]*)` · )?(?<tags>#\S+(?: #\S+)*) · «(?<excerpt>.*)»$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HookHeader();

    [GeneratedRegex(@"^\s*- Resultado (?<id>P-\d{4,}) · (?<time>\d{2}:\d{2}:\d{2}) · (?<status>[A-Za-z]+) · (?<seconds>\d+(?:,\d+)?) s · (?:tokens no disponibles|tokens (?<in>\d{1,3}(?:\.\d{3})*|n/d) → (?<out>\d{1,3}(?:\.\d{3})*|n/d) \((?<label>exactos|estimados)\))(?: · .*)?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Result();

    [GeneratedRegex(@"^\s*- (?<line>Hecho(?: \(.*?\))?:(?: .*)?)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Done();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Blanks();

    /// <summary>One day as read: its entries in order, its entries of the product by number, and the results it holds of earlier days.</summary>
    private sealed class DayRead(DateOnly day)
    {
        private readonly List<Draft> _drafts = [];

        public DateOnly Day { get; } = day;

        /// <summary>The last line of each number of the product read so far: where a result of that number goes.</summary>
        public Dictionary<int, Draft> Products { get; } = [];

        /// <summary>The results in this file of entries of earlier days, with the entry they name and where they stand.</summary>
        public List<((DateOnly Day, int Line, string Text) Line, JournalEntryId Owner, Draft? Host)> Later { get; } = [];

        public Draft Add(Draft draft)
        {
            _drafts.Add(draft);
            return draft;
        }

        /// <summary>The entries of the day, with a key of their own: a repeated name gets its place among those that share it.</summary>
        public IEnumerable<JournalEntryText> Build()
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var draft in _drafts)
            {
                var key = Keyed(draft.Name);
                if (draft.Build(key) is { } entry)
                {
                    yield return entry;
                }

                if (draft.Notes(key + " · notas") is { } notes)
                {
                    yield return notes;
                }
            }

            string Keyed(string name)
            {
                var count = seen[name] = seen.GetValueOrDefault(name) + 1;
                return count == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name} ({count})");
            }
        }
    }

    /// <summary>An entry being read: its line and the lines that join it, by their place (day and index).</summary>
    private sealed class Draft(DateOnly day, int line, JournalWriter writer, string name, int sequence, string header)
    {
        private readonly List<(DateOnly Day, int Line, string Text)> _lines = [];
        private readonly List<(DateOnly Day, int Line, string Text)> _notes = [];
        private readonly List<string> _results = [];

        public string Name { get; } = name;

        public void Add((DateOnly Day, int Line, string Text) line, bool result)
        {
            _lines.Add(line);
            if (result)
            {
                _results.Add(line.Text);
            }
        }

        /// <summary>A line under this entry that is not one of its results: the hook's own, or a note of an entry of the product.</summary>
        public void AddUnder((DateOnly Day, int Line, string Text) line)
        {
            if (writer == JournalWriter.Product)
            {
                _notes.Add(line);
            }
            else
            {
                _lines.Add(line);
            }
        }

        public JournalEntryText Build(string key) =>
            new(day, line, writer, key, sequence, [header, .. Ordered(_lines)], [.. _results]);

        /// <summary>The notes under an entry of the product, as an entry of their own; null when there are none.</summary>
        public JournalEntryText? Notes(string key) =>
            _notes.Count == 0 ? null : new(day, _notes.Min(n => n.Line), JournalWriter.Notes, key, 0, [.. Ordered(_notes)], []);

        private static IEnumerable<string> Ordered(List<(DateOnly Day, int Line, string Text)> lines) =>
            lines.OrderBy(l => l.Day).ThenBy(l => l.Line).Select(l => l.Text);
    }
}
