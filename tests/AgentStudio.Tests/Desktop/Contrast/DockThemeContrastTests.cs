using System.Windows;
using System.Windows.Media;
using AgentStudio.Desktop.Theming;
using AvalonDock;
using AvalonDock.Themes;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// WCAG 2.x AA contrast of the AvalonDock chrome (tabs, captions, navigator), after <see cref="DockThemeTokens"/> has
/// repainted the VS2013 dark theme with the tokens. The table names the real brush ids of the theme: a renamed or
/// new brush after an AvalonDock upgrade fails <see cref="Every_dock_brush_is_reviewed"/> instead of going unnoticed.
/// </summary>
public sealed class DockThemeContrastTests
{
    private const ContrastKind Text = ContrastKind.NormalText;
    private const ContrastKind Ui = ContrastKind.UiComponent;

    /// <summary>(pair id, foreground brush, background brush, kind, where it is). Glyphs are graphics (3:1).</summary>
    private static readonly (string Id, string Fg, string Bg, ContrastKind Kind, string Usage)[] Pairs =
    [
        // Document tabs.
        ("doc.selected-active.text", "DocumentWellTabSelectedActiveText", "DocumentWellTabSelectedActiveBackground", Text, "Pestaña de documento seleccionada, en el panel con el foco"),
        ("doc.selected-inactive.text", "DocumentWellTabSelectedInactiveText", "DocumentWellTabSelectedInactiveBackground", Text, "Pestaña de documento seleccionada, en un panel sin el foco"),
        ("doc.unselected.text", "DocumentWellTabUnselectedText", "DocumentWellTabUnselectedBackground", Text, "Pestaña de documento no seleccionada"),
        ("doc.unselected-hover.text", "DocumentWellTabUnselectedHoveredText", "DocumentWellTabUnselectedHoveredBackground", Text, "Pestaña de documento no seleccionada bajo el ratón"),
        ("doc.selected-active.close", "DocumentWellTabButtonSelectedActiveGlyph", "DocumentWellTabSelectedActiveBackground", Ui, "Botón cerrar de la pestaña seleccionada con el foco"),
        ("doc.selected-active.close.hover", "DocumentWellTabButtonSelectedActiveHoveredGlyph", "DocumentWellTabButtonSelectedActiveHoveredBackground", Ui, "Botón cerrar de la pestaña seleccionada con el foco, bajo el ratón"),
        ("doc.selected-active.close.pressed", "DocumentWellTabButtonSelectedActivePressedGlyph", "DocumentWellTabButtonSelectedActivePressedBackground", Ui, "Botón cerrar de la pestaña seleccionada con el foco, pulsado"),
        ("doc.selected-inactive.close", "DocumentWellTabButtonSelectedInactiveGlyph", "DocumentWellTabSelectedInactiveBackground", Ui, "Botón cerrar de la pestaña seleccionada sin el foco"),
        ("doc.selected-inactive.close.hover", "DocumentWellTabButtonSelectedInactiveHoveredGlyph", "DocumentWellTabButtonSelectedInactiveHoveredBackground", Ui, "Botón cerrar de la pestaña seleccionada sin el foco, bajo el ratón"),
        ("doc.selected-inactive.close.pressed", "DocumentWellTabButtonSelectedInactivePressedGlyph", "DocumentWellTabButtonSelectedInactivePressedBackground", Ui, "Botón cerrar de la pestaña seleccionada sin el foco, pulsado"),
        ("doc.unselected.close", "DocumentWellTabButtonUnselectedTabHoveredGlyph", "DocumentWellTabUnselectedHoveredBackground", Ui, "Botón cerrar de una pestaña no seleccionada bajo el ratón"),
        ("doc.unselected.close.hover", "DocumentWellTabButtonUnselectedTabHoveredButtonHoveredGlyph", "DocumentWellTabButtonUnselectedTabHoveredButtonHoveredBackground", Ui, "Botón cerrar de una pestaña no seleccionada, bajo el ratón"),
        ("doc.unselected.close.pressed", "DocumentWellTabButtonUnselectedTabHoveredButtonPressedGlyph", "DocumentWellTabButtonUnselectedTabHoveredButtonPressedBackground", Ui, "Botón cerrar de una pestaña no seleccionada, pulsado"),
        ("doc.overflow", "DocumentWellOverflowButtonDefaultGlyph", "Background", Ui, "Botón de pestañas ocultas, sobre la barra de pestañas"),
        ("doc.overflow.hover", "DocumentWellOverflowButtonHoveredGlyph", "DocumentWellOverflowButtonHoveredBackground", Ui, "Botón de pestañas ocultas bajo el ratón"),
        ("doc.overflow.pressed", "DocumentWellOverflowButtonPressedGlyph", "DocumentWellOverflowButtonPressedBackground", Ui, "Botón de pestañas ocultas pulsado"),

        // Tool window captions (title bar of each panel).
        ("caption.active.text", "ToolWindowCaptionActiveText", "ToolWindowCaptionActiveBackground", Text, "Título del panel con el foco"),
        ("caption.inactive.text", "ToolWindowCaptionInactiveText", "ToolWindowCaptionInactiveBackground", Text, "Título de un panel sin el foco"),
        ("caption.active.button", "ToolWindowCaptionButtonActiveGlyph", "ToolWindowCaptionActiveBackground", Ui, "Botones (anclar, cerrar, menú) del panel con el foco"),
        ("caption.active.button.hover", "ToolWindowCaptionButtonActiveHoveredGlyph", "ToolWindowCaptionButtonActiveHoveredBackground", Ui, "Botón del título activo bajo el ratón"),
        ("caption.active.button.pressed", "ToolWindowCaptionButtonActivePressedGlyph", "ToolWindowCaptionButtonActivePressedBackground", Ui, "Botón del título activo pulsado"),
        ("caption.inactive.button", "ToolWindowCaptionButtonInactiveGlyph", "ToolWindowCaptionInactiveBackground", Ui, "Botones del título de un panel sin el foco"),
        ("caption.inactive.button.hover", "ToolWindowCaptionButtonInactiveHoveredGlyph", "ToolWindowCaptionButtonInactiveHoveredBackground", Ui, "Botón del título inactivo bajo el ratón"),
        ("caption.inactive.button.pressed", "ToolWindowCaptionButtonInactivePressedGlyph", "ToolWindowCaptionButtonInactivePressedBackground", Ui, "Botón del título inactivo pulsado"),

        // Tool window tabs (Auto Doctor / Bitácora) and the auto-hide strip.
        ("tool.selected-active.text", "ToolWindowTabSelectedActiveText", "ToolWindowTabSelectedActiveBackground", Text, "Pestaña de panel seleccionada, en el panel con el foco"),
        ("tool.selected-inactive.text", "ToolWindowTabSelectedInactiveText", "ToolWindowTabSelectedInactiveBackground", Text, "Pestaña de panel seleccionada, en un panel sin el foco"),
        ("tool.unselected.text", "ToolWindowTabUnselectedText", "ToolWindowTabUnselectedBackground", Text, "Pestaña de panel no seleccionada"),
        ("tool.unselected-hover.text", "ToolWindowTabUnselectedHoveredText", "ToolWindowTabUnselectedHoveredBackground", Text, "Pestaña de panel no seleccionada bajo el ratón"),
        ("autohide.text", "AutoHideTabDefaultText", "AutoHideTabDefaultBackground", Text, "Pestaña de un panel ocultable"),
        ("autohide.hover.text", "AutoHideTabHoveredText", "AutoHideTabHoveredBackground", Text, "Pestaña de un panel ocultable bajo el ratón"),

        // Window switcher (Ctrl+Tab) and the default foreground of the chrome.
        ("navigator.text", "NavigatorWindowForeground", "NavigatorWindowBackground", Text, "Lista del conmutador de ventanas"),
        ("navigator.selected.text", "NavigatorWindowSelectedText", "NavigatorWindowSelectedBackground", Text, "Elemento seleccionado del conmutador de ventanas"),
        ("foreground.bg", "ForegroundBrush", "Background", Text, "Texto por defecto del chrome sobre el fondo"),
        ("foreground.floating", "ForegroundBrush", "FloatingToolWindowBackground", Text, "Texto por defecto del chrome en paneles flotantes"),
        ("foreground.floating-doc", "ForegroundBrush", "FloatingDocumentWindowBackground", Text, "Texto por defecto del chrome en documentos flotantes"),

        // Selected state against its neighbours (1.4.11): which tab or panel is selected must not rely on a faint fill.
        ("state.doc.selected-active", "DocumentWellTabSelectedActiveBackground", "DocumentWellTabUnselectedBackground", Ui, "Pestaña de documento seleccionada (con foco) frente a las demás"),
        ("state.doc.selected-inactive", "DocumentWellTabSelectedInactiveBackground", "DocumentWellTabUnselectedBackground", Ui, "Pestaña de documento seleccionada (sin foco) frente a las demás"),
        ("state.doc.selected-active.strip", "DocumentWellTabSelectedActiveBackground", "TabBackground", Ui, "Pestaña de documento seleccionada (con foco) frente a la barra de pestañas"),
        ("state.doc.selected-inactive.strip", "DocumentWellTabSelectedInactiveBackground", "TabBackground", Ui, "Pestaña de documento seleccionada (sin foco) frente a la barra de pestañas"),
        ("state.tool.selected-active", "ToolWindowTabSelectedActiveBackground", "ToolWindowTabUnselectedBackground", Ui, "Pestaña de panel seleccionada (con foco) frente a las demás"),
        ("state.tool.selected-inactive", "ToolWindowTabSelectedInactiveBackground", "ToolWindowTabUnselectedBackground", Ui, "Pestaña de panel seleccionada (sin foco) frente a las demás"),
        ("state.caption.active", "ToolWindowCaptionActiveBackground", "ToolWindowCaptionInactiveBackground", Ui, "Título del panel con el foco frente a los de los demás paneles"),
    ];

    /// <summary>Brushes that are decorative or only reinforce a hover or a drag; listed with the reason.</summary>
    private static readonly (string Id, string Usage, string Fg, string Bg, string Reason)[] ExemptPairs =
    [
        ("border.panel", "Línea que separa los paneles", "PanelBorderBrush", "Background", "Decorativo: separa paneles que ya se distinguen por su contenido"),
        ("border.floating", "Borde de una ventana flotante", "FloatingToolWindowBorder", "FloatingToolWindowBackground", "Decorativo: la ventana ya se distingue por su contenido"),
        ("border.autohide", "Borde de la pestaña de un panel ocultable", "AutoHideTabDefaultBorder", "AutoHideTabDefaultBackground", "Decorativo: la pestaña ya se lee por su texto"),
        ("grip.inactive", "Puntos de arrastre del título de un panel sin el foco", "ToolWindowCaptionInactiveGrip", "ToolWindowCaptionInactiveBackground", "Decorativo: duplican el arrastre de toda la barra de título"),
        ("grip.active", "Puntos de arrastre del título del panel con el foco", "ToolWindowCaptionActiveGrip", "ToolWindowCaptionActiveBackground", "Decorativo: duplican el arrastre de toda la barra de título"),
        ("hover.border", "Borde de un botón de título bajo el ratón", "ToolWindowCaptionButtonInactiveHoveredBorder", "ToolWindowCaptionButtonInactiveHoveredBackground", "Solo bajo el ratón: refuerza un efecto que WCAG no exige"),
        ("dock-guide.arrow", "Flechas de las guías de acople", "DockingButtonForegroundArrowBrushKey", "Background", "Transitorio: solo existen mientras se arrastra un panel"),
        ("dock-guide.preview", "Contorno de la vista previa del acople", "PreviewBoxBorderBrushKey", "Background", "Transitorio: solo existe mientras se arrastra un panel"),
    ];

    [Fact]
    public void Dock_pairs_meet_WCAG_AA()
    {
        var table = WpfHost.Run(() =>
        {
            var colours = LoadDockColours();
            var t = new ContrastTable("dock");
            foreach (var (id, fg, bg, kind, usage) in Pairs)
            {
                t.Add(id, usage, kind, fg, Colour(colours, fg), bg, Colour(colours, bg));
            }

            foreach (var (id, usage, fg, bg, reason) in ExemptPairs)
            {
                t.Exempt(id, usage, reason, fg, Colour(colours, fg), bg, Colour(colours, bg));
            }

            return t;
        });

        table.AssertAllPass();
    }

    [Fact]
    public void Every_dock_brush_is_reviewed()
    {
        var ids = WpfHost.Run(() => LoadDockColours().Keys.ToList());
        var referenced = Pairs.SelectMany(p => new[] { p.Fg, p.Bg })
            .Concat(ExemptPairs.SelectMany(p => new[] { p.Fg, p.Bg }))
            .ToHashSet(StringComparer.Ordinal);

        var unreviewed = ids.Where(id => !referenced.Contains(id) && !IsDecorativeByName(id)).Order().ToList();

        Assert.True(
            unreviewed.Count == 0,
            "Dock brushes with no contrast pair (add them to DockThemeContrastTests): " + string.Join(", ", unreviewed));
        Assert.True(ids.Count > 80, $"Only {ids.Count} dock brushes were found; the theme did not load.");
    }

    /// <summary>
    /// Borders, drag grips, docking guides and the drag preview carry no text and no required information:
    /// they are decorative or transient. They are reported through <see cref="ExemptPairs"/> by category.
    /// </summary>
    private static bool IsDecorativeByName(string id) =>
        id.EndsWith("Border", StringComparison.Ordinal)
        || id.EndsWith("BorderBrush", StringComparison.Ordinal)
        || id.EndsWith("BorderBrushKey", StringComparison.Ordinal)
        || id.EndsWith("Grip", StringComparison.Ordinal)
        || id.StartsWith("DockingButton", StringComparison.Ordinal)
        || id.StartsWith("PreviewBox", StringComparison.Ordinal)
        || id == "ControlAccentBrushKey";

    private static Rgba Colour(Dictionary<string, Rgba> colours, string id) =>
        colours.TryGetValue(id, out var colour) ? colour : throw new KeyNotFoundException($"The AvalonDock VS2013 dark theme has no brush '{id}'.");

    /// <summary>The solid brushes of the VS2013 dark theme after the token repaint, by resource id.</summary>
    private static Dictionary<string, Rgba> LoadDockColours()
    {
        var manager = new DockingManager { Theme = new Vs2013DarkTheme() };
        DockThemeTokens.Apply(manager.Resources, System.Windows.Application.Current.Resources);

        var colours = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        void Walk(ResourceDictionary dictionary)
        {
            foreach (var merged in dictionary.MergedDictionaries)
            {
                Walk(merged);
            }

            foreach (var key in dictionary.Keys.OfType<ComponentResourceKey>())
            {
                if (key.ResourceId is string id && dictionary[key] is SolidColorBrush brush)
                {
                    // Translucency (the drag preview) does not change the colour that is read against the text.
                    colours[id] = ThemeResources.ToRgba(brush.Color) with { A = 255 };
                }
            }
        }

        Walk(manager.Resources);
        return colours;
    }
}
