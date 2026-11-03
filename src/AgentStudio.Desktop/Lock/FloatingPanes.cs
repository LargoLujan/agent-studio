using System.Windows;
using AvalonDock;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// The floating windows of the dock, as the lock needs them (docs/16 D-13, ADR-0037): hidden while the interface is locked,
/// and shown again after. The app's is <see cref="DockFloatingPanes"/>; tests put a double here, since a floating window is a
/// real window on the screen.
/// </summary>
public interface IFloatingPanes
{
    /// <summary>Hides the floating panes that are shown, and remembers them.</summary>
    void HideShown();

    /// <summary>Shows again those <see cref="HideShown"/> hid that still float.</summary>
    void RestoreHidden();
}

/// <summary>
/// When the floating panes of the dock are hidden (docs/16 D-13 and D-14): while the interface is locked, and while the main
/// window is hidden in the tray, since Windows hides the windows a window owns when it is minimized, not when it is hidden. They
/// come back once neither holds: a pane hidden for both reasons is hidden once and shown once, after the last of them.
/// </summary>
public sealed class FloatingPanesVisibility(IFloatingPanes panes)
{
    /// <summary>Whether the floating panes are hidden now.</summary>
    public bool AreHidden { get; private set; }

    /// <summary>Hides or shows the floating panes as the lock and the window say.</summary>
    /// <param name="locked">The interface is locked.</param>
    /// <param name="hiddenInTray">The main window was shown and is hidden now (it lives in the tray).</param>
    public void Update(bool locked, bool hiddenInTray)
    {
        var hide = locked || hiddenInTray;
        if (hide == AreHidden)
        {
            return;
        }

        AreHidden = hide;
        if (hide)
        {
            panes.HideShown();
        }
        else
        {
            panes.RestoreHidden();
        }
    }
}

/// <summary>
/// The floating windows of AvalonDock (<see cref="DockingManager.FloatingWindows"/>): panes and documents taken out of the main
/// window (the terminal and the editor among them, which are WebView2 windows of their own). AvalonDock binds their visibility to
/// the layout again when they are shown, so hiding them here changes nothing of the layout.
/// </summary>
public sealed class DockFloatingPanes(DockingManager dock) : IFloatingPanes
{
    private readonly List<Window> _hidden = [];

    public void HideShown()
    {
        foreach (var window in dock.FloatingWindows.Where(w => w.IsVisible).ToList())
        {
            window.Hide();
            _hidden.Add(window);
        }
    }

    public void RestoreHidden()
    {
        var floating = dock.FloatingWindows.ToHashSet();
        foreach (var window in _hidden.Where(floating.Contains))
        {
            window.Show();
        }

        _hidden.Clear();
    }
}
