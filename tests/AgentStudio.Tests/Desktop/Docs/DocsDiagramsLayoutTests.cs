using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AgentStudio.Application.Diagrams;
using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Views.Docs;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory.Diagrams;

namespace AgentStudio.Tests.Desktop.Docs;

/// <summary>
/// P69: the list of diagrams and the text of the diagram take the height there is, drawn offscreen. With a low window and a card of conflicts with several rows the text stays
/// inside what the screen scrolls (nothing is cut); with a tall one it grows past the old 360; and a region that is not there leaves no gap.
/// </summary>
public sealed class DocsDiagramsLayoutTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static T Named<T>(FrameworkElement root, string name)
        where T : class => (root.FindName(name) as T) ?? throw new InvalidOperationException($"'{name}' is not in the view.");

    private static async Task<DocsDiagramsSection> OpenAsync(DiagramFakes.World world)
    {
        var section = new DocsDiagramsSection(new DocsBackend(world.Catalog, world.Catalog, world.Analysis, world.History, null), Spanish);
        await new GraphDocumentViewModel([section]).SetProjectAsync(world.Project, CancellationToken.None);
        await section.GenerateCommand.ExecuteAsync(null);
        await section.SourceShown;
        return section;
    }

    [Fact]
    public async Task With_a_low_window_and_several_conflicts_the_text_of_the_diagram_stays_inside_what_scrolls()
    {
        var (viewport, extent, layoutHeight, boxBottom, boxHeight, cardHeight) = await WpfHost.RunAsync(async () =>
        {
            using var world = new DiagramFakes.World();
            var section = await OpenAsync(world);
            section.SourceText = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"line {i}"));
            for (var i = 1; i <= 6; i++)
            {
                section.Conflicts.Add(new ExportConflictItem(new DiagramExportConflict(new DiagramRef(DiagramKind.Solution, string.Empty), $"docs/generado/diagramas/solucion-{i}.md", DiagramExportState.ChangedOutside), Spanish));
            }

            var view = new DocsDiagramsView { DataContext = section };
            OffscreenRender.Render(view, 900, 320);
            var scroller = Named<ScrollViewer>(view, "Scroller");
            var layout = Named<Grid>(view, "Layout");
            var box = Named<TextBox>(view, "SourceBox");
            return (scroller.ViewportHeight, scroller.ExtentHeight, layout.ActualHeight, box.TranslatePoint(new Point(0, box.ActualHeight), layout).Y, box.ActualHeight, Named<Border>(view, "ConflictsCard").ActualHeight);
        });

        Assert.True(extent > viewport, $"The screen does not scroll: extent {extent}, viewport {viewport}.");
        Assert.True(layoutHeight >= extent - 1, $"The grid ({layoutHeight}) is shorter than what the screen scrolls ({extent}).");
        Assert.True(boxBottom <= layoutHeight + 0.5, $"The text ends at {boxBottom}, outside the grid ({layoutHeight}).");
        Assert.True(boxHeight >= 140, $"The text is only {boxHeight} high.");
        Assert.True(cardHeight > 60, $"The card of conflicts is only {cardHeight} high.");
    }

    [Fact]
    public async Task With_a_tall_window_the_list_and_the_text_take_the_height_there_is_with_no_fixed_maximum()
    {
        var (viewport, layoutHeight, boxHeight, listHeight) = await WpfHost.RunAsync(async () =>
        {
            using var world = new DiagramFakes.World();
            var section = await OpenAsync(world);
            section.SourceText = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"line {i}"));
            var view = new DocsDiagramsView { DataContext = section };
            OffscreenRender.Render(view, 900, 1400);
            return (Named<ScrollViewer>(view, "Scroller").ViewportHeight, Named<Grid>(view, "Layout").ActualHeight, Named<TextBox>(view, "SourceBox").ActualHeight, Named<ListBox>(view, "DiagramList").ActualHeight);
        });

        Assert.Equal(viewport, layoutHeight, 0.5);
        Assert.True(boxHeight > 360, $"The text is {boxHeight} high: it still has a maximum.");
        Assert.True(listHeight > 150, $"The list is {listHeight} high.");
    }

    [Fact]
    public async Task Without_a_text_of_the_diagram_its_row_leaves_no_gap_and_without_the_catalog_neither_does_the_list()
    {
        var (sourceRow, listRow, noListRow, noListVisible) = await WpfHost.RunAsync(async () =>
        {
            using var world = new DiagramFakes.World();
            var section = new DocsDiagramsSection(new DocsBackend(world.Catalog, world.Catalog, world.Analysis, world.History, null), Spanish);
            await new GraphDocumentViewModel([section]).SetProjectAsync(world.Project, CancellationToken.None);
            var view = new DocsDiagramsView { DataContext = section };
            OffscreenRender.Render(view, 900, 900);
            var layout = Named<Grid>(view, "Layout");

            var unavailable = new DocsDiagramsSection(DocsBackend.None, Spanish);
            await new GraphDocumentViewModel([unavailable]).SetProjectAsync(world.Project, CancellationToken.None);
            var bare = new DocsDiagramsView { DataContext = unavailable };
            OffscreenRender.Render(bare, 900, 900);
            var bareLayout = Named<Grid>(bare, "Layout");
            return (layout.RowDefinitions[5].ActualHeight, layout.RowDefinitions[1].ActualHeight, bareLayout.RowDefinitions[1].ActualHeight, Named<ListBox>(bare, "DiagramList").Visibility);
        });

        Assert.Equal(0d, sourceRow, 0.5);
        Assert.True(listRow > 100, $"The list has {listRow}: it did not take the height that the text leaves.");
        Assert.Equal(Visibility.Collapsed, noListVisible);
        Assert.Equal(0d, noListRow, 0.5);
    }
}
