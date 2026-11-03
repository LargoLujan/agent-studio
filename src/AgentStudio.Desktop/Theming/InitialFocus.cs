using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AgentStudio.Desktop.Theming;

/// <summary>
/// Where the keyboard goes in a window that is activated with nothing focused in it: its default button, else its cancel
/// button, else the first element the Tab key reaches. Without it nothing has the keyboard focus, so Esc, Enter and the
/// shortcuts do nothing until a Tab. Only an element that can take the focus counts: focusable, a tab stop, enabled and
/// visible all the way up to the window. A decision on the visual tree alone, so it runs on views that are never shown.
/// </summary>
public static class InitialFocus
{
    /// <summary>
    /// Whether no control of <paramref name="root"/> has the keyboard: nothing has it, or only the window itself (WPF gives
    /// the keyboard to a window activated from another process, such as the menu of the tray, and there Esc and Enter reach
    /// no button), or an element that is not in it. <paramref name="focused"/> is <see cref="Keyboard.FocusedElement"/>.
    /// </summary>
    public static bool NeedsFocus(DependencyObject root, IInputElement? focused)
    {
        ArgumentNullException.ThrowIfNull(root);
        return focused is not DependencyObject element || ReferenceEquals(element, root) || !IsWithin(element, root);
    }

    /// <summary>The element of <paramref name="root"/> (never <paramref name="root"/> itself) that should take the keyboard focus.</summary>
    /// <returns>Null when nothing in it can take the focus.</returns>
    public static UIElement? ChooseTarget(DependencyObject root)
    {
        var stops = TabStops(root).ToList();
        return stops.OfType<Button>().FirstOrDefault(button => button.IsDefault)
            ?? stops.OfType<Button>().FirstOrDefault(button => button.IsCancel)
            ?? stops.FirstOrDefault();
    }

    /// <summary>
    /// The elements under <paramref name="root"/> that the Tab key can reach, in the order of the visual tree (the order of
    /// Tab when no TabIndex is set, as in this app). A hidden, collapsed or disabled element hides everything under it, and
    /// nothing under a container with <see cref="KeyboardNavigationMode.None"/> is reached.
    /// </summary>
    public static IEnumerable<UIElement> TabStops(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Walk(root);
    }

    private static IEnumerable<UIElement> Walk(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        PushChildren(root, pending);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is UIElement element)
            {
                if (element.Visibility != Visibility.Visible || !element.IsEnabled)
                {
                    continue;
                }

                if (element.Focusable && KeyboardNavigation.GetIsTabStop(element))
                {
                    yield return element;
                }

                if (KeyboardNavigation.GetTabNavigation(element) == KeyboardNavigationMode.None)
                {
                    continue;
                }
            }

            PushChildren(current, pending);
        }
    }

    /// <summary>
    /// Whether <paramref name="element"/> is <paramref name="root"/> or under it. The logical parent comes first, so an element
    /// in the popup of a control (the list of a combo box) is still under the window; the visual one, for the parts of a template.
    /// </summary>
    private static bool IsWithin(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null; current = Parent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        LogicalTreeHelper.GetParent(node) ?? (node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null);

    /// <summary>Pushed last to first, so they come out in the order of the tree.</summary>
    private static void PushChildren(DependencyObject parent, Stack<DependencyObject> pending)
    {
        if (parent is not (Visual or Visual3D))
        {
            return;
        }

        for (var i = VisualTreeHelper.GetChildrenCount(parent) - 1; i >= 0; i--)
        {
            pending.Push(VisualTreeHelper.GetChild(parent, i));
        }
    }
}
