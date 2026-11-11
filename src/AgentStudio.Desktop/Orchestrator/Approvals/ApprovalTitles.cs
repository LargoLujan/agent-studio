using AgentStudio.Application.Decisions;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Challenges;
using AgentStudio.Desktop.Localization;
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
    public static string Unknown => Texts.Get("Orchestrator_ApprovalTitle_Unknown");

    /// <summary>The key of the text of each known action, by the identifier the modules ask with.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        [DeliveryService.ApprovalAction] = "Orchestrator_ApprovalTitle_Deliver",
        [WorkspaceCleanup.ApprovalAction] = "Orchestrator_ApprovalTitle_Cleanup",
        [Engine.PlanApprovalAction] = "Orchestrator_ApprovalTitle_Plan",
        [Engine.HumanApprovalAction] = "Orchestrator_ApprovalTitle_HumanTask",
        [Engine.ReviewEscalationAction] = "Orchestrator_ApprovalTitle_ReviewEscalation",
        [ProjectAuditService.ApprovalAction] = "Orchestrator_ApprovalTitle_Audit",
        [DebateCoordinator.OpenAction] = "Orchestrator_ApprovalTitle_DebateOpen",
        [DebateCoordinator.ResumeAction] = "Orchestrator_ApprovalTitle_DebateResume",
        [DebateRun.HumanResolutionAction] = "Orchestrator_ApprovalTitle_DebateResolve",
        [ChallengeLog.RevisionAcceptanceAction] = "Orchestrator_ApprovalTitle_Revision",
        [PendingProblems.ReplanDismissalAction] = "Orchestrator_ApprovalTitle_Replan",
        [SkillsDocumentViewModel.AssistedAction] = "Orchestrator_ApprovalTitle_Skills",
        [UnanchoredHistory.AcceptAction] = "Orchestrator_ApprovalTitle_History",
    };

    /// <summary>
    /// The title of <paramref name="action"/>: the sentence of a known action; the text itself when it is already a sentence
    /// (it has a space, as a request written by hand); <see cref="Unknown"/> for any other identifier.
    /// </summary>
    public static string For(string? action) =>
        action is not null && Known.TryGetValue(action, out var key) ? Texts.Get(key)
        : IsIdentifier(action) || string.IsNullOrWhiteSpace(action) ? Unknown
        : action.Trim();

    /// <summary>Whether <paramref name="action"/> is an identifier (one word, as the modules name their actions), not a sentence.</summary>
    public static bool IsIdentifier(string? action) =>
        !string.IsNullOrWhiteSpace(action) && !action.Trim().Contains(' ', StringComparison.Ordinal);
}
