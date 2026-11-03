using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// WCAG 2.x AA contrast of the dark theme (Themes/Dark.xaml), pair by pair, measured on the real resources.
/// The pairs are the ones the UI really draws; docs/12-accesibilidad-tema-oscuro.md explains the method.
/// <list type="bullet">
/// <item>Text: 4.5:1 (1.4.3). Nothing in the UI qualifies as large text (22 px semibold is not bold).</item>
/// <item>UI component boundaries, glyphs, state fills and focus indicators: 3:1 against what is next to them (1.4.11).</item>
/// <item>Exempt, but listed with the ratio they really have: disabled controls (1.4.3 and 1.4.11 do not apply to
/// inactive components), purely decorative separators and hover-only effects (not required visual information).</item>
/// </list>
/// A token or style change that breaks any of this fails the build.
/// </summary>
public sealed class ThemeContrastTests
{
    private const string Bg = "BackgroundColor";
    private const string Surface = "SurfaceColor";
    private const string Raised = "SurfaceRaisedColor";

    // The three surfaces the UI is built on, with a plain-language name for the report.
    private static readonly (string Token, string Id, string Name)[] Surfaces =
    [
        (Bg, "bg", "ventana, bienvenida y visores"),
        (Surface, "surface", "barras y paneles"),
        (Raised, "raised", "tarjetas y botones bajo el ratón"),
    ];

    /// <summary>The palette: every foreground token over every surface it is used on.</summary>
    private static readonly (string Id, string Usage, string Foreground, string Background)[] PalettePairs =
    [
        ("primary.bg", "Texto principal en la bienvenida y los visores de ficheros", "TextPrimaryColor", Bg),
        ("primary.surface", "Texto principal en la barra superior, el Explorador, el Auto Doctor y la Bitácora", "TextPrimaryColor", Surface),
        ("primary.raised", "Texto principal en las tarjetas del Auto Doctor y bajo el ratón", "TextPrimaryColor", Raised),
        ("secondary.bg", "Texto secundario en la bienvenida y el terminal sin iniciar", "TextSecondaryColor", Bg),
        ("secondary.surface", "Texto secundario en la barra de estado, la ruta del proyecto y el estado del terminal", "TextSecondaryColor", Surface),
        ("secondary.raised", "Texto secundario en las tarjetas del Auto Doctor", "TextSecondaryColor", Raised),
        ("error.bg", "Texto de error en el visor de ficheros y en el terminal", "ErrorColor", Bg),
        ("error.surface", "Texto de error en el Auto Doctor", "ErrorColor", Surface),
        ("warning.surface", "Aviso de finales de línea mezclados", "WarningColor", Surface),
        ("onaccent.accent", "Texto sobre el acento: botón primario, selección activa, título y pestaña activos", "OnAccentColor", "AccentColor"),
        ("onaccent.hover", "Texto del botón primario bajo el ratón y glifos de botones de pestaña pulsados", "OnAccentColor", "AccentHoverColor"),
    ];

    [Fact]
    public void Palette_pairs_meet_WCAG_AA()
    {
        var table = WpfHost.Run(() =>
        {
            var t = new ContrastTable("paleta");
            foreach (var (id, usage, fg, bg) in PalettePairs)
            {
                t.Add(id, usage, ContrastKind.NormalText, fg, ThemeResources.Color(fg), bg, ThemeResources.Color(bg));
            }

            // Status colours as graphics (the dots next to the provider names), on the surfaces they sit on.
            foreach (var colour in new[] { "SuccessColor", "WarningColor", "ErrorColor" })
            {
                t.Add($"dot.{colour[..^5].ToLowerInvariant()}.surface", "Punto de estado en la barra superior", ContrastKind.UiComponent, colour, ThemeResources.Color(colour), Surface, ThemeResources.Color(Surface));
                t.Add($"dot.{colour[..^5].ToLowerInvariant()}.raised", "Punto de estado en las tarjetas del Auto Doctor", ContrastKind.UiComponent, colour, ThemeResources.Color(colour), Raised, ThemeResources.Color(Raised));
            }

            return t;
        });

        table.AssertAllPass();
    }

    [Fact]
    public void The_palette_keeps_its_documented_ordering_of_surfaces()
    {
        // Surfaces get lighter from the window to the cards; the contrast maths above assumes it.
        var (bg, surface, raised) = WpfHost.Run(() => (
            Wcag.Luminance(ThemeResources.Color(Bg)),
            Wcag.Luminance(ThemeResources.Color(Surface)),
            Wcag.Luminance(ThemeResources.Color(Raised))));

        Assert.True(bg < surface && surface < raised);
    }

    [Fact]
    public void Text_styles_meet_WCAG_AA_on_every_surface_they_are_used_on()
    {
        var table = WpfHost.Run(() =>
        {
            var t = new ContrastTable("texto");
            (string Style, string[] On)[] used =
            [
                ("BodyText", [Bg, Surface, Raised]),
                ("SecondaryText", [Bg, Surface, Raised]),
                ("OptionalSecondaryText", [Raised]),
                ("ErrorText", [Bg, Surface]),
                ("TitleText", [Bg]),
                ("SectionTitleText", [Bg, Surface, Raised]),
            ];

            foreach (var (name, surfaces) in used)
            {
                var fg = ThemeResources.ToRgba(ThemeResources.SetterBrush(ThemeResources.Style(name), TextBlock.ForegroundProperty), name + ".Foreground");
                foreach (var token in surfaces)
                {
                    var surface = Surfaces.Single(s => s.Token == token);
                    t.Add($"{name}.{surface.Id}", $"Estilo {name} sobre {surface.Name}", ContrastKind.NormalText, name + ".Foreground", fg, token, ThemeResources.Color(token));
                }
            }

            return t;
        });

        table.AssertAllPass();
    }

    [Fact]
    public void Button_styles_meet_WCAG_AA_in_every_state()
    {
        var table = WpfHost.Run(BuildButtonTable);

        table.AssertAllPass();
    }

    private static ContrastTable BuildButtonTable()
    {
        var t = new ContrastTable("botones");
        Rgba Token(string key) => ThemeResources.Color(key);
        Rgba Of(Brush? brush, string what) => ThemeResources.ToRgba(brush, what);

        // Primary button: text on its fill at rest and under the mouse; the fill is its boundary.
        var primary = ThemeResources.Style("PrimaryButton");
        var primaryText = Of(ThemeResources.SetterBrush(primary, Control.ForegroundProperty), "PrimaryButton.Foreground");
        var primaryFill = Of(ThemeResources.SetterBrush(primary, Control.BackgroundProperty), "PrimaryButton.Background");
        var primaryHover = Of(ThemeResources.TemplateTriggerBrush(primary, UIElement.IsMouseOverProperty, "Root", Border.BackgroundProperty), "PrimaryButton hover");
        t.Add("PrimaryButton.text", "Botón primario en reposo: texto sobre el relleno", ContrastKind.NormalText, "Foreground", primaryText, "Background", primaryFill);
        t.Add("PrimaryButton.hover", "Botón primario bajo el ratón: texto sobre el relleno", ContrastKind.NormalText, "Foreground", primaryText, "Hover", primaryHover);
        if (ThemeResources.TemplateTriggerBrush(primary, UIElement.IsKeyboardFocusedProperty, "Root", Border.BackgroundProperty) is { } primaryFocusFill)
        {
            t.Add("PrimaryButton.focused", "Botón primario con el foco: texto sobre el relleno", ContrastKind.NormalText, "Foreground", primaryText, "Focused", Of(primaryFocusFill, "PrimaryButton focused"));
        }

        t.Add("PrimaryButton.fill.surface", "Relleno del botón primario contra la barra superior y el Auto Doctor", ContrastKind.UiComponent, "Background", primaryFill, Surface, Token(Surface));
        t.Add("PrimaryButton.fill.bg", "Relleno del botón primario contra la bienvenida", ContrastKind.UiComponent, "Background", primaryFill, Bg, Token(Bg));

        // Ghost button: transparent, so text sits on the surface; the outline is its boundary.
        var ghost = ThemeResources.Style("GhostButton");
        var ghostText = Of(ThemeResources.SetterBrush(ghost, Control.ForegroundProperty), "GhostButton.Foreground");
        var ghostBorder = Of(ThemeResources.SetterBrush(ghost, Control.BorderBrushProperty), "GhostButton.BorderBrush");
        var ghostHover = Of(ThemeResources.TemplateTriggerBrush(ghost, UIElement.IsMouseOverProperty, "Root", Border.BackgroundProperty), "GhostButton hover");
        foreach (var surface in Surfaces.Where(s => s.Token != Raised))
        {
            t.Add($"GhostButton.text.{surface.Id}", $"Botón secundario: texto sobre {surface.Name}", ContrastKind.NormalText, "Foreground", ghostText, surface.Token, Token(surface.Token));
            t.Add($"GhostButton.border.{surface.Id}", $"Botón secundario: contorno contra {surface.Name}", ContrastKind.UiComponent, "BorderBrush", ghostBorder, surface.Token, Token(surface.Token));
        }

        t.Add("GhostButton.hover.text", "Botón secundario bajo el ratón: texto sobre el relleno", ContrastKind.NormalText, "Foreground", ghostText, "Hover", ghostHover);
        t.Add("GhostButton.hover.border", "Botón secundario bajo el ratón: contorno contra el relleno", ContrastKind.UiComponent, "BorderBrush", ghostBorder, "Hover", ghostHover);

        // Icon-only activity bar button: the glyph is the component.
        var activity = ThemeResources.Style("ActivityButton");
        var activityGlyph = Of(ThemeResources.SetterBrush(activity, Control.ForegroundProperty), "ActivityButton.Foreground");
        var activityHoverGlyph = Of(ThemeResources.TemplateTriggerBrush(activity, UIElement.IsMouseOverProperty, null, Control.ForegroundProperty), "ActivityButton hover glyph");
        var activityHoverFill = Of(ThemeResources.TemplateTriggerBrush(activity, UIElement.IsMouseOverProperty, "Root", Border.BackgroundProperty), "ActivityButton hover fill");
        t.Add("ActivityButton.glyph", "Icono de la barra de actividad contra la barra", ContrastKind.UiComponent, "Foreground", activityGlyph, Surface, Token(Surface));
        t.Add("ActivityButton.hover", "Icono de la barra de actividad bajo el ratón contra su relleno", ContrastKind.UiComponent, "Foreground", activityHoverGlyph, "Hover", activityHoverFill);
        if (ThemeResources.TemplateTriggerBrush(activity, UIElement.IsKeyboardFocusedProperty, "Root", Border.BorderBrushProperty) is { } activityFocusBar)
        {
            t.Add("ActivityButton.focused", "Barra de foco del botón de actividad contra la barra", ContrastKind.FocusIndicator, "BorderBrush", Of(activityFocusBar, "ActivityButton focus bar"), Surface, Token(Surface));
        }

        // A focus cue drawn by a trigger of the template (the themed FocusVisualStyle is checked in FocusVisualTests).
        if (ThemeResources.TemplateTriggerBrush(ghost, UIElement.IsKeyboardFocusedProperty, "Root", Border.BorderBrushProperty) is { } ghostFocus)
        {
            t.Add("GhostButton.focused", "Contorno con el foco del botón secundario contra la barra", ContrastKind.FocusIndicator, "BorderBrush", Of(ghostFocus, "GhostButton focused"), Surface, Token(Surface));
        }

        // Disabled controls are exempt (1.4.3 and 1.4.11 do not apply to inactive components). The ratio they really
        // have is reported all the same. The templates dim the whole control with Opacity.
        AddDisabled(t, "PrimaryButton.disabled", "Botón primario desactivado", ContrastKind.NormalText, 0.45, "Foreground", primaryText, "Background", primaryFill, Surface);
        AddDisabled(t, "GhostButton.disabled", "Botón secundario desactivado", ContrastKind.NormalText, 0.45, "Foreground", ghostText, "Surface", Token(Surface), Surface);
        AddDisabled(t, "ActivityButton.disabled", "Entrada de una fase futura en la barra de actividad (desactivada a propósito)", ContrastKind.UiComponent, 0.4, "Foreground", activityGlyph, "Surface", Token(Surface), Surface);

        return t;
    }

    private static void AddDisabled(ContrastTable table, string id, string usage, ContrastKind kind, double opacity, string foregroundName, Rgba foreground, string backgroundName, Rgba background, string surfaceToken)
    {
        var surface = ThemeResources.Color(surfaceToken);
        var alpha = (byte)Math.Round(255 * opacity);
        table.Exempt(
            id,
            usage,
            "Desactivado: WCAG no exige contraste a los componentes inactivos",
            foregroundName + " con Opacity " + opacity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            Wcag.Over(foreground with { A = alpha }, surface),
            backgroundName,
            Wcag.Over(background with { A = alpha }, surface),
            kind);
    }

    [Fact]
    public void Other_controls_and_states_meet_WCAG_AA()
    {
        var table = WpfHost.Run(BuildControlTable);

        table.AssertAllPass();
    }

    private static ContrastTable BuildControlTable()
    {
        var t = new ContrastTable("controles");
        Rgba Token(string key) => ThemeResources.Color(key);
        Rgba Of(Brush? brush, string what) => ThemeResources.ToRgba(brush, what);

        // Read-only text viewers (file fallback and journal): the caret is their keyboard focus indicator.
        var viewer = ThemeResources.Style("MonoViewer");
        var viewerText = Of(ThemeResources.SetterBrush(viewer, Control.ForegroundProperty), "MonoViewer.Foreground");
        var viewerBg = Of(ThemeResources.SetterBrush(viewer, Control.BackgroundProperty), "MonoViewer.Background");
        var caret = Of(ThemeResources.SetterBrush(viewer, TextBoxBase.CaretBrushProperty), "MonoViewer.CaretBrush");
        t.Add("MonoViewer.text", "Visor de ficheros y bitácora: texto sobre el fondo", ContrastKind.NormalText, "Foreground", viewerText, "Background", viewerBg);
        t.Add("MonoViewer.caret", "Visor de ficheros y bitácora: cursor de texto (indicador de foco del visor)", ContrastKind.FocusIndicator, "CaretBrush", caret, "Background", viewerBg);

        // The readable journal (V31): a read-only rich text box over its Markdown, with the reading caret of MonoViewer and a
        // translucent selection painted over the text (docs/12 §2.3: 4.5:1 for the default text under it, 3:1 for the others).
        var reader = ThemeResources.Style("ReadingViewer");
        var readerText = Of(ThemeResources.SetterBrush(reader, Control.ForegroundProperty), "ReadingViewer.Foreground");
        var readerBg = Of(ThemeResources.SetterBrush(reader, Control.BackgroundProperty), "ReadingViewer.Background");
        var readerCaret = Of(ThemeResources.SetterBrush(reader, TextBoxBase.CaretBrushProperty), "ReadingViewer.CaretBrush");
        var readerSelection = Of(ThemeResources.SetterBrush(reader, TextBoxBase.SelectionBrushProperty), "ReadingViewer.SelectionBrush");
        var readerOpacity = (byte)Math.Round(255 * (double)reader.Setters.OfType<Setter>().Single(s => s.Property == TextBoxBase.SelectionOpacityProperty).Value);
        var selected = readerSelection with { A = readerOpacity };
        var secondaryText = Token("TextSecondaryColor");
        var codeFill = Token("SurfaceRaisedColor");
        t.Add("ReadingViewer.text", "Bitácora: texto sobre el fondo", ContrastKind.NormalText, "Foreground", readerText, "Background", readerBg);
        t.Add("ReadingViewer.secondary", "Bitácora: cabecera y líneas de debajo de cada entrada («Resultado», «Hecho»)", ContrastKind.NormalText, "TextSecondaryColor", secondaryText, "Background", readerBg);
        t.Add("ReadingViewer.code", "Bitácora: código sobre su fondo", ContrastKind.NormalText, "Foreground", readerText, "SurfaceRaisedColor", codeFill);
        t.Add("ReadingViewer.code.secondary", "Bitácora: código en una línea secundaria", ContrastKind.NormalText, "TextSecondaryColor", secondaryText, "SurfaceRaisedColor", codeFill);
        t.Add("ReadingViewer.caret", "Bitácora: cursor de lectura (indicador de foco)", ContrastKind.FocusIndicator, "CaretBrush", readerCaret, "Background", readerBg);
        t.Add("ReadingViewer.selection", "Bitácora: texto seleccionado (la selección se pinta encima, translúcida)", ContrastKind.NormalText, "Foreground bajo la selección", Wcag.Over(selected, readerText), "Background bajo la selección", Wcag.Over(selected, readerBg));
        t.Add("ReadingViewer.selection.secondary", "Bitácora: línea secundaria seleccionada", ContrastKind.TextOnHighlight, "TextSecondaryColor bajo la selección", Wcag.Over(selected, secondaryText), "Background bajo la selección", Wcag.Over(selected, readerBg));

        // Status dots as the style paints them, on the two surfaces they sit on.
        var dot = ThemeResources.Style("StatusDot");
        var dotStates = new List<(string Name, Brush Fill)> { ("Predeterminado", ThemeResources.SetterBrush(dot, Shape.FillProperty)) };
        dotStates.AddRange(dot.Triggers.OfType<DataTrigger>().Select(trigger =>
            (trigger.Value?.ToString() ?? "?", trigger.Setters.OfType<Setter>().Single(s => s.Property == Shape.FillProperty).Value as Brush ?? throw new InvalidOperationException("StatusDot trigger without a brush"))));
        foreach (var (state, fill) in dotStates)
        {
            foreach (var surface in Surfaces.Where(s => s.Token != Bg))
            {
                t.Add($"StatusDot.{state}.{surface.Id}", $"Punto de estado ({state}) sobre {surface.Name}", ContrastKind.UiComponent, "Fill", Of(fill, "StatusDot.Fill"), surface.Token, Token(surface.Token));
            }
        }

        // The slim scrollbars replace the system ones, so their thumb is author-drawn and must be visible (1.4.11).
        var scrollBar = (Style)ThemeResources.Resources[typeof(ScrollBar)];
        var horizontalTemplate = (ControlTemplate)scrollBar.Triggers.OfType<Trigger>()
            .Single(trigger => trigger.Property == ScrollBar.OrientationProperty).Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.TemplateProperty).Value;
        foreach (var (orientation, template) in new[] { ("vertical", ThemeResources.Template(scrollBar)), ("horizontal", horizontalTemplate) })
        {
            var thumb = Of(ThumbBackground(template), $"Scrollbar thumb ({orientation})");
            foreach (var surface in Surfaces)
            {
                t.Add($"ScrollBar.{orientation}.{surface.Id}", $"Pulgar de la barra de desplazamiento {orientation} contra {surface.Name}", ContrastKind.UiComponent, "Thumb", thumb, surface.Token, Token(surface.Token));
            }
        }

        // Indeterminate progress bar: the moving bar against its track.
        var progress = ThemeResources.Style("AccentProgress");
        t.Add("AccentProgress.bar", "Barra de progreso contra su pista", ContrastKind.UiComponent, "Foreground", Of(ThemeResources.SetterBrush(progress, Control.ForegroundProperty), "AccentProgress.Foreground"), "Background", Of(ThemeResources.SetterBrush(progress, Control.BackgroundProperty), "AccentProgress.Background"));

        // Selection colours that WPF's own templates (TreeView, ListBox, text selection) take from the system keys.
        var highlight = Of(ThemeResources.Resources[SystemColors.HighlightBrushKey] as Brush, "Highlight");
        var highlightText = Of(ThemeResources.Resources[SystemColors.HighlightTextBrushKey] as Brush, "HighlightText");
        var inactive = Of(ThemeResources.Resources[SystemColors.InactiveSelectionHighlightBrushKey] as Brush, "InactiveSelectionHighlight");
        var inactiveText = Of(ThemeResources.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] as Brush, "InactiveSelectionHighlightText");
        t.Add("selection.active.text", "Fila seleccionada del Explorador con el foco: texto sobre el relleno", ContrastKind.NormalText, "HighlightText", highlightText, "Highlight", highlight);
        t.Add("selection.active.fill", "Fila seleccionada del Explorador con el foco: relleno contra el panel", ContrastKind.UiComponent, "Highlight", highlight, Surface, Token(Surface));
        t.Add("selection.inactive.text", "Fila seleccionada del Explorador sin el foco: texto sobre el relleno", ContrastKind.NormalText, "InactiveSelectionHighlightText", inactiveText, "InactiveSelectionHighlight", inactive);
        t.Add("selection.inactive.fill", "Fila seleccionada del Explorador sin el foco: relleno contra el panel (sigue siendo un estado visible)", ContrastKind.UiComponent, "InactiveSelectionHighlight", inactive, Surface, Token(Surface));

        // Purely decorative strokes: they separate regions that are already distinguishable by their content.
        const string Decorative = "Decorativo: separa regiones que ya se distinguen por su contenido";
        var card = ThemeResources.Style("Card");
        t.Exempt("Card.border", "Borde de las tarjetas (Auto Doctor)", Decorative, "BorderBrush", Of(ThemeResources.SetterBrush(card, Border.BorderBrushProperty), "Card.BorderBrush"), Surface, Token(Surface));
        t.Exempt("separator.surface", "Líneas que separan la barra superior, la barra de actividad y la de estado", Decorative, "BorderColor", Token("BorderColor"), Surface, Token(Surface));
        return t;
    }

    /// <summary>The brush the thumb of a scrollbar template is filled with.</summary>
    private static Brush? ThumbBackground(ControlTemplate scrollBarTemplate)
    {
        var root = (Panel)scrollBarTemplate.LoadContent();
        var track = root.Children.OfType<Track>().Single();
        var thumbRoot = (Border)track.Thumb.Template.LoadContent();
        return thumbRoot.Background;
    }
}
