using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentStudio.Desktop.Views;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// WCAG 2.x AA of the implicit styles that Themes/Dark.xaml gives WPF's own controls (Button, CheckBox, RadioButton,
/// ComboBox, TextBox, ToolTip, ContextMenu, MenuItem, ListBox, Expander) and of the rows of the Explorer, in every state they
/// draw: rest, hover, pressed, checked, selected, highlighted, focused and disabled. Every colour is read from the real
/// styles and templates (setters, template triggers and named parts), so changing a brush there changes the pair here. The
/// same rules as <see cref="ThemeContrastTests"/>: text 4.5:1; outlines, glyphs, state fills and focus cues 3:1 against
/// what is next to them; disabled controls and decorative frames are listed with the ratio they have, exempt.
/// </summary>
public sealed class ControlStylesContrastTests
{
    private const ContrastKind Text = ContrastKind.NormalText;
    private const ContrastKind Ui = ContrastKind.UiComponent;
    private const ContrastKind Focus = ContrastKind.FocusIndicator;
    private const string Disabled = "Desactivado: WCAG no exige contraste a los componentes inactivos";
    private const string Decorative = "Decorativo: separa regiones que ya se distinguen por su contenido";

    /// <summary>The three surfaces of the theme, darkest first, with the name the report uses.</summary>
    private static readonly (string Token, string Id)[] Surfaces =
    [
        ("BackgroundColor", "bg"),
        ("SurfaceColor", "surface"),
        ("SurfaceRaisedColor", "raised"),
    ];

    [Fact]
    public void Plain_buttons_meet_WCAG_AA_in_every_state() => WpfHost.Run(() =>
    {
        var t = new ContrastTable("wpf-boton");
        var style = ThemeResources.Style(typeof(Button));
        var template = ThemeResources.Template(style);
        var text = Setter(style, Control.ForegroundProperty);
        var outline = Setter(style, Control.BorderBrushProperty);
        var hover = Trigger(template, UIElement.IsMouseOverProperty, true, "Root", Border.BackgroundProperty);
        var pressed = Trigger(template, ButtonBase.IsPressedProperty, true, "Root", Border.BackgroundProperty);
        var pressedText = Trigger(template, ButtonBase.IsPressedProperty, true, null, Control.ForegroundProperty);
        foreach (var (token, id) in Surfaces)
        {
            t.Add($"Button.text.{id}", $"Botón sin estilo propio (terminal: «Reiniciar», «Detener»): texto sobre {id}", Text, "Foreground", text, token, Token(token));
            t.Add($"Button.outline.{id}", $"Botón sin estilo propio: contorno contra {id}", Ui, "BorderBrush", outline, token, Token(token));
            t.Add($"Button.pressed.fill.{id}", $"Botón sin estilo propio pulsado: relleno contra {id}", Ui, "Pressed", pressed, token, Token(token));
        }

        t.Add("Button.hover.text", "Botón sin estilo propio bajo el ratón: texto sobre el relleno", Text, "Foreground", text, "Hover", hover);
        t.Add("Button.hover.outline", "Botón sin estilo propio bajo el ratón: contorno contra el relleno", Ui, "BorderBrush", outline, "Hover", hover);
        t.Add("Button.pressed.text", "Botón sin estilo propio pulsado: texto sobre el relleno", Text, "Pressed Foreground", pressedText, "Pressed", pressed);
        AddDisabled(t, "Button.disabled", "Botón sin estilo propio desactivado", Text, TemplateOpacity(template), text, "SurfaceColor");
        t.AssertAllPass();
    });

    [Fact]
    public void Check_boxes_and_radio_buttons_meet_WCAG_AA_in_every_state() => WpfHost.Run(() =>
    {
        var t = new ContrastTable("wpf-casillas");

        var box = ThemeResources.Style(typeof(CheckBox));
        var boxTemplate = ThemeResources.Template(box);
        var label = Setter(box, Control.ForegroundProperty);
        var outline = Setter(box, Control.BorderBrushProperty);
        var hover = Trigger(boxTemplate, UIElement.IsMouseOverProperty, true, "Box", Border.BackgroundProperty);
        var pressed = Trigger(boxTemplate, ButtonBase.IsPressedProperty, true, "Box", Border.BackgroundProperty);
        var checkedFill = Trigger(boxTemplate, ToggleButton.IsCheckedProperty, true, "Box", Border.BackgroundProperty);
        var tick = Part(boxTemplate, "CheckMark", Shape.StrokeProperty);
        var mixed = Part(boxTemplate, "Mixed", Shape.FillProperty);
        var mixedOutline = Trigger(boxTemplate, ToggleButton.IsCheckedProperty, null, "Box", Border.BorderBrushProperty);

        var radio = ThemeResources.Style(typeof(RadioButton));
        var radioTemplate = ThemeResources.Template(radio);
        var radioLabel = Setter(radio, Control.ForegroundProperty);
        var ring = Setter(radio, Control.BorderBrushProperty);
        var radioHover = Trigger(radioTemplate, UIElement.IsMouseOverProperty, true, "Ring", Shape.FillProperty);
        var radioChecked = Trigger(radioTemplate, ToggleButton.IsCheckedProperty, true, "Ring", Shape.FillProperty);
        var dot = Part(radioTemplate, "Dot", Shape.FillProperty);

        foreach (var (token, id) in Surfaces)
        {
            var surface = Token(token);
            t.Add($"CheckBox.label.{id}", $"Etiqueta de una casilla sobre {id}", Text, "Foreground", label, token, surface);
            t.Add($"CheckBox.box.{id}", $"Contorno de la casilla contra {id}", Ui, "BorderBrush", outline, token, surface);
            t.Add($"CheckBox.pressed.{id}", $"Casilla pulsada: relleno contra {id}", Ui, "Pressed", pressed, token, surface);
            t.Add($"CheckBox.checked.{id}", $"Casilla marcada: relleno contra {id}", Ui, "Checked", checkedFill, token, surface);
            t.Add($"CheckBox.mixed.bar.{id}", $"Casilla indeterminada: barra contra {id} (la casilla es transparente)", Ui, "Mixed", mixed, token, surface);
            t.Add($"CheckBox.mixed.outline.{id}", $"Casilla indeterminada: contorno contra {id}", Ui, "Mixed outline", mixedOutline, token, surface);
            t.Add($"RadioButton.label.{id}", $"Etiqueta de una opción sobre {id}", Text, "Foreground", radioLabel, token, surface);
            t.Add($"RadioButton.ring.{id}", $"Círculo de la opción contra {id}", Ui, "BorderBrush", ring, token, surface);
            t.Add($"RadioButton.checked.{id}", $"Opción elegida: relleno contra {id}", Ui, "Checked", radioChecked, token, surface);
        }

        t.Add("CheckBox.box.hover", "Casilla bajo el ratón: contorno contra su relleno", Ui, "BorderBrush", outline, "Hover", hover);
        t.Add("CheckBox.mixed.bar.hover", "Casilla indeterminada bajo el ratón: barra contra el relleno", Ui, "Mixed", mixed, "Hover", hover);
        t.Add("CheckBox.checked.tick", "Casilla marcada: marca contra el relleno", Ui, "CheckMark", tick, "Checked", checkedFill);
        t.Add("RadioButton.ring.hover", "Opción bajo el ratón: círculo contra su relleno", Ui, "BorderBrush", ring, "Hover", radioHover);
        t.Add("RadioButton.checked.dot", "Opción elegida: punto contra el relleno", Ui, "Dot", dot, "Checked", radioChecked);
        AddDisabled(t, "CheckBox.disabled", "Casilla desactivada", Text, StyleOpacity(box), label, "SurfaceColor");
        AddDisabled(t, "RadioButton.disabled", "Opción desactivada", Text, StyleOpacity(radio), radioLabel, "SurfaceColor");
        t.AssertAllPass();
    });

    [Fact]
    public void Combo_boxes_and_their_lists_meet_WCAG_AA_in_every_state() => WpfHost.Run(() =>
    {
        var t = new ContrastTable("wpf-desplegable");
        var style = ThemeResources.Style(typeof(ComboBox));
        var template = ThemeResources.Template(style);
        var text = Setter(style, Control.ForegroundProperty);
        var field = Setter(style, Control.BackgroundProperty);
        var outline = Setter(style, Control.BorderBrushProperty);
        var arrow = Part(template, "Toggle", Control.ForegroundProperty);
        var hover = Trigger(template, UIElement.IsMouseOverProperty, true, "Toggle", Control.BackgroundProperty);
        var open = Trigger(template, ComboBox.IsDropDownOpenProperty, true, "Toggle", Control.BorderBrushProperty);
        var list = Part(template, "DropDown", Border.BackgroundProperty);
        var listOutline = Part(template, "DropDown", Border.BorderBrushProperty);

        var item = ThemeResources.Style(typeof(ComboBoxItem));
        var itemTemplate = ThemeResources.Template(item);
        var itemText = Setter(item, Control.ForegroundProperty);
        var highlighted = Trigger(itemTemplate, ComboBoxItem.IsHighlightedProperty, true, "Root", Border.BackgroundProperty);
        var highlightedText = Trigger(itemTemplate, ComboBoxItem.IsHighlightedProperty, true, null, Control.ForegroundProperty);
        var selected = Trigger(itemTemplate, ListBoxItem.IsSelectedProperty, true, "Root", Border.BackgroundProperty);
        var selectedText = Trigger(itemTemplate, ListBoxItem.IsSelectedProperty, true, null, Control.ForegroundProperty);

        t.Add("ComboBox.text", "Desplegable cerrado: texto sobre el campo", Text, "Foreground", text, "Background", field);
        t.Add("ComboBox.arrow", "Desplegable cerrado: flecha sobre el campo", Ui, "Arrow", arrow, "Background", field);
        t.Add("ComboBox.hover.text", "Desplegable bajo el ratón: texto sobre el campo", Text, "Foreground", text, "Hover", hover);
        t.Add("ComboBox.hover.arrow", "Desplegable bajo el ratón: flecha sobre el campo", Ui, "Arrow", arrow, "Hover", hover);
        foreach (var (token, id) in Surfaces)
        {
            t.Add($"ComboBox.outline.{id}", $"Desplegable: contorno contra {id}", Ui, "BorderBrush", outline, token, Token(token));
            t.Add($"ComboBox.open.{id}", $"Desplegable abierto: contorno de acento contra {id}", Ui, "Open", open, token, Token(token));
            t.Add($"ComboBox.list.outline.{id}", $"Lista desplegada: contorno contra lo que tapa ({id})", Ui, "List BorderBrush", listOutline, token, Token(token));
        }

        t.Add("ComboBoxItem.text", "Lista desplegada: texto de una entrada", Text, "Foreground", itemText, "List", list);
        t.Add("ComboBoxItem.highlighted.fill", "Lista desplegada: entrada señalada (ratón o teclado) contra la lista", Focus, "Highlighted", highlighted, "List", list);
        t.Add("ComboBoxItem.highlighted.text", "Lista desplegada: texto de la entrada señalada", Text, "Highlighted Foreground", highlightedText, "Highlighted", highlighted);
        t.Add("ComboBoxItem.selected.fill", "Lista desplegada: entrada elegida contra la lista", Ui, "Selected", selected, "List", list);
        t.Add("ComboBoxItem.selected.text", "Lista desplegada: texto de la entrada elegida", Text, "Selected Foreground", selectedText, "Selected", selected);
        AddDisabled(t, "ComboBox.disabled", "Desplegable desactivado", Text, StyleOpacity(style), text, "SurfaceColor", field);
        t.AssertAllPass();
    });

    [Fact]
    public void Text_boxes_meet_WCAG_AA_in_every_state() => WpfHost.Run(() =>
    {
        var t = new ContrastTable("wpf-campo-texto");
        var style = ThemeResources.Style(typeof(TextBox));
        var template = ThemeResources.Template(style);
        var text = Setter(style, Control.ForegroundProperty);
        var field = Setter(style, Control.BackgroundProperty);
        var outline = Setter(style, Control.BorderBrushProperty);
        var caret = Setter(style, TextBoxBase.CaretBrushProperty);
        var selection = Setter(style, TextBoxBase.SelectionBrushProperty);
        var hover = Trigger(template, UIElement.IsMouseOverProperty, true, "Frame", Border.BorderBrushProperty);
        var frame = Part(template, "FocusFrame", Border.BorderBrushProperty);
        var selectionOpacity = (byte)Math.Round(255 * new TextBox().SelectionOpacity);

        t.Add("TextBox.text", "Campo de texto: texto sobre el campo", Text, "Foreground", text, "Background", field);
        t.Add("TextBox.caret", "Campo de texto: cursor (indicador de foco)", Focus, "CaretBrush", caret, "Background", field);
        t.Add("TextBox.focus.field", "Campo de texto con el foco: marco de acento contra el campo", Focus, "FocusFrame", frame, "Background", field);
        foreach (var (token, id) in Surfaces)
        {
            t.Add($"TextBox.outline.{id}", $"Campo de texto: contorno contra {id}", Ui, "BorderBrush", outline, token, Token(token));
            t.Add($"TextBox.hover.{id}", $"Campo de texto bajo el ratón: contorno contra {id}", Ui, "Hover BorderBrush", hover, token, Token(token));
            t.Add($"TextBox.focus.{id}", $"Campo de texto con el foco: marco de acento contra {id}", Focus, "FocusFrame", frame, token, Token(token));
        }

        // The selection is drawn over the text with SelectionOpacity: both the letters and the field show through it.
        t.Add(
            "TextBox.selection",
            "Campo de texto: texto seleccionado (la selección se pinta encima, translúcida)",
            ContrastKind.TextOnHighlight,
            "Foreground bajo la selección",
            Wcag.Over(selection with { A = selectionOpacity }, text),
            "Background bajo la selección",
            Wcag.Over(selection with { A = selectionOpacity }, field));
        AddDisabled(t, "TextBox.disabled", "Campo de texto desactivado", Text, StyleOpacity(style), text, "SurfaceColor", field);
        t.AssertAllPass();
    });

    [Fact]
    public void Tooltips_and_menus_meet_WCAG_AA_in_every_state() => WpfHost.Run(() =>
    {
        var t = new ContrastTable("wpf-tooltip-menu");
        var tip = ThemeResources.Style(typeof(ToolTip));
        var tipText = Setter(tip, Control.ForegroundProperty);
        var tipFill = Setter(tip, Control.BackgroundProperty);
        var tipOutline = Setter(tip, Control.BorderBrushProperty);

        var menu = ThemeResources.Style(typeof(ContextMenu));
        var menuFill = Setter(menu, Control.BackgroundProperty);
        var menuOutline = Setter(menu, Control.BorderBrushProperty);

        var item = ThemeResources.Style(typeof(MenuItem));
        var itemTemplate = ThemeResources.Template(item);
        var itemText = Setter(item, Control.ForegroundProperty);
        var gesture = Part(itemTemplate, "Gesture", TextBlock.ForegroundProperty);
        var arrow = Part(itemTemplate, "Arrow", Shape.StrokeProperty);
        var submenuFill = Part(itemTemplate, "Submenu", Border.BackgroundProperty);
        var submenuOutline = Part(itemTemplate, "Submenu", Border.BorderBrushProperty);
        var highlighted = Trigger(itemTemplate, MenuItem.IsHighlightedProperty, true, "Root", Border.BackgroundProperty);
        var highlightedText = Trigger(itemTemplate, MenuItem.IsHighlightedProperty, true, null, Control.ForegroundProperty);
        var highlightedGesture = Trigger(itemTemplate, MenuItem.IsHighlightedProperty, true, "Gesture", TextBlock.ForegroundProperty);
        var highlightedArrow = Trigger(itemTemplate, MenuItem.IsHighlightedProperty, true, "Arrow", Shape.StrokeProperty);
        var separator = (Border)ThemeResources.Template(ThemeResources.Style(MenuItem.SeparatorStyleKey)).LoadContent();

        t.Add("ToolTip.text", "Tooltip: texto sobre su fondo", Text, "Foreground", tipText, "Background", tipFill);
        t.Add("ContextMenu.item.text", "Menú: texto de una entrada", Text, "Foreground", itemText, "Background", menuFill);
        t.Add("ContextMenu.item.gesture", "Menú: atajo de una entrada (Ctrl+X)", Text, "Gesture", gesture, "Background", menuFill);
        t.Add("ContextMenu.item.arrow", "Menú: flecha de un submenú", Ui, "Arrow", arrow, "Background", menuFill);
        t.Add("ContextMenu.item.check", "Menú: marca de una entrada activada (color del texto)", Ui, "Foreground", itemText, "Background", menuFill);
        t.Add("ContextMenu.submenu.text", "Submenú: texto de una entrada", Text, "Foreground", itemText, "Submenu", submenuFill);
        t.Add("ContextMenu.highlighted.fill", "Menú: entrada señalada (ratón o teclado) contra el menú", Focus, "Highlighted", highlighted, "Background", menuFill);
        t.Add("ContextMenu.highlighted.text", "Menú: texto de la entrada señalada", Text, "Highlighted Foreground", highlightedText, "Highlighted", highlighted);
        t.Add("ContextMenu.highlighted.gesture", "Menú: atajo de la entrada señalada", Text, "Highlighted Gesture", highlightedGesture, "Highlighted", highlighted);
        t.Add("ContextMenu.highlighted.arrow", "Menú: flecha del submenú señalado", Ui, "Highlighted Arrow", highlightedArrow, "Highlighted", highlighted);
        t.Add("ContextMenu.highlighted.check", "Menú: marca de la entrada señalada (color del texto)", Ui, "Highlighted Foreground", highlightedText, "Highlighted", highlighted);
        foreach (var (token, id) in Surfaces)
        {
            t.Add($"ToolTip.outline.{id}", $"Tooltip: contorno contra lo que tapa ({id})", Ui, "BorderBrush", tipOutline, token, Token(token));
            t.Add($"ContextMenu.outline.{id}", $"Menú: contorno contra lo que tapa ({id})", Ui, "BorderBrush", menuOutline, token, Token(token));
            t.Add($"ContextMenu.submenu.outline.{id}", $"Submenú: contorno contra lo que tapa ({id})", Ui, "Submenu BorderBrush", submenuOutline, token, Token(token));
        }

        t.Exempt("ContextMenu.separator", "Menú: línea entre grupos de entradas", Decorative, "Separator", Of(separator.Background, "Separator"), "Background", menuFill);
        AddDisabled(t, "ContextMenu.item.disabled", "Menú: entrada desactivada", Text, TemplateOpacity(itemTemplate), itemText, "SurfaceRaisedColor", menuFill);
        t.AssertAllPass();
    });

    [Fact]
    public void List_boxes_and_expanders_meet_WCAG_AA_in_every_state() => WpfHost.Run(() =>
    {
        var t = new ContrastTable("wpf-listas");
        var list = ThemeResources.Style(typeof(ListBox));
        var row = ThemeResources.Style(typeof(ListBoxItem));
        var rowTemplate = ThemeResources.Template(row);
        var text = Setter(row, Control.ForegroundProperty);
        var hover = Trigger(rowTemplate, UIElement.IsMouseOverProperty, true, "Root", Border.BackgroundProperty);
        var selected = Trigger(rowTemplate, ListBoxItem.IsSelectedProperty, true, "Root", Border.BackgroundProperty);
        var selectedText = Trigger(rowTemplate, ListBoxItem.IsSelectedProperty, true, null, Control.ForegroundProperty);
        var inactive = InactiveSelection(rowTemplate, ListBoxItem.IsSelectedProperty, "Root");
        var ring = Of(((Rectangle)ThemeResources.Template(ThemeResources.Style("FocusVisualInset")).LoadContent()).Stroke, "FocusVisualInset");

        var expander = ThemeResources.Style(typeof(Expander));
        var header = ThemeResources.Style("Expander.HeaderToggle");
        var headerTemplate = ThemeResources.Template(header);
        var headerText = Setter(expander, Control.ForegroundProperty);
        var chevron = Part(headerTemplate, "Chevron", Shape.StrokeProperty);
        var headerHover = Trigger(headerTemplate, UIElement.IsMouseOverProperty, true, "Root", Border.BackgroundProperty);

        foreach (var (token, id) in Surfaces)
        {
            var surface = Token(token);
            t.Add($"ListBoxItem.text.{id}", $"Lista: texto de una fila sobre {id}", Text, "Foreground", text, token, surface);
            t.Add($"ListBoxItem.selected.fill.{id}", $"Lista con el foco: fila elegida contra {id}", Ui, "Selected", selected, token, surface);
            t.Add($"ListBoxItem.inactive.fill.{id}", $"Lista sin el foco: fila elegida contra {id}", Ui, "Inactive selection", inactive, token, surface);
            t.Add($"Expander.header.{id}", $"Expansor: cabecera sobre {id}", Text, "Foreground", headerText, token, surface);
            t.Add($"Expander.chevron.{id}", $"Expansor: flecha de la cabecera contra {id}", Ui, "Chevron", chevron, token, surface);
            t.Exempt($"ListBox.frame.{id}", $"Lista: marco contra {id}", Decorative, "BorderBrush", Setter(list, Control.BorderBrushProperty), token, surface);
        }

        t.Add("ListBoxItem.hover.text", "Lista: texto de la fila bajo el ratón", Text, "Foreground", text, "Hover", hover);
        t.Add("ListBoxItem.selected.text", "Lista con el foco: texto de la fila elegida", Text, "Selected Foreground", selectedText, "Selected", selected);
        t.Add("ListBoxItem.inactive.text", "Lista sin el foco: texto de la fila elegida", Text, "Selected Foreground", selectedText, "Inactive selection", inactive);
        t.Add("ListBoxItem.ring.selected", "Lista: anillo de foco de la fila contra la fila elegida", Focus, "FocusVisualInset", ring, "Selected", selected);
        t.Add("ListBoxItem.ring.inactive", "Lista: anillo de foco de la fila contra la fila elegida sin el foco", Focus, "FocusVisualInset", ring, "Inactive selection", inactive);
        t.Add("Expander.hover.header", "Expansor: cabecera bajo el ratón", Text, "Foreground", headerText, "Hover", headerHover);
        t.Add("Expander.hover.chevron", "Expansor: flecha de la cabecera bajo el ratón", Ui, "Chevron", chevron, "Hover", headerHover);
        AddDisabled(t, "ListBoxItem.disabled", "Lista: fila desactivada", Text, TemplateOpacity(rowTemplate), text, "SurfaceColor");
        AddDisabled(t, "Expander.disabled", "Expansor desactivado", Text, StyleOpacity(expander), headerText, "SurfaceColor");
        t.AssertAllPass();
    });

    [Fact]
    public void Explorer_rows_meet_WCAG_AA_in_every_state() => WpfHost.Run(() =>
    {
        var t = new ContrastTable("explorador");
        var view = new ExplorerView();
        var tree = (TreeView)view.FindName("Tree");
        var style = tree.ItemContainerStyle;
        var template = ThemeResources.Template(style);
        var panel = Of(((Grid)view.Content).Background, "Explorer background");
        var text = Setter(style, Control.ForegroundProperty);
        var arrow = Part(template, "Arrow", Control.ForegroundProperty);
        var hover = HoverOfFocusableRow(template);
        var selected = Trigger(template, TreeViewItem.IsSelectedProperty, true, "Row", Border.BackgroundProperty);
        var selectedText = Trigger(template, TreeViewItem.IsSelectedProperty, true, null, Control.ForegroundProperty);
        var selectedArrow = Trigger(template, TreeViewItem.IsSelectedProperty, true, "Arrow", Control.ForegroundProperty);
        var inactive = InactiveSelection(template, TreeViewItem.IsSelectedProperty, "Row");

        t.Add("Explorer.text", "Explorador: nombre de un fichero o carpeta sobre el panel", Text, "Foreground", text, "SurfaceColor", panel);
        t.Add("Explorer.arrow", "Explorador: flecha de una carpeta contra el panel", Ui, "Arrow", arrow, "SurfaceColor", panel);
        t.Add("Explorer.hover.text", "Explorador: nombre en la fila bajo el ratón", Text, "Foreground", text, "Hover", hover);
        t.Add("Explorer.hover.arrow", "Explorador: flecha en la fila bajo el ratón", Ui, "Arrow", arrow, "Hover", hover);
        t.Add("Explorer.selected.fill", "Explorador con el foco: fila elegida (toda la fila) contra el panel", Ui, "Selected", selected, "SurfaceColor", panel);
        t.Add("Explorer.selected.text", "Explorador con el foco: nombre en la fila elegida", Text, "Selected Foreground", selectedText, "Selected", selected);
        t.Add("Explorer.selected.arrow", "Explorador con el foco: flecha en la fila elegida", Ui, "Selected Arrow", selectedArrow, "Selected", selected);
        t.Add("Explorer.inactive.fill", "Explorador sin el foco: fila elegida contra el panel", Ui, "Inactive selection", inactive, "SurfaceColor", panel);
        t.Add("Explorer.inactive.text", "Explorador sin el foco: nombre en la fila elegida", Text, "Selected Foreground", selectedText, "Inactive selection", inactive);
        t.Add("Explorer.inactive.arrow", "Explorador sin el foco: flecha en la fila elegida", Ui, "Selected Arrow", selectedArrow, "Inactive selection", inactive);
        t.AssertAllPass();
    });

    private static Rgba Token(string key) => ThemeResources.Color(key);

    private static Rgba Of(Brush? brush, string what) => ThemeResources.ToRgba(brush, what);

    private static Rgba Setter(Style style, DependencyProperty property) => Of(ThemeResources.SetterBrush(style, property), property.Name);

    /// <summary>The brush a template trigger (<paramref name="property"/> = <paramref name="value"/>) sets on a part, or on the control when <paramref name="part"/> is null.</summary>
    private static Rgba Trigger(ControlTemplate template, DependencyProperty property, object? value, string? part, DependencyProperty target)
    {
        var setter = template.Triggers.OfType<Trigger>()
            .Where(trigger => trigger.Property == property && Equals(trigger.Value, value))
            .SelectMany(trigger => trigger.Setters.OfType<Setter>())
            .FirstOrDefault(s => s.Property == target && s.TargetName == part);
        return Of(setter?.Value as Brush ?? throw new InvalidOperationException($"No trigger {property.Name}={value} sets {part}.{target.Name}."), $"{property.Name}={value}");
    }

    /// <summary>The fill of a selected row while its list has not the keyboard focus (IsSelected and not IsSelectionActive).</summary>
    private static Rgba InactiveSelection(ControlTemplate template, DependencyProperty isSelected, string part)
    {
        var setter = template.Triggers.OfType<MultiTrigger>()
            .Where(trigger => trigger.Conditions.Any(c => c.Property == isSelected && Equals(c.Value, true))
                && trigger.Conditions.Any(c => c.Property == Selector.IsSelectionActiveProperty && Equals(c.Value, false)))
            .SelectMany(trigger => trigger.Setters.OfType<Setter>())
            .FirstOrDefault(s => s.Property == Border.BackgroundProperty && s.TargetName == part);
        return Of(setter?.Value as Brush ?? throw new InvalidOperationException("No trigger paints the selection without the focus."), "Inactive selection");
    }

    /// <summary>The hover fill of an Explorer row (only rows that can take the focus light up).</summary>
    private static Rgba HoverOfFocusableRow(ControlTemplate template)
    {
        var setter = template.Triggers.OfType<MultiTrigger>()
            .Where(trigger => trigger.Conditions.Any(c => c.SourceName == "Row" && c.Property == UIElement.IsMouseOverProperty))
            .SelectMany(trigger => trigger.Setters.OfType<Setter>())
            .FirstOrDefault(s => s.Property == Border.BackgroundProperty && s.TargetName == "Row");
        return Of(setter?.Value as Brush ?? throw new InvalidOperationException("No trigger paints the row under the mouse."), "Hover");
    }

    /// <summary>The brush a named part of the template sets on itself (its local value), inside popups too.</summary>
    private static Rgba Part(ControlTemplate template, string name, DependencyProperty property)
    {
        var part = Find((DependencyObject)template.LoadContent(), name) ?? throw new InvalidOperationException($"The template has no part '{name}'.");
        return Of(part.GetValue(property) as Brush, $"{name}.{property.Name}");
    }

    private static FrameworkElement? Find(DependencyObject node, string name)
    {
        if (node is FrameworkElement { Name: var own } element && own == name)
        {
            return element;
        }

        // Logical and visual children both: a popup's card is only a logical child, a border's content is both.
        var children = LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList();
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            children.AddRange(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node)).Select(i => VisualTreeHelper.GetChild(node, i)));
        }

        return children.Distinct().Select(c => Find(c, name)).FirstOrDefault(found => found is not null);
    }

    /// <summary>The Opacity a style trigger gives the control while it is disabled.</summary>
    private static double StyleOpacity(Style style) => style.Triggers.OfType<Trigger>()
        .Where(trigger => trigger.Property == UIElement.IsEnabledProperty && Equals(trigger.Value, false))
        .SelectMany(trigger => trigger.Setters.OfType<Setter>())
        .Where(setter => setter.Property == UIElement.OpacityProperty)
        .Select(setter => (double)setter.Value)
        .Single();

    /// <summary>The Opacity a template trigger gives its root part while the control is disabled.</summary>
    private static double TemplateOpacity(ControlTemplate template) => template.Triggers.OfType<Trigger>()
        .Where(trigger => trigger.Property == UIElement.IsEnabledProperty && Equals(trigger.Value, false))
        .SelectMany(trigger => trigger.Setters.OfType<Setter>())
        .Where(setter => setter.Property == UIElement.OpacityProperty)
        .Select(setter => (double)setter.Value)
        .Single();

    /// <summary>Disabled controls are dimmed with Opacity over the surface they sit on: exempt, reported with the ratio they have.</summary>
    private static void AddDisabled(ContrastTable table, string id, string usage, ContrastKind kind, double opacity, Rgba foreground, string surfaceToken, Rgba? fill = null)
    {
        var surface = Token(surfaceToken);
        var alpha = (byte)Math.Round(255 * opacity);
        var background = fill is { } solid ? Wcag.Over(solid with { A = alpha }, surface) : surface;
        table.Exempt(
            id,
            usage,
            Disabled,
            "Foreground con Opacity " + opacity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            Wcag.Over(foreground with { A = alpha }, background),
            fill is null ? surfaceToken : "Background con la misma Opacity",
            background,
            kind);
    }
}
