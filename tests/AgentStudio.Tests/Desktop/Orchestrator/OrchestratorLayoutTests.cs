using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The tab at the widths it gets (#8): the preparation shares the width in columns while they fit, the requirements alone
/// over the other two when narrower and one under another when narrower still, never past the edge; where the plan stands
/// keeps the width of the band whatever the buttons and the summary are; and a previous run opened in the monitor is drawn.
/// Everything is drawn without ever being shown (<see cref="OffscreenRender"/>).
/// </summary>
public sealed class OrchestratorLayoutTests
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly RunId Done = new("20261030-c4a1f2");

    private sealed class NoLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    private static async Task<OrchestratorDocumentViewModel> OpenAsync(OrchestrationBackend? backend = null)
    {
        OrchestratorTheme.EnsureLoaded();
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(backend ?? Backends.Complete(new FakeOrchestrator())), new NoLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();
        return document;
    }

    /// <summary>Where <paramref name="element"/> lies within <paramref name="root"/>.</summary>
    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    [Theory]
    [InlineData(1400, 1)]
    [InlineData(760, 2)]
    [InlineData(520, 3)]
    public async Task The_preparation_shares_the_width_while_it_fits_and_never_goes_past_its_edge(int width, int rows)
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());

        var (taken, sections) = WpfHost.Run(() =>
        {
            document.IsPreparationExpanded = true;
            document.RequestText = "Añadir Calc.Multiply(a, b) con su prueba";
            var view = new PreparationView { DataContext = document };
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, width, 700), $"wpe-preparacion-{width}.png");
            var panel = (AdaptiveColumnsPanel)view.FindName("Columns");
            var parts = new[] { "RequirementsSection", "AgentsSection", "ProfileSection" }.Select(name => Bounds((FrameworkElement)view.FindName(name), view)).ToList();
            return (panel.Rows, parts);
        });

        Assert.Equal(rows, taken);
        Assert.All(sections, s => Assert.True(s.Left >= 0 && s.Right <= width + 0.5, $"A section lies at {s}, past the width {width}."));
        Assert.All(sections, s => Assert.True(s.Width >= 250, $"A section is {s.Width} wide: squeezed."));
    }

    [Fact]
    public async Task Where_the_plan_stands_keeps_the_width_of_the_band_without_a_summary_and_with_many_problems()
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());

        var (band, state, tallest) = WpfHost.Run(() =>
        {
            // Nodes made by hand: no summary, and problems the validator lists.
            document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
            document.Canvas.AddNodeCommand.Execute(WorkTaskKind.Integration);
            document.Canvas.AddNodeCommand.Execute(WorkTaskKind.Review);
            document.ValidateCommand.Execute(null);
            document.IsPreparationExpanded = false;
            var view = new OrchestratorView { DataContext = document };
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, 760, 900), "wpe-plan-sin-resumen-760.png");
            var review = (FrameworkElement)view.FindName("ReviewBand");
            var plan = (WrapPanel)view.FindName("PlanState");
            return (review.ActualWidth, plan.ActualWidth, plan.Children.OfType<TextBlock>().Max(t => t.ActualHeight));
        });

        Assert.Equal(string.Empty, document.PlanSummary);
        Assert.True(document.Violations.Count > 1);
        Assert.True(state >= band - 30, $"Where the plan stands is {state} wide in a band of {band}.");
        Assert.True(tallest < 60, $"A line of where the plan stands is {tallest} high: its words were split.");
    }

    /// <summary>
    /// docs/24 §1, rows 14 and 15: the tab never scrolls as a whole. In a short pane the bands take at most half of it and scroll
    /// inside, and the canvas and the monitor keep at least their least height (120 each), so the divider always has room.
    /// </summary>
    [Fact]
    public async Task In_a_short_pane_the_bands_scroll_inside_and_the_canvas_and_the_monitor_keep_their_least_height()
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());

        var (layoutHeight, bands, bandsScrollable, canvasHeight, monitorHeight) = WpfHost.Run(() =>
        {
            document.IsPreparationExpanded = true;
            var view = new OrchestratorView { DataContext = document };
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, 600, 450), "wpe-orquestador-600x450.png");
            var layout = (Grid)view.FindName("Layout");
            var scroller = (ScrollViewer)view.FindName("Bands");
            return (layout.ActualHeight, scroller.ActualHeight, scroller.ScrollableHeight, layout.RowDefinitions[1].ActualHeight, layout.RowDefinitions[3].ActualHeight);
        });

        Assert.Equal(450, layoutHeight, 0.5);
        Assert.True(bands <= 225.5, $"The bands take {bands} of 450.");
        Assert.True(bandsScrollable > 0, "The bands do not fit in half of 450: they scroll inside.");
        Assert.True(canvasHeight >= 119.5, $"The canvas got {canvasHeight}.");
        Assert.True(monitorHeight >= 119.5, $"The monitor got {monitorHeight}.");
    }

    /// <summary>
    /// A pane shorter than 400 px (a small window with the bottom panels open) scrolls as a whole, so the bands keep 100 px and
    /// the canvas and the monitor their least height: nothing disappears without a way to reach it.
    /// </summary>
    [Fact]
    public async Task A_pane_shorter_than_400_scrolls_as_a_whole_and_the_bands_keep_100()
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());

        var (layoutHeight, scrollable, bands, bandsMax, canvasHeight, monitorHeight) = WpfHost.Run(() =>
        {
            document.IsPreparationExpanded = true;
            var view = new OrchestratorView { DataContext = document };
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, 600, 260), "ola1-orquestador-600x260.png");
            var layout = (Grid)view.FindName("Layout");
            var bandsScroller = (ScrollViewer)view.FindName("Bands");
            return (layout.ActualHeight, ((ScrollViewer)view.FindName("Scroller")).ScrollableHeight, bandsScroller.ActualHeight, bandsScroller.MaxHeight, layout.RowDefinitions[1].ActualHeight, layout.RowDefinitions[3].ActualHeight);
        });

        Assert.Equal(400, layoutHeight, 0.5);
        Assert.True(scrollable > 0, "A pane of 260 does not scroll as a whole.");
        Assert.Equal(100, bandsMax, 0.5);
        Assert.Equal(100, bands, 0.5);
        Assert.True(canvasHeight >= 119.5, $"The canvas got {canvasHeight}.");
        Assert.True(monitorHeight >= 119.5, $"The monitor got {monitorHeight}.");
    }

    /// <summary>
    /// docs/24 §1, row 16: «Resumen del plan» takes the width of its band and wraps its words: three lines at least, and a long
    /// summary grows up to its limit and then scrolls, with «Pedir plan» above it, beside its label, instead of squeezing it.
    /// </summary>
    [Fact]
    public async Task The_summary_of_the_plan_takes_the_width_of_its_band_and_wraps_its_words()
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());

        var (box, band, wrapping, shortHeight, lines, longHeight, maxHeight, extent, viewport, requestAbove) = WpfHost.Run(() =>
        {
            document.IsPreparationExpanded = false;
            document.PlanSummary = "Añadir Calc.Multiply.";
            var view = new OrchestratorView { DataContext = document };

            // In a host of a fixed size, as in its pane: a root without one is laid out again with no limit after a change inside.
            var host = new Border { Width = 1040, Height = 900, Child = view };
            OffscreenRender.Render(host, 1040, 900);
            var summary = (TextBox)view.FindName("PlanSummaryBox");
            var review = (FrameworkElement)view.FindName("ReviewBand");
            var request = Descendants<Button>(review).First(b => Equals(b.Content, AgentStudio.Desktop.Localization.Texts.Get("Orchestrator_Review_RequestPlan")));
            var shortOne = summary.ActualHeight;

            document.PlanSummary = string.Join(" ", Enumerable.Repeat("El plan reparte el trabajo en tareas pequeñas, cada una con su revisión y sus pruebas antes de integrarla en la rama.", 30));
            OffscreenRender.Render(host, 1040, 900);
            var above = request.TransformToAncestor(view).Transform(new Point(0, request.ActualHeight)).Y <= summary.TransformToAncestor(view).Transform(new Point(0, 0)).Y + 0.5;
            var scroller = Descendants<ScrollViewer>(summary).First();
            return (summary.ActualWidth, review.ActualWidth, summary.TextWrapping, shortOne, summary.LineCount, summary.ActualHeight, summary.MaxHeight, scroller.ExtentHeight, scroller.ViewportHeight, above);
        });

        Assert.True(box >= band - 40, $"The summary is {box} wide in a band of {band}.");
        Assert.Equal(TextWrapping.Wrap, wrapping);
        Assert.True(shortHeight >= 3 * 13, $"A short summary keeps {shortHeight}, less than three lines.");
        Assert.True(lines > 3, $"A long summary takes {lines} lines: it does not wrap.");
        Assert.True(longHeight > shortHeight, "A long summary does not grow.");
        Assert.True(longHeight <= maxHeight + 0.5, $"A long summary takes {longHeight}, past its limit {maxHeight}.");
        Assert.True(extent > viewport, "A summary past its limit does not scroll.");
        Assert.True(requestAbove, "«Pedir plan» is not above the summary, beside its label.");
    }

    /// <summary>
    /// docs/24 §2, principle 4: the screens that bring the person here leave the keyboard where to write. The request waits on
    /// the document until a view on screen takes it, after the dock is done moving the keyboard (ContextIdle), and it is cleared
    /// once the keyboard landed there: the box of the request takes it (in a view never shown, as the focused element of its
    /// scope). «Requisitos», whose controls are not visible in a view never shown, cannot: it is tried once more and then the
    /// request expires (review fix 6), so a later showing of the tab never takes the keyboard by surprise.
    /// </summary>
    [Fact]
    public async Task A_view_on_screen_takes_the_part_the_keyboard_should_go_to_and_the_request_expires_after_one_retry()
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());

        var (expanded, requestLanded, focused, requirementsPending) = WpfHost.Run(() =>
        {
            document.IsPreparationExpanded = false;
            var view = new OrchestratorView { DataContext = document };
            OffscreenRender.Render(view, 1040, 900);

            // The dock raises Loaded when the tab is shown; offscreen it is raised by hand.
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
            document.FocusRequestBox();
            WpfHost.FlushBindings();
            var landed = document.PendingFocus is null;
            var box = ((PreparationView)view.FindName("PreparationBand")).FindName("RequestBox");
            var inScope = FocusManager.GetFocusedElement(FocusManager.GetFocusScope((DependencyObject)box));

            document.ShowRequirements();
            WpfHost.FlushBindings();
            return (document.IsPreparationExpanded, landed, ReferenceEquals(inScope, box), document.PendingFocus);
        });

        Assert.True(expanded);
        Assert.True(requestLanded, "The box of the request took the keyboard, but the request was not cleared.");
        Assert.True(focused, "The box of the request is not the focused element of its scope.");
        Assert.Null(requirementsPending);
    }

    /// <summary>
    /// Review fix 2: folded for a run, the band of the plan keeps in view what failed (a run resumed by the planner can fail to show
    /// its header), and its line says the truth: that the plan runs, or that the run waits for an answer or an approval of yours.
    /// </summary>
    [Fact]
    public async Task Folded_for_a_run_the_band_keeps_its_error_in_view_and_says_when_the_run_waits_for_you()
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());
        var waitingDocument = await WpfHost.RunAsync(() => OpenAsync());

        var (folded, errorShown, contentHidden, running, waiting) = WpfHost.Run(() =>
        {
            document.Monitor.Attach(new FakeRun(new RunSnapshot(new RunId("20261116-a1b2c3"), RunPhase.Running, [], FakeRun.Healthy, 0)), document.Context!.Project!);
            document.ErrorMessage = "No se pudo leer la cabecera de la ejecución.";
            var view = new OrchestratorView { DataContext = document };
            OffscreenRender.Render(view, 1040, 900);
            var error = (TextBlock)view.FindName("ReviewError");
            var content = (FrameworkElement)view.FindName("ReviewContent");
            waitingDocument.Monitor.Attach(new FakeRun(new RunSnapshot(new RunId("20261116-d4e5f6"), RunPhase.AwaitingHuman, [], FakeRun.Healthy, 0)), waitingDocument.Context!.Project!);
            return (document.IsReviewFolded, error.Visibility == Visibility.Visible && error.ActualHeight > 0, content.Visibility == Visibility.Collapsed, document.ReviewFoldedText, waitingDocument.ReviewFoldedText);
        });

        Assert.True(folded);
        Assert.True(contentHidden);
        Assert.True(errorShown, "Folded, the band hides the error.");
        Assert.Equal("El plan está en marcha y no cambia mientras se ejecuta. Síguelo en el monitor, abajo.", running);
        Assert.Equal("La ejecución espera tu respuesta o tu aprobación. La tienes en el monitor, abajo.", waiting);
        Assert.True(waitingDocument.ReviewWaitsForYou);
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

    [Fact]
    public async Task A_previous_run_opened_in_the_monitor_is_drawn_with_the_reason_and_the_error_line_of_its_failed_task()
    {
        var plan = PlanFixtures.Plan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var engine = new FakeOrchestrator();
        engine.Runs.Add(new RunSummary(Done, T0, RunPhase.Failed, plan.ContentHash, "Falló. Sin reintentos: T1.", null));
        var evidence = new RunMonitorInspectionTests.MemoryEvidenceStore();
        var test = evidence.Add("Probar (test)", EvidenceKind.TestRun, "# Agent Studio · verification step 'test' (Probar)\nstdout| Restore complete.\nstdout| No test projects were found.\n# exit code: 1\n");
        var inspector = new PreviousRunsTests.FakeInspector();
        inspector.Runs[Done] = () =>
        {
            var run = new FakeRun(new RunSnapshot(
                Done,
                RunPhase.Failed,
                [.. plan.Tasks.Select(t => t.Id.Value == "T1"
                    ? FakeRun.View("T1", status: WorkTaskStatus.Failed) with { Attempt = 4, RetryCount = 3 }
                    : FakeRun.View(t.Id.Value, t.Kind, WorkTaskStatus.Pending))],
                new GraphHealthReport([], [], [new TaskId("T1")], false),
                AgentRunsUsed: 4));
            run.Emit(
                new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada."),
                new EvidenceCapturedEvent(T0, Tid("T1"), test),
                new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Running, WorkTaskStatus.Failed, "La verificación falló en: test"));
            run.Finish(new RunResult(Done, RunPhase.Failed, new GraphHealthReport([], [], [new TaskId("T1")], false), null, null, "Falló. Sin reintentos: T1."));
            return run;
        };
        var backend = Backends.Complete(engine, plans) with { Inspector = inspector, Evidence = evidence };
        var document = await WpfHost.RunAsync(async () =>
        {
            var opened = await OpenAsync(backend);
            await opened.ViewRunCommand.ExecuteAsync(opened.PreviousRuns[0]);
            await opened.Monitor.Listening;
            await opened.Monitor.FailureLineRead;
            return opened;
        });

        var (drawn, monitorDrawn) = WpfHost.Run(() =>
        {
            var tab = OffscreenRender.Render(new OrchestratorView { DataContext = document }, 1240, 900);
            OffscreenRender.SaveIfRequested(tab, "wpe-ejecucion-anterior-1240.png");
            var monitor = OffscreenRender.Render(new RunMonitorView { DataContext = document.Monitor }, 1000, 420);
            OffscreenRender.SaveIfRequested(monitor, "wpe-monitor-ejecucion-anterior.png");
            return (OffscreenRender.CountUnlike(tab, Color.FromRgb(0x0A, 0x10, 0x20)), OffscreenRender.CountUnlike(monitor, Color.FromRgb(0x10, 0x1A, 0x30)));
        });

        var task = document.Monitor.Tasks.Single(t => t.Id == Tid("T1"));
        Assert.True(document.Monitor.IsInspecting);
        Assert.Equal("Fallida", document.Monitor.PhaseLabel);
        Assert.Equal("La verificación falló en: test", task.ReasonText);
        Assert.Equal("No test projects were found.", task.FailureLine);
        Assert.Equal("Intento 4 · 3 reintentos", task.AttemptText);
        Assert.True(drawn > 100_000, $"Only {drawn} pixels differ from the background.");
        Assert.True(monitorDrawn > 20_000, $"Only {monitorDrawn} pixels differ from the panel.");
    }
}
