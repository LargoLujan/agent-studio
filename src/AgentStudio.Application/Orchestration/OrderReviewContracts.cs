using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;

namespace AgentStudio.Application.Orchestration;

// Phase 13, WP1 (docs/17 D-20 and D-44, ADR-0049): the two auxiliary runs of the orchestrator that read the repository and
// only suggest. Each spends quota, so each use needs the person's OK with the estimate in front (QuotaConsent, spent once);
// what the agent answers is evidence, and every suggestion is checked again by the system and offered as a typed fix. Nothing
// is applied by itself.

/// <summary>
/// «Revisar el orden con el orquestador» for a plan the person changed after the proposal (docs/17 D-20): the plan and the team
/// as data, the run of the planning whose <c>_base</c> the orchestrator reads, and the person's OK for this use.
/// </summary>
public sealed record OrderReviewRequest(
    ProjectContext Project,
    RunId PlanningRun,
    PlanDefinition Plan,
    IReadOnlyList<AgentDefinition> Roster,
    AgentId Orchestrator,
    QuotaConsent Consent);

/// <summary>What a note of «Revisar el orden» is about (docs/17 D-20).</summary>
public enum OrderSuggestionKind
{
    /// <summary><c>Second</c> needs <c>First</c>.</summary>
    Dependency,

    /// <summary><c>First</c> and <c>Second</c> may run at once: it informs.</summary>
    ParallelOk,

    /// <summary>Doing <c>First</c> before <c>Second</c> would be harmful.</summary>
    Harmful,
}

/// <summary>
/// One suggestion of the orchestrator, checked by the system (docs/17 D-20): both tasks are in the plan, the reason is one
/// sentence of an agent (shown apart, framed as such), whether the plan already keeps it, and the dependency that would keep it
/// (<see cref="AddDependencyFix"/>) when <c>TaskGraph</c> admits it; null when it is kept already, when it only informs, or
/// when it would make a cycle.
/// </summary>
public sealed record OrderSuggestion(OrderSuggestionKind Kind, TaskId First, TaskId Second, string Reason, bool Kept, Fix? Fix);

/// <summary>
/// The answer of «Revisar el orden» (docs/17 D-20): the auxiliary run it was recorded under (purpose <c>OrderReview</c>), how it
/// ended, the evidence of the answer as it came, the notes the system kept, each as a checked suggestion, and why the rest were
/// left out (in English, for the details). <see cref="Error"/> says why there are none when the run failed or its answer could
/// not be read.
/// </summary>
public sealed record OrderReviewResult(
    RunId Run,
    AgentRunStatus Status,
    EvidenceId? AnswerEvidence,
    OrderNotes Notes,
    IReadOnlyList<OrderSuggestion> Suggestions,
    IReadOnlyList<string> Discarded,
    string? Error);

/// <summary>«Revisar el orden con el orquestador» (docs/17 D-20): read-only, with the person's OK each time; nothing is applied by itself.</summary>
public interface IOrderReview
{
    /// <exception cref="ArgumentNullException">There is no consent, project, plan or roster.</exception>
    /// <exception cref="ArgumentException">The consent says nothing, the plan has no task, or the orchestrator is not one of the roster.</exception>
    /// <exception cref="InvalidOperationException">The consent is too old, dated in the future or spent already: nothing is launched.</exception>
    Task<OrderReviewResult> ReviewAsync(OrderReviewRequest request, IProgress<AgentEvent>? progress, CancellationToken ct);
}
