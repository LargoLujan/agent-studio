using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Theming;
using AgentStudio.Desktop.Views.Lock;
using AgentStudio.Tests.Desktop.Contrast;
using AgentStudio.Tests.Desktop.Orchestrator;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Themes;

namespace AgentStudio.Tests.Desktop.Theming;

/// <summary>
/// What the implicit styles of Themes/Dark.xaml change and what they leave alone: the keyed styles keep their own look,
/// AvalonDock keeps its own buttons, rows and menu entries, its plain tab menus and the menu of every text box take the
/// theme, and long labels wrap instead of being cut. Everything is built off screen; no window is shown.
/// </summary>
public sealed class ImplicitStylesTests
{
    [Fact]
    public void The_keyed_button_styles_keep_their_own_template()
    {
        var wrong = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var plain = ThemeResources.Template(ThemeResources.Style(typeof(Button)));
            var problems = new List<string>();
            foreach (var name in new[] { "PrimaryButton", "GhostButton", "ActivityButton", "ToolbarButton", "ToolbarPrimaryButton" })
            {
                var style = ThemeResources.Style(name);
                var button = new Button { Style = style };
                button.ApplyTemplate();
                if (ReferenceEquals(button.Template, plain) || !ReferenceEquals(button.Template, KeyedTemplate(style)))
                {
                    problems.Add($"{name} does not draw with its own template.");
                }
            }

            return problems;
        });

        Assert.Empty(wrong);
    }

    [Fact]
    public void The_check_boxes_and_radio_buttons_of_the_views_are_based_on_the_theme_ones()
    {
        var (field, implicitBox) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            return (ThemeResources.Style("FieldCheckBox").BasedOn, ThemeResources.Style(typeof(CheckBox)));
        });

        Assert.Same(implicitBox, field);
    }

    [Fact]
    public void A_long_label_of_a_check_box_wraps_instead_of_being_cut()
    {
        // The live walkthrough showed «Pedir Windows Hello en las aprobaciones crítica…» cut in «Ajustes».
        var (wraps, lines, fits) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new LockSettingsView { DataContext = new LockSettingsViewModel(new InMemorySettingsStore()) };
            view.Measure(new Size(260, 900));
            view.Arrange(new Rect(0, 0, 260, 900));
            view.UpdateLayout();
            // «Ajustes» gives this check box a wrapping TextBlock of its own (and its name by hand): either way it must wrap.
            var text = HelloTexts.SettingLabel;
            var box = Descendants<CheckBox>(view).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == text || Equals(c.Content, text));
            var label = Descendants<TextBlock>(box).Single(t => t.Text == text);
            var oneLine = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize };
            oneLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var right = label.TranslatePoint(new Point(label.ActualWidth, 0), box).X;
            return (label.TextWrapping == TextWrapping.Wrap, label.ActualHeight / oneLine.DesiredSize.Height, right <= box.ActualWidth + 0.5);
        });

        Assert.True(wraps);
        Assert.True(lines >= 1.9, $"The label takes {lines:0.0} lines in 260 px: it should take two.");
        Assert.True(fits, "The label runs past the check box.");
    }

    [Fact]
    public void A_long_tooltip_wraps_at_its_maximum_width()
    {
        var (width, height, oneLine) = WpfHost.Run(() =>
        {
            const string text = "Audita el commit HEAD del proyecto con la verificación del sistema y un juez independiente, sin tocar el árbol de trabajo (Ctrl+Mayús+A)";
            var tip = new ToolTip { Content = text };
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tip.Arrange(new Rect(tip.DesiredSize));
            var single = new TextBlock { Text = text, FontFamily = tip.FontFamily, FontSize = tip.FontSize };
            single.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return (tip.ActualWidth, tip.ActualHeight, single.DesiredSize.Height);
        });

        Assert.True(width <= 420, $"The tooltip is {width} px wide.");
        Assert.True(height > 2 * oneLine, "The text of the tooltip did not wrap.");
    }

    [Fact]
    public void AvalonDock_keeps_its_own_buttons_rows_and_menu_entries()
    {
        var problems = WpfHost.Run(() =>
        {
            var found = new List<string>();
            var manager = new DockingManager { Theme = new Vs2013DarkTheme() };
            foreach (var (key, root) in TemplatesOf(manager.Resources))
            {
                foreach (var control in Descendants<Control>(root))
                {
                    // A plain button with no style of its own would take the implicit style of the app (an implicit style
                    // is looked up by the exact type, so AvalonDock's own button types never do).
                    if ((control.GetType() == typeof(Button) || control.GetType() == typeof(ToggleButton))
                        && control.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
                    {
                        found.Add($"{key}: {control.GetType().Name} '{control.Name}' has no style of its own.");
                    }

                    // The lists of the window switcher take the implicit ListBox frame, but their rows keep the theme's.
                    if (control.GetType() == typeof(ListBox) && control.ReadLocalValue(ItemsControl.ItemContainerStyleProperty) == DependencyProperty.UnsetValue)
                    {
                        found.Add($"{key}: ListBox '{control.Name}' has no row style of its own.");
                    }
                }
            }

            // The entries of the document list keep the template of the theme, not the app's menu entry.
            var appEntry = ThemeResources.Resources["MenuItem.Template"];
            var entry = (Style)manager.Resources[typeof(MenuItemEx)];
            if (ReferenceEquals(FirstTemplate(entry), appEntry) || FirstTemplate(entry) is null)
            {
                found.Add("MenuItemEx does not keep the template of the VS2013 theme.");
            }

            return found;
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void The_tab_menus_of_AvalonDock_take_the_dark_menu_of_the_theme()
    {
        var (menu, entries) = WpfHost.Run(() =>
        {
            var manager = new DockingManager { Theme = new Vs2013DarkTheme() };
            var tabs = manager.DocumentContextMenu;
            return (
                ReferenceEquals(tabs?.Style, ThemeResources.Style(typeof(ContextMenu))),
                tabs?.Items.OfType<MenuItem>().All(item => ReferenceEquals(item.Style, ThemeResources.Style(typeof(MenuItem)))));
        });

        Assert.True(menu, "The context menu of the tabs is not the theme's.");
        Assert.True(entries, "The entries of the context menu of the tabs are not the theme's.");
    }

    [Fact]
    public void The_internal_types_of_the_menu_of_a_text_box_are_where_the_theme_expects_them()
    {
        // A WPF that renamed them fails here, instead of leaving the menu of the text boxes light.
        foreach (var (typeName, baseType) in TextBoxMenuTheme.InternalTypes)
        {
            var type = typeof(TextBoxBase).Assembly.GetType(typeName);
            Assert.NotNull(type);
            Assert.True(baseType.IsAssignableFrom(type), $"{typeName} no longer derives from {baseType.Name}.");
        }
    }

    [Fact]
    public void The_menu_of_a_text_box_and_its_entries_take_the_theme_styles()
    {
        var (applied, menu, entry) = WpfHost.Run(() =>
        {
            var resources = ThemeResources.Resources;
            var count = TextBoxMenuTheme.Apply(resources);
            var assembly = typeof(TextBoxBase).Assembly;
            var editorMenu = (ContextMenu)Activator.CreateInstance(assembly.GetType(TextBoxMenuTheme.InternalTypes[0].TypeName)!, nonPublic: true)!;
            var editorEntry = (MenuItem)Activator.CreateInstance(assembly.GetType(TextBoxMenuTheme.InternalTypes[1].TypeName)!, nonPublic: true)!;

            // Initialized, as when WPF puts them in the popup of the menu: that is when an element fetches its implicit style.
            foreach (var element in new FrameworkElement[] { editorMenu, editorEntry })
            {
                element.BeginInit();
                element.EndInit();
            }

            return (count, ReferenceEquals(editorMenu.Style, resources[typeof(ContextMenu)]), ReferenceEquals(editorEntry.Style, resources[typeof(MenuItem)]));
        });

        Assert.Equal(2, applied);
        Assert.True(menu, "The menu of a text box is not the theme's.");
        Assert.True(entry, "The entries of the menu of a text box are not the theme's.");
    }

    private static ControlTemplate? KeyedTemplate(Style? style)
    {
        for (var current = style; current is not null; current = current.BasedOn)
        {
            if (current.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value is ControlTemplate template)
            {
                return template;
            }
        }

        return null;
    }

    private static object? FirstTemplate(Style style) => KeyedTemplate(style);

    /// <summary>Every template of every style (setters and triggers) and every bare template in a dictionary, loaded.</summary>
    private static IEnumerable<(object Key, DependencyObject Root)> TemplatesOf(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var found in TemplatesOf(merged))
            {
                yield return found;
            }
        }

        foreach (var key in dictionary.Keys)
        {
            var templates = dictionary[key] switch
            {
                Style style => style.Setters.OfType<Setter>().Select(s => s.Value)
                    .Concat(style.Triggers.OfType<Trigger>().SelectMany(t => t.Setters.OfType<Setter>()).Select(s => s.Value))
                    .OfType<FrameworkTemplate>(),
                FrameworkTemplate template => [template],
                _ => [],
            };

            foreach (var template in templates)
            {
                if (template.LoadContent() is { } root)
                {
                    yield return (key, root);
                }
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>([root]);
        var seen = new HashSet<DependencyObject>();
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            if (node is T match)
            {
                yield return match;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                pending.Push(child);
            }

            if (node is Visual)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    pending.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
        }
    }
}
