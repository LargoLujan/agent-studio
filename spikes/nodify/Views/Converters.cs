using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NodifySpike.ViewModels;

namespace NodifySpike.Views;

/// <summary>NodeStatus to the brush Status.&lt;name&gt;Brush of Themes/NodifyTheme.xaml: one lookup instead of eight triggers per template.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NodeStatus status && Application.Current?.TryFindResource($"Status.{status}Brush") is Brush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapsed for null or an empty string, Visible otherwise (the pending-connection hint).</summary>
public sealed class HasTextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
