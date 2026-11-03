using System.Runtime.InteropServices;

namespace NodifySpike.Harness;

/// <summary>
/// How long ago the user last touched the keyboard or the mouse. Windows refuses to let a background program take the
/// foreground for a short time after the user's own input and allows it later (the "foreground lock"). Measured on this
/// machine: refused with the last input 0.2 s earlier, allowed after 10 s of silence, although the registry value
/// <c>ForegroundLockTimeout</c> says 200 s. WPF's keyboard focus is a Win32 <c>SetFocus</c>, which Windows may turn into a
/// foreground change, so the harness takes the first focus only right after the user's input (or when nobody is there).
/// </summary>
internal static class UserActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Seconds since the last keyboard or mouse input in this session (window messages sent by the harness do not count).</summary>
    public static double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000d : 0;
    }
}
