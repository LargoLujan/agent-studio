using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// WCAG 2.x AA contrast of the orchestrator screen (Themes/Nodify.xaml), measured after <see cref="NodifyThemeTokens"/>
/// has repainted Nodify from the tokens, as the app does. The pairs are the ones the canvas and its panels draw: node
/// texts, the eight status colours as borders and dots, connectors, wires, the pending wire and the side-panel fields.
/// <see cref="Every_nodify_colour_key_is_repainted"/> keeps the token table exact: a key that a Nodify upgrade adds or
/// renames fails it instead of keeping Nodify's own colour.
/// </summary>
public sealed class NodifyThemeContrastTests
{
    private const string NodifyDarkUri = "pack://application:,,,/Nodify;component/Themes/Dark.xaml";
    private const string NodifyFocusUri = "pack://application:,,,/Nodify;component/Themes/FocusVisual.xaml";
    private const string AppDarkUri = "pack://application:,,,/AgentStudio.Desktop;component/Themes/Dark.xaml";
    private const ContrastKind Text = ContrastKind.NormalText;
    private const ContrastKind Ui = ContrastKind.UiComponent;

    [Fact]
    public void Every_nodify_colour_key_is_repainted()
    {
        var (nodifyKeys, unknownTokens) = WpfHost.Run(() =>
        {
            // Themes/Nodify.xaml merges both: the colours of the controls and those of the focus ring.
            var keys = ColourKeys(new ResourceDictionary { Source = new Uri(NodifyDarkUri) });
            keys.UnionWith(ColourKeys(new ResourceDictionary { Source = new Uri(NodifyFocusUri) }));
            var missing = NodifyThemeTokens.Map.Values.Distinct()
                .Where(token => ThemeResources.Resources[token] is not Color)
                .ToList();
            return (keys, missing);
        });

        var covered = NodifyThemeTokens.Map.Keys.Concat(NodifyThemeTokens.TransparentKeys).ToHashSet(StringComparer.Ordinal);
        Assert.True(nodifyKeys.Count >= 40, $"Only {nodifyKeys.Count} colour keys were found in Nodify's Dark.xaml; the theme did not load.");
        Assert.Empty(nodifyKeys.Where(k => !covered.Contains(k)).Order(StringComparer.Ordinal));
        Assert.Empty(covered.Where(k => !nodifyKeys.Contains(k)).Order(StringComparer.Ordinal));
        Assert.Empty(unknownTokens);
    }

    [Fact]
    public void Nodify_resources_do_not_shadow_the_app_tokens()
    {
        var shared = WpfHost.Run(() =>
        {
            var nodify = AllKeys(new ResourceDictionary { Source = new Uri(NodifyDarkUri) });
            var app = AllKeys(new ResourceDictionary { Source = new Uri(AppDarkUri) });
            return nodify.Intersect(app).Select(k => k.ToString() ?? string.Empty).Order(StringComparer.Ordinal).ToList();
        });

        Assert.Empty(shared);
    }

    [Fact]
    public void After_the_repaint_the_canvas_uses_the_tokens()
    {
        var (background, header, selection) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            return (
                (Color)ThemeResources.Resources["NodifyEditor.BackgroundColor"],
                (Color)ThemeResources.Resources["Node.HeaderColor"],
                (Color)ThemeResources.Resources["ItemContainer.SelectedColor"]);
        });

        Assert.Equal(Color.FromRgb(0x0A, 0x10, 0x20), background);
        Assert.Equal(Color.FromRgb(0x16, 0x22, 0x3D), header);
        Assert.Equal(Color.FromRgb(0x2E, 0x68, 0xF9), selection);
    }

    [Fact]
    public void Canvas_pairs_meet_WCAG_AA()
    {
        var table = WpfHost.Run(BuildCanvasTable);

        table.AssertAllPass();
    }

    [Fact]
    public void Side_panel_pairs_meet_WCAG_AA()
    {
        var table = WpfHost.Run(BuildPanelTable);

        table.AssertAllPass();
    }

    private static ContrastTable BuildCanvasTable()
    {
        OrchestratorTheme.EnsureLoaded();
        var t = new ContrastTable("lienzo");
        Rgba C(string key) => ThemeResources.Color(key);
        Rgba Of(Brush? brush, string what) => ThemeResources.ToRgba(brush, what);
        Rgba StyleForeground(string style) => Of(ThemeResources.SetterBrush(ThemeResources.Style(style), TextBlock.ForegroundProperty), style + ".Foreground");

        // Node texts: header (id and kind), content (title and the validator's messages) and footer (status).
        t.Add("node.id", "Identificador del nodo en su cabecera", Text, "TextPrimary", C("TextPrimaryColor"), "Node.HeaderColor", C("Node.HeaderColor"));
        t.Add("node.kind", "Tipo del nodo en su cabecera", Text, "SecondaryText", StyleForeground("SecondaryText"), "Node.HeaderColor", C("Node.HeaderColor"));
        t.Add("node.title", "Título de la tarea en el nodo", Text, "BodyText", StyleForeground("BodyText"), "Node.ContentColor", C("Node.ContentColor"));
        t.Add("node.problem", "Mensaje del validador junto al nodo", Text, "ProblemText", StyleForeground("ProblemText"), "Node.ContentColor", C("Node.ContentColor"));
        t.Add("node.status", "Estado de la tarea en el pie del nodo", Text, "SecondaryText", StyleForeground("SecondaryText"), "Node.FooterColor", C("Node.FooterColor"));
        t.Add("connector.label", "Etiqueta Sí/No de una salida de condición", Text, "NodeOutput.ForegroundColor", C("NodeOutput.ForegroundColor"), "Node.BackgroundColor", C("Node.BackgroundColor"));
        // Nodify 7.3 fills the label box of a wire with the wire's Stroke (Themes/Nodify.xaml, style Wire): the colours read
        // back from the style's triggers, so changing them re-runs this check.
        var wire = ThemeResources.Style("Wire");
        foreach (var (branch, id) in new[] { ("yes", "si"), ("no", "no") })
        {
            var trigger = wire.Triggers.OfType<DataTrigger>().Single(tr => Equals(tr.Value, branch));
            Brush Set(string property) => (Brush)trigger.Setters.OfType<Setter>().Single(s => s.Property.Name == property).Value;
            t.Add($"wire.label.{id}", $"Etiqueta «{branch}» sobre la caja de su cable (el trazo)", Text, "Foreground", Of(Set("Foreground"), "Wire label"), "Stroke", Of(Set("Stroke"), "Wire stroke"));
        }

        var selected = wire.Triggers.OfType<Trigger>().Single();
        Brush SetSelected(string property) => (Brush)selected.Setters.OfType<Setter>().Single(s => s.Property.Name == property).Value;
        t.Add("wire.label.selected", "Etiqueta de un cable seleccionado", Text, "Foreground", Of(SetSelected("Foreground"), "Selected label"), "Stroke", Of(SetSelected("Stroke"), "Selected stroke"));
        t.Add("pending.text", "Texto junto al cursor al arrastrar un cable", Text, "BodyText", StyleForeground("BodyText"), "PendingConnection.BackgroundColor", C("PendingConnection.BackgroundColor"));

        // The status colour is the border of the node against the canvas and the dot of the footer.
        foreach (var status in Enum.GetValues<WorkTaskStatus>())
        {
            var brush = $"Status.{status}Brush";
            t.Add($"status.{status}.border", $"Borde de un nodo «{status}» contra el lienzo", Ui, brush, C(brush), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
            t.Add($"status.{status}.header", $"Borde de un nodo «{status}» junto a su cabecera y su pie", Ui, brush, C(brush), "Node.HeaderColor", C("Node.HeaderColor"));
            t.Add($"status.{status}.panel", $"Punto de estado «{status}» en el monitor", Ui, brush, C(brush), "SurfaceColor", C("SurfaceColor"));
        }

        // Selection, focus, connectors and wires.
        t.Add("selection.frame", "Marco de un nodo seleccionado contra el lienzo", Ui, "ItemContainer.SelectedColor", C("ItemContainer.SelectedColor"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        t.Add("focus.ring", "Anillo de foco del lienzo y de los nodos", ContrastKind.FocusIndicator, "NodifyEditor.FocusVisualColor", C("NodifyEditor.FocusVisualColor"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        t.Add("focus.ring.node", "Anillo de foco sobre la cabecera de un nodo", ContrastKind.FocusIndicator, "NodifyEditor.FocusVisualColor", C("NodifyEditor.FocusVisualColor"), "Node.HeaderColor", C("Node.HeaderColor"));
        t.Add("connector.input", "Contorno de una entrada dentro del nodo", Ui, "NodeInput.BorderColor", C("NodeInput.BorderColor"), "Node.BackgroundColor", C("Node.BackgroundColor"));
        t.Add("connector.output", "Contorno de una salida dentro del nodo", Ui, "NodeOutput.BorderColor", C("NodeOutput.BorderColor"), "Node.BackgroundColor", C("Node.BackgroundColor"));
        t.Add("connector.yes", "Contorno de la salida Sí", Ui, "SuccessBrush", C("SuccessBrush"), "Node.BackgroundColor", C("Node.BackgroundColor"));
        t.Add("connector.no", "Contorno de la salida No", Ui, "ErrorBrush", C("ErrorBrush"), "Node.BackgroundColor", C("Node.BackgroundColor"));
        t.Add("wire", "Cable de una dependencia contra el lienzo", Ui, "Connection.StrokeColor", C("Connection.StrokeColor"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        t.Add("wire.yes", "Cable de la rama Sí contra el lienzo", Ui, "SuccessBrush", C("SuccessBrush"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        t.Add("wire.no", "Cable de la rama No contra el lienzo", Ui, "ErrorBrush", C("ErrorBrush"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        t.Add("wire.selected", "Cable seleccionado contra el lienzo", Ui, "AccentBrush", C("AccentBrush"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        t.Add("pending.wire", "Cable que se está arrastrando", Ui, "PendingConnection.StrokeColor", C("PendingConnection.StrokeColor"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        t.Add("pending.wire.refused", "Cable que se está arrastrando hacia una conexión rechazada", Ui, "ErrorBrush", C("ErrorBrush"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));

        // Decorative or inactive: listed with the ratio they really have.
        t.Exempt("pending.box", "Contorno del recuadro de texto del cable arrastrado", "Decorativo: el recuadro ya se distingue por su relleno y su texto", "PendingConnection.BorderColor", C("PendingConnection.BorderColor"), "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        var grid = Of(ThemeResources.Resources["NodifyEditor.SelectionRectangleBackgroundBrush"] as Brush, "Grid brush");
        t.Exempt("grid", "Rejilla del fondo del lienzo", "Decorativo: la rejilla no transmite información", "NodifyEditor.SelectionRectangleBackgroundBrush", grid, "NodifyEditor.BackgroundColor", C("NodifyEditor.BackgroundColor"));
        var debateAlpha = (byte)Math.Round(255 * 0.5);
        t.Exempt("debate.disabled", "Nodo de debate (desactivado: aplazado, docs/15)", "Desactivado: WCAG no exige contraste a los componentes inactivos", "TextPrimary con Opacity 0.5", Wcag.Over(C("TextPrimaryColor") with { A = debateAlpha }, C("Node.ContentColor")), "Node.ContentColor", C("Node.ContentColor"), Text);
        return t;
    }

    private static ContrastTable BuildPanelTable()
    {
        OrchestratorTheme.EnsureLoaded();
        var t = new ContrastTable("paneles-orquestador");
        Rgba C(string key) => ThemeResources.Color(key);
        Rgba Of(Brush? brush, string what) => ThemeResources.ToRgba(brush, what);

        // Text fields (properties of a node, the request, the plan summary).
        var field = ThemeResources.Style("FieldTextBox");
        var fieldText = Of(ThemeResources.SetterBrush(field, Control.ForegroundProperty), "FieldTextBox.Foreground");
        var fieldFill = Of(ThemeResources.SetterBrush(field, Control.BackgroundProperty), "FieldTextBox.Background");
        var fieldBorder = Of(ThemeResources.SetterBrush(field, Control.BorderBrushProperty), "FieldTextBox.BorderBrush");
        var fieldFocus = Of(ThemeResources.TemplateTriggerBrush(field, UIElement.IsKeyboardFocusedProperty, "Root", Border.BorderBrushProperty), "FieldTextBox focused border");
        t.Add("field.text", "Texto escrito en un campo", Text, "Foreground", fieldText, "Background", fieldFill);
        t.Add("field.caret", "Cursor de texto de un campo", ContrastKind.FocusIndicator, "CaretBrush", Of(ThemeResources.SetterBrush(field, TextBoxBase.CaretBrushProperty), "FieldTextBox.CaretBrush"), "Background", fieldFill);
        t.Add("field.border.surface", "Contorno de un campo contra el panel", Ui, "BorderBrush", fieldBorder, "SurfaceColor", C("SurfaceColor"));
        t.Add("field.border.fill", "Contorno de un campo contra su propio relleno", Ui, "BorderBrush", fieldBorder, "Background", fieldFill);
        t.Add("field.focused.surface", "Contorno de un campo con el foco contra el panel", ContrastKind.FocusIndicator, "Focused BorderBrush", fieldFocus, "SurfaceColor", C("SurfaceColor"));
        t.Add("checkbox.label", "Etiqueta de una casilla (requisitos, pasos de verificación)", Text, "FieldCheckBox.Foreground", Of(ThemeResources.SetterBrush(ThemeResources.Style("FieldCheckBox"), Control.ForegroundProperty), "FieldCheckBox.Foreground"), "SurfaceColor", C("SurfaceColor"));
        t.Add("label", "Etiqueta de un campo", Text, "FieldLabel", Of(ThemeResources.SetterBrush(ThemeResources.Style("FieldLabel"), TextBlock.ForegroundProperty), "FieldLabel.Foreground"), "SurfaceColor", C("SurfaceColor"));
        t.Add("problem.surface", "Mensaje del validador o error en un panel", Text, "ProblemText", Of(ThemeResources.SetterBrush(ThemeResources.Style("ProblemText"), TextBlock.ForegroundProperty), "ProblemText.Foreground"), "SurfaceColor", C("SurfaceColor"));

        // State dots of the plan bar and the monitor (ToneToBrushConverter); the text next to each one says the state in
        // words, in the primary text colour.
        foreach (var (token, usage) in new[]
        {
            ("TextSecondaryColor", "sin estado"),
            ("AccentColor", "«En ejecución»"),
            ("SuccessColor", "«Plan válido», «Completada»"),
            ("WarningColor", "«Esperando cuota», «Interrumpida», «Motor no disponible»"),
            ("ErrorColor", "«Fallida», «Bloqueada», problemas del plan"),
        })
        {
            t.Add($"tone.{token[..^5].ToLowerInvariant()}.dot", $"Punto de estado: {usage}", Ui, token, C(token), "SurfaceColor", C("SurfaceColor"));
        }

        t.Add("notice.warning", "Aviso «Motor no disponible» y fases de espera", Text, "WarningColor", C("WarningColor"), "SurfaceColor", C("SurfaceColor"));

        // A problem of the plan is a button (it selects its node): under the mouse the ghost button fills with Raised.
        var ghostHover = Of(ThemeResources.TemplateTriggerBrush(ThemeResources.Style("GhostButton"), UIElement.IsMouseOverProperty, "Root", Border.BackgroundProperty), "GhostButton hover");
        t.Add("problem.hover", "Mensaje del plan bajo el ratón (botón que selecciona su nodo)", Text, "ProblemText", Of(ThemeResources.SetterBrush(ThemeResources.Style("ProblemText"), TextBlock.ForegroundProperty), "ProblemText.Foreground"), "GhostButton hover", ghostHover);
        t.Exempt("splitter", "Separadores arrastrables entre el lienzo, el panel lateral y el monitor", "Decorativo: separa regiones que ya se distinguen por su contenido; el cursor cambia al pasar", "BorderColor", C("BorderColor"), "SurfaceColor", C("SurfaceColor"));

        return t;
    }

    /// <summary>The keys of every <see cref="Color"/> in a dictionary and its merged dictionaries.</summary>
    private static HashSet<string> ColourKeys(ResourceDictionary dictionary)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Walk(ResourceDictionary d)
        {
            foreach (var merged in d.MergedDictionaries)
            {
                Walk(merged);
            }

            foreach (var key in d.Keys.OfType<string>())
            {
                if (d[key] is Color)
                {
                    keys.Add(key);
                }
            }
        }

        Walk(dictionary);
        return keys;
    }

    private static HashSet<object> AllKeys(ResourceDictionary dictionary)
    {
        var keys = new HashSet<object>();
        void Walk(ResourceDictionary d)
        {
            foreach (var merged in d.MergedDictionaries)
            {
                Walk(merged);
            }

            foreach (var key in d.Keys)
            {
                keys.Add(key);
            }
        }

        Walk(dictionary);
        return keys;
    }
}
