using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The orchestrator views drawn without ever being shown (<see cref="OffscreenRender"/>). They prove that the templates,
/// the theme and the bindings produce a picture; with <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved for
/// docs/evidencias.
/// </summary>
public sealed class OrchestratorRenderTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);
    private static readonly Color Raised = Color.FromRgb(0x16, 0x22, 0x3D);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);
    private static readonly Color Skipped = Color.FromRgb(0x5E, 0x6E, 0x8C);

    [Fact]
    public void The_canvas_renders_offscreen_with_its_nodes_and_status_colours()
    {
        var (drawn, headers, completed, skipped, builtIn) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var canvas = new CanvasViewModel(new FakeTimeProvider(T0));
            CanvasSamples.LoadRunInProgress(canvas);
            canvas.Select(canvas.Nodes[0]); // the selection frame is themed too
            canvas.ViewportZoom = 0.72;
            canvas.ViewportLocation = new Point(-30, -70);
            var view = new CanvasView { DataContext = canvas };

            var bitmap = OffscreenRender.Render(view, 1440, 780);
            OffscreenRender.SaveIfRequested(bitmap, "fase3-lienzo.png");
            return (
                OffscreenRender.CountUnlike(bitmap, Background),
                OffscreenRender.Count(bitmap, Raised),
                OffscreenRender.Count(bitmap, Success),
                OffscreenRender.Count(bitmap, Skipped),
                OffscreenRender.Count(bitmap, NodifyFrame, tolerance: 0) + OffscreenRender.Count(bitmap, NodifySelection, tolerance: 0));
        });

        Assert.True(drawn > 50_000, $"Only {drawn} pixels differ from the background.");
        Assert.True(headers > 5_000, $"Only {headers} pixels have the node header colour: the nodes were not drawn.");
        Assert.True(completed > 200, $"Only {completed} pixels have the completed colour.");
        Assert.True(skipped > 100, $"Only {skipped} pixels have the skipped colour.");

        // Nodify's built-in frame colours (DodgerBlue, orange) mean the theme chain is broken: the focus-visual styles
        // were based on Nodify's built-in theme instead of its Dark.xaml (merge order, see Themes/Nodify.xaml).
        Assert.Equal(0, builtIn);
    }

    [Fact]
    public void The_monitor_renders_offscreen_with_its_tasks_activity_and_evidence()
    {
        var (drawn, warning, monitor) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
            model.Attach(MonitorSamples.RunInProgress()); // the sample's events are buffered: read at once
            var view = new RunMonitorView { DataContext = model };

            var bitmap = OffscreenRender.Render(view, 1440, 440);
            OffscreenRender.SaveIfRequested(bitmap, "fase3-monitor.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Warning), model);
        });

        Assert.Equal(6, monitor.Tasks.Count);
        Assert.Equal("En cola · codex-cli", monitor.Tasks[3].StatusLabel);
        Assert.True(drawn > 30_000, $"Only {drawn} pixels differ from the panel.");
        // The line is short since the agent cards took a column of their own (WP8), and a Windows without ClearType (the
        // runner of the CI) draws its text with fewer pixels of the exact colour: 45 there, more on a desktop.
        Assert.True(warning > 30, $"Only {warning} pixels have the warning colour (the pending approval line).");
    }

    [Fact]
    public void The_properties_panel_and_the_approval_question_render_offscreen()
    {
        var (properties, question) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var canvas = new CanvasViewModel(new FakeTimeProvider(T0));
            canvas.LoadTasks(PlanFixtures.Plan().Tasks, CanvasLayout.Empty);
            var editor = new NodeEditorViewModel(canvas.Nodes[3], new EditorOptions(PlanFixtures.Roster(), PlanFixtures.Requirements().Requirements, [("build", "Compilar"), ("test", "Probar")]), canvas.Nodes, isReadOnly: false);
            var panel = new Border { Background = (Brush)System.Windows.Application.Current.Resources["SurfaceBrush"], Child = new NodePropertiesView { DataContext = editor } };
            var approval = new ApprovalView
            {
                DataContext = new ApprovalDialogViewModel(new ApprovalRequest("Borrar el worktree T1", "Tiene cambios sin integrar.", RiskLevel.Destructive, null, ["src/Calc/Calc.cs"])),
            };

            return (
                OffscreenRender.CountUnlike(OffscreenRender.Render(panel, 360, 900), Surface),
                OffscreenRender.Count(OffscreenRender.Render(approval, 600, 420), Accent));
        });

        Assert.True(properties > 5_000, $"Only {properties} pixels differ from the panel: the properties were not drawn.");
        Assert.True(question > 500, $"Only {question} pixels have the accent colour: the «Aprobar» button was not drawn.");
    }

    [Fact]
    public async Task The_whole_orchestrator_tab_renders_offscreen()
    {
        var document = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = new OrchestratorDocumentViewModel(
                @"C:\proyectos\calc",
                new OrchestrationSession(Backends.Complete(new FakeOrchestrator())),
                new FileCanvasLayoutStore(new ProjectStorageFiles()),
                new FakeTimeProvider(T0),
                new InlineSynchronizationContext());
            await model.OpenAsync();
            return model;
        });

        var (drawn, problems) = WpfHost.Run(() =>
        {
            // Not through LoadTasks with a project root: nothing is written under C:\proyectos.
            document.Canvas.ProjectRoot = null;
            document.Canvas.LoadTasks(PlanFixtures.Replace("T1", t => t with { Objective = string.Empty }), CanvasLayout.Empty);
            document.ValidateCommand.Execute(null);
            document.Canvas.Select(document.Canvas.Nodes[0]);
            var view = new OrchestratorView { DataContext = document };

            var bitmap = OffscreenRender.Render(view, 1500, 980);
            OffscreenRender.SaveIfRequested(bitmap, "fase3-orquestador.png");
            return (OffscreenRender.CountUnlike(bitmap, Background), document.Problems.Count);
        });

        Assert.Equal(1, problems);
        Assert.True(drawn > 100_000, $"Only {drawn} pixels differ from the background.");
    }

    [Fact]
    public async Task The_plan_review_draws_the_warning_about_steps_without_isolation_offscreen()
    {
        static async Task<OrchestratorDocumentViewModel> OpenAsync(VerificationProfile profile)
        {
            OrchestratorTheme.EnsureLoaded();
            var backend = Backends.Complete(new FakeOrchestrator()) with { Verification = new FakeProfileSource(profile) };
            var model = new OrchestratorDocumentViewModel(@"C:\proyectos\calc", new OrchestrationSession(backend), new FileCanvasLayoutStore(new ProjectStorageFiles()), new FakeTimeProvider(T0), new InlineSynchronizationContext());
            await model.OpenAsync();
            return model;
        }

        var unisolated = Backends.Profile with { Steps = [.. Backends.Profile.Steps.Select(s => s with { Isolation = ProcessIsolation.None })] };
        var warned = await WpfHost.RunAsync(() => OpenAsync(unisolated));
        var isolated = await WpfHost.RunAsync(() => OpenAsync(Backends.Profile));

        var (withWarning, withoutWarning) = WpfHost.Run(() =>
        {
            int WarningPixels(OrchestratorDocumentViewModel document)
            {
                document.Canvas.ProjectRoot = null; // nothing is written under C:\proyectos
                document.Canvas.LoadTasks(PlanFixtures.Plan().Tasks, CanvasLayout.Empty);
                document.ApproveCommand.Execute(null);
                return OffscreenRender.Count(OffscreenRender.Render(new OrchestratorView { DataContext = document }, 1500, 980), Warning);
            }

            return (WarningPixels(warned), WarningPixels(isolated));
        });

        Assert.True(warned.IsApproved && isolated.IsApproved);
        Assert.True(withWarning - withoutWarning > 1_000, $"The warning colour covers {withWarning} pixels with steps without isolation and {withoutWarning} without: the warning was not drawn.");
    }

    [Fact]
    public async Task The_preparation_band_renders_offscreen_with_the_requirements_the_agents_and_the_proposed_profile()
    {
        var document = await WpfHost.RunAsync(async () =>
        {
            OrchestratorTheme.EnsureLoaded();
            var backend = Backends.Complete(new FakeOrchestrator()) with
            {
                Roster = new FakeRoster(),
                Verification = new FakeProfileSource(null, proposal: Backends.Profile),
            };
            var model = new OrchestratorDocumentViewModel(@"C:\proyectos\calc", new OrchestrationSession(backend), new FileCanvasLayoutStore(new ProjectStorageFiles()), new FakeTimeProvider(T0), new InlineSynchronizationContext());
            await model.OpenAsync();
            return model;
        });

        var (drawn, accent) = WpfHost.Run(() =>
        {
            document.RequestText = "Añadir Calc.Multiply(a, b) con su prueba";
            document.Requirements.EditCommand.Execute(null);
            var view = new PreparationView { DataContext = document };

            var bitmap = OffscreenRender.Render(view, 1500, 420);
            OffscreenRender.SaveIfRequested(bitmap, "fase3-preparacion.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Accent));
        });

        Assert.Equal(7, document.RosterLines.Count);
        Assert.True(document.ShowsProfileProposal);
        Assert.Equal(2, document.Requirements.Drafts.Count);
        Assert.True(drawn > 30_000, $"Only {drawn} pixels differ from the panel.");
        Assert.True(accent > 500, $"Only {accent} pixels have the accent colour: «Congelar requisitos» and «Usar perfil propuesto» were not drawn.");
    }

    private static readonly Color NodifyFrame = Color.FromRgb(0x1E, 0x90, 0xFF);
    private static readonly Color NodifySelection = Color.FromRgb(0xFF, 0xA5, 0x00);
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);
}
