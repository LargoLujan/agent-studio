using System.Windows.Threading;
using AgentStudio.Application.Security;
using Windows.Foundation;
using Windows.Security.Credentials.UI;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// <see cref="IUserPresenceVerifier"/> with Windows Hello (docs/16 D-13; ADR-0037): <c>UserConsentVerifier.CheckAvailabilityAsync</c>
/// to know whether it is there, and <c>UserConsentVerifierInterop.RequestVerificationForWindowAsync</c> over the window of the
/// app to ask it (the interop of Windows 11, build 22000, that a desktop app needs). Windows does the verification: the app
/// gets a result and nothing else, never biometric data nor the PIN, and never asks for the Windows password.
/// </summary>
/// <remarks>
/// The handle of the window is read at each verification, on the UI thread: the window can be created after this, and hidden
/// or shown again. Without a window, Windows Hello is not asked: it throws, and the caller decides (an approval is denied, the
/// interface stays locked). A cancellation of the caller cancels the operation of WinRT.
/// </remarks>
public sealed class WindowsHelloVerifier : IUserPresenceVerifier
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<nint> _window;

    /// <param name="dispatcher">The UI thread, where the handle of the window is read and Windows Hello is asked.</param>
    /// <param name="window">The handle of the window Windows Hello opens over (the main window); 0 when there is none.</param>
    public WindowsHelloVerifier(Dispatcher dispatcher, Func<nint> window)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(window);
        _dispatcher = dispatcher;
        _window = window;
    }

    public async Task<UserPresenceAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        var availability = await UserConsentVerifier.CheckAvailabilityAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return UserPresenceMapping.ToAvailability(availability);
    }

    public async Task<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        var operation = _dispatcher.CheckAccess()
            ? Request(message)
            : await _dispatcher.InvokeAsync(() => Request(message), DispatcherPriority.Normal, cancellationToken);
        var result = await operation.AsTask(cancellationToken).ConfigureAwait(false);
        return UserPresenceMapping.ToResult(result);
    }

    private IAsyncOperation<UserConsentVerificationResult> Request(string message)
    {
        var window = _window();
        if (window == 0)
        {
            throw new InvalidOperationException("There is no window for Windows Hello to open over.");
        }

        return UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, message);
    }
}

/// <summary>
/// The answers of WinRT as the contracts read them: one to one, by name (docs/04 §18.4). A value Windows does not document
/// today is never taken for one it does: it throws, and the caller asks Hello (an availability) or denies (a result).
/// </summary>
public static class UserPresenceMapping
{
    public static UserPresenceAvailability ToAvailability(UserConsentVerifierAvailability availability) => availability switch
    {
        UserConsentVerifierAvailability.Available => UserPresenceAvailability.Available,
        UserConsentVerifierAvailability.DeviceNotPresent => UserPresenceAvailability.DeviceNotPresent,
        UserConsentVerifierAvailability.NotConfiguredForUser => UserPresenceAvailability.NotConfiguredForUser,
        UserConsentVerifierAvailability.DisabledByPolicy => UserPresenceAvailability.DisabledByPolicy,
        UserConsentVerifierAvailability.DeviceBusy => UserPresenceAvailability.DeviceBusy,
        _ => throw new ArgumentOutOfRangeException(nameof(availability), availability, "Windows answered an availability of Windows Hello that Agent Studio does not know."),
    };

    public static UserPresenceResult ToResult(UserConsentVerificationResult result) => result switch
    {
        UserConsentVerificationResult.Verified => UserPresenceResult.Verified,
        UserConsentVerificationResult.DeviceNotPresent => UserPresenceResult.DeviceNotPresent,
        UserConsentVerificationResult.NotConfiguredForUser => UserPresenceResult.NotConfiguredForUser,
        UserConsentVerificationResult.DisabledByPolicy => UserPresenceResult.DisabledByPolicy,
        UserConsentVerificationResult.DeviceBusy => UserPresenceResult.DeviceBusy,
        UserConsentVerificationResult.RetriesExhausted => UserPresenceResult.RetriesExhausted,
        UserConsentVerificationResult.Canceled => UserPresenceResult.Canceled,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Windows answered a result of Windows Hello that Agent Studio does not know."),
    };
}
