using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;

namespace AgentStudio.Memory.Semantic;

/// <summary>
/// What of an entry of the journal the meaning of the semantic index is computed from (ADR-0047, addenda of 2026-11-03 and
/// 2026-11-04): a clean view of its anchored text, without the template the two writers of the journal give every entry. The
/// template (the time, the number <c>P-NNNN</c>, who, the CLI and model, the tags, and the result line with its status,
/// duration, tokens and the anchor of its launch) is the same in every entry, so it pulls the meanings of every entry
/// together. The words of the index still search the whole text (dates, roles, numbers and runs are what a person asks for
/// by name). Deterministic and pure: the index computes the view again from the anchored entry, never stores it.
/// <list type="bullet">
/// <item>The line of an entry, as the app or the hook writes it (<c>- **HH:mm:ss** · P-NNNN · … · «summary» (…)</c>, or
/// <c>- **HH:mm:ss** · sesión `…` · …</c>), keeps its summary: the text between its first <c>«</c> and its last <c>»</c>.</item>
/// <item>A result line as the app writes it (<c>- Resultado P-NNNN · HH:mm:ss · status · …</c>) keeps only its free note:
/// its time, status, duration, tokens and the anchor of its launch (<c>ejecución</c>, <c>tarea</c>, <c>intento</c>,
/// <c>entrada</c>) go.</item>
/// <item>A «Hecho» line keeps what follows its label (<c>Hecho:</c>, <c>Hecho (en curso):</c>…).</item>
/// <item>Any other line keeps its text, even one that starts like them («Resultado final…», «**Nota** · …»). List markers
/// and the escapes of Markdown go everywhere.</item>
/// </list>
/// A text whose view is empty is used whole, so no entry is ever without a meaning. The words of the template are those of
/// <see cref="JournalTemplate"/>, the one list the search shares.
/// </summary>
public static partial class JournalSearchText
{
    /// <summary>The version of these rules: it goes into the fingerprint of the vectors, so a change computes them again.</summary>
    public const int Version = 2;

    private static readonly HashSet<string> Statuses = new(JournalTemplate.Statuses, StringComparer.Ordinal);

    /// <summary>The clean view of the content of a journal entry, its kept lines joined by <c>\n</c>; the content itself when nothing is left.</summary>
    public static string Of(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var kept = new List<string>();
        foreach (var raw in content.Split('\n'))
        {
            var line = ListMarker().Replace(raw.Trim(), string.Empty);
            var clean = line.StartsWith(JournalTemplate.Done, StringComparison.Ordinal) && Done().Match(line) is { Success: true } done ? done.Groups["text"].Value
                : ResultLine().Match(line) is { Success: true } result && Statuses.Contains(result.Groups["status"].Value) ? Note(line)
                : EntryLine().IsMatch(line) ? Summary(line)
                : line;
            clean = Unescape(clean).Trim();
            if (clean.Length > 0)
            {
                kept.Add(clean);
            }
        }

        return kept.Count == 0 ? content : string.Join('\n', kept);
    }

    /// <summary>The summary of the line of an entry: between its first «, and its last »; nothing without them.</summary>
    private static string Summary(string line)
    {
        var open = line.IndexOf('«', StringComparison.Ordinal);
        var close = line.LastIndexOf('»');
        return open >= 0 && close > open ? line[(open + 1)..close] : string.Empty;
    }

    /// <summary>The free note of a result line: what is left of its parts without its template and the anchor of its launch.</summary>
    private static string Note(string line)
    {
        var parts = line.Split(" · ", StringSplitOptions.TrimEntries).Skip(1);
        return string.Join(" · ", parts.Where(p => p.Length > 0
            && !Statuses.Contains(p)
            && !Time().IsMatch(p)
            && !Duration().IsMatch(p)
            && !p.StartsWith(JournalTemplate.Tokens + " ", StringComparison.Ordinal)
            && !p.StartsWith(JournalTemplate.Run + " `", StringComparison.Ordinal)
            && !p.StartsWith(JournalTemplate.Task + " `", StringComparison.Ordinal)
            && !p.StartsWith(JournalTemplate.Entry + " `", StringComparison.Ordinal)
            && !Attempt().IsMatch(p)));
    }

    /// <summary>The text without the backslashes the writer of the journal puts before Markdown's characters.</summary>
    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var unescaped = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
            {
                continue;
            }

            unescaped.Append(text[i]);
        }

        return unescaped.ToString();
    }

    [GeneratedRegex(@"^(?:[-*+]\s+)+")]
    private static partial Regex ListMarker();

    [GeneratedRegex("^" + JournalTemplate.Done + @"(?:\s*\([^)]*\))?\s*:\s*(?<text>.*)$")]
    private static partial Regex Done();

    /// <summary>The line of an entry as the app (<c>**HH:mm:ss** · P-NNNN · </c>) or the hook (<c>**HH:mm:ss** · sesión `…` · </c>) writes it.</summary>
    [GeneratedRegex(@"^\*\*\d{2}:\d{2}:\d{2}\*\* · (?:P-\d+|" + JournalTemplate.Session + @" `[^`]+`) · ")]
    private static partial Regex EntryLine();

    /// <summary>A result line as the app writes it: <c>Resultado P-NNNN · HH:mm:ss · status · </c>, its status one of <see cref="JournalTemplate.Statuses"/>.</summary>
    [GeneratedRegex("^" + JournalTemplate.Result + @" P-\d+ · \d{2}:\d{2}:\d{2} · (?<status>[A-Za-z]+)(?: · |$)")]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(?::\d{2})?$")]
    private static partial Regex Time();

    [GeneratedRegex(@"^[\d.,]+\s*s$")]
    private static partial Regex Duration();

    [GeneratedRegex("^" + JournalTemplate.Attempt + @" \d+$")]
    private static partial Regex Attempt();
}
