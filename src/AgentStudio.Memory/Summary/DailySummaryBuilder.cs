using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Memory.Export;

namespace AgentStudio.Memory.Summary;

/// <summary>
/// Builds the deterministic summary of a day of the journal (docs/14 D-31; ADR-0025). It understands the lines of the two
/// writers of the journal and counts and quotes them; it never interprets free text.
/// <list type="bullet">
/// <item><b>The product</b> (<c>MarkdownDailyJournal</c>): <c>- **HH:mm:ss** · P-NNNN · actor · proveedor/modelo · #tags · «resumen»</c>
/// and, later and not always right below, <c>  - Resultado P-NNNN · HH:mm:ss · estado · 12,3 s · tokens 1.200 → 340 (exactos)</c>;
/// a result belongs to the last prompt of its id above it.</item>
/// <item><b>The development hook</b> (<c>.claude/hooks/bitacora.ps1</c>): <c>- **HH:mm:ss** · sesión `xxxxxxxx` · #tags · «extracto»</c>,
/// also with a word instead of the time and without a session, as the entries journaled by hand; and the «Hecho» lines
/// Claude adds under them (<c>Hecho:</c>, <c>Hecho (en curso):</c>, <c>Hecho (00:27, cierre):</c>…).</item>
/// <item>Headings, the two lines under the title (the hook's, or the app's since V31) and blank lines are part of the
/// format. Anything else (notes, broken lines) is only counted.</item>
/// </list>
/// The summary lists the prompts by actor, provider and model; the results by status, with their total duration; the
/// tokens, exact only when every prompt of the product gave exact figures and «parcial» otherwise; the failures and
/// limits; the five most frequent tags; the «Hecho» lines, literal and escaped (each one cut at
/// <see cref="MaxDoneLine"/> characters); and how many lines were not recognised. It is Markdown with every piece of the
/// journal escaped, and it fits in a memory entry (<see cref="MemoryContent.MaxCharacters"/>): the «Hecho» lines that do
/// not fit are counted at the end of their list.
/// </summary>
internal static partial class DailySummaryBuilder
{
    /// <summary>The longest «Hecho» line the summary quotes; a longer one is cut with an ellipsis.</summary>
    public const int MaxDoneLine = 500;

    private const int MaxFailures = 20;
    private const int TopTags = 5;
    /// <summary>The lines of the summary that say something of the system are read back into codes by <see cref="DailySummaryTexts"/>, which names these three.</summary>
    internal const string Intro = "Resumen determinista de la bitácora del día: solo cuenta y cita, no interpreta.";
    internal const string NoJournal = "No hay bitácora de este día.";
    internal const string NoProductPrompts = "- Sin prompts del producto: el hook de desarrollo no registra tokens.";
    private const string Preamble = "Registro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.";
    private const string DonePreamble = "Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.";

    /// <summary>The lines under the title: the hook's, which the app also wrote before V31, and the app's.</summary>
    private static readonly HashSet<string> Preambles = new(StringComparer.Ordinal)
    {
        Preamble,
        DonePreamble,
        Journal.MarkdownDailyJournal.Preamble,
        Journal.MarkdownDailyJournal.ResultPreamble,
    };

    private static readonly string[] Statuses = ["Succeeded", "Failed", "Cancelled", "LimitReached", "RateLimited"];
    private static readonly string[] Failures = ["Failed", "LimitReached", "RateLimited"];

    // The Spanish numbers the journal writes (1.200 / 12,3), independent of the machine culture.
    private static readonly NumberFormatInfo Spanish = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    public static DailySummaryText Build(DateOnly day, bool provisional, string? journal)
    {
        var head = new StringBuilder()
            .Append("# Resumen del ").Append(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(provisional ? " (provisional)" : string.Empty).Append('\n')
            .Append('\n').Append(Intro).Append('\n');
        if (journal is null)
        {
            return new DailySummaryText(head.Append('\n').Append(NoJournal).Append('\n').ToString(), 0, 0);
        }

        var parsed = Parse(journal, day);
        var done = parsed.Done.Select(line => "- " + MarkdownText.Escape(Limit(line, MaxDoneLine))).ToList();
        var shown = done.Count;
        var text = Render(head.ToString(), parsed, done);
        while (text.Length > MemoryContent.MaxCharacters && shown > 0)
        {
            shown--;
            text = Render(head.ToString(), parsed, [.. done.Take(shown), $"- … y {done.Count - shown} líneas «Hecho» más en la bitácora."]);
        }

        return new DailySummaryText(text, parsed.Prompts.Count, parsed.Unrecognized);
    }

    private static string Render(string head, ParsedJournal parsed, IReadOnlyList<string> done)
    {
        var text = new StringBuilder(head);
        var prompts = parsed.Prompts
            .GroupBy(p => p.Label, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"- {MarkdownText.Escape(g.Key)}: {g.Count()}");
        Section(text, $"Prompts: {parsed.Prompts.Count}", prompts);

        var unanswered = parsed.Prompts.Count(p => p.Product && !p.Answered);
        var results = parsed.Outcomes
            .GroupBy(o => o.Status, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"- {g.Key}: {g.Count()}")
            .ToList();
        if (parsed.Outcomes.Count > 0)
        {
            results.Add($"- Duración total: {parsed.Outcomes.Sum(o => o.Seconds).ToString("F1", Spanish)} s");
        }

        if (unanswered > 0)
        {
            results.Add($"- Sin resultado en la bitácora: {unanswered}");
        }

        Section(text, $"Resultados: {parsed.Outcomes.Count}", results);
        Section(text, "Tokens", [Tokens(parsed, unanswered)]);

        var failures = parsed.Outcomes.Where(o => Failures.Contains(o.Status, StringComparer.Ordinal)).ToList();
        var failureLines = failures
            .Take(MaxFailures)
            .Select(o => $"- {o.Id} · {o.Time} · {o.Status} · {MarkdownText.Escape(o.Actor ?? "-")}")
            .ToList();
        if (failures.Count > MaxFailures)
        {
            failureLines.Add($"- … y {failures.Count - MaxFailures} más.");
        }

        Section(text, $"Fallos y límites: {failures.Count}", failureLines);

        var tags = parsed.Prompts
            .SelectMany(p => p.Tags)
            .Where(t => !string.Equals(t, "#prompt", StringComparison.Ordinal))
            .GroupBy(t => t, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(TopTags)
            .Select(g => $"- {MarkdownText.Escape(g.Key)}: {g.Count()}")
            .ToList();
        Section(text, "Etiquetas más frecuentes", tags.Count > 0 ? tags : ["- Ninguna."]);
        Section(text, $"Líneas «Hecho»: {parsed.Done.Count}", done);
        Section(text, $"Líneas no reconocidas: {parsed.Unrecognized}", []);
        return text.ToString();
    }

    /// <summary>The token sums of the results: exact only when every prompt of the product gave exact figures.</summary>
    private static string Tokens(ParsedJournal parsed, int unanswered)
    {
        var prompts = parsed.Outcomes.Count + unanswered;
        if (prompts == 0)
        {
            return NoProductPrompts;
        }

        var input = parsed.Outcomes.Sum(o => o.Input ?? 0).ToString("N0", Spanish);
        var output = parsed.Outcomes.Sum(o => o.Output ?? 0).ToString("N0", Spanish);
        var exact = parsed.Outcomes.Count(o => o.Exact && o.Input is not null && o.Output is not null);
        return exact == prompts
            ? $"- Entrada {input} → salida {output} (exactos)"
            : $"- Entrada {input} → salida {output} (parcial: exactos en {exact} de {prompts} prompts del producto)";
    }

    private static void Section(StringBuilder text, string title, IEnumerable<string> lines)
    {
        text.Append('\n').Append("## ").Append(title).Append('\n');
        var items = lines.ToList();
        if (items.Count > 0)
        {
            text.Append('\n').AppendJoin('\n', items).Append('\n');
        }
    }

    private static ParsedJournal Parse(string journal, DateOnly day)
    {
        var parsed = new ParsedJournal();
        foreach (var raw in journal.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || Heading().IsMatch(line) || Preambles.Contains(line))
            {
                continue;
            }

            if (ProductEntry().Match(line) is { Success: true } product)
            {
                parsed.Prompts.Add(new JournalPrompt(
                    product.Groups["id"].Value,
                    product.Groups["actor"].Value,
                    $"{product.Groups["actor"].Value} · {product.Groups["model"].Value}",
                    product.Groups["tags"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    Product: true));
            }
            else if (DevelopmentEntry().Match(line) is { Success: true } development)
            {
                var session = development.Groups["session"];
                parsed.Prompts.Add(new JournalPrompt(
                    null,
                    null,
                    session.Success ? $"Claude Code · sesión {session.Value}" : "Claude Code · sin sesión",
                    development.Groups["tags"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    Product: false));
            }
            else if (Outcome().Match(line) is { Success: true } outcome && Statuses.Contains(outcome.Groups["status"].Value, StringComparer.Ordinal))
            {
                parsed.Outcomes.Add(OutcomeOf(outcome, line, day, parsed.Prompts));
            }
            else if (Done().Match(line) is { Success: true } done)
            {
                parsed.Done.Add(done.Groups["line"].Value);
            }
            else
            {
                parsed.Unrecognized++;
            }
        }

        return parsed;
    }

    /// <summary>
    /// A result line, which answers the last prompt of its id above it (a result may come later than the next entries). One
    /// that names an entry of another day (<c>· entrada `AAAA-MM-DD/P-NNNN`</c>, docs/17 D-02: its agent ended after
    /// midnight) is a result of this day that answers none of its prompts.
    /// </summary>
    private static JournalOutcome OutcomeOf(Match outcome, string line, DateOnly day, List<JournalPrompt> prompts)
    {
        var id = outcome.Groups["id"].Value;
        var elsewhere = JournalResultAnchor.TryParse(line, out _, out var entry) && entry is { } named && named.Day != day;
        var prompt = elsewhere ? null : prompts.LastOrDefault(p => p.Product && string.Equals(p.Id, id, StringComparison.Ordinal));
        if (prompt is not null)
        {
            prompt.Answered = true;
        }

        return new JournalOutcome(
            id,
            outcome.Groups["time"].Value,
            outcome.Groups["status"].Value,
            decimal.Parse(outcome.Groups["seconds"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            Count(outcome.Groups["in"]),
            Count(outcome.Groups["out"]),
            string.Equals(outcome.Groups["label"].Value, "exactos", StringComparison.Ordinal),
            prompt?.Actor);
    }

    /// <summary>A figure as the journal writes it (<c>18.300</c>), or null for <c>n/d</c> and when there is none.</summary>
    private static long? Count(Group figure) =>
        figure.Success && !string.Equals(figure.Value, "n/d", StringComparison.Ordinal)
            ? long.Parse(figure.Value.Replace(".", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture)
            : null;

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"^#{1,6} ")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^- \*\*(?<time>\d{2}:\d{2}:\d{2})\*\* · (?<id>P-\d{4,}) · (?<actor>.+?) · (?<model>\S+) · (?<tags>#\S+(?: #\S+)*) · «(?<summary>.*)»(?: \(.*\))?$")]
    private static partial Regex ProductEntry();

    [GeneratedRegex(@"^- \*\*(?<time>[^*]+)\*\* · (?:sesión `(?<session>[^`]*)` · )?(?<tags>#\S+(?: #\S+)*) · «(?<excerpt>.*)»$")]
    private static partial Regex DevelopmentEntry();

    [GeneratedRegex(@"^\s*- Resultado (?<id>P-\d{4,}) · (?<time>\d{2}:\d{2}:\d{2}) · (?<status>[A-Za-z]+) · (?<seconds>\d+(?:,\d+)?) s · (?:tokens no disponibles|tokens (?<in>\d{1,3}(?:\.\d{3})*|n/d) → (?<out>\d{1,3}(?:\.\d{3})*|n/d) \((?<label>exactos|estimados)\))(?: · .*)?$")]
    private static partial Regex Outcome();

    [GeneratedRegex(@"^\s*- (?<line>Hecho(?: \(.*?\))?:(?: .*)?)$")]
    private static partial Regex Done();

    private sealed class ParsedJournal
    {
        public List<JournalPrompt> Prompts { get; } = [];

        public List<JournalOutcome> Outcomes { get; } = [];

        public List<string> Done { get; } = [];

        public int Unrecognized { get; set; }
    }

    private sealed record JournalPrompt(string? Id, string? Actor, string Label, IReadOnlyList<string> Tags, bool Product)
    {
        public bool Answered { get; set; }
    }

    private sealed record JournalOutcome(string Id, string Time, string Status, decimal Seconds, long? Input, long? Output, bool Exact, string? Actor);
}

/// <summary>The text of a daily summary, the prompts it counted and the lines it did not recognise.</summary>
internal sealed record DailySummaryText(string Text, int Prompts, int UnrecognizedLines);
