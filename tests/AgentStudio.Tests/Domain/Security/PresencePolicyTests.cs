using AgentStudio.Domain.Security;

namespace AgentStudio.Tests.Domain.Security;

/// <summary>
/// The fixed barrier of Windows Hello (docs/17 D-37 and D-38): a list in the code, by action. What is «Siempre» asks for the
/// person whatever the setting says; the setting only governs what is «Configurable»; answering a question and the OK of quota
/// never ask. The wiring to every path, with manipulated settings and files, is WP5's (F13-16).
/// </summary>
public sealed class PresencePolicyTests
{
    public static TheoryData<PresenceAction> Always() =>
    [
        PresenceAction.DeliverToBranch, PresenceAction.AcceptHistoryAsOrigin, PresenceAction.GrantHighRiskCapability,
        PresenceAction.ApprovePlanWithHighRiskCapability, PresenceAction.ChangeVerificationProfile, PresenceAction.RemoveProtection,
        PresenceAction.RemoteOrSecretOperation,
    ];

    public static TheoryData<PresenceAction> Configurable() =>
    [
        PresenceAction.ApprovePlan, PresenceAction.OpenOrResumeDebate, PresenceAction.AuditProject, PresenceAction.AcceptReview,
        PresenceAction.DiscardAgentWork,
    ];

    [Theory]
    [MemberData(nameof(Always))]
    public void What_is_always_asked_is_asked_whatever_the_setting_says(PresenceAction action)
    {
        Assert.Equal(PresenceRequirement.Always, PresencePolicy.RequirementOf(action));
        Assert.True(PresencePolicy.AsksPresence(action, importantApprovalsSetting: false));
        Assert.True(PresencePolicy.AsksPresence(action, importantApprovalsSetting: true));
    }

    [Theory]
    [MemberData(nameof(Configurable))]
    public void What_is_configurable_follows_the_setting(PresenceAction action)
    {
        Assert.Equal(PresenceRequirement.Configurable, PresencePolicy.RequirementOf(action));
        Assert.False(PresencePolicy.AsksPresence(action, importantApprovalsSetting: false));
        Assert.True(PresencePolicy.AsksPresence(action, importantApprovalsSetting: true));
    }

    [Fact]
    public void Answering_a_question_and_the_ok_of_quota_never_ask()
    {
        foreach (var action in new[] { PresenceAction.AnswerAgentQuestion, PresenceAction.AuxiliaryQuotaConsent })
        {
            Assert.Equal(PresenceRequirement.Never, PresencePolicy.RequirementOf(action));
            Assert.False(PresencePolicy.AsksPresence(action, importantApprovalsSetting: true));
        }
    }

    /// <summary>Every action of the list is classified, and an action the list does not have is never let through.</summary>
    [Fact]
    public void Every_action_is_classified_and_an_unknown_one_is_refused()
    {
        Assert.Equal(
            Enum.GetValues<PresenceAction>().Length,
            Always().Count + Configurable().Count + 2);
        Assert.All(Enum.GetValues<PresenceAction>(), action => Assert.True(Enum.IsDefined(PresencePolicy.RequirementOf(action))));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresencePolicy.RequirementOf((PresenceAction)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresencePolicy.AsksPresence((PresenceAction)99, importantApprovalsSetting: false));
    }
}
