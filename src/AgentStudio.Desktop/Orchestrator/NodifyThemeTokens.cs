using System.Windows;
using System.Windows.Media;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// Repaints Nodify with the colour tokens of the app (Themes/Dark.xaml), the technique <c>DockThemeTokens</c> uses for
/// AvalonDock (ADR-0015, pattern 7). Nodify paints itself from ~50 <c>*Color</c> keys that its brushes read with
/// DynamicResource, so writing those keys into the root of the application resources, after Nodify's own Dark.xaml is
/// merged (Themes/Nodify.xaml), repaints the whole editor without touching a template.
/// <para>
/// Why code and not aliases in Nodify.xaml: <c>&lt;StaticResource x:Key="…" ResourceKey="…"/&gt;</c> crashes the .NET 10
/// markup compiler (MC1000, "Index was out of range"), and copying the hex values would let the canvas drift from the
/// tokens. The pairs are checked for WCAG 2.x AA by tests/AgentStudio.Tests/Desktop/Contrast/NodifyThemeContrastTests.cs.
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
    private const string Error = "ErrorColor";

    /// <summary>
    /// Nodify colour key to app token key. Together with <see cref="TransparentKeys"/> it covers every colour key of
    /// Nodify's Dark.xaml. Strokes that must be seen against the canvas or a node use the accent fill, which is at least
    /// 3:1 on every surface; the hover shade of the accent is not.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Editor and keyboard focus ring (FocusVisual.xaml): the brightest text token.
        ["NodifyEditor.BackgroundColor"] = Background,
        ["NodifyEditor.ForegroundColor"] = Primary,
        ["NodifyEditor.SelectionRectangleColor"] = Accent,
        ["NodifyEditor.PushedAreaColor"] = Secondary,
        ["NodifyEditor.CuttingLineColor"] = Error,
        ["NodifyEditor.FocusVisualColor"] = Primary,

        // Item container: the selection frame around a node.
        ["ItemContainer.SelectedColor"] = Accent,

        // Node: the border is the status colour (set by the template); these are the neutral parts.
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
        ["StateNode.HighlightColor"] = Accent,
        ["GroupingNode.BackgroundColor"] = Surface,
        ["GroupingNode.ForegroundColor"] = Primary,
        ["GroupingNode.HeaderColor"] = Raised,
        ["GroupingNode.BorderColor"] = Border,
        ["KnotNode.ForegroundColor"] = Accent,

        // Connectors.
        ["Connector.ForegroundColor"] = Primary,
        ["Connector.BorderColor"] = Accent,
        ["NodeInput.BackgroundColor"] = Surface,
        ["NodeInput.ForegroundColor"] = Primary,
        ["NodeInput.BorderColor"] = Accent,
        ["NodeOutput.BackgroundColor"] = Surface,
        ["NodeOutput.ForegroundColor"] = Primary,
        ["NodeOutput.BorderColor"] = Accent,

        // Wires.
        ["Connection.StrokeColor"] = Secondary,
        ["LineConnection.StrokeColor"] = Secondary,
        ["CircuitConnection.StrokeColor"] = Secondary,
        ["StepConnection.StrokeColor"] = Secondary,
        ["PendingConnection.StrokeColor"] = Accent,
        ["PendingConnection.BorderColor"] = Border,
        ["PendingConnection.ForegroundColor"] = Primary,
        ["PendingConnection.BackgroundColor"] = Raised,

        // Minimap and hot keys (not used yet).
        ["Minimap.BackgroundColor"] = Surface,
        ["Minimap.ViewportStrokeColor"] = Accent,
        ["Minimap.ViewportBackgroundColor"] = Accent,
        ["MinimapItem.BackgroundColor"] = Accent,
        ["HotKey.BackgroundColor"] = Primary,
        ["HotKey.ForegroundColor"] = Background,
        ["HotKey.BorderColor"] = Accent,
    };

    /// <summary>Keys that are transparent on purpose: an unselected node has no frame (its status border is the outline).</summary>
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
