using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// The main window of the app as the background needs it (<c>Application.Current.MainWindow</c>): shown again restored and
/// in front, or hidden. It is never closed by the background: the app lives in the tray until it exits.
/// </summary>
public sealed class WpfShellWindow : IShellWindow
{
    public void Show()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } window)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            // Back to how it was before it was minimized, maximized included: Windows remembers it and SW_RESTORE honours it,
            // where WindowState = Normal left a maximized window at its normal size.
            _ = ShellNativeMethods.ShowWindow(new WindowInteropHelper(window).Handle, ShellNativeMethods.SwRestore);
        }

        window.Activate();
    }

    /// <summary>
    /// The user32 function that restores a window. <c>DllImport</c> and not <c>LibraryImport</c>: the desktop project does not
    /// allow unsafe code, which the source generator needs, and this signature is blittable without it.
    /// </summary>
    private static class ShellNativeMethods
    {
        /// <summary>Activates and shows the window as it was before it was minimized (normal or maximized).</summary>
        internal const int SwRestore = 9;

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);
    }

    /// <summary>
    /// Hides the window once the dispatcher gets to it: the managed exit may ask from inside the close of the window (closing
    /// it with «Seguir en la bandeja», or while the app waits to exit), and the window is not hidden while it is closing.
    /// </summary>
    public void Hide()
    {
        if (System.Windows.Application.Current?.MainWindow is { } window)
        {
            _ = window.Dispatcher.BeginInvoke(window.Hide);
        }
    }
}

/// <summary>Ends the app: <c>Application.Current.Shutdown()</c>, on its thread.</summary>
public sealed class WpfAppShutdown : IAppShutdown
{
    public void Shutdown()
    {
        if (System.Windows.Application.Current is { } app)
        {
            app.Dispatcher.BeginInvoke(() => app.Shutdown());
        }
    }
}
