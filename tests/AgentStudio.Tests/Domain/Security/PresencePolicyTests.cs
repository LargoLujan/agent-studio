using AgentStudio.Application.Security;
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

    /// <summary>
    /// D-37 and D-40 (a): what is «Siempre» is satisfied only by Windows Hello or by the lock of Windows verified as unlocked
    /// after the request, whatever the setting says; never by the dialog, nor by the dialog with Hello unavailable.
    /// </summary>
    [Theory]
    [MemberData(nameof(Always))]
    public void Only_hello_or_the_verified_lock_satisfy_what_is_always_asked(PresenceAction action)
    {
        foreach (var setting in new[] { false, true })
        {
            Assert.True(PresencePolicy.IsSatisfiedBy(action, setting, PresenceProof.WindowsHello));
            Assert.True(PresencePolicy.IsSatisfiedBy(action, setting, PresenceProof.WindowsLockUnlockedAfterRequest));
            Assert.False(PresencePolicy.IsSatisfiedBy(action, setting, PresenceProof.Dialog));
            Assert.False(PresencePolicy.IsSatisfiedBy(action, setting, PresenceProof.DialogWithHelloUnavailable));
            Assert.False(PresencePolicy.IsSatisfiedBy(action, setting, PresenceProof.None));
            Assert.False(PresencePolicy.IsSatisfiedBy(action, setting, (PresenceProof)42));
        }
    }

    [Theory]
    [MemberData(nameof(Configurable))]
    public void What_is_configurable_takes_the_dialog_when_the_setting_is_off_and_hello_or_its_warning_when_on(PresenceAction action)
    {
        Assert.True(PresencePolicy.IsSatisfiedBy(action, importantApprovalsSetting: false, PresenceProof.Dialog));
        Assert.False(PresencePolicy.IsSatisfiedBy(action, importantApprovalsSetting: false, PresenceProof.None));
        Assert.False(PresencePolicy.IsSatisfiedBy(action, importantApprovalsSetting: true, PresenceProof.Dialog));
        Assert.True(PresencePolicy.IsSatisfiedBy(action, importantApprovalsSetting: true, PresenceProof.DialogWithHelloUnavailable));
        Assert.True(PresencePolicy.IsSatisfiedBy(action, importantApprovalsSetting: true, PresenceProof.WindowsHello));
        Assert.True(PresencePolicy.IsSatisfiedBy(action, importantApprovalsSetting: true, PresenceProof.WindowsLockUnlockedAfterRequest));
    }

    /// <summary>The results of Windows confirm the person only when Hello verified, or the lock was unlocked after the request.</summary>
    [Fact]
    public void Only_a_verified_hello_or_a_lock_unlocked_after_the_request_prove_the_person()
    {
        Assert.Equal(PresenceProof.WindowsHello, PresenceProofs.FromHello(UserPresenceResult.Verified));
        Assert.All(Enum.GetValues<UserPresenceResult>().Where(r => r != UserPresenceResult.Verified), r => Assert.Equal(PresenceProof.None, PresenceProofs.FromHello(r)));
        Assert.Equal(PresenceProof.WindowsLockUnlockedAfterRequest, PresenceProofs.FromWindowsLock(WindowsLockResult.UnlockedAfterRequest));
        Assert.All(Enum.GetValues<WindowsLockResult>().Where(r => r != WindowsLockResult.UnlockedAfterRequest), r => Assert.Equal(PresenceProof.None, PresenceProofs.FromWindowsLock(r)));
        Assert.Equal(ApprovalVerification.WindowsLock, PresenceProofs.Verification(PresenceProof.WindowsLockUnlockedAfterRequest));
        Assert.Null(PresenceProofs.Verification(PresenceProof.None));
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
