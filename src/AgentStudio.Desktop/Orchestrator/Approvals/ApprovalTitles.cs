using AgentStudio.Application.Decisions;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Challenges;
using AgentStudio.Desktop.Skills;
using AgentStudio.Orchestration.Integration;
using Engine = AgentStudio.Orchestration.Engine.Orchestrator;

namespace AgentStudio.Desktop.Orchestrator.Approvals;

/// <summary>
/// What an approval question is called on the screens (V25): a plain sentence for each action the app asks about, never its
/// identifier. The identifier stays what the log records (<c>ApprovalRequested</c> and the answer keep it, and so does
/// <c>ApprovalRequest.Action</c>); the dialog shows it only as a technical detail. The map is
/// keyed on the constants of the modules that ask, so an identifier that is renamed cannot lose its title silently.
/// </summary>
public static class ApprovalTitles
{
    /// <summary>The title of an action whose identifier the app does not know.</summary>
    public const string Unknown = "Una acción necesita tu aprobación";

    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        [DeliveryService.ApprovalAction] = "Entregar a tu rama",
        [WorkspaceCleanup.ApprovalAction] = "Descartar el trabajo de una ejecución",
        [Engine.PlanApprovalAction] = "Aprobar el plan",
        [Engine.HumanApprovalAction] = "Aprobar una tarea del plan",
        [Engine.ReviewEscalationAction] = "Decidir sobre una revisión",
        [ProjectAuditService.ApprovalAction] = "Auditar el proyecto",
        [DebateCoordinator.OpenAction] = "Abrir un debate",
        [DebateCoordinator.ResumeAction] = "Reanudar un debate",
        [DebateRun.HumanResolutionAction] = "Decidir el resultado del debate",
        [ChallengeLog.RevisionAcceptanceAction] = "Aceptar la revisión de un veredicto",
        [PendingProblems.ReplanDismissalAction] = "Descartar la petición de replanificar",
        [SkillsDocumentViewModel.AssistedAction] = "Pedir a un agente que recomiende skills",
        [UnanchoredHistory.AcceptAction] = "Aceptar el historial como nuevo origen",
    };

    /// <summary>
    /// The title of <paramref name="action"/>: the sentence of a known action; the text itself when it is already a sentence
    /// (it has a space, as a request written by hand); <see cref="Unknown"/> for any other identifier.
    /// </summary>
    public static string For(string? action) =>
        action is not null && Known.TryGetValue(action, out var title) ? title
        : IsIdentifier(action) || string.IsNullOrWhiteSpace(action) ? Unknown
        : action.Trim();

    /// <summary>Whether <paramref name="action"/> is an identifier (one word, as the modules name their actions), not a sentence.</summary>
    public static bool IsIdentifier(string? action) =>
        !string.IsNullOrWhiteSpace(action) && !action.Trim().Contains(' ', StringComparison.Ordinal);
}
