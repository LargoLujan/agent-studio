using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// What the person was preparing in the orchestrator of a project, kept on this machine so closing the app loses neither the
/// request nor the plan on the canvas (a plan the orchestrator proposed spent quota): the literal request, and the plan as
/// it was proposed or edited and not run. A draft never carries an approval: one brought back is approved again before it
/// runs.
/// </summary>
/// <param name="Proposal">The proposal of the orchestrator the plan came from, if any: «Ejecutar» keeps its run while the plan is the proposed one.</param>
public sealed record PlanDraft(
    string Request,
    string Summary,
    IReadOnlyList<string> Risks,
    IReadOnlyList<TaskSpec> Tasks,
    PlanDraftProposal? Proposal = null)
{
    /// <summary>Nothing worth keeping: no request, no summary and no task.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Request) && string.IsNullOrWhiteSpace(Summary) && Tasks.Count == 0;
}

/// <summary>
/// The proposal of the orchestrator agent a draft came from: the run its planning allocated, and the hash of the plan as the
/// agent proposed it, which the plan store keeps with its provenance.
/// </summary>
/// <param name="HistoryToolNotice">
/// Why the orchestrator planned without the search in the history (<c>PlanProposal.HistoryToolNotice</c>, Phase 12), so a
/// draft brought back still says it; null when it had it, or for a draft of before.
/// </param>
/// <param name="PlannedRequest">
/// V43: the request the orchestrator planned with, trimmed, so a draft brought back sends the answers it got to the run only
/// while the request is that one; null for a draft of before, which then sends none.
/// </param>
/// <param name="RequirementsHash">V43: the hash of the requirements the orchestrator planned with, for the same check; null for a draft of before.</param>
public sealed record PlanDraftProposal(
    RunId Run,
    string PlanHash,
    bool Corrected,
    string? HistoryToolNotice = null,
    string? PlannedRequest = null,
    string? RequirementsHash = null);

/// <summary>What the store found of the draft of a project.</summary>
/// <param name="Draft">The draft this installation saved for this project, whole and within <see cref="PlanDraftLimits"/>; null otherwise.</param>
/// <param name="Ignored">
/// Why a draft found in the project is not brought back, in Spanish (it cannot be read, it is too big, or it was not saved by
/// Agent Studio on this machine for this folder); null when there was none, or when it is <paramref name="Draft"/>.
/// </param>
public sealed record DraftReading(PlanDraft? Draft, string? Ignored = null)
{
    public static DraftReading None { get; } = new(Draft: null, Ignored: null);
}

/// <summary>The most a draft brings back: past any of these it is ignored, never read in part.</summary>
public static class PlanDraftLimits
{
    /// <summary>Tasks of the plan (ten times what a plan may have, <c>PlanLimits.MaxTasks</c>).</summary>
    public const int MaxTasks = 200;

    /// <summary>Risks of the plan.</summary>
    public const int MaxRisks = 100;

    /// <summary>Characters of the request, of the summary, and of each text of a task or a risk.</summary>
    public const int MaxText = 20_000;

    /// <summary>Bytes of the file the draft is kept in, positions included.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;
}

/// <summary>Keeps the draft of the orchestrator of each project (<see cref="PlanDraft"/>).</summary>
public interface IPlanDraftStore
{
    /// <summary>The draft of the project this installation saved, or why one found there is ignored. Never throws for its content.</summary>
    DraftReading ReadDraft(string projectRoot);

    /// <summary>Saves the draft of the project as this installation's; null forgets it.</summary>
    void SaveDraft(string projectRoot, PlanDraft? draft);
}
