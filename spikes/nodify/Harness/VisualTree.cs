using System.Windows;
using System.Windows.Media;

namespace NodifySpike.Harness;

internal static class VisualTree
{
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    public static T? Ancestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// The element's point in physical pixels of the hosting window's client area, which is what window messages carry (the
    /// window is off-screen, so there is no screen position to speak of).
    /// </summary>
    public static (int X, int Y) ClientPoint(FrameworkElement element, double localX, double localY)
    {
        var window = Window.GetWindow(element) ?? throw new InvalidOperationException("The element is not in a window.");
        var point = element.TranslatePoint(new Point(localX, localY), window);
        var dpi = VisualTreeHelper.GetDpi(window);
        return ((int)Math.Round(point.X * dpi.DpiScaleX), (int)Math.Round(point.Y * dpi.DpiScaleY));
    }

    public static (int X, int Y) Centre(FrameworkElement element) => ClientPoint(element, element.ActualWidth / 2, element.ActualHeight / 2);
}
