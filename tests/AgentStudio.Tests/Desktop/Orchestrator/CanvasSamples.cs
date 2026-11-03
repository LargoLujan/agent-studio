using System.Windows;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// Test data for the renders: a run in progress that shows every kind and every status at once (the spike's small
/// sample, built through the canvas view model). T1 fans out to three lanes; T3 is a condition whose «Sí» branch ran
/// and whose «No» branch was skipped; T12 is a debate node, disabled until Phase 5.
/// </summary>
internal static class CanvasSamples
{
    public static void LoadRunInProgress(CanvasViewModel canvas)
    {
        canvas.Clear();
        var t1 = Add(canvas, "T1", WorkTaskKind.AgentWork, "Implementar Calc.Multiply", 0, 160, WorkTaskStatus.Completed);
        var t2 = Add(canvas, "T2", WorkTaskKind.ToolRun, "Compilar y probar", 320, 40, WorkTaskStatus.Completed);
        var t3 = Add(canvas, "T3", WorkTaskKind.Condition, "¿Pruebas en verde?", 640, 40, WorkTaskStatus.Completed);
        var t4 = Add(canvas, "T4", WorkTaskKind.Integration, "Integrar ramas", 960, 0, WorkTaskStatus.Running);
        var t5 = Add(canvas, "T5", WorkTaskKind.AgentWork, "Corregir fallos", 960, 190, WorkTaskStatus.Skipped);
        var t6 = Add(canvas, "T6", WorkTaskKind.Review, "Revisión independiente", 1280, 0, WorkTaskStatus.Pending);
        var t7 = Add(canvas, "T7", WorkTaskKind.HumanApproval, "Aprobar entrega", 1600, 0, WorkTaskStatus.Pending);
        var t8 = Add(canvas, "T8", WorkTaskKind.AgentWork, "Actualizar documentación", 320, 330, WorkTaskStatus.Failed);
        var t9 = Add(canvas, "T9", WorkTaskKind.ToolRun, "Validar enlaces", 640, 360, WorkTaskStatus.Blocked);
        var t10 = Add(canvas, "T10", WorkTaskKind.AgentWork, "Migrar ejemplos", 320, 540, WorkTaskStatus.Cancelled);
        var t11 = Add(canvas, "T11", WorkTaskKind.HumanApproval, "Aprobar borrado", 960, 380, WorkTaskStatus.AwaitingApproval);
        _ = Add(canvas, "T12", WorkTaskKind.Debate, "Debate de diseño", 640, 560, WorkTaskStatus.Pending);

        Wire(canvas, t1, t2);
        Wire(canvas, t2, t3);
        Wire(canvas, t3, t4, output: 0);
        Wire(canvas, t3, t5, output: 1);
        Wire(canvas, t4, t6);
        Wire(canvas, t5, t6);
        Wire(canvas, t6, t7);
        Wire(canvas, t1, t8);
        Wire(canvas, t8, t9);
        Wire(canvas, t9, t11);
        Wire(canvas, t1, t10);
        t6.Join = JoinPolicy.AnyCompleted;
        canvas.ShowProblems(new Dictionary<TaskId, IReadOnlyList<string>>
        {
            [t7.Id] = ["La tarea T7 (Aprobación humana) no admite reintentos: un reintento gastaría cuota o volvería a preguntar."],
        });
    }

    private static NodeViewModel Add(CanvasViewModel canvas, string id, WorkTaskKind kind, string title, double x, double y, WorkTaskStatus status)
    {
        var node = canvas.Add(new TaskId(id), kind, title, new Point(x, y));
        node.Status = status;
        return node;
    }

    private static void Wire(CanvasViewModel canvas, NodeViewModel from, NodeViewModel to, int output = 0) =>
        _ = canvas.Link(from.Outputs[output], to.Inputs[0]) ?? throw new InvalidOperationException($"The sample wire {from.Id} -> {to.Id} was refused.");
}

/// <summary>A run in progress for the monitor render: every kind of line the monitor shows, from a fake run.</summary>
internal static class MonitorSamples
{
    public static FakeRun RunInProgress()
    {
        static TaskView View(string id, string title, WorkTaskKind kind, WorkTaskStatus status, string? agent = null, string? blockReason = null, int attempt = 1, int retries = 0) =>
            new(new TaskId(id), title, kind, status, agent is null ? null : new AgentId(agent), attempt, retries, null, null, blockReason, []);

        var run = new FakeRun(new RunSnapshot(
            new RunId("20261016-c4a1f2"),
            RunPhase.Running,
            [
                View("T1", "Implementar Calc.Multiply", WorkTaskKind.AgentWork, WorkTaskStatus.Completed, "ejecutor", attempt: 2, retries: 1),
                View("T2", "Compilar y probar", WorkTaskKind.ToolRun, WorkTaskStatus.Completed),
                View("T3", "Integrar ramas", WorkTaskKind.Integration, WorkTaskStatus.Running),
                View("T4", "Revisión independiente", WorkTaskKind.Review, WorkTaskStatus.Pending, "juez"),
                View("T5", "Validar enlaces", WorkTaskKind.ToolRun, WorkTaskStatus.Blocked, blockReason: "RateLimitedUntil(2026-01-01T10:15:00+00:00)"),
                View("T6", "Aprobar entrega", WorkTaskKind.HumanApproval, WorkTaskStatus.AwaitingApproval),
            ],
            new GraphHealthReport([], [], [], false),
            AgentRunsUsed: 3));

        var tests = new EvidenceRecord(new EvidenceId("ev-1"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Pruebas de T1", "system", T0, HashA, Green);
        var diff = new EvidenceRecord(new EvidenceId("ev-2"), EvidenceKind.CodeDiff, EvidenceOrigin.CapturedBySystem, "Diff de T1", "system", T0, HashB);
        run.Emit(
            new RunPhaseChangedEvent(T0, RunPhase.Running, "3 tareas a la vez como máximo"),
            new AgentActivityEvent(T0.AddSeconds(5), new TaskId("T1"), new SessionStarted(T0, "s-1", "claude-opus")),
            new AgentActivityEvent(T0.AddSeconds(9), new TaskId("T1"), new ToolInvoked(T0, "Edit", "src/Calc/Calc.cs")),
            new EvidenceCapturedEvent(T0.AddSeconds(40), new TaskId("T1"), diff),
            new EvidenceCapturedEvent(T0.AddSeconds(52), new TaskId("T1"), tests),
            new AgentActivityEvent(T0.AddSeconds(70), new TaskId("T3"), new ToolInvoked(T0, "git", "merge-tree --write-tree agentstudio/run-20261016-c4a1f2 T1")),
            new TaskQueuedEvent(T0.AddSeconds(75), new TaskId("T4"), ProviderId.CodexCli),
            new ApprovalPendingEvent(T0.AddSeconds(80), new TaskId("T6"), new ApprovalRequest("Fusionar en main", "Entrega con --ff-only", RiskLevel.Critical, null, ["src/Calc/"])));
        return run;
    }
}
