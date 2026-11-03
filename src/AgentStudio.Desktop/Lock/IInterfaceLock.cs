using AgentStudio.Application.Security;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// What the rest of the app sees of the lock of the interface (docs/16 D-13, F8-10): whether it is locked, when that changes,
/// and, for the approvals, the wait for the unlock. The work goes on while it is locked, and the questions wait. Only their
/// action and since when they wait are shown on the lock screen, never their description nor their paths: locking hides the
/// content. The window and the tray hide theirs while <see cref="IsLocked"/>, following <see cref="Changed"/>.
/// </summary>
public interface IInterfaceLock
{
    /// <summary>Raised, on the thread of the change, when it locks or unlocks, and when what the lock screen shows changes.</summary>
    event EventHandler? Changed;

    /// <summary>Whether the interface is locked now.</summary>
    bool IsLocked { get; }

    /// <summary>
    /// Returns at once while the interface is unlocked; while it is locked, waits for the unlock with
    /// <paramref name="question"/> on the lock screen. The token returned is cancelled when the interface locks again: a
    /// question open then is withdrawn, and asked again once unlocked.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    Task<CancellationToken> WaitUntilUnlockedAsync(ApprovalRequest question, CancellationToken cancellationToken);
}
