using AgentStudio.Application.Projects;

namespace AgentStudio.Application.Knowledge;

// Phase 4 contract of the daily summary (docs/14 D-31).

/// <summary>
/// The deterministic summary of a day of the journal (D-31), in the local day: prompts by actor, provider and model,
/// results by status and total duration, tokens (an exact sum only when every figure is exact, else «parcial»), failures
/// and limits, the most frequent tags, the «Hecho» lines quoted and escaped, and only the count of the lines that were not
/// recognised. Today's is <paramref name="Provisional"/>. A closed day is summarized once (by <paramref name="JournalSha256"/>):
/// <paramref name="Recorded"/> is the <c>DailySummary</c> entry it left in the memory, null when it left none (today, or
/// already summarized). A day without a journal has no prompts.
/// </summary>
public sealed record DailySummaryResult(
    DateOnly Day,
    bool Provisional,
    string JournalSha256,
    string Summary,
    int Prompts,
    int UnrecognizedLines,
    MemoryEntry? Recorded);

/// <summary>
/// Summarizes a day of the journal (D-31). The journal is read through <c>IProjectStorageReader</c>, which refuses a link
/// planted as a day; the summary goes to the memory as <c>DailySummary</c> with the system as its origin.
/// </summary>
public interface IDailySummaryService
{
    Task<DailySummaryResult> SummarizeAsync(ProjectContext project, DateOnly day, CancellationToken ct);
}
