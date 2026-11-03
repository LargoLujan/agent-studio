using System.Windows;
using System.Windows.Media;

namespace NodifySpike.Theming;

/// <summary>
/// Repaints Nodify with the colour tokens of the app (Themes/Dark.xaml), the same technique the Desktop project already
/// uses for AvalonDock in <c>DockThemeTokens</c>. Nodify paints itself from ~60 <c>*Color</c> keys (its Brushes.xaml
/// turns them into brushes with DynamicResource), so writing those keys into the application resources, after Nodify's
/// own Dark.xaml is merged, repaints the whole editor without touching a template.
/// <para>
/// Why code and not a Nodify.xaml full of aliases: <c>&lt;StaticResource x:Key="..." ResourceKey="..."/&gt;</c> is the usual
/// XAML idiom for aliasing a resource, but the .NET 10 markup compiler crashes on it (MC1000, "Index was out of
/// range"), and copying the hex values would let the canvas drift from the tokens.
/// </para>
/// </summary>
public static class NodifyThemeTokens
{
    private const string Background = "BackgroundColor";
    private const string Surface = "SurfaceColor";
    private const string Raised = "SurfaceRaisedColor";
    private const string Border = "BorderColor";
    private const string Primary = "TextPrimaryColor";
    private const string Secondary = "TextSecondaryColor";
    private const string Accent = "AccentColor";
    private const string AccentHover = "AccentHoverColor";
    private const string Success = "SuccessColor";
    private const string Error = "ErrorColor";

    /// <summary>Nodify colour key to app token key. Together with <see cref="TransparentKeys"/> it covers every key of Nodify's Dark.xaml.</summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>
    {
        // Editor and keyboard focus ring (FocusVisual.xaml): the brightest text token, 15:1 on canvas and nodes.
        ["NodifyEditor.BackgroundColor"] = Background,
        ["NodifyEditor.ForegroundColor"] = Primary,
        ["NodifyEditor.SelectionRectangleColor"] = Accent,
        ["NodifyEditor.PushedAreaColor"] = Secondary,
        ["NodifyEditor.CuttingLineColor"] = Error,
        ["NodifyEditor.FocusVisualColor"] = Primary,

        // Item container: the selection frame.
        ["ItemContainer.SelectedColor"] = AccentHover,

        // Node: the border is the status colour (set by the template), the frame colours here are the neutral ones.
        ["Node.BackgroundColor"] = Surface,
        ["Node.ContentColor"] = Surface,
        ["Node.ForegroundColor"] = Primary,
        ["Node.HeaderForegroundColor"] = Secondary,
        ["Node.HeaderColor"] = Raised,
        ["Node.FooterColor"] = Raised,
        ["Node.BorderColor"] = Border,

        // State, grouping and knot nodes: not used by the orchestrator, themed so they never show up in Nodify blue.
        ["StateNode.BackgroundColor"] = Raised,
        ["StateNode.ForegroundColor"] = Primary,
        ["StateNode.BorderColor"] = Border,
        ["StateNode.HighlightColor"] = AccentHover,
        ["GroupingNode.BackgroundColor"] = Surface,
        ["GroupingNode.ForegroundColor"] = Primary,
        ["GroupingNode.HeaderColor"] = Raised,
        ["GroupingNode.BorderColor"] = Border,
        ["KnotNode.ForegroundColor"] = AccentHover,

        // Connectors.
        ["Connector.ForegroundColor"] = Primary,
        ["Connector.BorderColor"] = AccentHover,
        ["NodeInput.BackgroundColor"] = Surface,
        ["NodeInput.ForegroundColor"] = Primary,
        ["NodeInput.BorderColor"] = AccentHover,
        ["NodeOutput.BackgroundColor"] = Surface,
        ["NodeOutput.ForegroundColor"] = Primary,
        ["NodeOutput.BorderColor"] = AccentHover,

        // Wires.
        ["Connection.StrokeColor"] = Secondary,
        ["LineConnection.StrokeColor"] = Secondary,
        ["CircuitConnection.StrokeColor"] = Secondary,
        ["StepConnection.StrokeColor"] = Secondary,
        ["PendingConnection.StrokeColor"] = AccentHover,
        ["PendingConnection.BorderColor"] = Border,
        ["PendingConnection.ForegroundColor"] = Primary,
        ["PendingConnection.BackgroundColor"] = Raised,

        // Minimap and hot keys.
        ["Minimap.BackgroundColor"] = Surface,
        ["Minimap.ViewportStrokeColor"] = AccentHover,
        ["Minimap.ViewportBackgroundColor"] = Accent,
        ["MinimapItem.BackgroundColor"] = AccentHover,
        ["HotKey.BackgroundColor"] = Primary,
        ["HotKey.ForegroundColor"] = Background,
        ["HotKey.BorderColor"] = AccentHover,
    };

    /// <summary>Keys that are transparent on purpose: unselected nodes have no frame (the status border is the outline).</summary>
    public static IReadOnlyList<string> TransparentKeys { get; } =
    [
        "ItemContainer.BorderColor",
        "KnotNode.BackgroundColor",
        "KnotNode.BorderColor",
        "Connector.BackgroundColor",
    ];

    /// <summary>Writes every mapped Nodify colour into <paramref name="target"/>, resolving the tokens from <paramref name="tokens"/>.</summary>
    /// <returns>How many colour keys were written.</returns>
    public static int Apply(ResourceDictionary target, ResourceDictionary tokens)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var (nodifyKey, tokenKey) in Map)
        {
            target[nodifyKey] = tokens[tokenKey] is Color color
                ? color
                : throw new InvalidOperationException($"Token '{tokenKey}' is not a Color; cannot paint '{nodifyKey}'.");
        }

        foreach (var key in TransparentKeys)
        {
            target[key] = Colors.Transparent;
        }

        return Map.Count + TransparentKeys.Count;
    }
}
