using System.Windows;
using System.Windows.Controls;
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

    [Fact]
    public async Task The_tab_scrolls_when_its_pane_is_shorter_than_its_bands_and_the_least_of_the_canvas_and_the_monitor()
    {
        var document = await WpfHost.RunAsync(() => OpenAsync());

        var (layoutHeight, canvasHeight, scrollable) = WpfHost.Run(() =>
        {
            document.IsPreparationExpanded = true;
            var view = new OrchestratorView { DataContext = document };
            OffscreenRender.SaveIfRequested(OffscreenRender.Render(view, 600, 450), "wpe-orquestador-600x450.png");
            var layout = (Grid)view.FindName("Layout");
            var scroller = (ScrollViewer)view.FindName("Scroller");
            return (layout.ActualHeight, layout.RowDefinitions[3].ActualHeight, scroller.ScrollableHeight);
        });

        Assert.True(canvasHeight >= 259, $"The canvas got {canvasHeight}.");
        Assert.True(layoutHeight > 450);
        Assert.True(scrollable > 0);
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
