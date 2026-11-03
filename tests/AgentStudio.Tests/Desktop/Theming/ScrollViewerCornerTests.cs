using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.Theming;

/// <summary>
/// V30, seen live in the tree of «Skills»: a light square where the two scroll bars meet. The theme makes it the surface
/// colour and changes nothing else of the scroll viewer: its template is still WPF's, and a scroll viewer with a style of its
/// own keeps its look. Drawn without ever being shown (<see cref="OffscreenRender"/>).
/// </summary>
public sealed class ScrollViewerCornerTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);

    [Fact]
    public void The_corner_between_the_two_scroll_bars_is_the_surface_and_a_scroll_viewer_with_its_own_style_keeps_its_look()
    {
        var (light, corner, keyedCorner, sameTemplate) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var themed = Viewer();
            var keyed = Viewer();
            keyed.Style = new Style(typeof(ScrollViewer));

            var bitmap = OffscreenRender.Render(Host(themed), 220, 220);
            OffscreenRender.SaveIfRequested(bitmap, "scrollviewer-rincon.png");
            OffscreenRender.Render(Host(keyed), 220, 220);
            return (
                OffscreenRender.Count(bitmap, SystemColors.ControlColor, tolerance: 4),
                CornerColour(themed),
                CornerColour(keyed),
                ReferenceEquals(themed.Template, keyed.Template));
        });

        Assert.Equal(0, light);
        Assert.Equal(Surface, corner);
        Assert.Equal(SystemColors.ControlColor, keyedCorner);
        Assert.True(sameTemplate, "The implicit style replaced the template of the scroll viewer.");
    }

    /// <summary>
    /// The documents (the tree of «Skills» among them) live under AvalonDock's <c>DockingManager</c>, whose theme is merged
    /// into its own resources, nearer than the app's: an implicit scroll viewer style there would hide the theme's one.
    /// </summary>
    [Fact]
    public void The_theme_of_the_dock_has_no_scroll_viewer_style_that_would_hide_the_one_of_the_app()
    {
        var types = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var manager = new AvalonDock.DockingManager { Theme = new AvalonDock.Themes.Vs2013DarkTheme() };
            var keys = new List<Type>();
            void Walk(ResourceDictionary dictionary)
            {
                foreach (var merged in dictionary.MergedDictionaries)
                {
                    Walk(merged);
                }

                keys.AddRange(dictionary.Keys.OfType<Type>());
            }

            Walk(manager.Resources);
            return keys;
        });

        // It styles its own controls only (DockingManager, LayoutDocumentControl…), so the documents take the app's.
        Assert.NotEmpty(types);
        Assert.DoesNotContain(typeof(ScrollViewer), types);
        Assert.DoesNotContain(typeof(System.Windows.Controls.Primitives.ScrollBar), types);
    }

    private static ScrollViewer Viewer() => new()
    {
        Width = 200,
        Height = 200,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
        VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        Content = new Border { Width = 400, Height = 400, Background = new SolidColorBrush(Background) },
    };

    private static Border Host(ScrollViewer viewer) => new() { Background = new SolidColorBrush(Background), Padding = new Thickness(10), Child = viewer };

    private static Color CornerColour(ScrollViewer viewer) =>
        viewer.Template.FindName("Corner", viewer) is Rectangle { Fill: SolidColorBrush brush } ? brush.Color : Colors.Transparent;
}
