using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// What the inventory of the interface (docs/17 D-62) asks of a laid-out screen: its visual tree, which elements are shown, the
/// text of each and its accessible name. Everything is read from the tree of layout, never from pixels: the runner of the CI has
/// no ClearType and no animations, so a measure in pixels would not be the same on every machine.
/// </summary>
internal static class UiTree
{
    /// <summary>The element and every visual descendant of it, in tree order.</summary>
    public static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        yield return root;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var inner in All(VisualTreeHelper.GetChild(root, i)))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The elements of type <typeparamref name="T"/> that are shown on the screen.</summary>
    public static IEnumerable<T> Shown<T>(FrameworkElement root)
        where T : FrameworkElement => All(root).OfType<T>().Where(e => IsShown(e, root));

    /// <summary>
    /// Whether the element is drawn: it and every ancestor up to <paramref name="root"/> are visible, and it has a size once laid
    /// out. (<see cref="UIElement.IsVisible"/> is false for a tree that has no window, so it cannot be used here.)
    /// </summary>
    public static bool IsShown(FrameworkElement element, DependencyObject root)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            if (ReferenceEquals(current, root))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    /// <summary>The text a <see cref="TextBlock"/> shows, with its inlines.</summary>
    public static string TextOf(TextBlock block) => string.IsNullOrEmpty(block.Text) ? new TextRange(block.ContentStart, block.ContentEnd).Text.Trim() : block.Text;

    /// <summary>What UI Automation says the element is called (its peer's name); empty when it has none.</summary>
    public static string NameOf(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? string.Empty;

    /// <summary>
    /// Measures, arranges and lays out <paramref name="root"/> at the size given, and lets the bindings and the templates that WPF
    /// queues settle over a few passes (as <c>OffscreenRender</c> does, without drawing a bitmap). Call it on the UI thread.
    /// </summary>
    public static void Lay(FrameworkElement root, double width, double height)
    {
        var size = new Size(width, height);
        for (var pass = 0; pass < 4; pass++)
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            WpfHost.FlushBindings();
        }

        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
    }
}
