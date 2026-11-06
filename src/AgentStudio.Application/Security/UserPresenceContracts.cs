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
