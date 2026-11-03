using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Desktop.Controls.Editor;
using AgentStudio.Desktop.Controls.Terminal;
using Microsoft.Web.WebView2.Wpf;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// WCAG 2.x AA contrast of the two web surfaces: the Monaco editor (Assets/editor/editor.js) and the xterm.js
/// terminal (Assets/terminal/terminal.js and terminal.css). The colours live in JavaScript, so the tests parse the
/// theme literals out of the shipped files (they are copied next to the test assembly) instead of repeating hex
/// values here. The parsing is strict: a literal that cannot be found or read fails the test.
/// </summary>
public sealed partial class WebThemeContrastTests
{
    private const ContrastKind Text = ContrastKind.NormalText;
    private const ContrastKind Ui = ContrastKind.UiComponent;

    /// <summary>The translucent colours Monaco paints behind text. Each must be reviewed here to exist in the theme.</summary>
    private static readonly string[] Overlays =
    [
        "editor.selectionBackground",
        "editor.inactiveSelectionBackground",
        "editor.selectionHighlightBackground",
        "editor.wordHighlightBackground",
        "editor.wordHighlightStrongBackground",
        "editor.wordHighlightTextBackground",
        "editor.findMatchBackground",
        "editor.findMatchHighlightBackground",
        "editor.findRangeHighlightBackground",
        "editorBracketMatch.background",
        "diffEditor.insertedTextBackground",
        "diffEditor.removedTextBackground",
    ];

    private static readonly string[] AnsiNames =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    ];

    [GeneratedRegex(@"^\s*'?(?<key>[A-Za-z0-9.]+)'?\s*:\s*'(?<hex>#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)'", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HexEntry();

    [GeneratedRegex(@"\{\s*token:\s*'(?<token>[^']*)'\s*,\s*foreground:\s*'(?<hex>[0-9A-Fa-f]{6})'\s*\}", RegexOptions.CultureInvariant)]
    private static partial Regex ColouredRule();

    [GeneratedRegex(@"minimumContrastRatio:\s*(?<value>[0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex MinimumContrastRatio();

    [GeneratedRegex(@"html,\s*body\s*\{[^}]*?background:\s*(?<hex>#[0-9A-Fa-f]{6})", RegexOptions.CultureInvariant)]
    private static partial Regex PageBackground();

    [GeneratedRegex(@"#error\s*\{[^}]*?\bcolor:\s*(?<hex>#[0-9A-Fa-f]{6})", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorColour();

    // ---- Monaco ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Editor_theme_meets_WCAG_AA()
    {
        var colours = EditorColours();
        var rules = EditorRules();
        var t = new ContrastTable("editor");
        Rgba C(string key) => colours.TryGetValue(key, out var colour) ? colour : throw new KeyNotFoundException($"editor.js THEME_COLORS has no '{key}'.");

        var bg = C("editor.background");
        var line = C("editor.lineHighlightBackground");
        var fg = C("editor.foreground");
        var surfaces = new[] { ("editor.background", bg), ("editor.lineHighlightBackground", line) };

        t.Add("fg.bg", "Texto del editor sobre el fondo", Text, "editor.foreground", fg, "editor.background", bg);
        t.Add("fg.line", "Texto de la línea actual", Text, "editor.foreground", fg, "editor.lineHighlightBackground", line);
        t.Add("linenumber", "Números de línea sobre el fondo", Text, "editorLineNumber.foreground", C("editorLineNumber.foreground"), "editor.background", bg);
        t.Add("linenumber.active", "Número de la línea actual sobre el fondo", Text, "editorLineNumber.activeForeground", C("editorLineNumber.activeForeground"), "editor.background", bg);

        // Translucent highlights behind text, over the editor and over the current line.
        var composites = new List<(string Name, Rgba Colour)>();
        foreach (var key in Overlays)
        {
            foreach (var (surfaceName, surface) in surfaces)
            {
                var composite = Wcag.Over(C(key), surface);
                var name = $"{key} sobre {surfaceName}";
                composites.Add((name, composite));
                t.Add($"fg.{key}.{(surface == bg ? "bg" : "line")}", $"Texto del editor bajo {key}", Text, "editor.foreground", fg, name, composite);
            }
        }

        // Syntax colours: on the editor and on the current line (4.5:1), and on the worst highlight (3:1).
        foreach (var (token, colour) in rules)
        {
            t.Add($"token.{token}.bg", $"Color de sintaxis '{token}' sobre el fondo", Text, "token " + token, colour, "editor.background", bg);
            t.Add($"token.{token}.line", $"Color de sintaxis '{token}' sobre la línea actual", Text, "token " + token, colour, "editor.lineHighlightBackground", line);
            var worst = composites.MinBy(c => Wcag.Ratio(colour, c.Colour));
            t.Add($"token.{token}.highlight", $"Color de sintaxis '{token}' bajo el peor resaltado", ContrastKind.TextOnHighlight, "token " + token, colour, worst.Name, worst.Colour);
        }

        // Widgets: find/replace, suggestions, hover.
        t.Add("widget.text", "Texto de los widgets (buscar, reemplazar)", Text, "editorWidget.foreground", C("editorWidget.foreground"), "editorWidget.background", C("editorWidget.background"));
        t.Add("suggest.text", "Texto de la lista de sugerencias", Text, "editorSuggestWidget.foreground", C("editorSuggestWidget.foreground"), "editorSuggestWidget.background", C("editorSuggestWidget.background"));
        t.Add("suggest.match", "Letras coincidentes de la lista de sugerencias", Text, "editorSuggestWidget.highlightForeground", C("editorSuggestWidget.highlightForeground"), "editorSuggestWidget.background", C("editorSuggestWidget.background"));
        t.Add("suggest.selected.text", "Sugerencia seleccionada", Text, "editorSuggestWidget.selectedForeground", C("editorSuggestWidget.selectedForeground"), "editorSuggestWidget.selectedBackground", C("editorSuggestWidget.selectedBackground"));
        t.Add("suggest.selected.match", "Letras coincidentes de la sugerencia seleccionada", Text, "editorSuggestWidget.focusHighlightForeground", C("editorSuggestWidget.focusHighlightForeground"), "editorSuggestWidget.selectedBackground", C("editorSuggestWidget.selectedBackground"));
        t.Add("hover.text", "Texto del panel flotante de información", Text, "editorHoverWidget.foreground", C("editorHoverWidget.foreground"), "editorHoverWidget.background", C("editorHoverWidget.background"));
        t.Add("input.text", "Texto escrito en los campos de los widgets", Text, "input.foreground", C("input.foreground"), "input.background", C("input.background"));
        t.Add("input.placeholder", "Texto de ayuda de los campos de los widgets", Text, "input.placeholderForeground", C("input.placeholderForeground"), "input.background", C("input.background"));

        // Non-text: caret, focus, field boundaries, thumbs, selected suggestion, find and bracket outlines.
        t.Add("cursor.bg", "Cursor de texto sobre el fondo", ContrastKind.FocusIndicator, "editorCursor.foreground", C("editorCursor.foreground"), "editor.background", bg);
        t.Add("cursor.line", "Cursor de texto sobre la línea actual", ContrastKind.FocusIndicator, "editorCursor.foreground", C("editorCursor.foreground"), "editor.lineHighlightBackground", line);
        t.Add("focus.bg", "Contorno de foco contra el editor", ContrastKind.FocusIndicator, "focusBorder", C("focusBorder"), "editor.background", bg);
        t.Add("focus.widget", "Contorno de foco contra los widgets", ContrastKind.FocusIndicator, "focusBorder", C("focusBorder"), "editorWidget.background", C("editorWidget.background"));
        t.Add("focus.input", "Contorno de foco contra los campos", ContrastKind.FocusIndicator, "focusBorder", C("focusBorder"), "input.background", C("input.background"));
        t.Add("input.border", "Contorno de los campos contra el widget", Ui, "input.border", C("input.border"), "editorWidget.background", C("editorWidget.background"));
        t.Add("scrollbar", "Pulgar de la barra de desplazamiento contra el editor", Ui, "scrollbarSlider.background", C("scrollbarSlider.background"), "editor.background", bg);
        t.Add("scrollbar.hover", "Pulgar bajo el ratón contra el editor", Ui, "scrollbarSlider.hoverBackground", C("scrollbarSlider.hoverBackground"), "editor.background", bg);
        t.Add("scrollbar.active", "Pulgar arrastrado contra el editor", Ui, "scrollbarSlider.activeBackground", C("scrollbarSlider.activeBackground"), "editor.background", bg);
        t.Add("suggest.selected.state", "Sugerencia seleccionada frente a las demás", Ui, "editorSuggestWidget.selectedBackground", C("editorSuggestWidget.selectedBackground"), "editorSuggestWidget.background", C("editorSuggestWidget.background"));
        t.Add("find.border", "Contorno de la coincidencia actual de la búsqueda", Ui, "editor.findMatchBorder", C("editor.findMatchBorder"), "editor.background", bg);
        t.Add("bracket.border", "Contorno del paréntesis que hace pareja", Ui, "editorBracketMatch.border", C("editorBracketMatch.border"), "editor.background", bg);

        const string Decorative = "Decorativo: guía o borde que separa regiones que ya se distinguen por su contenido";
        t.Exempt("indent.guide", "Guías de sangría", Decorative, "editorIndentGuide.background1", C("editorIndentGuide.background1"), "editor.background", bg);
        t.Exempt("indent.guide.active", "Guía de sangría activa", Decorative, "editorIndentGuide.activeBackground1", C("editorIndentGuide.activeBackground1"), "editor.background", bg);
        t.Exempt("widget.border", "Borde de los widgets", Decorative, "editorWidget.border", C("editorWidget.border"), "editor.background", bg);
        t.Exempt("suggest.border", "Borde de la lista de sugerencias", Decorative, "editorSuggestWidget.border", C("editorSuggestWidget.border"), "editor.background", bg);
        t.Exempt("hover.border", "Borde del panel flotante", Decorative, "editorHoverWidget.border", C("editorHoverWidget.border"), "editor.background", bg);

        t.AssertAllPass();
    }

    [Fact]
    public void Every_translucent_colour_of_the_editor_theme_is_reviewed()
    {
        var colours = EditorColours();
        var translucent = colours.Where(c => !c.Value.IsOpaque).Select(c => c.Key).Order().ToList();

        // A new overlay must be added to Overlays so that text on it is measured.
        Assert.Equal(Overlays.Order().ToList(), translucent);
    }

    [Fact]
    public void Editor_theme_does_not_inherit_monaco_rules_that_this_file_cannot_show()
    {
        var js = ReadAsset("editor", "editor.js");

        Assert.Matches(@"inherit:\s*false", js);
        Assert.True(EditorRules().Count >= 30, "THEME_RULES looks truncated.");
    }

    [Fact]
    public void Editor_colours_mirror_the_tokens_they_come_from()
    {
        var colours = EditorColours();
        var rules = EditorRules();
        var mismatches = WpfHost.Run(() =>
        {
            var list = new List<string>();
            void Expect(string key, string token)
            {
                if (!colours.TryGetValue(key, out var colour))
                {
                    list.Add($"editor.js has no '{key}'.");
                }
                else if (!SameRgb(colour, ThemeResources.Color(token)))
                {
                    list.Add($"{key} is {colour} but {token} is {ThemeResources.Color(token)}.");
                }
            }

            Expect("editor.background", "BackgroundColor");
            Expect("editor.foreground", "TextPrimaryColor");
            Expect("editor.lineHighlightBackground", "SurfaceColor");
            Expect("editorCursor.foreground", "AccentColor");
            Expect("focusBorder", "AccentColor");
            Expect("editorLineNumber.activeForeground", "TextPrimaryColor");
            Expect("editorIndentGuide.background1", "BorderColor");
            Expect("editorIndentGuide.activeBackground1", "BorderStrongColor");
            foreach (var widget in new[] { "editorWidget", "editorSuggestWidget", "editorHoverWidget" })
            {
                Expect(widget + ".background", "SurfaceRaisedColor");
                Expect(widget + ".foreground", "TextPrimaryColor");
                Expect(widget + ".border", "BorderColor");
            }

            Expect("editorSuggestWidget.selectedBackground", "AccentColor");
            Expect("editorSuggestWidget.selectedForeground", "OnAccentColor");
            Expect("input.background", "SurfaceColor");
            Expect("input.foreground", "TextPrimaryColor");
            Expect("input.border", "BorderStrongColor");
            Expect("input.placeholderForeground", "TextSecondaryColor");
            Expect("scrollbarSlider.background", "BorderStrongColor");
            Expect("scrollbarSlider.activeBackground", "AccentColor");

            // Every highlight is the accent at some alpha; the diff ones are the status colours.
            foreach (var key in Overlays)
            {
                Expect(key, key.StartsWith("diffEditor.inserted", StringComparison.Ordinal) ? "SuccessColor" : key.StartsWith("diffEditor.removed", StringComparison.Ordinal) ? "ErrorColor" : "AccentColor");
            }

            var sql = rules.Single(r => r.Token == "string.sql").Colour;
            if (!SameRgb(sql, ThemeResources.Color("ErrorColor")))
            {
                list.Add($"string.sql is {sql} but ErrorColor is {ThemeResources.Color("ErrorColor")}.");
            }

            return list;
        });

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    // ---- xterm.js -------------------------------------------------------------------------------------------------

    [Fact]
    public void Terminal_theme_meets_WCAG_AA()
    {
        var js = ReadAsset("terminal", "terminal.js");
        var theme = TerminalTheme(js);
        var css = ReadAsset("terminal", "terminal.css");
        var t = new ContrastTable("terminal");
        Rgba C(string key) => theme.TryGetValue(key, out var colour) ? colour : throw new KeyNotFoundException($"terminal.js TERMINAL_THEME has no '{key}'.");

        var bg = C("background");
        t.Add("fg.bg", "Texto del terminal sobre el fondo", Text, "foreground", C("foreground"), "background", bg);
        t.Add("cursor.glyph", "Carácter bajo el cursor de bloque", Text, "cursorAccent", C("cursorAccent"), "cursor", C("cursor"));
        t.Add("selection.text", "Texto seleccionado del terminal", Text, "selectionForeground", C("selectionForeground"), "selectionBackground", C("selectionBackground"));
        t.Add("cursor.bg", "Cursor del terminal contra el fondo", ContrastKind.FocusIndicator, "cursor", C("cursor"), "background", bg);
        t.Add("scrollbar", "Pulgar de la barra de desplazamiento contra el fondo", Ui, "scrollbarSliderBackground", C("scrollbarSliderBackground"), "background", bg);
        t.Add("scrollbar.hover", "Pulgar bajo el ratón contra el fondo", Ui, "scrollbarSliderHoverBackground", C("scrollbarSliderHoverBackground"), "background", bg);
        t.Add("scrollbar.active", "Pulgar arrastrado contra el fondo", Ui, "scrollbarSliderActiveBackground", C("scrollbarSliderActiveBackground"), "background", bg);

        foreach (var name in AnsiNames)
        {
            if (name == "black")
            {
                t.Exempt(
                    "ansi.black",
                    "Color ANSI 0 (negro) como texto",
                    "Solo es legible como fondo de líneas de estado de colores, que es para lo que lo usan los programas; el texto negro lo eleva minimumContrastRatio al dibujarlo",
                    name,
                    C(name),
                    "background",
                    bg,
                    Text);
            }
            else
            {
                t.Add($"ansi.{name}", $"Color ANSI '{name}' como texto sobre el fondo", Text, name, C(name), "background", bg);
            }
        }

        t.Add("error.css", "Mensaje de error de la página del terminal (terminal.css)", Text, "#error color", ParseCss(ErrorColour(), css, "terminal.css #error color"), "background", bg);

        t.AssertAllPass();
    }

    [Fact]
    public void Terminal_lifts_any_other_colour_pair_to_4_5_at_render_time()
    {
        var js = ReadAsset("terminal", "terminal.js");

        var match = MinimumContrastRatio().Match(js);

        Assert.True(match.Success, "terminal.js does not set minimumContrastRatio.");
        Assert.True(double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) >= Wcag.NormalText);
        Assert.Contains("theme: TERMINAL_THEME", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_colours_mirror_the_tokens_they_come_from()
    {
        var theme = TerminalTheme(ReadAsset("terminal", "terminal.js"));
        var css = ReadAsset("terminal", "terminal.css");
        var mismatches = WpfHost.Run(() =>
        {
            var list = new List<string>();
            void Expect(string key, string token)
            {
                if (!theme.TryGetValue(key, out var colour))
                {
                    list.Add($"terminal.js has no '{key}'.");
                }
                else if (!SameRgb(colour, ThemeResources.Color(token)))
                {
                    list.Add($"{key} is {colour} but {token} is {ThemeResources.Color(token)}.");
                }
            }

            Expect("background", "BackgroundColor");
            Expect("foreground", "TextPrimaryColor");
            Expect("cursor", "AccentColor");
            Expect("cursorAccent", "OnAccentColor");
            Expect("selectionBackground", "BorderColor");
            Expect("selectionForeground", "TextPrimaryColor");
            Expect("scrollbarSliderBackground", "BorderStrongColor");
            Expect("scrollbarSliderActiveBackground", "AccentColor");
            if (!SameRgb(ParseCss(ErrorColour(), css, "terminal.css #error color"), ThemeResources.Color("ErrorColor")))
            {
                list.Add("terminal.css #error color is not ErrorColor.");
            }

            return list;
        });

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
        Assert.Equal(16 + 9, theme.Count);
    }

    // ---- Page backgrounds -----------------------------------------------------------------------------------------

    [Fact]
    public void Page_and_host_backgrounds_match_the_background_token()
    {
        var editorHtml = ReadAsset("editor", "editor.html");
        var terminalCss = ReadAsset("terminal", "terminal.css");
        var (token, editorControl, editorWeb, terminalWeb) = WpfHost.Run(() =>
        {
            using var editor = new CodeEditorControl();
            var terminal = new TerminalControl();
            return (
                ThemeResources.Color("BackgroundColor"),
                ThemeResources.ToRgba(editor.Background, "CodeEditorControl.Background"),
                FromDrawing(((WebView2)editor.FindName("WebView")).DefaultBackgroundColor),
                FromDrawing(((WebView2)terminal.FindName("WebView")).DefaultBackgroundColor));
        });

        var mismatches = new List<string>();
        void Expect(string what, Rgba colour)
        {
            if (!SameRgb(colour, token))
            {
                mismatches.Add($"{what} is {colour} but BackgroundColor is {token}.");
            }
        }

        Expect("editor.html body background", ParseCss(PageBackground(), editorHtml, "editor.html html, body background"));
        Expect("terminal.css body background", ParseCss(PageBackground(), terminalCss, "terminal.css html, body background"));
        Expect("CodeEditorControl.Background", editorControl);
        Expect("CodeEditorControl WebView DefaultBackgroundColor", editorWeb);
        Expect("TerminalControl WebView DefaultBackgroundColor", terminalWeb);

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    // ---- Parsing --------------------------------------------------------------------------------------------------

    private static string ReadAsset(params string[] relative) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Assets", .. relative]));

    private static Dictionary<string, Rgba> EditorColours() => ColourLiteral(ReadAsset("editor", "editor.js"), "THEME_COLORS", '{', '}');

    private static Dictionary<string, Rgba> TerminalTheme(string js) => ColourLiteral(js, "TERMINAL_THEME", '{', '}');

    private static List<(string Token, Rgba Colour)> EditorRules()
    {
        var literal = Literal(ReadAsset("editor", "editor.js"), "THEME_RULES", '[', ']');
        var rules = ColouredRule().Matches(literal)
            .Select(m => (m.Groups["token"].Value, Rgba.ParseCss("#" + m.Groups["hex"].Value)))
            .ToList();
        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(r => r.Item1).Distinct().Count());
        return rules;
    }

    /// <summary>The <c>key: '#hex'</c> entries of a JS object literal, with duplicate keys rejected (JS would keep the last).</summary>
    private static Dictionary<string, Rgba> ColourLiteral(string js, string name, char open, char close)
    {
        var colours = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        foreach (Match entry in HexEntry().Matches(Literal(js, name, open, close)))
        {
            var key = entry.Groups["key"].Value;
            Assert.True(colours.TryAdd(key, Rgba.ParseCss(entry.Groups["hex"].Value)), $"'{key}' appears twice in {name}.");
        }

        Assert.NotEmpty(colours);
        return colours;
    }

    /// <summary>The text of <c>var NAME = {...};</c> or <c>var NAME = [...];</c> (the literal that starts and ends a line).</summary>
    private static string Literal(string js, string name, char open, char close)
    {
        var header = $"var {name} = {open}";
        var start = js.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{header}' not found in the page script.");
        var end = js.IndexOf($"{close};", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The literal {name} is not closed by '{close};'.");
        return js[(start + header.Length)..end];
    }

    private static Rgba ParseCss(Regex pattern, string text, string what)
    {
        var match = pattern.Match(text);
        Assert.True(match.Success, $"{what} not found.");
        return Rgba.ParseCss(match.Groups["hex"].Value);
    }

    private static Rgba FromDrawing(System.Drawing.Color colour) => new(colour.R, colour.G, colour.B, colour.A);

    private static bool SameRgb(Rgba a, Rgba b) => a.R == b.R && a.G == b.G && a.B == b.B;
}
