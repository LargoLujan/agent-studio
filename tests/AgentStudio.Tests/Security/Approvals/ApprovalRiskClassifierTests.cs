using AgentStudio.Application.Security;
using AgentStudio.Security.Approvals;

namespace AgentStudio.Tests.Security.Approvals;

public sealed class ApprovalRiskClassifierTests
{
    private static ApprovalRequest Request(string action, RiskLevel declared) => new(action, "descripción", declared, null, []);

    [Theory]
    [InlineData("worktree-remove", RiskLevel.Low, RiskLevel.Destructive)]
    [InlineData("delete-ref", RiskLevel.Low, RiskLevel.Destructive)]
    [InlineData("discard_changes", RiskLevel.Low, RiskLevel.Destructive)]
    [InlineData("Cleanup.Run", RiskLevel.Low, RiskLevel.Destructive)]
    [InlineData("deliver", RiskLevel.Destructive, RiskLevel.Critical)]
    [InlineData("merge-into-user-branch", RiskLevel.Low, RiskLevel.Critical)]
    [InlineData("force-push", RiskLevel.Low, RiskLevel.Critical)]
    public void An_action_that_removes_or_reaches_the_users_branch_is_raised_to_its_real_level(string action, RiskLevel declared, RiskLevel expected)
    {
        Assert.Equal(expected, ApprovalRiskClassifier.Classify(Request(action, declared)));
    }

    [Theory]
    [InlineData("approve-plan", RiskLevel.Low)]
    [InlineData("approve-plan", RiskLevel.Critical)]
    [InlineData("worktree-remove", RiskLevel.Critical)]
    [InlineData("human-approval", RiskLevel.Destructive)]
    public void The_declared_level_is_never_lowered(string action, RiskLevel declared)
    {
        Assert.Equal(declared, ApprovalRiskClassifier.Classify(Request(action, declared)));
    }

    [Fact]
    public void Words_inside_other_words_do_not_count()
    {
        Assert.Equal(RiskLevel.Low, ApprovalRiskClassifier.Classify(Request("undeleted-report", RiskLevel.Low)));
    }
}
