using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Views;

public partial class ExplorerView : UserControl
{
    public ExplorerView() => InitializeComponent();

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The event bubbles through every parent item; only the innermost one handles it.
        if (sender is TreeViewItem { DataContext: ExplorerNodeViewModel node } && DataContext is ExplorerViewModel explorer)
        {
            e.Handled = true;
            explorer.OpenCommand.Execute(node);
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is ExplorerNodeViewModel node && DataContext is ExplorerViewModel explorer)
        {
            e.Handled = true;
            explorer.OpenCommand.Execute(node);
        }
    }
}

/// <summary>
/// The left margin of the content of a tree row: one <see cref="Step"/> per item above it. The row itself spans the whole
/// width of the tree (so a click anywhere on it is a click on the item), and the indent of the folders it is in is drawn
/// inside it instead of around it. Bound to the <see cref="TreeViewItem"/> of the row.
/// </summary>
public sealed class TreeIndentConverter : IValueConverter
{
    /// <summary>The indent of one level, in device-independent pixels.</summary>
    public double Step { get; set; } = 16;

    /// <summary>How many tree items an item is nested in: 0 for the items of the tree itself.</summary>
    public static int Depth(TreeViewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var depth = 0;
        for (var parent = ItemsControl.ItemsControlFromItemContainer(item); parent is TreeViewItem outer; parent = ItemsControl.ItemsControlFromItemContainer(outer))
        {
            depth++;
        }

        return depth;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is TreeViewItem item ? Depth(item) * Step : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("The indent of a tree row is computed, never edited.");
}
