using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Challenges;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Tests.Debate.Challenges;

/// <summary>
/// The Debate module cannot reference Orchestration (ADR-0002), so the challenge and the audit keep the values of the actions
/// the delivery reads (docs/15 D-35 and D-40). These are the constants of <see cref="DeliveryService"/>: a change on either side fails here.
/// </summary>
public sealed class DeliveryActionsTests
{
    [Fact]
    public void The_challenge_and_the_audit_write_the_actions_the_delivery_reads()
    {
        Assert.Equal(DeliveryService.RevisionAcceptanceAction, ChallengeLog.RevisionAcceptanceAction);
        Assert.Equal(DeliveryService.DebateOpenAction, ChallengeLog.DebateOpenAction);
        Assert.Equal(DeliveryService.ReplanDismissalAction, PendingProblems.ReplanDismissalAction);
        Assert.Equal(DeliveryService.ApprovalAction, OrchestratorAudit.DeliveryApprovalAction);
    }
}
