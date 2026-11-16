using System.Windows;

namespace AgentStudio.Desktop.Controls;

/// <summary>
/// Whether the activity bar shows the names of its entries and the captions of its groups (docs/24 §3.1), inherited by everything
/// inside the bar: the entries are drawn by templates whose data is the entry, not the shell, so they read it from here.
/// </summary>
public static class ActivityBarState
{
    public static readonly DependencyProperty ShowsTitlesProperty = DependencyProperty.RegisterAttached(
        "ShowsTitles",
        typeof(bool),
        typeof(ActivityBarState),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetShowsTitles(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ShowsTitlesProperty);
    }

    public static void SetShowsTitles(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ShowsTitlesProperty, value);
    }
}
