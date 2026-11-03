using System.Windows;
using System.Windows.Media;

namespace AgentStudio.Desktop.Theming;

/// <summary>
/// Repaints the AvalonDock VS2013 Dark theme with the colour tokens of Themes/Dark.xaml, so the docking chrome
/// shares one palette with the rest of the UI. Every brush of the theme is mapped to a token; none keeps a VS colour.
/// The mapping is checked for WCAG 2.x AA by tests/AgentStudio.Tests/Desktop/Contrast/DockThemeContrastTests.cs.
/// Selected tabs follow one rule (same as the Explorer selection): Accent while their pane has the focus,
/// BorderStrong while it has not, always with OnAccent text, so the selected tab never depends on a faint fill.
/// </summary>
public static class DockThemeTokens
{
    private const string Background = "BackgroundColor";
    private const string Surface = "SurfaceColor";
    private const string Raised = "SurfaceRaisedColor";
    private const string Border = "BorderColor";
    private const string BorderStrong = "BorderStrongColor";
    private const string Primary = "TextPrimaryColor";
    private const string Secondary = "TextSecondaryColor";
    private const string Accent = "AccentColor";
    private const string AccentHover = "AccentHoverColor";
    private const string OnAccent = "OnAccentColor";

    /// <summary>Token keys the theme may use; handy to check that nothing else is left.</summary>
    public static IReadOnlyList<string> TokenKeys { get; } =
        [Background, Surface, Raised, Border, BorderStrong, Primary, Secondary, Accent, AccentHover, OnAccent];

    /// <summary>Repaints every brush found in <paramref name="dockResources"/> and its merged dictionaries.</summary>
    /// <returns>How many brushes were repainted.</returns>
    public static int Apply(ResourceDictionary dockResources, ResourceDictionary tokens)
    {
        ArgumentNullException.ThrowIfNull(dockResources);
        ArgumentNullException.ThrowIfNull(tokens);
        return Walk(dockResources, tokens);
    }

    /// <summary>Token used for a theme brush id, or null when the id is not recognised.</summary>
    public static string? Classify(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        bool Has(string part) => id.Contains(part, StringComparison.Ordinal);

        switch (id)
        {
            case "Background":
            case "TabBackground":
                return Background;
            case "ControlAccentBrushKey":
                return Accent;
            case "NavigatorWindowForeground":
            case "ForegroundBrush":
                return Secondary;
            case "DockingButtonBackgroundBrushKey":
            case "DockingButtonStarBackgroundBrushKey":
                return Raised;
            case "DockingButtonForegroundBrushKey":
                return Accent;
            case "DockingButtonForegroundArrowBrushKey":
                return Primary;
            case "PreviewBoxBackgroundBrushKey":
                return Accent;
            case "ToolWindowTabSelectedActiveBackground":
                return Accent;
            case "DocumentWellTabSelectedInactiveBackground":
            case "ToolWindowTabSelectedInactiveBackground":
                return BorderStrong;
            case "DocumentWellTabButtonSelectedInactiveGlyph":
                return OnAccent;
        }

        if (Has("Border"))
        {
            return Has("Pressed") ? Accent : Has("Hovered") || id.StartsWith("PreviewBox", StringComparison.Ordinal) ? AccentHover : Border;
        }

        if (Has("Grip"))
        {
            return Has("Active") && !Has("Inactive") ? OnAccent : Border;
        }

        if (id.EndsWith("Glyph", StringComparison.Ordinal))
        {
            if (Has("CaptionButtonActive"))
            {
                return OnAccent;
            }

            return Has("Pressed") || Has("Hovered") || Has("SelectedActive") ? Primary : Secondary;
        }

        if (id.EndsWith("Text", StringComparison.Ordinal))
        {
            if (Has("CaptionActive") || Has("TabSelected") || Has("NavigatorWindowSelected"))
            {
                return OnAccent;
            }

            return (Has("Unselected") && !Has("Hovered")) || Has("Inactive") || Has("Default") ? Secondary : Primary;
        }

        if (id.EndsWith("Background", StringComparison.Ordinal))
        {
            if (Has("Pressed"))
            {
                return AccentHover;
            }

            if (Has("Hovered"))
            {
                return Has("CaptionButtonActive") || Has("SelectedActive") ? AccentHover : Border;
            }

            if (id is "ToolWindowCaptionActiveBackground" or "DocumentWellTabSelectedActiveBackground" or "NavigatorWindowSelectedBackground")
            {
                return Accent;
            }

            return Has("Unselected") || Has("Inactive") || Has("Default") ? Raised : Surface;
        }

        return null;
    }

    private static int Walk(ResourceDictionary dictionary, ResourceDictionary tokens)
    {
        var count = 0;
        foreach (var merged in dictionary.MergedDictionaries)
        {
            count += Walk(merged, tokens);
        }

        foreach (var key in dictionary.Keys.OfType<ComponentResourceKey>().ToList())
        {
            if (dictionary[key] is not SolidColorBrush brush || key.ResourceId is not string id)
            {
                continue;
            }

            if (Classify(id) is not { } tokenKey || tokens[tokenKey] is not Color color)
            {
                continue;
            }

            // Theme brushes with alpha (drag preview) keep their translucency through Opacity.
            var opacity = brush.Color.A / 255.0;
            if (brush.IsFrozen)
            {
                dictionary[key] = new SolidColorBrush(color) { Opacity = opacity };
            }
            else
            {
                brush.Color = color;
                brush.Opacity = opacity;
            }

            count++;
        }

        return count;
    }
}
