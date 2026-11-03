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
