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
    string? Note = null);

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
