using System.Windows;
using System.Windows.Threading;
using AgentStudio.Desktop.Views.Background;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// The status panel in a window of its own next to the notification area (docs/16 D-15): it opens in the corner of the
/// work area, above the taskbar, reads the figures again when it opens and every few seconds while it is open, and hides
/// when it loses the focus or with Esc. Create it on the UI thread. The tests never use it: they render the view
/// (<see cref="StatusPanelView"/>) without a window.
/// </summary>
public sealed class WindowStatusPanelPresenter : IStatusPanelPresenter, IDisposable
{
    /// <summary>How often an open panel reads the logs and the quota again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private const double Margin = 12;

    /// <summary>A click on the icon takes the focus from the panel before the click arrives: within this, it only closes it.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    private readonly StatusPanelViewModel _panel;
    private readonly DispatcherTimer _timer;
    private StatusPanelWindow? _window;
    private DateTime _hiddenAt = DateTime.MinValue;

    public WindowStatusPanelPresenter(StatusPanelViewModel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        _panel = panel;
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await _panel.RefreshAsync().ConfigureAwait(true);
    }

    public bool IsShown => _window is { IsVisible: true };

    public void Toggle()
    {
        if (IsShown)
        {
            Hide();
        }
        else if (DateTime.UtcNow - _hiddenAt > ReopenGuard)
        {
            Show();
        }
    }

    public void Show()
    {
        var window = _window ??= Create();
        _ = _panel.RefreshAsync();
        window.Show();
        Place(window);
        window.Activate();
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        if (_window is { IsVisible: true } window)
        {
            window.Hide();
            _hiddenAt = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _window?.Close();
        _window = null;
    }

    private StatusPanelWindow Create()
    {
        var window = new StatusPanelWindow { DataContext = _panel };
        window.Deactivated += (_, _) => Hide();
        window.IsVisibleChanged += (_, _) =>
        {
            if (!window.IsVisible)
            {
                _timer.Stop();
                _hiddenAt = DateTime.UtcNow;
            }
        };
        window.SizeChanged += (_, _) => Place(window);
        return window;
    }

    /// <summary>Bottom right of the work area (where the notification area usually is), kept inside it.</summary>
    private static void Place(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Left = Math.Max(area.Left, area.Right - window.ActualWidth - Margin);
        window.Top = Math.Max(area.Top, area.Bottom - window.ActualHeight - Margin);
    }
}
