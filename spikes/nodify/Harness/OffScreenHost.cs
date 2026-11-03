using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NodifySpike.Harness;

/// <summary>
/// Hosts the harness window where the user can neither see nor reach it: far to the right of every monitor, not in the
/// taskbar, not in Alt+Tab and shown without activating it. A scripted run must not put a window on the user's screen, so the position is
/// checked against the virtual desktop <b>before</b> the window is shown, and the run refuses to start if it would be visible.
/// </summary>
internal static class OffScreenHost
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    /// <summary>The window that had the foreground when the run started, to hand it back if the harness window ever takes it.</summary>
    public static nint ForegroundBefore { get; private set; }

    /// <summary>The foreground window right after the host was shown (it must still be <see cref="ForegroundBefore"/>).</summary>
    public static nint ForegroundAfterShow { get; private set; }

    /// <summary>Creates the (hidden) native window, moves it off every monitor and only then shows it without activating it.</summary>
    public static void ShowOffScreen(Window window)
    {
        ForegroundBefore = WindowInput.GetForegroundWindow();
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.None;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        var handle = new WindowInteropHelper(window).EnsureHandle(); // creates the HWND, still hidden

        var left = WindowInput.GetSystemMetrics(SmXVirtualScreen);
        var top = WindowInput.GetSystemMetrics(SmYVirtualScreen);
        var width = WindowInput.GetSystemMetrics(SmCxVirtualScreen);
        var height = WindowInput.GetSystemMetrics(SmCyVirtualScreen);
        var x = left + width + 2000;
        var y = top + 100;
        if (x > 30000)
        {
            throw new InvalidOperationException("The virtual desktop is too wide to park the window beyond it (window messages carry 16-bit coordinates).");
        }

        SetWindowLongPtr(handle, GwlExStyle, (nint)(GetWindowLongPtr(handle, GwlExStyle) | (nint)WsExToolWindow));
        WindowInput.SetWindowPos(handle, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        AssertOffScreen(handle, left, top, width, height, "before showing it");

        window.Show(); // ShowActivated = false: SW_SHOWNOACTIVATE
        try
        {
            AssertOffScreen(handle, left, top, width, height, "after showing it");
        }
        catch (InvalidOperationException)
        {
            window.Hide();
            throw;
        }

        ForegroundAfterShow = WindowInput.GetForegroundWindow();
    }

    private static void AssertOffScreen(nint handle, int left, int top, int width, int height, string when)
    {
        WindowInput.GetWindowRect(handle, out var rect);
        var visible = rect.Right > left && rect.Left < left + width && rect.Bottom > top && rect.Top < top + height;
        if (visible)
        {
            throw new InvalidOperationException($"Refusing to run: the harness window would be on a monitor {when} (rect {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}; desktop {left},{top},{width}x{height}).");
        }
    }
}
