using AgentStudio.Application.Analysis;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Desktop.Views.Orchestrator.Questions;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>The screens of the orchestrator (Phase 3 onwards): the document with its canvas, preparation and monitor, and its questions.</summary>
internal static class UiScreensOrchestrator
{

    private static async Task<OrchestratorDocumentViewModel> OpenDocumentAsync()
    {
        var model = new OrchestratorDocumentViewModel(
            @"C:\proyectos\calc",
            new OrchestrationSession(Backends.Complete(new FakeOrchestrator())),
            new FileCanvasLayoutStore(new ProjectStorageFiles()),
            new FakeTimeProvider(T0),
            new InlineSynchronizationContext());
        await model.OpenAsync();
        // Not through LoadTasks with a project root: nothing is written under C:\proyectos.
        model.Canvas.ProjectRoot = null;
        return model;
    }

    private static ApprovalDialogViewModel ApprovalModel() =>
        new(new ApprovalRequest("Borrar el worktree T1", "Tiene cambios sin integrar.", RiskLevel.Destructive, null, ["src/Calc/Calc.cs"]));

    private static QuestionViewModel QuestionModel() =>
        new(Pending(allowOther: true, estimates: [Estimate("a", 9_000, 15_000, 6), Estimate("b", null, null, 2)]), _ => Task.CompletedTask);

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Orchestrator with the plan under review", async () =>
        {
            var document = await OpenDocumentAsync();
            document.Canvas.LoadTasks(PlanFixtures.Replace("T1", t => t with { Objective = string.Empty }), CanvasLayout.Empty);
            document.ValidateCommand.Execute(null);
            document.Canvas.Select(document.Canvas.Nodes[0]);
            return UiScreens.Document(document);
        }, 1500, 980),
        UiScreens.ScreenAsync("Orchestrator preparation", async () =>
        {
            var backend = Backends.Complete(new FakeOrchestrator()) with
            {
                Roster = new FakeRoster(),
                Verification = new FakeProfileSource(null, proposal: Backends.Profile),
            };
            var model = new OrchestratorDocumentViewModel(@"C:\proyectos\calc", new OrchestrationSession(backend), new FileCanvasLayoutStore(new ProjectStorageFiles()), new FakeTimeProvider(T0), new InlineSynchronizationContext());
            await model.OpenAsync();
            model.RequestText = "Añadir Calc.Multiply(a, b) con su prueba";
            model.Requirements.EditCommand.Execute(null);
            model.IsPreparationExpanded = true;
            return UiScreens.Of(new PreparationView(), model);
        }, 1500, 900),
        UiScreens.ScreenAsync("Orchestrator with the visual team builder", async () =>
        {
            var backend = new OrchestrationBackend(
                new FakeOrchestrator(),
                new AgentStudio.Tests.Desktop.Orchestrator.FakeWorkspace(),
                new AgentStudio.Tests.Desktop.Orchestrator.FakeRequirementStore(PlanFixtures.Requirements()),
                new AgentStudio.Tests.Desktop.Orchestrator.FakeRoster(),
                new FakeProfileSource(Backends.Profile));
            var model = new OrchestratorDocumentViewModel(
                @"C:\proyectos\calc",
                new OrchestrationSession(backend),
                new InMemoryLayoutStore(),
                new FakeTimeProvider(T0),
                new InlineSynchronizationContext(),
                builder: BuilderServices.None);
            await model.OpenAsync();
            model.IsPreparationExpanded = true;
            return UiScreens.Document(model);
        }, 1500, 1900),
        UiScreens.ScreenAsync("Run monitor with the analysis of consumption", async () =>
        {
            var model = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
            model.Attach(MonitorSamples.RunInProgress());
            var run = new RunId("20260101-abc123");
            model.Consumption.Analyzer = new ScriptedAnalyzer(new ConsumptionReport(
                run,
                new string('a', 64),
                T0,
                [
                    ConsumptionFinding.Of(ConsumptionFindingCodes.PackOmitted, new ConsumptionTarget(ConsumptionTargetKind.Agent, "exec1"), MetricValue<long>.Exact(42_000, "the CLI"), 2, "exec1", 5, 2),
                    ConsumptionFinding.Of(ConsumptionFindingCodes.WorkDiscarded, new ConsumptionTarget(ConsumptionTargetKind.Run), MetricValue<long>.NotExposed("codex does not report it"), 3, 3, 1),
                ]));
            model.Consumption.Bind(new ProjectContext("p1", "C:/p"), run);
            await model.Consumption.AnalyzeCommand.ExecuteAsync(null);
            return UiScreens.Of(new RunMonitorView(), model);
        }, 1440, 1100),
        UiScreens.Screen("Node properties", () =>
        {
            var canvas = new CanvasViewModel(new FakeTimeProvider(T0));
            canvas.LoadTasks(PlanFixtures.Plan().Tasks, CanvasLayout.Empty);
            var editor = new NodeEditorViewModel(canvas.Nodes[3], new EditorOptions(PlanFixtures.Roster(), PlanFixtures.Requirements().Requirements, [("build", "Compilar"), ("test", "Probar")]), canvas.Nodes, isReadOnly: false);
            return UiScreens.Of(new NodePropertiesView(), editor);
        }, 360, 900),
        UiScreens.Screen("Approval window", () => UiScreens.OfWindow(new ApprovalWindow(), ApprovalModel()), 600, 520),
        UiScreens.Screen("Question window", () => UiScreens.OfWindow(new QuestionWindow(), QuestionModel()), 560, 640),
    ];

    /// <summary>The layout of the canvas kept in memory: nothing is written under the project.</summary>
    private sealed class InMemoryLayoutStore : ICanvasLayoutStore
    {
        private CanvasLayout _saved = CanvasLayout.Empty;

        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => _saved;

        public void Save(string projectRoot, CanvasLayout layout) => _saved = layout;
    }

    private sealed class ScriptedAnalyzer(ConsumptionReport report) : IConsumptionAnalyzer
    {
        public Task<ConsumptionReport> AnalyzeAsync(ProjectContext project, RunId run, CancellationToken ct) => Task.FromResult(report);
    }
}
