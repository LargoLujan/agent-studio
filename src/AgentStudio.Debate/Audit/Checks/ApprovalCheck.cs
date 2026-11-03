using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Approvals requested (D-33, <c>orchestrator.approval-missing</c>, critical): the start of the run (the approval of its
/// plan), each delivery to the user's branch and each removal of a worktree with its changes come after a person's
/// <c>ApprovalGranted</c> of their action that answers an <c>ApprovalRequested</c> of the same action, as the approval gate
/// records them. The log carries no binding of an approval (only the gate and the git layer see it), so «bound» is what the
/// log can show: the action, the order, a person, and, for a delivery, an approval of its own (one asked after the previous
/// delivery). A denial is no approval. The removals of one cleanup share its single request.
/// </summary>
internal static class ApprovalCheck
{
    public static void Run(AuditedRun run, AuditFindings findings)
    {
        var requests = new List<(string Action, long Sequence)>();
        var grants = new List<(string Action, long Sequence)>();
        var fences = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in run.Items)
        {
            switch (item.Payload)
            {
                case ApprovalRequestedPayload request when item.Kind == DecisionKind.ApprovalRequested:
                    requests.Add((request.Action, item.Sequence));
                    continue;
                case ApprovalAnsweredPayload { Approved: true } answer when item.Kind == DecisionKind.ApprovalGranted && item.Entry.Actor.Kind == ActorKind.User:
                    grants.Add((answer.Action, item.Sequence));
                    continue;
            }

            if (Guarded(run, item) is not { } guarded)
            {
                continue;
            }

            var (action, what, perUse) = guarded;
            var fence = fences.GetValueOrDefault(action);
            var grant = grants.LastOrDefault(g => g.Action == action && g.Sequence > fence);
            var bound = grant.Action is not null && requests.Any(r => r.Action == action && r.Sequence > fence && r.Sequence < grant.Sequence);
            if (!bound)
            {
                findings.Add(OrchestratorAudit.ApprovalMissingCode, Severity.Critical, [], [item.Sequence], FormattableString.Invariant(
                    $"Entry {item.Sequence} ({item.Kind}) {what}, and no ApprovalGranted of '{action}' by a person answered a request for it before."));
            }

            if (perUse)
            {
                fences[action] = item.Sequence;
            }
        }
    }

    /// <summary>The action an entry needs approved, what it does and whether each one needs its own; null for any other entry.</summary>
    private static (string Action, string What, bool PerUse)? Guarded(AuditedRun run, RunItem item) => item.Payload switch
    {
        RunStartedPayload when item.Sequence == run.StartedItem.Sequence => (OrchestratorAudit.PlanApprovalAction, "starts the run on its plan", true),
        IntegrationPerformedPayload { IsDelivery: true } => (OrchestratorAudit.DeliveryApprovalAction, "delivers the run to the user's branch", true),
        WorktreeRemovedPayload { DiscardedChanges: true } removed => (OrchestratorAudit.DiscardApprovalAction, $"removes the worktree '{removed.Name}' with its changes", false),
        _ => null,
    };
}
