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

    /// <summary>
    /// The share of its row the editor of the canvas, and the lists of the monitor, keep at least while a run goes on, before the
    /// divider is moved. A share, not pixels, so it holds whatever the fonts of the computer (measured: 114 of 249 and 107 of 249
    /// in Spanish at 1264 × 760); the caps of the toolbar (30 %) and of the top of the monitor (50 %) are what give it.
    /// </summary>
    private const double ShareWithARun = 0.35;

    /// <summary>The bands take at most 40 % of the tab, and leave 360 px to the canvas, the divider and the monitor; 100 px at least.</summary>
    private static double BandsCap(double tab) => Math.Max(100, Math.Min(tab * 0.4, tab - 360));

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
        bool ReviewFolded,
        bool ApproveInView)
    {
        public override string ToString() =>
            $"tab {Width:F0} × {Height:F0}, bands {Bands:F0} (at most {BandsMax:F0}), canvas {CanvasRow:F0} (least {CanvasMin:F0}), monitor {MonitorRow:F0} (least {MonitorMin:F0}), editor {Editor:F0}, lists {Lists:F0}";
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
            ((FrameworkElement)view.FindName("ReviewContent")).Visibility != Visibility.Visible,
            InView(Labelled(bands, "Orchestrator_Review_Approve"), bands));
    }

    /// <summary>Opens the orchestrator of a project in the real window at its default size and reads its tab.</summary>
    private async Task<(Tab Tab, OrchestratorDocumentViewModel Document)> OpenInTheWindowAsync(OrchestrationBackend backend, bool withRun, string picture, double clientHeight = ClientHeight)
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
            OffscreenRender.SaveIfRequested(OffscreenRender.Render((FrameworkElement)window.Content, ClientWidth, clientHeight), picture);
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

    /// <summary>The heights measured, in the output of the test and, with the evidence folder set, in a file beside the pictures.</summary>
    private static void Note(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable(OffscreenRender.EvidenceVariable) is { Length: > 0 } folder)
        {
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "ola1-medidas.txt"), TestContext.Current.Test?.TestDisplayName + ": " + line + Environment.NewLine);
        }
    }

    /// <summary>A drag of the mouse as the thumb of the divider reports it: no window and no pointer needed.</summary>
    private static void Drag(GridSplitter splitter, double by)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
        splitter.RaiseEvent(new DragDeltaEventArgs(0, by));
        splitter.RaiseEvent(new DragCompletedEventArgs(0, by, false));
    }

    private static void AssertMoves(Moved moved, Tab tab)
    {
        // Review fix 7: the caps of the bands are the ones the tab promises.
        Assert.Equal(BandsCap(tab.Height), tab.BandsMax, 0.5);
        Assert.True(tab.Bands <= tab.BandsMax + 0.5, $"The bands pass their cap: {tab}.");
        var report = $"{tab}; canvas {moved.Before:F0}, dragged down {moved.AfterDown:F0}, dragged up {moved.AfterUp:F0}, arrow down {moved.AfterKeyDown:F0}, arrow up {moved.AfterKeyUp:F0}";
        Note(report);
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

    /// <summary>
    /// Review fix 1 (ADR-0018 §3): what «Aprobar plan» approves is said before it. A plan whose steps run without isolation warns
    /// above the button, and with the bands scrolled to «Aprobar plan» the warning is in view too: it cannot be reached without it.
    /// With a warning the bands may take 60 % of the tab, so the warnings of a long plan and the button fit together.
    /// </summary>
    [Fact]
    public async Task The_warning_of_a_plan_comes_before_approve_and_is_in_view_with_it()
    {
        var steps = Backends.Profile.Steps;
        var profile = Backends.Profile with { Steps = [steps[0] with { Isolation = AgentStudio.Application.Execution.ProcessIsolation.None }, .. steps.Skip(1)] };
        var backend = Backends.Complete(new FakeOrchestrator()) with { Verification = new FakeProfileSource(profile) };
        var (tab, document) = await OpenInTheWindowAsync(backend, withRun: false, "ola1-orquestador-aviso.png");

        var (warns, approveInView, warningInView, warningAbove, where, cap) = WpfHost.Run(() =>
        {
            document.Canvas.LoadTasks(AgentStudio.Tests.Domain.Orchestration.PlanFixtures.Plan().Tasks, CanvasLayout.Empty);
            document.PlanSummary = "Añadir Calc.Multiply(a, b) con su prueba, revisada por el juez antes de integrarla en la rama del proyecto.";
            var view = new OrchestratorView { DataContext = document };
            var host = new Border { Width = tab.Width, Height = tab.Height, Child = view };
            OffscreenRender.Render(host, tab.Width, tab.Height);
            var bands = (ScrollViewer)view.FindName("Bands");
            var approve = Labelled(bands, "Orchestrator_Review_Approve");
            var warning = Descendants<TextBlock>(bands).First(t => t.Text == document.IsolationWarningText);
            approve.BringIntoView();
            OffscreenRender.Render(host, tab.Width, tab.Height);
            var warningBottom = warning.TransformToAncestor(bands).Transform(new Point(0, warning.ActualHeight)).Y;
            var approveTop = approve.TransformToAncestor(bands).Transform(new Point(0, 0)).Y;
            var warningTop = warning.TransformToAncestor(bands).Transform(new Point(0, 0)).Y;
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(host, tab.Width, tab.Height), "ola1-orquestador-aviso-aprobar.png");
            var where = $"warning {warningTop:F0}..{warningBottom:F0}, «Aprobar plan» from {approveTop:F0}, viewport {bands.ViewportHeight:F0}";
            return (document.HasIsolationWarning, InView(approve, bands), InView(warning, bands), warningBottom <= approveTop + 0.5, where, bands.MaxHeight);
        });

        Assert.True(warns, "The plan has no warning: the test does not test anything.");
        Assert.Equal(WarnedCap(tab.Height), cap, 0.5);
        Assert.True(approveInView, "«Aprobar plan» was brought into view, and it is not.");
        Assert.True(warningAbove, "The warning is not above «Aprobar plan».");
        Assert.True(warningInView, $"With «Aprobar plan» in view, its warning is not: {where}.");
    }

    /// <summary>With a warning on the plan the bands may take 60 %: of the tab, never the 248 px of the canvas, the divider and the monitor.</summary>
    private static double WarnedCap(double tab) => Math.Max(100, Math.Min(tab * 0.6, tab - 248));

    private static (OrchestrationBackend Backend, Action<OrchestratorDocumentViewModel> Load) PlanWithWarnings()
    {
        var steps = Backends.Profile.Steps;
        var profile = Backends.Profile with { Steps = [steps[0] with { Isolation = AgentStudio.Application.Execution.ProcessIsolation.None }, .. steps.Skip(1)] };
        return (
            Backends.Complete(new FakeOrchestrator()) with { Verification = new FakeProfileSource(profile) },
            document => document.Canvas.LoadTasks(AgentStudio.Tests.Domain.Orchestration.PlanFixtures.Plan().Tasks, CanvasLayout.Empty));
    }

    /// <summary>
    /// Review round 3, fix 1: the bands take 60 % only while the plan can be approved. With a run on screen the warnings are still
    /// worked out, but the monitor keeps its room for «Entregar a mi rama» and its warning, which comes above the button.
    /// </summary>
    [Fact]
    public async Task With_a_run_on_screen_the_bands_keep_their_usual_cap_and_the_monitor_warns_above_its_delivery()
    {
        var (backend, load) = PlanWithWarnings();
        var (tab, document) = await OpenInTheWindowAsync(backend, withRun: false, "ola1-orquestador-aviso-ejecucion.png");

        var (warned, approvable, withRun, warningFirst) = WpfHost.Run(() =>
        {
            load(document);
            var view = new OrchestratorView { DataContext = document };
            var host = new Border { Width = tab.Width, Height = tab.Height, Child = view };
            OffscreenRender.Render(host, tab.Width, tab.Height);
            var bands = (ScrollViewer)view.FindName("Bands");
            var beforeTheRun = bands.MaxHeight;
            document.Monitor.Attach(new FakeRun(new RunSnapshot(new RunId("20261116-a1b2c3"), RunPhase.Running, [], FakeRun.Healthy, 0)), document.Context!.Project!);
            OffscreenRender.Render(host, tab.Width, tab.Height);
            var monitor = Descendants<RunMonitorView>(view).Single();
            var warning = (FrameworkElement)monitor.FindName("PreviousDeliveryText");
            var commands = (FrameworkElement)monitor.FindName("RunCommands");
            var column = (Panel)VisualTreeHelper.GetParent(warning);
            return (document.HasIsolationWarning, beforeTheRun, bands.MaxHeight, ReferenceEquals(column, VisualTreeHelper.GetParent(commands)) && column.Children.IndexOf(warning) < column.Children.IndexOf(commands));
        });

        Assert.True(warned, "The plan has no warning: the test does not test anything.");
        Assert.Equal(WarnedCap(tab.Height), approvable, 0.5);
        Assert.Equal(BandsCap(tab.Height), withRun, 0.5);
        Assert.True(warningFirst, "The warning of a delivery made already is not above «Entregar a mi rama».");
    }

    /// <summary>
    /// Review round 3, fix 2: at the least size of the window (560 high outside, so about 521 inside) the bands with warnings have
    /// little room. Wherever the bands are scrolled, «Aprobar plan» is never in view with the top of its warnings out of view above
    /// it; and there is a place where both are seen, because the box of the warnings scrolls inside and leaves room for the button.
    /// </summary>
    [Fact]
    public async Task At_the_least_window_size_approve_is_never_seen_with_its_warnings_cut_above_it()
    {
        var (backend, load) = PlanWithWarnings();
        var (tab, document) = await OpenInTheWindowAsync(backend, withRun: false, "ola1-orquestador-minimo.png", clientHeight: 521);

        var (warns, cap, violations, offsets, together) = WpfHost.Run(() =>
        {
            load(document);
            document.PlanSummary = "Añadir Calc.Multiply(a, b) con su prueba, revisada por el juez antes de integrarla en la rama del proyecto.";
            var view = new OrchestratorView { DataContext = document };
            var host = new Border { Width = tab.Width, Height = tab.Height, Child = view };
            OffscreenRender.Render(host, tab.Width, tab.Height);
            var bands = (ScrollViewer)view.FindName("Bands");
            var warnings = (FrameworkElement)view.FindName("PlanWarningsScroller");
            var row = (FrameworkElement)view.FindName("DecisionRow");
            var cut = new List<string>();
            var tried = 0;
            for (var offset = 0.0; offset <= bands.ScrollableHeight + 8; offset += 8)
            {
                bands.ScrollToVerticalOffset(offset);
                OffscreenRender.Render(host, tab.Width, tab.Height);
                tried++;
                var warningsTop = warnings.TransformToAncestor(bands).Transform(default).Y;
                var rowTop = row.TransformToAncestor(bands).Transform(default).Y;
                if (rowTop < bands.ViewportHeight && rowTop + row.ActualHeight > 0 && warningsTop < -0.5)
                {
                    cut.Add($"asked {offset:F0}: the row at {rowTop:F0}, the warnings from {warningsTop:F0}");
                }
            }

            var approve = Labelled(bands, "Orchestrator_Review_Approve");
            approve.BringIntoView();
            OffscreenRender.Render(host, tab.Width, tab.Height);
            var top = warnings.TransformToAncestor(bands).Transform(default).Y;
            return (document.HasIsolationWarning, bands.MaxHeight, cut, tried, InView(approve, bands) && top >= -0.5 && top < bands.ViewportHeight);
        });

        Assert.True(warns, "The plan has no warning: the test does not test anything.");
        Assert.Equal(WarnedCap(tab.Height), cap, 0.5);
        Assert.True(offsets > 1, "The bands did not scroll: the test does not test anything.");
        Assert.Empty(violations);
        Assert.True(together, "There is no place where «Aprobar plan» and the top of its warnings are seen together.");
    }

    [Fact]
    public async Task In_a_fresh_project_the_divider_moves_both_ways_at_the_default_size()
    {
        var fresh = Backends.Complete(new FakeOrchestrator()) with { Requirements = new FakeRequirementStore(), Verification = null };

        var (tab, document) = await OpenInTheWindowAsync(fresh, withRun: false, "ola1-orquestador-nuevo.png");
        var moved = MoveTheDivider(document, tab);

        Assert.True(document.IsPreparationExpanded);
        AssertMoves(moved, tab);
    }

    /// <summary>
    /// The owner's complaint was that everything was cut. With a run going on the band of the plan folds to one line (there is
    /// nothing to approve), so the canvas shows its nodes and the monitor several tasks before the divider is moved.
    /// </summary>
    [Fact]
    public async Task With_a_run_going_on_the_editor_and_the_lists_have_room_and_the_divider_moves_both_ways()
    {
        var (tab, document) = await OpenInTheWindowAsync(Backends.Complete(new FakeOrchestrator()), withRun: true, "ola1-orquestador-con-ejecucion.png");
        var moved = MoveTheDivider(document, tab);

        Assert.True(tab.ReviewFolded, $"The band of the plan is not folded while the run goes on: {tab}.");
        Assert.True(tab.Editor >= tab.CanvasRow * ShareWithARun, $"The canvas leaves its editor {tab.Editor:F0} of {tab.CanvasRow:F0}: {tab}.");
        Assert.True(tab.Lists >= tab.MonitorRow * ShareWithARun, $"The monitor leaves its lists {tab.Lists:F0} of {tab.MonitorRow:F0}: {tab}.");
        AssertMoves(moved, tab);
    }

    /// <summary>
    /// With no run, in a complete project («Preparación» folded), «Aprobar plan» is in view without scrolling. The decision comes
    /// right under the summary, before where the plan stands, so how many lines that takes does not move it.
    /// </summary>
    [Fact]
    public async Task In_a_complete_project_without_a_run_approve_is_in_view_at_the_default_size()
    {
        var (tab, document) = await OpenInTheWindowAsync(Backends.Complete(new FakeOrchestrator()), withRun: false, "ola1-orquestador-completo.png");

        Assert.False(document.IsPreparationExpanded);
        Note(tab.ToString());
        Assert.False(tab.ReviewFolded);
        Assert.True(tab.ApproveInView, $"«Aprobar plan» is out of the view of the bands: {tab}.");
    }
}
