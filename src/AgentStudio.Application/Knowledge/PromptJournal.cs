using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Knowledge;

/// <summary>One prompt launched by Agent Studio to an agent (the orchestrator is an agent too).</summary>
public sealed record PromptJournalEntry(
    DateTimeOffset At,
    string Actor,
    ProviderId? Provider,
    string? Model,
    string Summary,
    IReadOnlyList<string> Tags,
    string? TaskId = null,
    string? DebateId = null)
{
    /// <summary>
    /// The <c>AgentRunStarted</c> the launch was announced with (<see cref="AgentRunRequest.Anchor"/>). The journal then names
    /// the entry with its day in its own line (<c>(… · entrada 2026-11-03/P-0001)</c>), as its result line will
    /// (<see cref="JournalResultAnchor"/>, docs/17 D-02 and its addendum of 2026-11-03), and keeps the line to give the receipt
    /// of the entry when the result is written (<see cref="IPromptJournal.RecordReceiptedOutcomeAsync"/>). Null writes the line
    /// as before.
    /// </summary>
    public AgentRunAnchor? Anchor { get; init; }
}

/// <summary>How a journaled prompt ended.</summary>
public sealed record PromptOutcome(
    DateTimeOffset At,
    AgentRunStatus Status,
    TimeSpan Duration,
    MetricValue<long> InputTokens,
    MetricValue<long> OutputTokens,
    string? Note = null)
{
    /// <summary>
    /// The <c>AgentRunStarted</c> the launch was announced with (<see cref="AgentRunRequest.Anchor"/>): the journal writes it
    /// at the very end of the result line (<see cref="JournalResultAnchor"/>). Null writes the line as before.
    /// </summary>
    public AgentRunAnchor? Anchor { get; init; }
}

/// <summary>
/// The end of the result line of a journal entry that names its <c>AgentRunStarted</c> (docs/17 D-02, Phase 12): a contract
/// between the writer of the journal (<c>MarkdownDailyJournal</c>, fed by <see cref="JournalingAgentProvider"/>) and the
/// derivation that anchors the journal as memory, which matches each entry with the start it names.
/// <code>
///   - Resultado P-0007 · 11:44:31 · Succeeded · 144,2 s · tokens 18.300 → 2.150 (exactos) · ejecución `20261015-a1b2c3` · tarea `T-12` · intento 1 · entrada `2026-10-15/P-0007`
///   - Resultado P-0001 · 00:00:12 · Failed · 3,0 s · tokens no disponibles · boom · ejecución `20261015-a1b2c3` · intento 2 · entrada `2026-10-15/P-0042`
/// </code>
/// <list type="bullet">
/// <item>It goes last, after the note, as <c> · ejecución `&lt;run&gt;`</c>, then <c> · tarea `&lt;task&gt;`</c> when the start
/// has a task, then <c> · intento &lt;n&gt;</c>, then (since the addendum of 2026-11-03) <c> · entrada `&lt;day&gt;/P-NNNN`</c>:
/// the entry whose result it is, with the day of its line. A result goes to the file of the day the agent ended, so after
/// midnight it is in the next day's file, whose <c>P-NNNN</c> are others; the day says which entry it is. The run and the
/// task are written with <see cref="Uri.EscapeDataString"/>, so they only hold <c>[A-Za-z0-9._~%-]</c> and any value
/// round-trips.</item>
/// <item>The note before it is free text, but the journal replaces every backtick in it: a note can never write the two
/// backticks around the run, so the end of a line Agent Studio wrote can only be its own.</item>
/// <item>The daily summary reads these lines as before: its result line already admits anything after <c> · </c>
/// (<c>DailySummaryBuilder</c>). A line without the end is an entry of before Phase 12, or of a launch nobody announced.</item>
/// <item>Matching proves nothing by itself, since anyone who can write the versioned file can copy an end: the derivation
/// gives <c>System</c> only to an entry whose exact text is the one the receipt of its launch names
/// (<see cref="JournalReceipt"/>, in its <c>AgentRunFinished</c>), in a run of its own, and claimed by no other entry.</item>
/// </list>
/// </summary>
public static partial class JournalResultAnchor
{
    /// <summary>The end of a result line for <paramref name="anchor"/>, without the separator that goes before it nor the entry.</summary>
    public static string Format(AgentRunAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var task = anchor.Task is null ? string.Empty : " · tarea `" + Uri.EscapeDataString(anchor.Task) + "`";
        return string.Create(CultureInfo.InvariantCulture, $"ejecución `{Uri.EscapeDataString(anchor.Run)}`{task} · intento {anchor.Attempt}");
    }

    /// <summary>The end of the result line of the entry <paramref name="entry"/> for <paramref name="anchor"/>, as the journal writes it.</summary>
    public static string Format(AgentRunAnchor anchor, JournalEntryId entry) => Format(anchor) + " · entrada `" + entry.Qualified() + "`";

    /// <summary>The anchor at the end of a result line of the journal; false when the line does not end with one.</summary>
    public static bool TryParse(string? resultLine, [NotNullWhen(true)] out AgentRunAnchor? anchor) => TryParse(resultLine, out anchor, out _);

    /// <summary>
    /// The anchor at the end of a result line of the journal and the entry it names (null on a line of before the addendum
    /// of 2026-11-03, which names none); false when the line does not end with an anchor.
    /// </summary>
    public static bool TryParse(string? resultLine, [NotNullWhen(true)] out AgentRunAnchor? anchor, out JournalEntryId? entry)
    {
        anchor = null;
        entry = null;
        if (resultLine is null || AnchorEnd().Match(resultLine.TrimEnd()) is not { Success: true } match)
        {
            return false;
        }

        try
        {
            var task = match.Groups["task"];
            JournalEntryId? named = null;
            if (match.Groups["entry"] is { Success: true } qualified)
            {
                if (!JournalEntryId.TryParseQualified(qualified.Value, out var id))
                {
                    return false;
                }

                named = id;
            }

            anchor = new AgentRunAnchor(
                Uri.UnescapeDataString(match.Groups["run"].Value),
                task.Success ? Uri.UnescapeDataString(task.Value) : null,
                int.Parse(match.Groups["attempt"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture));
            entry = named;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex(@" · ejecución `(?<run>[A-Za-z0-9._~%-]+)`(?: · tarea `(?<task>[A-Za-z0-9._~%-]+)`)? · intento (?<attempt>[1-9][0-9]{0,8})(?: · entrada `(?<entry>[0-9]{4}-[0-9]{2}-[0-9]{2}/P-[0-9]{4,9})`)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorEnd();
}

/// <summary>Identifies a journal entry: the day it was written and its sequence number within that day.</summary>
public readonly record struct JournalEntryId(DateOnly Day, int Sequence)
{
    private const string DayFormat = "yyyy-MM-dd";

    public override string ToString() => $"P-{Sequence:D4}";

    /// <summary>The id with the day of its line, as the journal names an entry outside its own day: <c>2026-11-03/P-0001</c>.</summary>
    public string Qualified() => string.Create(CultureInfo.InvariantCulture, $"{Day.ToString(DayFormat, CultureInfo.InvariantCulture)}/{this}");

    /// <summary>Reads <see cref="Qualified"/>: <c>yyyy-MM-dd/P-NNNN</c>, with a real day and a positive number.</summary>
    public static bool TryParseQualified(string? text, out JournalEntryId id)
    {
        id = default;
        if (text is null
            || text.Length < DayFormat.Length + 7
            || text[DayFormat.Length] != '/'
            || !text.AsSpan(DayFormat.Length + 1).StartsWith("P-", StringComparison.Ordinal)
            || !DateOnly.TryParseExact(text.AsSpan(0, DayFormat.Length), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || !int.TryParse(text.AsSpan(DayFormat.Length + 3), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence <= 0)
        {
            return false;
        }

        id = new JournalEntryId(day, sequence);
        return true;
    }
}

/// <summary>
/// What the journal wrote for one anchored launch (docs/17 D-02, addendum of 2026-11-03): the entry, by the day of its line
/// and its number, and the SHA-256 of its exact text, its line and its result line joined by <c>\n</c> (<see cref="Of"/>),
/// which is the text the derivation of the journal reads for it. The recorder of the launch keeps it in its
/// <c>AgentRunFinished</c> (<see cref="Decisions.AgentRunFinishedPayload.Journal"/>), so the derivation gives
/// <c>System</c> only to an entry whose text is the very one Agent Studio wrote: an end of line copied onto another entry, or
/// an entry changed afterwards, has another hash.
/// </summary>
/// <param name="Day">The day of the line of the entry (its file).</param>
/// <param name="Entry">Its number in that day (<c>P-NNNN</c>).</param>
/// <param name="Sha256">The SHA-256 of its text, lowercase hex.</param>
public sealed record JournalReceipt(DateOnly Day, int Entry, string Sha256)
{
    /// <summary>The receipt of the entry <paramref name="id"/>, whose line is <paramref name="line"/> and whose result line is <paramref name="result"/>.</summary>
    public static JournalReceipt Of(JournalEntryId id, string line, string result)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(result);
        return new JournalReceipt(id.Day, id.Sequence, ContentHash.Sha256Hex(line + "\n" + result));
    }

    /// <summary>The entry this receipt is of.</summary>
    public JournalEntryId EntryId() => new(Day, Entry);
}

/// <summary>
/// Mandatory daily prompt journal (one Markdown file per day and project). A prompt must be recorded before it is
/// launched; see <see cref="JournalingAgentProvider"/>.
/// </summary>
public interface IPromptJournal
{
    Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct);

    Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct);

    /// <summary>
    /// Records the outcome as <see cref="RecordOutcomeAsync"/> does and returns the receipt of the entry (docs/17 D-02,
    /// addendum of 2026-11-03) when the outcome carries its anchor and the journal wrote the line of that entry with it; null
    /// otherwise. A journal that keeps no receipts records the outcome and returns null.
    /// </summary>
    async Task<JournalReceipt?> RecordReceiptedOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct)
    {
        await RecordOutcomeAsync(id, outcome, ct).ConfigureAwait(false);
        return null;
    }
}

/// <summary>Opens the prompt journal of a project; the IDE switches projects at runtime, so journals are per project.</summary>
public interface IPromptJournalFactory
{
    IPromptJournal ForProject(string projectRoot);
}
