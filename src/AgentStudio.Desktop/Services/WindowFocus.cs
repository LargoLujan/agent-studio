using System.Runtime.InteropServices;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// Where Windows has the keyboard for the UI thread (user32 <c>GetFocus</c>). A tab that held it in a WebView2 (the editor)
/// takes it along when it closes: the window stays active with the keyboard nowhere, and its shortcuts are dead until a
/// click. WPF does not notice, so the window asks Windows.
/// </summary>
internal static class WindowFocus
{
    /// <summary>Whether the keyboard of this thread is in <paramref name="window"/> or in a window inside it (a WebView2, say).</summary>
    public static bool IsWithin(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        var focus = FocusNativeMethods.GetFocus();
        return focus != 0 && (focus == window || FocusNativeMethods.IsChild(window, focus));
    }

    /// <summary>
    /// The user32 functions of the focus. <c>DllImport</c> and not <c>LibraryImport</c>: the desktop project does not allow
    /// unsafe code, which the source generator needs, and these signatures are blittable without it.
    /// </summary>
    private static class FocusNativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetFocus();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsChild(nint parent, nint child);
    }
}
