using System.Windows;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// Whether the animations of the canvas run (docs/17 D-15 and F13-7): never when the person turned on «Reducir movimiento» and
/// never when Windows has the animations of its windows off. Without animation the state is still in the text and the icon.
/// </summary>
public static class MotionPolicy
{
    /// <summary>The animations of the canvas run: neither the own setting nor the option of Windows turns them off.</summary>
    public static bool AnimationsEnabled(bool reduceMotion, bool windowsAnimates) => !reduceMotion && windowsAnimates;

    /// <summary>Whether Windows has the animations of its windows on (<c>SystemParameters.ClientAreaAnimation</c>).</summary>
    public static bool WindowsAnimates => SystemParameters.ClientAreaAnimation;

    /// <summary>Raised when «Reducir movimiento» was saved: the canvases that are open apply it at once, without waiting for the next live event.</summary>
    public static event EventHandler? SettingChanged;

    /// <summary>Tells the open canvases that the setting changed (the settings screen calls it when it saves).</summary>
    public static void NotifySettingChanged() => SettingChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>The animations run, reading the option of Windows now.</summary>
    public static bool AnimationsEnabled(bool reduceMotion) => AnimationsEnabled(reduceMotion, WindowsAnimates);
}
