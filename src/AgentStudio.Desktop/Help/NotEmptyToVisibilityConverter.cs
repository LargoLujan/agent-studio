using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AgentStudio.Desktop.Help;

/// <summary>Visible when the text is not empty; collapsed when it is null or empty.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
