using System.Windows;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Views;

/// <summary>
/// Shows and hides the "go to file" list of a main window. A seam of its own because showing it activates a window:
/// tests replace it so that nothing appears on screen or takes the foreground.
/// </summary>
public interface IQuickOpenPresenter
{
    /// <summary>Shows the list anchored to <paramref name="owner"/>; does nothing when it is already shown.</summary>
    void Show(Window owner, QuickOpenViewModel viewModel);

    /// <summary>Closes the list if it is shown.</summary>
    void Close();
}

/// <summary>
/// Shows the list in a window of its own, near the top of the owner. It cannot be a panel on top of the document
/// area: the editor is an HWND (WebView2) and WPF content never draws over an HWND.
/// </summary>
public sealed class WindowQuickOpenPresenter : IQuickOpenPresenter
{
    private QuickOpenWindow? _window;

    public void Show(Window owner, QuickOpenViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_window is not null || !owner.IsVisible)
        {
            return; // Already on screen, or the owner is not shown (nothing to anchor the list to).
        }

        var window = new QuickOpenWindow { Owner = owner, DataContext = viewModel };
        var origin = owner.PointToScreen(new Point(0, 0));
        var fromDevice = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = fromDevice.Transform(origin);
        window.Left = topLeft.X + ((owner.ActualWidth - window.Width) / 2);
        window.Top = topLeft.Y + 64;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        _window = window;
        window.Show();
        window.Activate();
    }

    public void Close()
    {
        var window = _window;
        _window = null;
        if (window is { IsVisible: true })
        {
            window.Close(); // Not when the user already closed it: a closed window cannot be closed again.
        }
    }
}
