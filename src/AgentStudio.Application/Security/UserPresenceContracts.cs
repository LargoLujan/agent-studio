using AgentStudio.Domain.Security;

namespace AgentStudio.Application.Security;

// Phase 8 contract (docs/16 D-13, F8-10 and F8-11): Windows Hello as a reinforced confirmation of the critical approvals and
// to unlock the interface. The desktop implements it (WP4, Desktop/Lock) with UserConsentVerifierInterop and the window of
// the app; the port knows no window and no type of Windows, so the contracts stay in net10.0.

/// <summary>Whether Windows can confirm the person now. It mirrors <c>UserConsentVerifierAvailability</c> one to one, names and values.</summary>
public enum UserPresenceAvailability
{
    /// <summary>A verifier is ready: Windows Hello (face, fingerprint or PIN).</summary>
    Available,

    /// <summary>There is no device to verify with.</summary>
    DeviceNotPresent,

    /// <summary>Windows Hello is not set up for this user.</summary>
    NotConfiguredForUser,

    /// <summary>A group policy turned the verification off.</summary>
    DisabledByPolicy,

    /// <summary>The device is busy with another operation.</summary>
    DeviceBusy,
}

/// <summary>
/// The result of asking Windows to confirm the person. It mirrors <c>UserConsentVerificationResult</c> one to one, names and
/// values. This is all that reaches the app: never biometric data nor the PIN.
/// </summary>
public enum UserPresenceResult
{
    /// <summary>Windows confirmed the person.</summary>
    Verified,

    /// <summary>There is no device to verify with.</summary>
    DeviceNotPresent,

    /// <summary>Windows Hello is not set up for this user.</summary>
    NotConfiguredForUser,

    /// <summary>A group policy turned the verification off.</summary>
    DisabledByPolicy,

    /// <summary>The device is busy with another operation.</summary>
    DeviceBusy,

    /// <summary>Too many failed attempts: Windows gave up.</summary>
    RetriesExhausted,

    /// <summary>The person cancelled the verification.</summary>
    Canceled,
}

/// <summary>
/// Confirms with Windows Hello that the person in front of the computer is the user (docs/16 D-13): after the dialog of a
/// critical approval, and to unlock the interface. It only ever learns a result. It does not stop a program running with the
/// user's session from acting without going through Agent Studio (residual 1 of ADR-0018), and it protects no data at rest.
/// </summary>
public interface IUserPresenceVerifier
{
    /// <summary>Whether Windows Hello can be asked now. Nothing is shown.</summary>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    Task<UserPresenceAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks Windows to confirm the person, over the window of the app, with <paramref name="message"/> in its dialog. A
    /// result it did not get is never returned: when Windows cannot be asked at all, it throws, and the caller decides.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled before the answer.</exception>
    Task<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken);
}

// Phase 13 contract (docs/17 D-40 (a), decision 6 accepted on 2026-11-06): when Windows Hello cannot be asked for an action of
// the fixed barrier (Domain/Security/PresencePolicy), the app locks Windows and checks the state of the session after the
// unlock. WP5 implements it in Desktop/Lock with LockWorkStation and WTSQuerySessionInformation (WTSSessionInfoEx and its
// SessionFlags); the port knows no type of Windows.

/// <summary>What the verified lock of Windows said (docs/17 D-40 (a)).</summary>
public enum WindowsLockResult
{
    /// <summary>
    /// The session was locked and then unlocked after the request: the state of the session says so, not a message that
    /// could be forged. The person unlocked it with their PIN or password, which the app never sees.
    /// </summary>
    UnlockedAfterRequest,

    /// <summary>Windows did not lock (the call failed, or a policy forbids locking): nothing is confirmed.</summary>
    LockFailed,

    /// <summary>The state of the session could not be read, or does not say the unlock came after the request: nothing is confirmed.</summary>
    NotVerified,

    /// <summary>Nobody unlocked within the time given: the system denies, as for any approval without an answer.</summary>
    TimedOut,
}

/// <summary>
/// The alternative to Windows Hello for the actions of the fixed barrier (docs/17 D-40 (a)): it locks Windows and confirms,
/// from the state of the session, that it was unlocked after the request. The answer is recorded with
/// <see cref="ApprovalVerification.WindowsLock"/>. It closes the residual of the forged notice of ADR-0037, decision 5,
/// because it trusts the state of the session and not a message. There is no path with the password of Windows: Agent Studio
/// never asks for it.
/// </summary>
public interface IWindowsLockConfirmation
{
    /// <summary>
    /// Locks Windows and waits, at most <paramref name="timeout"/>, until it is unlocked; then says whether the unlock is
    /// confirmed. Only <see cref="WindowsLockResult.UnlockedAfterRequest"/> confirms the person.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled before the unlock.</exception>
    Task<WindowsLockResult> LockAndConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The results of Windows as the barrier judges them (<see cref="PresenceProof"/>, <c>PresencePolicy.IsSatisfiedBy</c>): only a
/// verified Hello or a lock unlocked after the request confirm the person; anything else confirms nothing.
/// </summary>
public static class PresenceProofs
{
    /// <summary>What a Windows Hello result proves: <see cref="PresenceProof.WindowsHello"/> when Windows confirmed the person, else nothing.</summary>
    public static PresenceProof FromHello(UserPresenceResult result) =>
        result == UserPresenceResult.Verified ? PresenceProof.WindowsHello : PresenceProof.None;

    /// <summary>What a lock of Windows proves: <see cref="PresenceProof.WindowsLockUnlockedAfterRequest"/> only when the state of the session says so, else nothing.</summary>
    public static PresenceProof FromWindowsLock(WindowsLockResult result) =>
        result == WindowsLockResult.UnlockedAfterRequest ? PresenceProof.WindowsLockUnlockedAfterRequest : PresenceProof.None;

    /// <summary>
    /// The method an answer records for a proof (<see cref="ApprovalVerification"/>, in <c>ApprovalGranted</c> and
    /// <c>ApprovalDenied</c> version 2); null for <see cref="PresenceProof.None"/>, which approves nothing.
    /// </summary>
    public static ApprovalVerification? Verification(PresenceProof proof) => proof switch
    {
        PresenceProof.Dialog => ApprovalVerification.Dialog,
        PresenceProof.DialogWithHelloUnavailable => ApprovalVerification.WindowsHelloUnavailable,
        PresenceProof.WindowsHello => ApprovalVerification.WindowsHello,
        PresenceProof.WindowsLockUnlockedAfterRequest => ApprovalVerification.WindowsLock,
        _ => null,
    };

    /// <summary>
    /// What an answer recorded with <paramref name="verification"/> claims confirmed the person (WP5): what the approval gate,
    /// the engine and the decision recorder judge an approval by, since they never see the results of Windows, only the method
    /// the desktop recorded. It is the inverse of <see cref="Verification"/>: the two proofs that confirm the person come from
    /// <see cref="FromHello"/> and <see cref="FromWindowsLock"/>, as a verified Hello or a lock unlocked after the request
    /// would give them. A method that is none of these claims nothing (<see cref="PresenceProof.None"/>).
    /// </summary>
    public static PresenceProof Claimed(ApprovalVerification verification) => verification switch
    {
        ApprovalVerification.Dialog => PresenceProof.Dialog,
        ApprovalVerification.WindowsHelloUnavailable => PresenceProof.DialogWithHelloUnavailable,
        ApprovalVerification.WindowsHello => FromHello(UserPresenceResult.Verified),
        ApprovalVerification.WindowsLock => FromWindowsLock(WindowsLockResult.UnlockedAfterRequest),
        _ => PresenceProof.None,
    };
}
