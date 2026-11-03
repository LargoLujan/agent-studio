using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The rows of the Explorer (V7 of the walkthrough): a file only opened on a double click on the text of its name, because
/// the rest of the row was nobody's. Now the row is the item across the whole width of the tree, at every depth. The view is
/// laid out and drawn off screen (<see cref="OffscreenRender"/>); the double click and Enter are routed events raised on it,
/// as WPF would raise them, never input sent to the screen.
/// </summary>
public sealed class ExplorerViewTests
{
    private const double Width = 320;
    private const double Height = 360;

    /// <summary>A project with a nested file: proj / src / deep / Inner.cs, all folders expanded.</summary>
    private static (ExplorerViewModel Explorer, string Inner) Project(TempDirectory temp)
    {
        var inner = temp.WriteFile("src/deep/Inner.cs");
        temp.WriteFile("readme.md");
        var explorer = new ExplorerViewModel();
        explorer.Load(temp.Path);
        var src = explorer.Roots.Single().Children.Single(c => c.Name == "src");
        src.IsExpanded = true;
        src.Children.Single(c => c.Name == "deep").IsExpanded = true;
        return (explorer, inner);
    }

    private static (ExplorerView View, TreeView Tree) Laid(ExplorerViewModel explorer, string? evidence = null)
    {
        var view = new ExplorerView { DataContext = explorer, Width = Width, Height = Height };
        var bitmap = OffscreenRender.Render(view, Width, Height);
        if (evidence is not null)
        {
            OffscreenRender.SaveIfRequested(bitmap, evidence);
        }

        return (view, (TreeView)view.FindName("Tree"));
    }

    private static TreeViewItem ItemOf(DependencyObject root, string name) =>
        Descendants<TreeViewItem>(root).Single(item => item.DataContext is ExplorerNodeViewModel { Name: var own } && own == name);

    private static Border RowOf(TreeViewItem item) => (Border)item.Template.FindName("Row", item);

    /// <summary>A point of the row at the right of what the tree shows of it: far from the name, inside the view.</summary>
    private static Point FarRightOf(Border row, TreeView tree) =>
        new(tree.ActualWidth - 24, row.TranslatePoint(new Point(0, row.ActualHeight / 2), tree).Y);

    [Fact]
    public void Every_row_spans_the_whole_tree_and_only_what_it_shows_is_indented()
    {
        using var temp = new TempDirectory();
        var (explorer, _) = Project(temp);

        var rows = WpfHost.Run(() =>
        {
            var (_, tree) = Laid(explorer);
            return new[] { Path.GetFileName(temp.Path), "src", "deep", "Inner.cs" }.Select(name =>
            {
                var item = ItemOf(tree, name);
                var row = RowOf(item);
                var header = (FrameworkElement)item.Template.FindName("PART_Header", item);
                return (Left: row.TranslatePoint(default, tree).X, row.ActualWidth, Text: header.TranslatePoint(default, tree).X, Tree: tree.ActualWidth);
            }).ToList();
        });

        // Every row starts at the edge of the tree (inside its 1 px frame), whatever its depth, and is as wide as the tree, or
        // as its widest row when a long name makes the tree scroll sideways.
        Assert.True(rows[0].Left <= 1.5, $"The first row starts {rows[0].Left} px into the tree.");
        Assert.All(rows, row => Assert.Equal(rows[0].Left, row.Left, 1));
        Assert.All(rows, row => Assert.Equal(rows[0].ActualWidth, row.ActualWidth, 1));
        Assert.All(rows, row => Assert.True(row.ActualWidth >= row.Tree - 12, $"A row is {row.ActualWidth} px wide in a tree of {row.Tree} px."));
        // One step of 16 px per folder above: the name moves, the row does not.
        Assert.Equal([16d, 32d, 48d], rows.Skip(1).Select(r => Math.Round(r.Text - rows[0].Text)).ToList());
    }

    [Fact]
    public void A_click_far_to_the_right_of_a_name_still_lands_on_its_item()
    {
        using var temp = new TempDirectory();
        var (explorer, inner) = Project(temp);

        var (hit, expected) = WpfHost.Run(() =>
        {
            var (_, tree) = Laid(explorer);
            var item = ItemOf(tree, "Inner.cs");
            var name = Descendants<TextBlock>((FrameworkElement)item.Template.FindName("PART_Header", item)).First(t => t.Text == "Inner.cs");
            var point = FarRightOf(RowOf(item), tree);
            Assert.True(point.X > name.TranslatePoint(new Point(name.DesiredSize.Width, 0), tree).X + 100, "The point is not far from the name.");
            var landed = VisualTreeHelper.HitTest(tree, point)?.VisualHit;
            return (Ancestor<TreeViewItem>(landed)?.DataContext, item.DataContext);
        });

        Assert.NotNull(hit);
        Assert.Same(expected, hit);
        Assert.Equal(inner, ((ExplorerNodeViewModel)hit).FullPath);
    }

    [Fact]
    public void The_selection_is_painted_on_the_whole_row()
    {
        using var temp = new TempDirectory();
        var (explorer, _) = Project(temp);

        var (fill, width, treeWidth, foreground) = WpfHost.Run(() =>
        {
            var (view, tree) = Laid(explorer);
            var item = ItemOf(tree, "Inner.cs");
            item.IsSelected = true;
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, Width, Height), "explorador-fila-completa.png");
            var row = RowOf(item);
            return (((SolidColorBrush)row.Background).Color, row.ActualWidth, tree.ActualWidth, ((SolidColorBrush)item.Foreground).Color);
        });

        // Off screen the tree never has the keyboard focus: the selection is the one without it, BorderStrong under OnAccent.
        Assert.Equal(Color.FromRgb(0x5F, 0x73, 0x96), fill);
        Assert.Equal(Colors.White, foreground);
        Assert.True(width >= treeWidth - 12, $"The selected row is {width} px wide in a tree of {treeWidth} px.");
    }

    [Fact]
    public void A_double_click_anywhere_on_a_row_opens_its_file_and_so_does_Enter_on_the_selected_one()
    {
        using var temp = new TempDirectory();
        var (explorer, inner) = Project(temp);
        var readme = temp.Combine("readme.md");
        var opened = new List<string>();
        explorer.FileActivated += (_, path) => opened.Add(path);

        WpfHost.Run(() =>
        {
            var (_, tree) = Laid(explorer);

            // Far to the right of the name, where nothing answered before.
            var item = ItemOf(tree, "Inner.cs");
            var landed = VisualTreeHelper.HitTest(tree, FarRightOf(RowOf(item), tree))!.VisualHit;
            var target = Ancestor<TreeViewItem>(landed)!;
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = landed });

            ItemOf(tree, "readme.md").IsSelected = true;
            RaiseKeyDown(tree, Key.Enter);
        });

        Assert.Equal([inner, readme], opened);
    }

    [Fact]
    public void A_line_that_only_says_something_takes_no_focus_nor_selection()
    {
        var focusable = WpfHost.Run(() =>
        {
            var tree = (TreeView)new ExplorerView().FindName("Tree");
            return tree.ItemContainerStyle.Triggers.OfType<DataTrigger>()
                .Where(trigger => trigger.Binding is System.Windows.Data.Binding { Path.Path: "IsMessage" } && trigger.Value?.ToString() == bool.TrueString)
                .SelectMany(trigger => trigger.Setters.OfType<Setter>())
                .Single(setter => setter.Property == UIElement.FocusableProperty).Value;
        });

        Assert.False((bool)focusable);
    }

    /// <summary>Raises the bubbling key-down event on a view that is never shown, from a window that is never shown either.</summary>
    private static void RaiseKeyDown(UIElement target, Key key)
    {
        // A popup tool window that cannot be activated, far off screen, created without WS_VISIBLE (as TestKeys does).
        using var source = new HwndSource(new HwndSourceParameters("ExplorerViewTests")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x00000080 | 0x08000000,
            PositionX = -20000,
            PositionY = -20000,
            Width = 1,
            Height = 1,
        });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static T? Ancestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node as T;
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
