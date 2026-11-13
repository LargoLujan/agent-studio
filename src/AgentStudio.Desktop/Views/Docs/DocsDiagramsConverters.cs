using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AgentStudio.Desktop.Views.Docs;

/// <summary>
/// The length of a row of «Diagramas» that holds a region that may be collapsed (P69): the length given as the parameter (<c>2*</c>) while the region is
/// visible, and <see cref="GridLength.Auto"/> when it is not, so a collapsed list or text leaves no blank gap.
/// </summary>
public sealed class VisibleToRowLengthConverter : IValueConverter
{
    private static readonly GridLengthConverter Lengths = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible && parameter is string text && Lengths.ConvertFromInvariantString(text) is GridLength length ? length : GridLength.Auto;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The least height of that row: the number given as the parameter (an invariant number) while the region is visible, 0 when it is not.</summary>
public sealed class VisibleToMinHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible && parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum) ? minimum : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The least height of the whole screen of «Diagramas» (P69): the heights of the blocks that take what their content asks (the numbers bound first), plus a fixed
/// extra (their margins) and the least of the list and of the text of the diagram while each is visible (the <see cref="Visibility"/> values bound after them).
/// The parameter is <c>extra;least of the list;least of the text</c>, invariant numbers. Under that height the screen scrolls whole instead of cutting anything.
/// </summary>
public sealed class DiagramsMinHeightConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        var parts = (parameter as string ?? string.Empty).Split(';');
        double Part(int index) => index < parts.Length && double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0d;

        var total = Part(0);
        var lengths = values.Where(v => v is double).Select(v => (double)v!);
        total += lengths.Where(double.IsFinite).Sum();
        var regions = values.Where(v => v is Visibility).Select(v => (Visibility)v!).ToList();
        if (regions.Count > 0 && regions[0] == Visibility.Visible)
        {
            total += Part(1);
        }

        if (regions.Count > 1 && regions[1] == Visibility.Visible)
        {
            total += Part(2);
        }

        return total;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
