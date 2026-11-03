using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace AgentStudio.Tests.Desktop.Contrast;

/// <summary>
/// Keyboard focus (WCAG 2.4.7 visible, 1.4.11 at least 3:1 against what is next to it). WPF's own focus visual is a
/// dotted ring in SystemColors.ControlText (black), which vanishes on a dark surface; Themes/Dark.xaml replaces it
/// application-wide and in every custom style with a 2 px ring in the lightest text colour.
/// </summary>
public sealed class FocusVisualTests
{
    /// <summary>Styles of focusable controls defined in Dark.xaml, and which ring each one must use.</summary>
    private static readonly (string Style, bool Inset)[] FocusableStyles =
    [
        ("PrimaryButton", false),
        ("GhostButton", false),
        ("ActivityButton", true),
        ("MonoViewer", true),
    ];

    private static Style Outset => (Style)ThemeResources.Resources[SystemParameters.FocusVisualStyleKey];

    private static Style Inset => ThemeResources.Style("FocusVisualInset");

    [Fact]
    public void The_focus_ring_meets_WCAG_AA_against_every_colour_it_is_drawn_next_to()
    {
        var table = WpfHost.Run(() =>
        {
            var t = new ContrastTable("foco");
            Rgba Token(string key) => ThemeResources.Color(key);

            var outset = Ring(Outset);
            var outsetColour = ThemeResources.ToRgba(outset.Stroke, "Outset ring stroke");
            // Outside the control: the panel behind it. Inside, touching the ring: the control's own edge.
            foreach (var (name, id) in new[] { ("BackgroundColor", "bg"), ("SurfaceColor", "surface"), ("SurfaceRaisedColor", "raised") })
            {
                t.Add($"ring.outset.{id}", $"Anillo de foco exterior (botones, controles sin estilo propio) contra {id}", ContrastKind.FocusIndicator, "FocusRing", outsetColour, name, Token(name));
            }

            t.Add("ring.outset.accent", "Anillo exterior contra el relleno del botón primario", ContrastKind.FocusIndicator, "FocusRing", outsetColour, "AccentColor", Token("AccentColor"));
            t.Add("ring.outset.accent-hover", "Anillo exterior contra el relleno del botón primario bajo el ratón", ContrastKind.FocusIndicator, "FocusRing", outsetColour, "AccentHoverColor", Token("AccentHoverColor"));
            t.Add("ring.outset.border-strong", "Anillo exterior contra el contorno del botón secundario", ContrastKind.FocusIndicator, "FocusRing", outsetColour, "BorderStrongColor", Token("BorderStrongColor"));

            var inset = Ring(Inset);
            var insetColour = ThemeResources.ToRgba(inset.Stroke, "Inset ring stroke");
            foreach (var (name, id) in new[] { ("BackgroundColor", "bg"), ("SurfaceColor", "surface"), ("SurfaceRaisedColor", "raised") })
            {
                t.Add($"ring.inset.{id}", $"Anillo de foco interior (visores, barra de actividad) contra {id}", ContrastKind.FocusIndicator, "FocusRing", insetColour, name, Token(name));
            }

            return t;
        });

        table.AssertAllPass();
    }

    [Fact]
    public void The_focus_ring_is_at_least_two_pixels_thick()
    {
        var (outset, inset) = WpfHost.Run(() => (Ring(Outset).StrokeThickness, Ring(Inset).StrokeThickness));

        Assert.True(outset >= 2, $"Outset ring is {outset}px.");
        Assert.True(inset >= 2, $"Inset ring is {inset}px.");
    }

    [Fact]
    public void Every_focusable_style_of_the_theme_sets_the_themed_ring()
    {
        var wrong = WpfHost.Run(() =>
        {
            var problems = new List<string>();
            foreach (var (name, inset) in FocusableStyles)
            {
                var style = ThemeResources.Style(name);
                var setter = style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == FrameworkElement.FocusVisualStyleProperty);
                var expected = inset ? Inset : Outset;
                if (setter is null)
                {
                    problems.Add($"{name} does not set FocusVisualStyle (WPF would draw its dotted black default).");
                }
                else if (!ReferenceEquals(setter.Value, expected))
                {
                    problems.Add($"{name} sets a FocusVisualStyle that is not the themed {(inset ? "inset" : "outset")} ring.");
                }
            }

            return problems;
        });

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Controls_without_a_style_of_their_own_get_the_themed_ring_too()
    {
        // The terminal toolbar uses plain Button and ComboBox. WPF's theme styles for them draw their own dotted focus
        // visual (not SystemParameters.FocusVisualStyleKey), so Dark.xaml sets the ring on each type implicitly.
        var wrong = WpfHost.Run(() =>
        {
            var problems = new List<string>();
            foreach (var type in new[] { typeof(Button), typeof(ToggleButton), typeof(CheckBox), typeof(RadioButton), typeof(ComboBox) })
            {
                var style = ThemeResources.Resources[type] as Style;
                var setter = style?.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == FrameworkElement.FocusVisualStyleProperty);
                if (!ReferenceEquals(setter?.Value, Outset))
                {
                    problems.Add($"{type.Name} has no implicit style that sets the themed focus ring.");
                }
            }

            // And it really reaches an instance, with WPF's own theme style underneath.
            foreach (var control in new Control[] { new Button(), new ComboBox(), new CheckBox() })
            {
                control.BeginInit();
                control.EndInit();
                control.ApplyTemplate();
                if (!ReferenceEquals(control.FocusVisualStyle, Outset))
                {
                    problems.Add($"{control.GetType().Name} draws {(control.FocusVisualStyle is null ? "no focus visual" : "WPF's default focus visual")} instead of the themed ring.");
                }
            }

            return problems;
        });

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void The_implicit_styles_of_WPF_controls_draw_a_themed_focus_cue()
    {
        var wrong = WpfHost.Run(() =>
        {
            var problems = new List<string>();
            void Ring(object key, Style expected, string which)
            {
                var setter = ThemeResources.Style(key).Setters.OfType<Setter>().FirstOrDefault(s => s.Property == FrameworkElement.FocusVisualStyleProperty);
                if (!ReferenceEquals(setter?.Value, expected))
                {
                    problems.Add($"{key} does not draw the themed {which} ring.");
                }
            }

            // Controls that take the focus as a whole: the ring around them.
            foreach (var type in new[] { typeof(Button), typeof(CheckBox), typeof(RadioButton), typeof(ComboBox) })
            {
                Ring(type, Outset, "outset");
            }

            // Lists, their rows and the header of an expander fill their panel: the ring inside them.
            foreach (var key in new object[] { typeof(ListBox), typeof(ListBoxItem), "Expander.HeaderToggle" })
            {
                Ring(key, Inset, "inset");
            }

            // A text box shows the focus with an accent frame drawn over its outline, besides its caret.
            var textBox = ThemeResources.Template(ThemeResources.Style(typeof(TextBox)));
            if (!textBox.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsKeyboardFocusedProperty && Equals(t.Value, true)
                && t.Setters.OfType<Setter>().Any(s => s.TargetName == "FocusFrame" && Equals(s.Value, Visibility.Visible))))
            {
                problems.Add("TextBox does not show its focus frame.");
            }

            // In a menu or an open list the keyboard moves the highlight, which fills the whole entry.
            foreach (var (type, highlighted) in new[] { (typeof(MenuItem), MenuItem.IsHighlightedProperty), (typeof(ComboBoxItem), ComboBoxItem.IsHighlightedProperty) })
            {
                var template = ThemeResources.Template(ThemeResources.Style(type));
                if (!template.Triggers.OfType<Trigger>().Any(t => t.Property == highlighted && Equals(t.Value, true)
                    && t.Setters.OfType<Setter>().Any(s => s.TargetName == "Root" && s.Property == Border.BackgroundProperty)))
                {
                    problems.Add($"{type.Name} does not fill the entry the keyboard is on.");
                }
            }

            return problems;
        });

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Text_viewers_show_their_caret_as_a_second_keyboard_cue()
    {
        var (readOnlyCaret, isReadOnly) = WpfHost.Run(() =>
        {
            var viewer = new TextBox { Style = ThemeResources.Style("MonoViewer") };
            return (viewer.IsReadOnlyCaretVisible, viewer.IsReadOnly);
        });

        Assert.True(isReadOnly);
        Assert.True(readOnlyCaret, "A read-only viewer must still show a caret, or keyboard users cannot see where they are.");
    }

    /// <summary>The rectangle of a focus visual style, instantiated from its control template.</summary>
    private static Rectangle Ring(Style focusVisual)
    {
        var template = (ControlTemplate)focusVisual.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
        return (Rectangle)template.LoadContent();
    }
}
