using System.Diagnostics;
using System.IO;

namespace AgentStudio.Desktop.Background;

/// <summary>One entry of the tray menu: its text, what it does, whether it can be chosen now, and its submenu.</summary>
public sealed record TrayMenuItem(string Text, Func<Task>? Action = null, bool IsEnabled = true, IReadOnlyList<TrayMenuItem>? Children = null)
{
    /// <summary>A line between groups of entries.</summary>
    public static TrayMenuItem Separator => new(string.Empty) { IsSeparator = true };

    public bool IsSeparator { get; init; }
}

/// <summary>
/// A notification of the tray icon (docs/16 D-14): a title and a text, never anything of a project (no name, no path, no
/// run), since Windows may show it where others can see it.
/// </summary>
public sealed record TrayNotification(string Title, string Text);

/// <summary>
/// The icon of the app in the notification area (docs/16 D-14). The app has <see cref="NotifyIconTray"/>; the tests a
/// double, so no icon ever shows up on the screen of whoever runs them.
/// </summary>
public interface ITrayIcon : IDisposable
{
    /// <summary>Puts the icon in the notification area; it stays until it is disposed.</summary>
    void Show();

    /// <summary>The text shown on hovering it, at most <see cref="TrayToolTip.MaxLength"/> characters.</summary>
    void SetToolTip(string text);

    /// <summary>What the menu of the right button holds: built again each time it opens, so it is never stale.</summary>
    void SetMenu(Func<IReadOnlyList<TrayMenuItem>> build);

    void Notify(TrayNotification notification);

    /// <summary>A click with the left button: the status panel.</summary>
    event EventHandler? Clicked;

    /// <summary>The person clicked a notification: the window.</summary>
    event EventHandler? NotificationClicked;
}

/// <summary>Opens a folder for the person (the app's logs). An interface so the tests open nothing.</summary>
public interface IFolderOpener
{
    void Open(string folder);
}

/// <summary>Opens a folder in the Windows file explorer, creating it first when it does not exist yet.</summary>
public sealed class ExplorerFolderOpener : IFolderOpener
{
    public void Open(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);
        using var explorer = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
    }
}

/// <summary>The status panel of the tray (docs/16 D-15), shown next to the notification area.</summary>
public interface IStatusPanelPresenter
{
    bool IsShown { get; }

    /// <summary>Shows the panel when hidden, and hides it when shown: what a click on the icon does.</summary>
    void Toggle();

    void Show();

    void Hide();
}
