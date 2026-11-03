using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// Reads the real Themes/Dark.xaml (loaded by <see cref="WpfHost"/> as application resources) so the tests measure
/// what the user sees. Every member must be called inside <c>WpfHost.Run</c>: WPF objects belong to its STA thread.
/// </summary>
internal static class ThemeResources
{
    public static ResourceDictionary Resources => System.Windows.Application.Current.Resources;

    /// <summary>A colour token (<c>BackgroundColor</c>) or the colour of a solid brush (<c>BackgroundBrush</c>).</summary>
    public static Rgba Color(string key) => ToRgba(Find(key));

    public static Rgba ToRgba(System.Windows.Media.Color colour) => new(colour.R, colour.G, colour.B, colour.A);

    public static Rgba ToRgba(Brush? brush, string what)
    {
        if (brush is not SolidColorBrush solid)
        {
            throw new InvalidOperationException($"{what} is {brush?.GetType().Name ?? "null"}, not a solid brush.");
        }

        return ToRgba(solid.Color) with { A = (byte)Math.Round(solid.Color.A * solid.Opacity) };
    }

    public static Style Style(object key) =>
        Resources[key] as Style ?? throw new KeyNotFoundException($"Style '{key}' not found in the theme.");

    public static ControlTemplate Template(Style style) =>
        style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value as ControlTemplate
        ?? throw new InvalidOperationException("The style has no control template.");

    /// <summary>The brush set by a style, or by the style it is based on, for a plain property.</summary>
    public static Brush SetterBrush(Style style, DependencyProperty property) =>
        TrySetterBrush(style, property) ?? throw new KeyNotFoundException($"The style does not set {property.Name}.");

    public static Brush? TrySetterBrush(Style style, DependencyProperty property)
    {
        for (var current = style; current is not null; current = current.BasedOn)
        {
            foreach (var setter in current.Setters.OfType<Setter>())
            {
                if (setter.Property == property && setter.TargetName is null)
                {
                    return setter.Value as Brush ?? throw new InvalidOperationException($"{property.Name} is not set to a brush.");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The brush the control template sets on <paramref name="target"/> (of the template part
    /// <paramref name="targetName"/>, or of the control itself when null) while <paramref name="trigger"/> is true.
    /// </summary>
    public static Brush? TemplateTriggerBrush(Style style, DependencyProperty trigger, string? targetName, DependencyProperty target)
    {
        foreach (var t in Template(style).Triggers.OfType<Trigger>().Where(t => t.Property == trigger && Equals(t.Value, true)))
        {
            foreach (var setter in t.Setters.OfType<Setter>().Where(s => s.Property == target && s.TargetName == targetName))
            {
                return setter.Value as Brush;
            }
        }

        return null;
    }

    /// <summary>The colour a theme resource key resolves to: a <see cref="System.Windows.Media.Color"/> or a solid brush.</summary>
    private static System.Windows.Media.Color Find(object key) => Resources[key] switch
    {
        System.Windows.Media.Color colour => colour,
        SolidColorBrush brush => brush.Color,
        null => throw new KeyNotFoundException($"Resource '{key}' not found in the theme."),
        var other => throw new InvalidOperationException($"Resource '{key}' is a {other.GetType().Name}, not a colour or a solid brush."),
    };
}
