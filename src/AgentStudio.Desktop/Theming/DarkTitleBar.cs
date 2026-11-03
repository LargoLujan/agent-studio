using System.Runtime.InteropServices;

namespace AgentStudio.Desktop.Theming;

/// <summary><c>DwmSetWindowAttribute(hwnd, attribute, ref value, size)</c>: an HRESULT, negative when DWM did not take it.</summary>
public delegate int SetWindowAttributeFunction(nint hwnd, int attribute, ref int value, int size);

/// <summary>
/// The dark title bar of Windows on one window: DWM's <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> set to <c>TRUE</c>. The attribute
/// is 20 since Windows 10 2004 and in Windows 11, and was 19 in the builds before; when DWM takes neither, or is not there
/// at all, nothing happens and the title bar stays as Windows draws it.
/// </summary>
public sealed class DarkTitleBar
{
    /// <summary><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (Windows 10 2004, build 19041, and Windows 11).</summary>
    public const int ImmersiveDarkModeAttribute = 20;

    /// <summary>The same attribute in the builds before Windows 10 2004 (17763 to 18363), where it was not documented.</summary>
    public const int LegacyImmersiveDarkModeAttribute = 19;

    private readonly SetWindowAttributeFunction _setAttribute;

    /// <summary>The calls go to DWM.</summary>
    public DarkTitleBar()
        : this(NativeMethods.DwmSetWindowAttribute)
    {
    }

    /// <param name="setAttribute">The DWM call itself; the tests give one that records its arguments.</param>
    public DarkTitleBar(SetWindowAttributeFunction setAttribute)
    {
        ArgumentNullException.ThrowIfNull(setAttribute);
        _setAttribute = setAttribute;
    }

    /// <summary>Asks DWM for the dark title bar of <paramref name="hwnd"/>; never throws.</summary>
    /// <returns>Whether DWM took one of the two attributes.</returns>
    public bool Apply(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return Set(hwnd, ImmersiveDarkModeAttribute) || Set(hwnd, LegacyImmersiveDarkModeAttribute);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows without DWM: the title bar stays light, and the window works the same.
            return false;
        }
    }

    private bool Set(nint hwnd, int attribute)
    {
        var enabled = 1;
        return _setAttribute(hwnd, attribute, ref enabled, sizeof(int)) >= 0;
    }

    /// <summary>
    /// <c>DllImport</c> and not <c>LibraryImport</c>: the desktop project does not allow unsafe code, which the source
    /// generator needs, and this signature is blittable without it.
    /// </summary>
    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    }
}
