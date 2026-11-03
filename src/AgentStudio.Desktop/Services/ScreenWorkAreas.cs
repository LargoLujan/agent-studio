using System.Runtime.InteropServices;
using System.Windows;

namespace AgentStudio.Desktop.Services;

/// <summary>The work areas of the monitors connected now (each without its taskbar), in device-independent pixels.</summary>
public interface IScreenWorkAreas
{
    /// <param name="dpi">The DPI of the window to place: the device pixels of the monitors are divided by its scale.</param>
    IReadOnlyList<Rect> Current(DpiScale dpi);
}

/// <summary>
/// The monitors as Windows reports them (user32 <c>EnumDisplayMonitors</c> and <c>GetMonitorInfo</c>, in device pixels),
/// converted to the device-independent pixels WPF places windows in with the DPI of the window. No Windows Forms: it stays
/// in the icon of the tray (ADR-0036), and this runs before the window is first painted.
/// </summary>
public sealed class SystemScreenWorkAreas : IScreenWorkAreas
{
    public IReadOnlyList<Rect> Current(DpiScale dpi)
    {
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        var areas = new List<Rect>();

        bool Collect(nint monitor, nint deviceContext, ref MonitorNativeMethods.Rect32 bounds, nint data)
        {
            var info = new MonitorNativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorNativeMethods.MonitorInfo>() };
            if (MonitorNativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var work = info.Work;
                areas.Add(new Rect(work.Left / scaleX, work.Top / scaleY, (work.Right - work.Left) / scaleX, (work.Bottom - work.Top) / scaleY));
            }

            return true;
        }

        MonitorNativeMethods.MonitorEnumProc callback = Collect;
        _ = MonitorNativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return areas;
    }

    /// <summary>
    /// The user32 functions of the monitors. <c>DllImport</c> and not <c>LibraryImport</c>: the desktop project does not allow
    /// unsafe code, which the source generator needs, and these signatures are blittable without it.
    /// </summary>
    private static class MonitorNativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool MonitorEnumProc(nint monitor, nint deviceContext, ref Rect32 bounds, nint data);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(nint deviceContext, nint clip, MonitorEnumProc callback, nint data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect32
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public uint Size;
            public Rect32 Monitor;
            public Rect32 Work;
            public uint Flags;
        }
    }
}
