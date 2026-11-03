using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
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
    string? DebateId = null);

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
///   - Resultado P-0007 · 11:44:31 · Succeeded · 144,2 s · tokens 18.300 → 2.150 (exactos) · ejecución `20261015-a1b2c3` · tarea `T-12` · intento 1
///   - Resultado P-0008 · 11:50:02 · Failed · 3,0 s · tokens no disponibles · boom · ejecución `20261015-a1b2c3` · intento 2
/// </code>
/// <list type="bullet">
/// <item>It goes last, after the note, as <c> · ejecución `&lt;run&gt;`</c>, then <c> · tarea `&lt;task&gt;`</c> when the start
/// has a task, then <c> · intento &lt;n&gt;</c>. The run and the task are written with <see cref="Uri.EscapeDataString"/>,
/// so they only hold <c>[A-Za-z0-9._~%-]</c> and any value round-trips.</item>
/// <item>The note before it is free text, but the journal replaces every backtick in it: a note can never write the two
/// backticks around the run, so the end of a line Agent Studio wrote can only be its own.</item>
/// <item>The daily summary reads these lines as before: its result line already admits anything after <c> · </c>
/// (<c>DailySummaryBuilder</c>). A line without the end is an entry of before Phase 12, or of a launch nobody announced.</item>
/// <item>Matching proves nothing by itself: the derivation checks that the start exists in the verified log, with the same
/// run, task and attempt (and the agent and provider of the entry), and that no other entry of the journal claimed it.</item>
/// </list>
/// </summary>
public static partial class JournalResultAnchor
{
    /// <summary>The end of a result line for <paramref name="anchor"/>, without the separator that goes before it.</summary>
    public static string Format(AgentRunAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var task = anchor.Task is null ? string.Empty : " · tarea `" + Uri.EscapeDataString(anchor.Task) + "`";
        return string.Create(CultureInfo.InvariantCulture, $"ejecución `{Uri.EscapeDataString(anchor.Run)}`{task} · intento {anchor.Attempt}");
    }

    /// <summary>The anchor at the end of a result line of the journal; false when the line does not end with one.</summary>
    public static bool TryParse(string? resultLine, [NotNullWhen(true)] out AgentRunAnchor? anchor)
    {
        anchor = null;
        if (resultLine is null || AnchorEnd().Match(resultLine.TrimEnd()) is not { Success: true } match)
        {
            return false;
        }

        try
        {
            var task = match.Groups["task"];
            anchor = new AgentRunAnchor(
                Uri.UnescapeDataString(match.Groups["run"].Value),
                task.Success ? Uri.UnescapeDataString(task.Value) : null,
                int.Parse(match.Groups["attempt"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex(@" · ejecución `(?<run>[A-Za-z0-9._~%-]+)`(?: · tarea `(?<task>[A-Za-z0-9._~%-]+)`)? · intento (?<attempt>[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorEnd();
}

/// <summary>Identifies a journal entry: the day it was written and its sequence number within that day.</summary>
public readonly record struct JournalEntryId(DateOnly Day, int Sequence)
{
    public override string ToString() => $"P-{Sequence:D4}";
}

/// <summary>
/// Mandatory daily prompt journal (one Markdown file per day and project). A prompt must be recorded before it is
/// launched; see <see cref="JournalingAgentProvider"/>.
/// </summary>
public interface IPromptJournal
{
    Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct);

    Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct);
}

/// <summary>Opens the prompt journal of a project; the IDE switches projects at runtime, so journals are per project.</summary>
public interface IPromptJournalFactory
{
    IPromptJournal ForProject(string projectRoot);
}
