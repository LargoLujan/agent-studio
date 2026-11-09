namespace AgentStudio.Domain.Security;

/// <summary>
/// The actions that may ask the person in front of the computer to confirm their presence (docs/17 D-37), named by what they
/// do, never by the words of the action. The caller decides which one applies from the effective action: the arguments that
/// will be launched, the profile that will be saved, the destination of a delivery. The capabilities that change the threat
/// model are the ones <c>MediumIntegrityWriters</c> computes (ADR-0018, point 3): the policy has no list of its own.
/// </summary>
/// <remarks>The names are recorded nowhere, but WP5 and its tests refer to them: append, never rename.</remarks>
public enum PresenceAction
{
    // ---- Always: no setting and no file turns them off ----

    /// <summary>Delivering the work of a run to the person's branch: it changes their repository.</summary>
    DeliverToBranch,

    /// <summary>Accepting a history as the new origin of the log: it changes what is trusted.</summary>
    AcceptHistoryAsOrigin,

    /// <summary>
    /// Giving an agent a capability that changes the threat model: a shell (<c>Bash</c>), the network (<c>WebFetch</c>,
    /// <c>WebSearch</c>), Codex writing, or more folders. It makes a writer at medium integrity.
    /// </summary>
    GrantHighRiskCapability,

    /// <summary>Approving a plan whose template or profile has any of the above: it is what sets it going.</summary>
    ApprovePlanWithHighRiskCapability,

    /// <summary>Changing the verification profile, or a step with <c>isolation: none</c>: code that runs on the person's computer.</summary>
    ChangeVerificationProfile,

    /// <summary>Removing a protection from the app (the isolated verification, say): it lowers the security of everything else.</summary>
    RemoveProtection,

    /// <summary>With Phases 10 and 11: merging, deleting on the remote, deleting persistent data, using or revealing a secret (docs/16 D-13 and D-26).</summary>
    RemoteOrSecretOperation,

    // ---- Configurable: on by default ----

    /// <summary>Approving a plan with none of the above.</summary>
    ApprovePlan,

    /// <summary>Opening or resuming a debate.</summary>
    OpenOrResumeDebate,

    /// <summary>Auditing the whole project.</summary>
    AuditProject,

    /// <summary>Accepting a review (an independent review, a challenge).</summary>
    AcceptReview,

    /// <summary>Discarding the work of an agent that has changes: the destructive kind, which today never asks Hello.</summary>
    DiscardAgentWork,

    // ---- Never ----

    /// <summary>Answering the question of an agent: it grants nothing (D-31 and D-33).</summary>
    AnswerAgentQuestion,

    /// <summary>The OK to spend quota on an auxiliary run (the order review, the analysis of consumption, the chats).</summary>
    AuxiliaryQuotaConsent,
}

/// <summary>Whether an action asks for the person's presence (docs/17 D-37).</summary>
public enum PresenceRequirement
{
    /// <summary>Always, whatever any setting or file says.</summary>
    Always,

    /// <summary>When the setting «Pedir Windows Hello también en las aprobaciones importantes» is on (the default).</summary>
    Configurable,

    /// <summary>Never: it grants nothing.</summary>
    Never,
}

/// <summary>
/// What confirmed the person when they approved (docs/17 D-37 and D-40): the domain's view of how the app confirmed them, which
/// <see cref="PresencePolicy.IsSatisfiedBy"/> judges. The application turns the results of Windows into it (<c>PresenceProofs</c>,
/// in <c>Application/Security/UserPresenceContracts.cs</c>).
/// </summary>
public enum PresenceProof
{
    /// <summary>Nothing confirmed the person: Windows Hello failed or was cancelled, or the lock of Windows was not verified.</summary>
    None,

    /// <summary>The dialog alone: Windows Hello was not asked.</summary>
    Dialog,

    /// <summary>The dialog alone, because Windows Hello is not available on this computer, with a warning (Phase 8).</summary>
    DialogWithHelloUnavailable,

    /// <summary>Windows Hello confirmed the person.</summary>
    WindowsHello,

    /// <summary>The verified lock of Windows (D-40 (a)): the state of the session says it was unlocked after the request.</summary>
    WindowsLockUnlockedAfterRequest,
}

/// <summary>
/// The fixed barrier of Windows Hello (docs/17 D-37 to D-39): a list in the code, pure, that no setting and no file turns off.
/// The settings of Agent Studio only govern what is <see cref="PresenceRequirement.Configurable"/>; editing them by hand,
/// adding keys of one's own or deleting the file does not change <see cref="PresenceRequirement.Always"/>. Every path (the
/// approval gate, «Aprobar plan» of the canvas, accepting a history, saving a template or a profile) asks here and uses the
/// same reinforced confirmation (WP5). What a computer without Hello does instead is WP5's too (D-40 (a): the verified lock of
/// Windows).
/// </summary>
public static class PresencePolicy
{
    /// <summary>What <paramref name="action"/> asks for.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The action is not one of the list: an unknown action is never let through.</exception>
    public static PresenceRequirement RequirementOf(PresenceAction action) => action switch
    {
        PresenceAction.DeliverToBranch
            or PresenceAction.AcceptHistoryAsOrigin
            or PresenceAction.GrantHighRiskCapability
            or PresenceAction.ApprovePlanWithHighRiskCapability
            or PresenceAction.ChangeVerificationProfile
            or PresenceAction.RemoveProtection
            or PresenceAction.RemoteOrSecretOperation => PresenceRequirement.Always,
        PresenceAction.ApprovePlan
            or PresenceAction.OpenOrResumeDebate
            or PresenceAction.AuditProject
            or PresenceAction.AcceptReview
            or PresenceAction.DiscardAgentWork => PresenceRequirement.Configurable,
        PresenceAction.AnswerAgentQuestion
            or PresenceAction.AuxiliaryQuotaConsent => PresenceRequirement.Never,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action: the barrier lets no unknown action through."),
    };

    /// <summary>
    /// Whether <paramref name="action"/> asks for the person's presence, given the setting of the configurable ones
    /// (<paramref name="importantApprovalsSetting"/>). The setting is only read for <see cref="PresenceRequirement.Configurable"/>:
    /// what is <see cref="PresenceRequirement.Always"/> asks whatever it says.
    /// </summary>
    public static bool AsksPresence(PresenceAction action, bool importantApprovalsSetting) => RequirementOf(action) switch
    {
        PresenceRequirement.Always => true,
        PresenceRequirement.Configurable => importantApprovalsSetting,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="proof"/> is enough to approve <paramref name="action"/> (docs/17 D-37 and D-40 (a)). The one rule
    /// every path asks (WP5):
    /// <list type="bullet">
    /// <item>«Siempre», with Windows Hello available on this computer: only Windows Hello. The lock of Windows is the
    /// alternative for a computer without it (D-40 (a)), never a way around it.</item>
    /// <item>«Siempre», without Windows Hello: Windows Hello, or the lock of Windows verified as unlocked after the request.
    /// Nothing else: not the dialog alone, not the dialog with Hello unavailable, whatever the setting says.</item>
    /// <item>«Configurable» with the setting on: Windows Hello when it is available; without it, also the lock of Windows or
    /// the dialog with its warning that Hello is unavailable, as in Phase 8.</item>
    /// <item>«Configurable» with the setting off: the dialog is enough.</item>
    /// <item>«Nunca»: nothing is asked.</item>
    /// </list>
    /// </summary>
    /// <param name="helloAvailable">
    /// Whether Windows Hello can be asked on this computer: false only when Windows said for certain that it is not there (not
    /// set up, no device, a policy). A caller that cannot know judges with false, the floor no computer lowers.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The action is not one of the list.</exception>
    public static bool IsSatisfiedBy(PresenceAction action, bool importantApprovalsSetting, bool helloAvailable, PresenceProof proof) => RequirementOf(action) switch
    {
        PresenceRequirement.Always when helloAvailable => proof is PresenceProof.WindowsHello,
        PresenceRequirement.Always => proof is PresenceProof.WindowsHello or PresenceProof.WindowsLockUnlockedAfterRequest,
        PresenceRequirement.Configurable when importantApprovalsSetting && helloAvailable => proof is PresenceProof.WindowsHello,
        PresenceRequirement.Configurable when importantApprovalsSetting =>
            proof is PresenceProof.WindowsHello or PresenceProof.WindowsLockUnlockedAfterRequest or PresenceProof.DialogWithHelloUnavailable,
        PresenceRequirement.Configurable => proof is not PresenceProof.None && Enum.IsDefined(proof),
        _ => true,
    };
}
