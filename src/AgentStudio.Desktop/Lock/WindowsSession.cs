using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// The Windows session of the app (ADR-0037): <c>LockWorkStation</c> to lock it, and <see cref="SystemEvents.SessionSwitch"/>
/// to know when the person unlocked it. Tests never build it: locking the session of the person running them is out of the
/// question. A program running with the same session can post a forged notice of an unlock to the app (residual 1 of
/// ADR-0018); <see cref="InterfaceLock"/> only listens to one after it asked Windows to lock.
/// </summary>
public sealed class WindowsSession : IWindowsSession, IDisposable
{
    private bool _disposed;

    public WindowsSession() => SystemEvents.SessionSwitch += OnSessionSwitch;

    public event EventHandler? Unlocked;

    public bool Lock() => LockNativeMethods.LockWorkStation();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // SystemEvents keeps its handlers in a static list: unsubscribing is what lets this go.
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            Unlocked?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// The time since the last input of the person, keyboard or mouse, in the whole session (<c>GetLastInputInfo</c>): the idle
/// lock counts from it, so working in another app is not idle. The app's is <see cref="SessionIdleInput"/>.
/// </summary>
public interface IIdleInput
{
    /// <summary>How long there has been no input in the session.</summary>
    /// <exception cref="Win32Exception">Windows could not say.</exception>
    TimeSpan IdleFor();
}

/// <summary>
/// <see cref="IIdleInput"/> with <c>GetLastInputInfo</c>: it reads a tick count and nothing else, and simulates nothing. The
/// tick count wraps every 49.7 days; the difference, in unsigned arithmetic, does not.
/// </summary>
public sealed class SessionIdleInput : IIdleInput
{
    public TimeSpan IdleFor()
    {
        var info = new LockNativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<LockNativeMethods.LastInputInfo>() };
        if (!LockNativeMethods.GetLastInputInfo(ref info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not say when the last input was.");
        }

        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }
}

/// <summary>
/// The user32 functions of the lock. <c>DllImport</c> and not <c>LibraryImport</c>: the desktop project does not allow unsafe
/// code, which the source generator needs, and these signatures are blittable without it.
/// </summary>
internal static class LockNativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }
}
