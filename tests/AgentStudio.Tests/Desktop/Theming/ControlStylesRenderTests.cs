using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentStudio.Tests.Desktop.Contrast;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Theming;

/// <summary>
/// WPF's own controls with the implicit styles of the theme, drawn without ever being shown (<see cref="OffscreenRender"/>):
/// a combo box closed and the card of its list, check boxes, radio buttons, plain buttons and text boxes in their states,
/// and a tooltip, a context menu, a list and an expander. No popup is opened: the card of the list is the one of the
/// template, laid out on its own. With <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set the pictures are saved, to review the look.
/// </summary>
public sealed class ControlStylesRenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Raised = Color.FromRgb(0x16, 0x22, 0x3D);
    private static readonly Color BorderStrong = Color.FromRgb(0x5F, 0x73, 0x96);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);

    /// <summary>
    /// The light chrome of Windows (Aero2) that these styles replace: the face of a combo box and of a button. A face left
    /// light is thousands of pixels; a few dozen are the antialiased edges of the letters.
    /// </summary>
    private static readonly Color[] LightChrome = [Color.FromRgb(0xF0, 0xF0, 0xF0), Color.FromRgb(0xE5, 0xE5, 0xE5), Color.FromRgb(0xDD, 0xDD, 0xDD)];

    /// <summary>An entry the mouse or the keyboard is on (the property can only be set by the control itself).</summary>
    private sealed class HighlightedComboBoxItem : ComboBoxItem
    {
        public HighlightedComboBoxItem()
        {
            IsHighlighted = true;
            SetResourceReference(StyleProperty, typeof(ComboBoxItem));
        }
    }

    private sealed class HighlightedMenuItem : MenuItem
    {
        public HighlightedMenuItem()
        {
            IsHighlighted = true;
            SetResourceReference(StyleProperty, typeof(MenuItem));
        }
    }

    private static TextBlock Caption(string text) => new() { Text = text, Margin = new Thickness(0, 12, 0, 6), Style = ThemeResources.Style("SecondaryText") };

    private static int LightPixels(System.Windows.Media.Imaging.BitmapSource bitmap) => LightChrome.Sum(colour => OffscreenRender.Count(bitmap, colour, tolerance: 4));

    /// <summary>
    /// A control that only lives in a popup (a tooltip, a context menu, the card of a list), laid out on its own and painted
    /// where a panel puts it: WPF refuses it any other parent, and opening its popup would show a window.
    /// </summary>
    private static Rectangle Painted(FrameworkElement popupContent, double maxWidth = 460)
    {
        popupContent.Measure(new Size(maxWidth, double.PositiveInfinity));
        popupContent.Arrange(new Rect(popupContent.DesiredSize));
        popupContent.UpdateLayout();
        return new Rectangle
        {
            Width = popupContent.ActualWidth,
            Height = popupContent.ActualHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new VisualBrush(popupContent) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
        };
    }

    [Fact]
    public void A_combo_box_renders_dark_closed_and_in_the_card_of_its_list()
    {
        var (field, card, selected, highlighted, light) = WpfHost.Run(() =>
        {
            var combo = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = 0 };
            combo.Items.Add(new ComboBoxItem { Content = "PowerShell 7" });
            combo.Items.Add(new HighlightedComboBoxItem { Content = "Símbolo del sistema" });
            combo.Items.Add(new ComboBoxItem { Content = "Windows PowerShell" });
            combo.Items.Add(new ComboBoxItem { Content = "Git Bash" });
            var disabled = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false, ItemsSource = new[] { "Sin shells disponibles" }, SelectedIndex = 0 };
            var panel = new StackPanel
            {
                Background = new SolidColorBrush(Surface),
                Children = { Caption("Desplegable cerrado"), combo, Caption("Desactivado"), disabled, Caption("Lista desplegada (la tarjeta de la plantilla, sin abrir el popup)") },
            };
            var root = new Border { Padding = new Thickness(16), Background = new SolidColorBrush(Surface), Child = panel };
            root.Measure(new Size(320, 420));
            root.Arrange(new Rect(0, 0, 320, 420));
            root.UpdateLayout();

            // The card of the list, laid out on its own and drawn under the closed combo box.
            panel.Children.Add(Painted((FrameworkElement)((Popup)combo.Template.FindName("PART_Popup", combo)).Child, 240));

            var bitmap = OffscreenRender.Render(root, 320, 420);
            OffscreenRender.SaveIfRequested(bitmap, "estilos-desplegable.png");
            return (OffscreenRender.Count(bitmap, Background), OffscreenRender.Count(bitmap, Raised), OffscreenRender.Count(bitmap, Accent), OffscreenRender.Count(bitmap, BorderStrong), LightPixels(bitmap));
        });

        Assert.True(field > 4_000, $"Only {field} pixels have the colour of the field: the closed combo box is not dark.");
        Assert.True(card > 4_000, $"Only {card} pixels have the colour of the card of the list.");
        Assert.True(selected > 1_500, $"Only {selected} pixels have the accent of the chosen entry.");
        Assert.True(highlighted > 2_500, $"Only {highlighted} pixels have the colour of the outlines and of the entry the keyboard is on.");
        Assert.True(light < 500, $"{light} pixels have the light face of Windows' own combo box.");
    }

    [Fact]
    public void Check_boxes_radio_buttons_buttons_and_text_boxes_render_dark_in_their_states()
    {
        var (accent, outline, light, lines) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var wrapped = new CheckBox { Style = ThemeResources.Style("FieldCheckBox"), IsChecked = true, Content = "Pedir Windows Hello en las aprobaciones críticas y al desbloquear la interfaz tras un tiempo sin actividad" };
            var panel = new StackPanel
            {
                Children =
                {
                    Caption("Casillas: sin marcar, marcada, indeterminada, desactivada y con una etiqueta larga"),
                    new CheckBox { Content = "Impedir la suspensión mientras hay trabajo", Margin = new Thickness(0, 2, 0, 2) },
                    new CheckBox { Content = "Leer el límite de uso de Codex", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) },
                    new CheckBox { Content = "Parte de las tareas", IsThreeState = true, IsChecked = null, Margin = new Thickness(0, 2, 0, 2) },
                    new CheckBox { Content = "Desactivada y marcada", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 2, 0, 2) },
                    wrapped,
                    Caption("Opciones: elegida, sin elegir y desactivada"),
                    new RadioButton { Content = "Seguir en la bandeja", IsChecked = true, GroupName = "render", Margin = new Thickness(0, 2, 0, 2) },
                    new RadioButton { Content = "Preguntar qué hacer", GroupName = "render", Margin = new Thickness(0, 2, 0, 2) },
                    new RadioButton { Content = "Salir de la aplicación", GroupName = "render", IsEnabled = false, Margin = new Thickness(0, 2, 0, 2) },
                    Caption("Botones sin estilo propio (terminal) y los de la app, que no cambian"),
                    new WrapPanel
                    {
                        Children =
                        {
                            new Button { Content = "Reiniciar", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 8) },
                            new Button { Content = "Detener", Padding = new Thickness(12, 4, 12, 4), IsEnabled = false, Margin = new Thickness(0, 0, 8, 8) },
                            new Button { Content = "Guardar", Style = ThemeResources.Style("PrimaryButton"), Margin = new Thickness(0, 0, 8, 8) },
                            new Button { Content = "Actualizar", Style = ThemeResources.Style("GhostButton"), Margin = new Thickness(0, 0, 8, 8) },
                        },
                    },
                    Caption("Campos de texto"),
                    new TextBox { Text = "pwsh -NoLogo", Margin = new Thickness(0, 0, 0, 6) },
                    new TextBox { Text = "Desactivado", IsEnabled = false },
                },
            };
            var root = new Border { Padding = new Thickness(16), Background = new SolidColorBrush(Surface), Child = panel };
            var bitmap = OffscreenRender.Render(root, 380, 620);
            OffscreenRender.SaveIfRequested(bitmap, "estilos-casillas-botones.png");
            var label = Descendants<TextBlock>(wrapped).Single(t => t.Text == (string)wrapped.Content);
            return (OffscreenRender.Count(bitmap, Accent), OffscreenRender.Count(bitmap, BorderStrong), LightPixels(bitmap), label.ActualHeight / (label.FontSize * 1.2));
        });

        Assert.True(accent > 1_500, $"Only {accent} pixels have the accent of the checked boxes, the chosen option and the primary button.");
        Assert.True(outline > 1_000, $"Only {outline} pixels have the colour of the outlines.");
        Assert.True(light < 500, $"{light} pixels have the light face of Windows' own controls.");
        Assert.True(lines >= 1.9, "The long label did not wrap.");
    }

    [Fact]
    public void A_tooltip_a_menu_a_list_and_an_expander_render_dark()
    {
        var (raised, highlighted, chosenRow, light) = WpfHost.Run(() =>
        {
            var menu = new ContextMenu
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Items =
                {
                    new MenuItem { Header = "Cor_tar", InputGestureText = "Ctrl+X" },
                    new HighlightedMenuItem { Header = "_Copiar", InputGestureText = "Ctrl+C" },
                    new MenuItem { Header = "_Pegar", InputGestureText = "Ctrl+V", IsEnabled = false },
                    new Separator(),
                    new MenuItem { Header = "Ajustar las líneas", IsCheckable = true, IsChecked = true },
                    new MenuItem { Header = "Mover a", Items = { new MenuItem { Header = "Panel izquierdo" } } },
                },
            };
            var tip = new ToolTip { Content = "Audita el commit HEAD del proyecto con la verificación del sistema y un juez independiente (Ctrl+Mayús+A)" };
            var list = new ListBox { Height = 96, SelectedIndex = 1, ItemsSource = new[] { "20261021-c4a1f2 · completada", "20261022-9b3e10 · en ejecución", "20261023-0d5a77 · pausada" } };
            var panel = new StackPanel
            {
                Children =
                {
                    Caption("Tooltip (texto largo: se ajusta a 420 px)"),
                    Painted(tip),
                    Caption("Menú contextual: atajos, entrada señalada, desactivada, marcada y submenú"),
                    Painted(menu),
                    Caption("Lista (la fila elegida, sin el foco)"),
                    list,
                    Caption("Expansor abierto"),
                    new Expander { Header = "Detalles de la ejecución", IsExpanded = true, Content = new TextBlock { Text = "4 tareas · 2 agentes · sin bloqueos", Style = ThemeResources.Style("BodyText") } },
                },
            };
            var root = new Border { Padding = new Thickness(16), Background = new SolidColorBrush(Surface), Child = panel };
            var bitmap = OffscreenRender.Render(root, 460, 640);
            OffscreenRender.SaveIfRequested(bitmap, "estilos-tooltip-menu-lista.png");
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var rowFill = ((Border)row.Template.FindName("Root", row)).Background;
            return (OffscreenRender.Count(bitmap, Raised), OffscreenRender.Count(bitmap, BorderStrong), (rowFill as SolidColorBrush)?.Color, LightPixels(bitmap));
        });

        Assert.True(raised > 15_000, $"Only {raised} pixels have the colour of the cards of the tooltip and of the menu.");
        Assert.True(highlighted > 3_000, $"Only {highlighted} pixels have the colour of the entry the keyboard is on, of the outlines and of the chosen row.");
        // Off screen the list never has the keyboard focus: its chosen row is the one without it.
        Assert.Equal(BorderStrong, chosenRow);
        Assert.True(light < 500, $"{light} pixels have the light face of Windows' own controls.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
