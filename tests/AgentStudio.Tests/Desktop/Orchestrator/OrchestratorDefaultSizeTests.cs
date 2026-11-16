using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Orchestration;
using AvalonDock;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The «Orquestador» tab at the default size of the window (docs/24 §1, rows 14 to 16): 1280 × 800 outside, so a client area of
/// about 1264 × 760, with the activity bar and the explorer of the default layout beside it. Two projects: a fresh one (the band
/// «Preparación» open, with what is missing listed) and a complete one with a run on screen. The size of the tab is read from the
/// real window; the divider is then moved, with the mouse and with the keyboard, on the tab alone at that size (a window that is
/// never shown cannot be laid out twice the same way). Everything is read from the layout, never from pixels, and nothing is shown.
/// </summary>
public sealed class OrchestratorDefaultSizeTests : IDisposable
{
    /// <summary>The client area of the window at its default size (MainWindow.xaml: 1280 × 800 with the title bar and the borders).</summary>
    private const double ClientWidth = 1264;
    private const double ClientHeight = 760;

    /// <summary>What each row keeps beyond its least height, so the divider moves both ways by a useful step.</summary>
    private const double Travel = 24;

    /// <summary>The least height the editor of the canvas and the lists of the monitor keep: two rows of a list at least.</summary>
    private const double Usable = 48;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    private sealed record Tab(
        double Width,
        double Height,
        double Bands,
        double BandsMax,
        double CanvasRow,
        double CanvasMin,
        double MonitorRow,
        double MonitorMin,
        double Editor,
        double Lists,
        bool ApproveInView,
        bool ExecuteInView,
        double ExecuteBottom)
    {
        public override string ToString() =>
            $"tab {Width:F0} × {Height:F0}, bands {Bands:F0} (at most {BandsMax:F0}), canvas {CanvasRow:F0} (least {CanvasMin:F0}), monitor {MonitorRow:F0} (least {MonitorMin:F0}), editor {Editor:F0}, lists {Lists:F0}, «Ejecutar» ends at {ExecuteBottom:F0}";
    }

    private sealed record Moved(double Before, double AfterDown, double AfterUp, double AfterKeyDown, double AfterKeyUp, bool KeyHandled, bool Focusable, double Thickness, string? Name, Cursor? Cursor);

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

    /// <summary>Whether <paramref name="element"/> lies whole inside the viewport of <paramref name="scroller"/>.</summary>
    private static bool InView(FrameworkElement element, ScrollViewer scroller)
    {
        var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return element.Visibility == Visibility.Visible && element.ActualHeight > 0
            && bounds.Top >= -0.5 && bounds.Bottom <= scroller.ViewportHeight + 0.5;
    }

    private static Button Labelled(DependencyObject root, string key) =>
        Descendants<Button>(root).First(b => Equals(b.Content, Texts.Get(key)));

    private static Tab Read(OrchestratorView view)
    {
        var layout = (Grid)view.FindName("Layout");
        var bands = (ScrollViewer)view.FindName("Bands");
        var canvas = Descendants<CanvasView>(view).Single();
        var monitor = Descendants<RunMonitorView>(view).Single();
        return new Tab(
            view.ActualWidth,
            view.ActualHeight,
            bands.ActualHeight,
            bands.MaxHeight,
            layout.RowDefinitions[1].ActualHeight,
            layout.RowDefinitions[1].MinHeight,
            layout.RowDefinitions[3].ActualHeight,
            layout.RowDefinitions[3].MinHeight,
            ((RowDefinition)canvas.FindName("EditorRow")).ActualHeight,
            ((FrameworkElement)monitor.FindName("Lists")).ActualHeight,
            InView(Labelled(bands, "Orchestrator_Review_Approve"), bands),
            InView(Labelled(bands, "Orchestrator_Review_Execute"), bands),
            Labelled(bands, "Orchestrator_Review_Execute") is var execute
                ? execute.TransformToAncestor(bands).Transform(new Point(0, execute.ActualHeight)).Y
                : 0);
    }

    /// <summary>Opens the orchestrator of a project in the real window at its default size and reads its tab.</summary>
    private async Task<(Tab Tab, OrchestratorDocumentViewModel Document)> OpenInTheWindowAsync(OrchestrationBackend backend, bool withRun, string picture)
    {
        var root = _temp.CreateDirectory(withRun ? "listo" : "nuevo");
        return await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var vm = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), TimeProvider.System),
                new ExplorerViewModel(),
                new JournalViewModel(TimeProvider.System),
                new QuickOpenViewModel(),
                orchestrators: new OrchestratorDocumentFactory(new OrchestrationSession(backend), new MemoryLayoutStore(), new FakeTimeProvider(T0)));
            var window = new MainWindow(vm, new FileLayoutStore(new AppDataPaths(_temp.Combine("data"))));
            await vm.OpenProjectAsync(root);
            await vm.OpenOrchestratorCommand.ExecuteAsync(null);
            var document = Assert.IsType<OrchestratorDocumentViewModel>(vm.ActiveContent);
            if (withRun)
            {
                var run = new FakeRun(new RunSnapshot(new RunId("20261116-a1b2c3"), RunPhase.Running, [], FakeRun.Healthy, 0));
                document.Monitor.Attach(run, document.Context!.Project!);
            }

            var dock = (DockingManager)window.FindName("DockManager");
            dock.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, dock));
            OffscreenRender.SaveIfRequested(OffscreenRender.Render((FrameworkElement)window.Content, ClientWidth, ClientHeight), picture);
            return (Read(Descendants<OrchestratorView>(dock).Single()), document);
        });
    }

    /// <summary>
    /// The tab alone at <paramref name="tab"/>'s size: the divider dragged 60 down and 120 up, then moved with the arrows. The tab
    /// sits in a host of that fixed size, as in its pane: a root without one is laid out again with no limit after a change inside.
    /// </summary>
    private static Moved MoveTheDivider(OrchestratorDocumentViewModel document, Tab tab) => WpfHost.Run(() =>
    {
        var view = new OrchestratorView { DataContext = document };
        var host = new Border { Width = tab.Width, Height = tab.Height, Child = view };
        OffscreenRender.Render(host, tab.Width, tab.Height);
        var splitter = (GridSplitter)view.FindName("RowSplitter");
        var canvasRow = ((Grid)view.FindName("Layout")).RowDefinitions[1];
        var before = canvasRow.ActualHeight;

        Drag(splitter, 60);
        OffscreenRender.Render(host, tab.Width, tab.Height);
        var afterDown = canvasRow.ActualHeight;
        Drag(splitter, -120);
        OffscreenRender.Render(host, tab.Width, tab.Height);
        var afterUp = canvasRow.ActualHeight;

        var handled = TestKeys.KeyDown(splitter, Key.Down);
        OffscreenRender.Render(host, tab.Width, tab.Height);
        var afterKeyDown = canvasRow.ActualHeight;
        handled &= TestKeys.KeyDown(splitter, Key.Up);
        OffscreenRender.Render(host, tab.Width, tab.Height);
        var afterKeyUp = canvasRow.ActualHeight;

        return new Moved(
            before, afterDown, afterUp, afterKeyDown, afterKeyUp, handled, splitter.Focusable && splitter.IsTabStop, splitter.ActualHeight,
            System.Windows.Automation.AutomationProperties.GetName(splitter), splitter.Cursor);
    });

    /// <summary>A drag of the mouse as the thumb of the divider reports it: no window and no pointer needed.</summary>
    private static void Drag(GridSplitter splitter, double by)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
        splitter.RaiseEvent(new DragDeltaEventArgs(0, by));
        splitter.RaiseEvent(new DragCompletedEventArgs(0, by, false));
    }

    private static void AssertRoom(Tab tab)
    {
        Assert.True(tab.Height >= 560, $"The tab is only {tab.Height:F0} high: {tab}.");
        Assert.True(tab.Bands <= tab.BandsMax + 0.5, $"The bands pass their limit: {tab}.");
        Assert.True(tab.Bands <= tab.Height * 0.5 + 0.5, $"The bands take more than half of the tab: {tab}.");
        Assert.True(tab.CanvasRow >= tab.CanvasMin + Travel, $"The canvas is at its least height: the divider cannot go up. {tab}.");
        Assert.True(tab.MonitorRow >= tab.MonitorMin + Travel, $"The monitor is at its least height: the divider cannot go down. {tab}.");
        Assert.True(tab.Editor >= Usable, $"The canvas leaves its editor {tab.Editor:F0}: {tab}.");
        Assert.True(tab.Lists >= Usable, $"The monitor leaves its lists {tab.Lists:F0}: {tab}.");
    }

    private static void AssertMoves(Moved moved, Tab tab)
    {
        var report = $"{tab}; canvas {moved.Before:F0}, dragged down {moved.AfterDown:F0}, dragged up {moved.AfterUp:F0}, arrow down {moved.AfterKeyDown:F0}, arrow up {moved.AfterKeyUp:F0}";
        Assert.True(moved.AfterDown > moved.Before + Travel - 0.5, $"Dragged down, the canvas did not grow: {report}.");
        Assert.True(moved.AfterUp < moved.AfterDown - Travel, $"Dragged up, the canvas did not shrink: {report}.");
        Assert.True(moved.KeyHandled, "The arrows do not move the divider.");
        Assert.True(moved.AfterKeyDown > moved.AfterUp + 0.5, $"The arrow down did not grow the canvas: {report}.");
        Assert.True(moved.AfterKeyUp < moved.AfterKeyDown - 0.5, $"The arrow up did not shrink the canvas: {report}.");
        Assert.True(moved.Focusable, "The divider is not reached with the keyboard.");
        Assert.InRange(moved.Thickness, 6, 8);
        Assert.Equal(Texts.Get("Orchestrator_Review_SplitterName"), moved.Name);
        Assert.Same(Cursors.SizeNS, moved.Cursor);
    }

    [Fact]
    public async Task In_a_fresh_project_the_divider_moves_both_ways_at_the_default_size()
    {
        var fresh = Backends.Complete(new FakeOrchestrator()) with { Requirements = new FakeRequirementStore(), Verification = null };

        var (tab, document) = await OpenInTheWindowAsync(fresh, withRun: false, "ola1-orquestador-nuevo.png");
        var moved = MoveTheDivider(document, tab);

        Assert.True(document.IsPreparationExpanded);
        AssertRoom(tab);
        AssertMoves(moved, tab);
    }

    [Fact]
    public async Task In_a_complete_project_with_a_run_the_divider_moves_both_ways_and_approve_and_run_are_in_view()
    {
        var (tab, document) = await OpenInTheWindowAsync(Backends.Complete(new FakeOrchestrator()), withRun: true, "ola1-orquestador-con-ejecucion.png");
        var moved = MoveTheDivider(document, tab);

        Assert.False(document.IsPreparationExpanded);
        AssertRoom(tab);
        AssertMoves(moved, tab);
        Assert.True(tab.ApproveInView, $"«Aprobar plan» is out of the view of the bands: {tab}.");
        Assert.True(tab.ExecuteInView, $"«Ejecutar» is out of the view of the bands: {tab}.");
    }
}
