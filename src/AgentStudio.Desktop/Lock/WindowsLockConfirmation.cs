using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentStudio.Application.Security;

namespace AgentStudio.Desktop.Lock;

/// <summary>Whether the Windows session of the app is locked, as the session manager of Windows says it (docs/17 D-40 (a)).</summary>
public enum SessionLockState
{
    /// <summary>Windows did not say, or what it said is not one of the two states it documents.</summary>
    Unknown,

    /// <summary>The session is locked: Windows shows its own lock screen.</summary>
    Locked,

    /// <summary>The session is unlocked.</summary>
    Unlocked,
}

/// <summary>
/// The state of the Windows session that the verified lock reads (docs/17 D-40 (a)): it locks the session and reads whether it
/// is locked. The app's is <see cref="WindowsSessionState"/>; tests use a double, so no test ever locks the session of the
/// person running them.
/// </summary>
public interface IWindowsSessionState
{
    /// <summary>Asks Windows to lock the session (<c>LockWorkStation</c>); false when Windows refused. Locking takes effect a moment later.</summary>
    bool Lock();

    /// <summary>Whether the session is locked now, read from the session manager; <see cref="SessionLockState.Unknown"/> when Windows cannot say.</summary>
    SessionLockState Read();
}

/// <summary>
/// The verified lock of Windows (docs/17 D-40 (a), ADR-0053): the alternative to Windows Hello for the actions of the fixed
/// barrier on a computer without it. It locks Windows, and the person unlocks it with their PIN or password, which Agent
/// Studio never sees. It trusts the state of the session, never a message: a notice of an unlock can be forged by any program
/// of the session (residual of ADR-0037, decision 5), the state the session manager keeps cannot.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Before locking, the session must read as unlocked: the person has just answered in the app. Otherwise the state
/// cannot be trusted, nothing is locked and the result is <see cref="WindowsLockResult.NotVerified"/>.</item>
/// <item>After locking, the session must read as locked within <see cref="LockedWithin"/>, and only then as unlocked: that
/// unlock came after the request. A session never seen locked is <see cref="WindowsLockResult.NotVerified"/>; a lock Windows
/// refused is <see cref="WindowsLockResult.LockFailed"/>; no unlock within the time given is
/// <see cref="WindowsLockResult.TimedOut"/>.</item>
/// <item>The state is read every <see cref="PollInterval"/> with the timestamps of the <see cref="TimeProvider"/>, which never
/// go back. A read that fails counts as <see cref="SessionLockState.Unknown"/>: it confirms nothing.</item>
/// </list>
/// </remarks>
public sealed class WindowsLockConfirmation : IWindowsLockConfirmation
{
    /// <summary>How long Windows has to show its lock screen after it accepted to lock (<c>LockWorkStation</c> returns at once).</summary>
    public static readonly TimeSpan LockedWithin = TimeSpan.FromSeconds(10);

    /// <summary>How often the state of the session is read while the lock is waited on.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWindowsSessionState _session;
    private readonly TimeProvider _time;

    public WindowsLockConfirmation(IWindowsSessionState session, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(time);
        _session = session;
        _time = time;
    }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    public async Task<WindowsLockResult> LockAndConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (Read() != SessionLockState.Unlocked)
        {
            return WindowsLockResult.NotVerified;
        }

        if (!_session.Lock())
        {
            return WindowsLockResult.LockFailed;
        }

        var start = _time.GetTimestamp();
        var seenLocked = false;
        while (true)
        {
            var state = Read();
            if (state == SessionLockState.Locked)
            {
                seenLocked = true;
            }
            else if (state == SessionLockState.Unlocked && seenLocked)
            {
                return WindowsLockResult.UnlockedAfterRequest;
            }

            var elapsed = _time.GetElapsedTime(start);
            if (!seenLocked && elapsed >= LockedWithin)
            {
                return WindowsLockResult.NotVerified;
            }

            if (elapsed >= timeout)
            {
                return WindowsLockResult.TimedOut;
            }

            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private SessionLockState Read()
    {
        try
        {
            return _session.Read();
        }
#pragma warning disable CA1031 // A state Windows could not give confirms nothing: it is read as unknown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Debug.WriteLine($"Windows lock: the state of the session could not be read ({ex.GetType().Name}: {ex.Message}).");
            return SessionLockState.Unknown;
        }
    }
}

/// <summary>
/// <see cref="IWindowsSessionState"/> over Windows: <c>LockWorkStation</c>, and <c>WTSQuerySessionInformationW</c> with
/// <c>WTSSessionInfoEx</c> for the <c>SessionFlags</c> of the session of the app (<c>WTS_SESSIONSTATE_LOCK</c> is 0 and
/// <c>WTS_SESSIONSTATE_UNLOCK</c> is 1; the reversed meaning documented for Windows 7 and Windows Server 2008 R2 does not apply:
/// the app needs Windows 11). It reads the level, the flags and nothing else: never the name of the user or of the domain.
/// Tests never lock with it.
/// </summary>
public sealed class WindowsSessionState : IWindowsSessionState
{
    public bool Lock() => SessionNativeMethods.LockWorkStation();

    /// <exception cref="Win32Exception">Windows could not say.</exception>
    public SessionLockState Read()
    {
        if (!SessionNativeMethods.WTSQuerySessionInformationW(
                SessionNativeMethods.CurrentServer, SessionNativeMethods.CurrentSession, SessionNativeMethods.SessionInfoEx, out var buffer, out var bytes))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not say whether the session is locked.");
        }

        try
        {
            if (buffer == 0 || bytes < SessionNativeMethods.MinimumBytes)
            {
                return SessionLockState.Unknown;
            }

            var info = Marshal.PtrToStructure<SessionNativeMethods.InfoExHeader>(buffer);
            return SessionNativeMethods.StateOf(info.Level, info.SessionFlags);
        }
        finally
        {
            SessionNativeMethods.WTSFreeMemory(buffer);
        }
    }
}

/// <summary>
/// The functions of the verified lock. <c>DllImport</c> and not <c>LibraryImport</c>, as in <c>LockNativeMethods</c>: the
/// desktop project does not allow unsafe code, and these signatures are blittable without it.
/// </summary>
internal static class SessionNativeMethods
{
    /// <summary><c>WTS_CURRENT_SERVER_HANDLE</c>: this computer.</summary>
    internal static readonly nint CurrentServer = 0;

    /// <summary><c>WTS_CURRENT_SESSION</c>: the session of the calling process.</summary>
    internal const int CurrentSession = -1;

    /// <summary><c>WTSSessionInfoEx</c> of <c>WTS_INFO_CLASS</c>.</summary>
    internal const int SessionInfoEx = 25;

    /// <summary><c>WTS_SESSIONSTATE_LOCK</c>.</summary>
    internal const int StateLock = 0;

    /// <summary><c>WTS_SESSIONSTATE_UNLOCK</c>.</summary>
    internal const int StateUnlock = 1;

    /// <summary>
    /// The bytes <see cref="InfoExHeader"/> reads from <c>WTSINFOEXW</c>: its level (4 bytes, then 4 of padding, because the
    /// union of the levels holds <c>LARGE_INTEGER</c>s) and the first three fields of <c>WTSINFOEX_LEVEL1_W</c>, the last of
    /// them the <c>SessionFlags</c>. The whole structure is 232 bytes; a shorter answer says nothing.
    /// </summary>
    internal const int MinimumBytes = 20;

    /// <summary>What a level and its <c>SessionFlags</c> say: only level 1 and the two documented values say anything.</summary>
    internal static SessionLockState StateOf(uint level, int flags) => (level, flags) switch
    {
        (1, StateLock) => SessionLockState.Locked,
        (1, StateUnlock) => SessionLockState.Unlocked,
        _ => SessionLockState.Unknown,
    };

    /// <summary>The start of <c>WTSINFOEXW</c>: the level and, at offset 16, the <c>SessionFlags</c> of level 1. Nothing else is read.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InfoExHeader
    {
        [FieldOffset(0)]
        public uint Level;

        [FieldOffset(16)]
        public int SessionFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LockWorkStation();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void WTSFreeMemory(nint memory);
}
