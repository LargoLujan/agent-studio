using AgentStudio.Desktop.Background;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>The tray icon, as a record of what it was told: never in the notification area of whoever runs the tests.</summary>
internal sealed class FakeTrayIcon : ITrayIcon
{
    private Func<IReadOnlyList<TrayMenuItem>>? _build;

    public bool Shown { get; private set; }

    public bool Disposed { get; private set; }

    public string? ToolTip { get; private set; }

    public List<TrayNotification> Notifications { get; } = [];

    public event EventHandler? Clicked;

    public event EventHandler? NotificationClicked;

    public void Show() => Shown = true;

    public void SetToolTip(string text) => ToolTip = text;

    public void SetMenu(Func<IReadOnlyList<TrayMenuItem>> build) => _build = build;

    public void Notify(TrayNotification notification) => Notifications.Add(notification);

    /// <summary>The menu as the person would open it now.</summary>
    public IReadOnlyList<TrayMenuItem> OpenMenu() => _build?.Invoke() ?? [];

    public void Click() => Clicked?.Invoke(this, EventArgs.Empty);

    public void ClickNotification() => NotificationClicked?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Disposed = true;
}

/// <summary>The status panel, counted: never on screen.</summary>
internal sealed class FakePanelPresenter : IStatusPanelPresenter
{
    public bool IsShown { get; private set; }

    public int Toggles { get; private set; }

    public void Toggle()
    {
        Toggles++;
        IsShown = !IsShown;
    }

    public void Show() => IsShown = true;

    public void Hide() => IsShown = false;
}

/// <summary>Records the folders it was asked to open; opens none.</summary>
internal sealed class FakeFolderOpener : IFolderOpener
{
    public List<string> Opened { get; } = [];

    public void Open(string folder) => Opened.Add(folder);
}

/// <summary>Finds an entry of a menu by the start of its text, down its submenus.</summary>
internal static class Menus
{
    public static TrayMenuItem Find(IReadOnlyList<TrayMenuItem> items, string startsWith) =>
        Flatten(items).FirstOrDefault(i => i.Text.StartsWith(startsWith, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No entry starts with «{startsWith}»: {string.Join(" | ", Flatten(items).Select(i => i.Text))}");

    public static IEnumerable<TrayMenuItem> Flatten(IReadOnlyList<TrayMenuItem> items) =>
        items.SelectMany(i => (IEnumerable<TrayMenuItem>)[i, .. Flatten(i.Children ?? [])]);

    public static Task Choose(IReadOnlyList<TrayMenuItem> items, string startsWith)
    {
        var item = Find(items, startsWith);
        Assert.True(item.IsEnabled, $"«{item.Text}» cannot be chosen now.");
        return item.Action?.Invoke() ?? throw new InvalidOperationException($"«{item.Text}» does nothing.");
    }
}
