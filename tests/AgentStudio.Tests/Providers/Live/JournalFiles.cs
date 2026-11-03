using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>A prompt entry of the daily Markdown journal: <c>- **HH:mm:ss** · P-0001 · …· «summary»</c>.</summary>
internal sealed record JournalEntryLine(string Id, DateTime LocalTime, string Summary);

/// <summary>An outcome line of the daily Markdown journal: <c>  - Resultado P-0001 · HH:mm:ss · Succeeded · …</c>.</summary>
internal sealed record JournalOutcomeLine(string Id, DateTime LocalTime, string Status);

/// <summary>Reads back what <c>MarkdownDailyJournal</c> wrote in <c>&lt;project&gt;/.agentstudio/bitacora/*.md</c>.</summary>
internal static partial class JournalFiles
{
    public static (IReadOnlyList<JournalEntryLine> Entries, IReadOnlyList<JournalOutcomeLine> Outcomes) Read(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var entries = new List<JournalEntryLine>();
        var outcomes = new List<JournalOutcomeLine>();
        var folder = Path.Combine(projectRoot, ".agentstudio", "bitacora");
        if (!Directory.Exists(folder))
        {
            return (entries, outcomes);
        }

        // One file per local day: a run that crosses midnight writes into two of them.
        foreach (var file in Directory.GetFiles(folder, "*.md").Order(StringComparer.Ordinal))
        {
            var day = DateOnly.ParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var line in File.ReadLines(file))
            {
                if (EntryPattern().Match(line) is { Success: true } entry)
                {
                    entries.Add(new JournalEntryLine(entry.Groups["id"].Value, At(day, entry.Groups["time"].Value), entry.Groups["summary"].Value));
                }
                else if (OutcomePattern().Match(line) is { Success: true } outcome)
                {
                    outcomes.Add(new JournalOutcomeLine(outcome.Groups["id"].Value, At(day, outcome.Groups["time"].Value), outcome.Groups["status"].Value));
                }
            }
        }

        return (entries, outcomes);
    }

    /// <summary>The journal lines of the project (entries and outcomes), for evidence.</summary>
    public static IReadOnlyList<string> ReadLines(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var folder = Path.Combine(projectRoot, ".agentstudio", "bitacora");
        return Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*.md").Order(StringComparer.Ordinal).SelectMany(File.ReadLines).Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal))]
            : [];
    }

    private static DateTime At(DateOnly day, string time) =>
        day.ToDateTime(TimeOnly.ParseExact(time, "HH:mm:ss", CultureInfo.InvariantCulture));

    [GeneratedRegex(@"^- \*\*(?<time>\d{2}:\d{2}:\d{2})\*\* · (?<id>P-\d{4}) · .*«(?<summary>.*)»")]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"^\s+- Resultado (?<id>P-\d{4}) · (?<time>\d{2}:\d{2}:\d{2}) · (?<status>\w+) · ")]
    private static partial Regex OutcomePattern();
}
