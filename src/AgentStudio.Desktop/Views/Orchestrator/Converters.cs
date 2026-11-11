using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Views.Orchestrator;

/// <summary>A task status to the brush <c>Status.&lt;status&gt;Brush</c> of Themes/Nodify.xaml: one lookup instead of eight triggers per template.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is WorkTaskStatus status && System.Windows.Application.Current?.TryFindResource($"Status.{status}Brush") is Brush brush
            ? brush
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A <see cref="Tone"/> to the theme brush of its state dot. Only for dots and glyphs (3:1): the accent is below 4.5:1 on
/// the panels, so the words next to a dot keep the text colours.
/// </summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Tone.Active => "AccentBrush",
            Tone.Success => "SuccessBrush",
            Tone.Warning => "WarningBrush",
            Tone.Error => "ErrorBrush",
            _ => "TextSecondaryBrush",
        };
        return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Fills a text of the resources, named by the parameter (<c>Orchestrator_Runs_ViewName</c>), with the value bound, in the
/// language of the interface: «Para ejecutar: {0}» with the blockers. A null or empty value gives an empty text, so a line
/// that has nothing to say shows nothing. Used where a <c>StringFormat</c> would write a text in the XAML.
/// </summary>
public sealed class FormatTextConverter : IValueConverter
{
    /// <summary>The one instance: <c>Converter={x:Static views:FormatTextConverter.Instance}</c>.</summary>
    public static FormatTextConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string key && value is not null && value is not string { Length: 0 } ? Texts.Format(key, value) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapsed for null or an empty string, Visible otherwise.</summary>
public sealed class HasTextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the number bound is above zero (a count of items): a title of a list that is empty is not shown.</summary>
public sealed class NotZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is not null (or, with <see cref="Invert"/>, when it is).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is not null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The sum of the lengths bound (a value that is not a finite number counts as 0) and of the one given as the parameter (an
/// invariant number): the height a tab needs for its bands plus the minimum of its regions.
/// </summary>
public sealed class SumConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sum = values.Sum(v => v is double length && double.IsFinite(length) ? length : 0d);
        if (parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var extra))
        {
            sum += extra;
        }

        return sum;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A share of a length (<see cref="Factor"/>) kept between <see cref="Minimum"/> and <see cref="Maximum"/>: the most a band may
/// take of the height of its tab, so a short pane still shows what comes under it. <see cref="Maximum"/> without a length.
/// </summary>
public sealed class ShareOfLengthConverter : IValueConverter
{
    public double Factor { get; set; } = 0.5;

    public double Minimum { get; set; }

    public double Maximum { get; set; } = double.PositiveInfinity;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double length && double.IsFinite(length) && length > 0 ? Math.Clamp(length * Factor, Minimum, Maximum) : Maximum;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is false (the counterpart of <see cref="BooleanToVisibilityConverter"/>).</summary>
public sealed class FalseToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
