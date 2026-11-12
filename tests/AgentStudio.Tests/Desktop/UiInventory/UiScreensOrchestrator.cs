using System.Windows;
using AgentStudio.Application.Security;
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
        UiScreens.Screen("Run monitor", () =>
        {
            var model = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
            model.Attach(MonitorSamples.RunInProgress());
            return UiScreens.Of(new RunMonitorView(), model);
        }, 1440, 900),
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
}
