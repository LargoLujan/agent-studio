using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Views;

/// <summary>
/// The "go to file" list (Ctrl+P). The search box keeps the keyboard focus while the list is used:
/// Up/Down move the highlight, Enter chooses it, Esc closes. Clicking away also closes it.
/// </summary>
public partial class QuickOpenWindow : Window
{
    public QuickOpenWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
    }

    private QuickOpenViewModel? ViewModel => DataContext as QuickOpenViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                viewModel.MoveSelection(1);
                break;
            case Key.Up:
                viewModel.MoveSelection(-1);
                break;
            case Key.Enter:
                viewModel.AcceptCommand.Execute(null);
                break;
            case Key.Escape:
                viewModel.CloseCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Keeps the highlighted row in view while the highlight moves with the keyboard.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is { } selected)
        {
            ResultList.ScrollIntoView(selected);
        }
    }

    /// <summary>A click on a row chooses it (the press already highlighted it).</summary>
    private void OnListMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ResultList, source) is ListBoxItem
            && ViewModel is { } viewModel)
        {
            e.Handled = true;
            viewModel.AcceptCommand.Execute(null);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => ViewModel?.CloseCommand.Execute(null);

    private void OnClosed(object? sender, EventArgs e) => ViewModel?.CloseCommand.Execute(null);
}
