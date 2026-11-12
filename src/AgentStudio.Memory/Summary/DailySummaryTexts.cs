using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Localization;

namespace AgentStudio.Memory.Summary;

/// <summary>
/// The daily summary as a coded text (docs/17 D-47 and D-48, ADR-0054 addendum of Phase 14). The summary is anchored when its day
/// closes (<c>DailySummaryService</c> records it as a memory entry of the system) and goes to the agents, so <see cref="DailySummaryBuilder"/>
/// keeps writing it in Spanish, byte for byte. What the screens show is the same text said in the language of the person: each line the
/// builder writes from a template is read back into the code of that template and its figures, and every other line (a prompt of the
/// journal, a tag, a «Hecho» line, a label, an identifier) goes through as it is, because it is what the journal said. The result is a
/// pure function of the text, so it serves the summary of today (not anchored) and the entry of a closed day alike.
/// </summary>
public static partial class DailySummaryTexts
{
    private static readonly (Regex Pattern, string Code, string[] Kinds)[] Templates =
    [
        (Title(), "memory.daily.title", ["day"]),
        (TitleProvisional(), "memory.daily.title-provisional", ["day"]),
        (Requests(), "memory.daily.requests", ["int"]),
        (Results(), "memory.daily.results", ["int"]),
        (Failures(), "memory.daily.failures", ["int"]),
        (DoneLines(), "memory.daily.done-lines", ["int"]),
        (Unrecognized(), "memory.daily.unrecognized", ["int"]),
        (Duration(), "memory.daily.duration", ["seconds"]),
        (Unanswered(), "memory.daily.unanswered", ["int"]),
        (TokensExact(), "memory.daily.tokens-exact", ["figure", "figure"]),
        (TokensPartial(), "memory.daily.tokens-partial", ["figure", "figure", "int", "int"]),
        (MoreFailures(), "memory.daily.more-failures", ["int"]),
        (MoreDone(), "memory.daily.more-done", ["int"]),
    ];

    private static readonly (string Line, string Code)[] Fixed =
    [
        (DailySummaryBuilder.Intro, "memory.daily.intro"),
        (DailySummaryBuilder.NoJournal, "memory.daily.no-journal"),
        ("## Tokens", "memory.daily.tokens"),
        ("## Etiquetas más frecuentes", "memory.daily.tags"),
        (DailySummaryBuilder.NoProductPrompts, "memory.daily.no-product-requests"),
        ("- Ninguna.", "memory.daily.no-tags"),
    ];

    /// <summary>
    /// The text of a daily summary as a coded text, line by line; a text that is not one the builder wrote (a fake, an entry of another
    /// version) is its lines as they are, so what is shown is never less than what was anchored.
    /// </summary>
    public static UserText Coded(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return markdown.Split('\n')
            .Select(Line)
            .Aggregate((joined, line) => new UserText("memory.join-lines", [joined, line]));
    }

    /// <summary>The coded topic of the daily summary of a day (<c>Resumen del 2026-10-15</c>), only when it is exactly that.</summary>
    public static UserText? TryTopic(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        const string Start = "Resumen del ";
        return topic.StartsWith(Start, StringComparison.Ordinal)
            && DateOnly.TryParseExact(topic.AsSpan(Start.Length), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            && string.Equals(topic, Start + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                ? new UserText("memory.topic.daily-summary", [day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)])
                : null;
    }

    private static UserText Line(string raw)
    {
        var line = raw.TrimEnd('\r');
        if (line.Length != raw.Length)
        {
            return new UserText("memory.raw", [raw]);
        }

        foreach (var (text, code) in Fixed)
        {
            if (string.Equals(line, text, StringComparison.Ordinal))
            {
                return new UserText(code);
            }
        }

        foreach (var (pattern, code, kinds) in Templates)
        {
            if (pattern.Match(line) is { Success: true } match && Arguments(match, kinds) is { } arguments)
            {
                return new UserText(code, arguments);
            }
        }

        return new UserText("memory.raw", [line]);
    }

    /// <summary>The figures of a line, read as the builder wrote them (Spanish numbers: <c>1.200</c> and <c>12,3</c>); null if one does not read.</summary>
    private static List<object>? Arguments(Match match, string[] kinds)
    {
        var arguments = new List<object>(kinds.Length);
        for (var i = 0; i < kinds.Length; i++)
        {
            var text = match.Groups[i + 1].Value;
            switch (kinds[i])
            {
                case "day":
                    arguments.Add(text);
                    break;
                case "int" when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number):
                    arguments.Add(number);
                    break;
                case "figure" when long.TryParse(text.Replace(".", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var figure):
                    arguments.Add(figure);
                    break;
                case "seconds" when decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds):
                    arguments.Add(seconds);
                    break;
                default:
                    return null;
            }
        }

        return arguments;
    }

    [GeneratedRegex(@"^# Resumen del (\d{4}-\d{2}-\d{2})$")]
    private static partial Regex Title();

    [GeneratedRegex(@"^# Resumen del (\d{4}-\d{2}-\d{2}) \(provisional\)$")]
    private static partial Regex TitleProvisional();

    [GeneratedRegex(@"^## Prompts: (\d+)$")]
    private static partial Regex Requests();

    [GeneratedRegex(@"^## Resultados: (\d+)$")]
    private static partial Regex Results();

    [GeneratedRegex(@"^## Fallos y límites: (\d+)$")]
    private static partial Regex Failures();

    [GeneratedRegex(@"^## Líneas «Hecho»: (\d+)$")]
    private static partial Regex DoneLines();

    [GeneratedRegex(@"^## Líneas no reconocidas: (\d+)$")]
    private static partial Regex Unrecognized();

    [GeneratedRegex(@"^- Duración total: (\d+,\d) s$")]
    private static partial Regex Duration();

    [GeneratedRegex(@"^- Sin resultado en la bitácora: (\d+)$")]
    private static partial Regex Unanswered();

    [GeneratedRegex(@"^- Entrada (\d{1,3}(?:\.\d{3})*) → salida (\d{1,3}(?:\.\d{3})*) \(exactos\)$")]
    private static partial Regex TokensExact();

    [GeneratedRegex(@"^- Entrada (\d{1,3}(?:\.\d{3})*) → salida (\d{1,3}(?:\.\d{3})*) \(parcial: exactos en (\d+) de (\d+) prompts del producto\)$")]
    private static partial Regex TokensPartial();

    [GeneratedRegex(@"^- … y (\d+) más\.$")]
    private static partial Regex MoreFailures();

    [GeneratedRegex(@"^- … y (\d+) líneas «Hecho» más en la bitácora\.$")]
    private static partial Regex MoreDone();
}
