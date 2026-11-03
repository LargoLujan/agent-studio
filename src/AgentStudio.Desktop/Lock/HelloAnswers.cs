using System.Diagnostics;
using AgentStudio.Application.Security;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// What Agent Studio makes of the answers of Windows Hello (docs/16 D-13, ADR-0037), and how it says them. Only a definite
/// answer of Windows that Hello is not there (<see cref="IsAbsent"/>) lets a critical approval be decided without Hello, or
/// turns «Bloquear» into «Bloquear Windows»; anything else (busy, Windows could not say, or did not answer in
/// <see cref="AvailabilityTimeout"/>) asks Hello, and only <see cref="UserPresenceResult.Verified"/> confirms the person.
/// </summary>
public static class HelloAnswers
{
    /// <summary>What Windows Hello shows when it is asked to unlock the interface.</summary>
    public const string UnlockMessage = "Agent Studio: confirma que eres tú para desbloquear la interfaz.";

    /// <summary>
    /// How long Windows has to say whether Windows Hello is there (it answers in milliseconds). Past it, Windows «could not
    /// say»: an approval asks Hello, still within the time the gate gives the question, and the lock goes on as with Hello.
    /// </summary>
    public static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether Windows said, for certain, that it cannot confirm the person: not set up for the user, no device, or turned off
    /// by a policy. <see cref="UserPresenceAvailability.DeviceBusy"/> is not: it passes, and Hello is asked.
    /// </summary>
    public static bool IsAbsent(UserPresenceAvailability availability) =>
        availability is UserPresenceAvailability.NotConfiguredForUser or UserPresenceAvailability.DeviceNotPresent or UserPresenceAvailability.DisabledByPolicy;

    /// <summary>
    /// What Windows says of Windows Hello, within <see cref="AvailabilityTimeout"/> of <paramref name="time"/>; null when it could
    /// not say: an error, or no answer in time. Nothing is shown.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<UserPresenceAvailability?> AvailabilityAsync(IUserPresenceVerifier verifier, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            return await verifier.CheckAvailabilityAsync(cancellationToken).WaitAsync(AvailabilityTimeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Windows could not say (an error, or no answer in time): the caller asks Hello as if it were there.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Debug.WriteLine($"Windows Hello: its availability could not be checked ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    /// <summary>What Windows Hello shows when it is asked to confirm a critical approval.</summary>
    public static string ApprovalMessage(string action) => $"Agent Studio: confirma que eres tú para aprobar «{action}».";

    /// <summary>What Windows Hello shows when it is asked to confirm the approval of a plan.</summary>
    public static string PlanMessage(string planHash) => $"Agent Studio: confirma que eres tú para aprobar el plan {planHash}.";

    /// <summary>Why Windows Hello is not there, as the screens say it.</summary>
    public static string WhyAbsent(UserPresenceAvailability availability) => availability switch
    {
        UserPresenceAvailability.NotConfiguredForUser => "no está configurado para tu usuario",
        UserPresenceAvailability.DeviceNotPresent => "no hay ningún dispositivo con el que verificarte",
        UserPresenceAvailability.DisabledByPolicy => "lo desactiva una directiva",
        UserPresenceAvailability.DeviceBusy => "el dispositivo está ocupado",
        _ => "Windows no lo pudo decir",
    };

    /// <summary>What a result other than a confirmation means, as the screens and the log say it.</summary>
    public static string WhyNotConfirmed(UserPresenceResult result) => result switch
    {
        UserPresenceResult.Canceled => "se canceló la verificación",
        UserPresenceResult.RetriesExhausted => "demasiados intentos fallidos",
        UserPresenceResult.DeviceBusy => "el dispositivo estaba ocupado",
        UserPresenceResult.DeviceNotPresent => "no hay ningún dispositivo con el que verificarte",
        UserPresenceResult.NotConfiguredForUser => "no está configurado para tu usuario",
        UserPresenceResult.DisabledByPolicy => "lo desactiva una directiva",
        UserPresenceResult.Verified => "te confirmó",
        _ => "Windows dio un resultado que Agent Studio no conoce",
    };

    /// <summary>The reason of the denial of a yes that Windows Hello did not confirm: it names the result, so the log says why.</summary>
    public static string ApprovalNotConfirmed(UserPresenceResult result) =>
        $"Windows Hello no confirmó que eres tú ({result}: {WhyNotConfirmed(result)}): se deniega.";

    /// <summary>The reason of the denial of a yes when Windows Hello could not be asked at all.</summary>
    public static string ApprovalNotAsked(string problem) => $"No se pudo pedir Windows Hello ({problem}): se deniega.";
}
