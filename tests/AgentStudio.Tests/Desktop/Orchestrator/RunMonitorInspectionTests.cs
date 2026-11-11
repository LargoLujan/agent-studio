using System.IO;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The monitor shows a run read back from its log in read-only (B8): never active, no command, no <c>Finished</c>, and the
/// delivery of a run that succeeded asked as for a run followed live. Each task says why it is where it is, and a failed
/// verification step the main line of the error of its evidence (B7).
/// </summary>
public sealed class RunMonitorInspectionTests
{
    private static readonly RunId Run = new("20260101-abc123");
    private static readonly ProjectContext Project = new("p-1", @"C:\proyectos\calc");

    /// <summary>Evidence in memory: each record with its content, read back as the store reads it.</summary>
    internal sealed class MemoryEvidenceStore : IEvidenceStore
    {
        private readonly Dictionary<EvidenceId, byte[]> _content = [];
        private int _next;

        public List<EvidenceId> Opened { get; } = [];

        public EvidenceRecord Add(string title, EvidenceKind kind, string content, EvidenceOrigin origin = EvidenceOrigin.CapturedBySystem)
        {
            var id = new EvidenceId($"ev-{++_next:0000}");
            _content[id] = Encoding.UTF8.GetBytes(content);
            return new EvidenceRecord(id, kind, origin, title, "agent-studio:verification", T0, new string('a', 64), kind == EvidenceKind.TestRun ? new TestRunSummary(0, 1, 0, 1) : null);
        }

        public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken)
        {
            Opened.Add(id);
            return _content.TryGetValue(id, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : Task.FromException<Stream>(new KeyNotFoundException(id.Value));
        }

        public Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static RunMonitorViewModel NewMonitor(IDeliveryService? delivery = null, IEvidenceStore? evidence = null) =>
        new(new FakeTimeProvider(T0), new InlineSynchronizationContext(), delivery, evidence);

    private static TaskStatusChangedEvent Move(string task, WorkTaskStatus from, WorkTaskStatus to, string reason) => new(T0, Tid(task), from, to, reason);

    private static string StepLog(params string[] lines) =>
        string.Join("\n", ["# Agent Studio · verification step 'test' (Pruebas)", "# command: dotnet test", .. lines, "# exit code: 1", "# result: failed"]);

    [Fact]
    public async Task A_failed_verification_says_its_reason_and_the_main_line_of_the_error_of_the_step_that_failed()
    {
        var store = new MemoryEvidenceStore();
        var build = store.Add("Compilar (build)", EvidenceKind.BuildLog, StepLog("stdout| Build succeeded.", "stdout|     0 Error(s)"));
        var test = store.Add("Pruebas (test) · sin aislamiento", EvidenceKind.TestRun, StepLog("stdout| Restore complete.", "stdout| No test projects were found."));
        var monitor = NewMonitor(evidence: store);
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, Project);

        run.Emit(
            Move("T1", WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada."),
            new EvidenceCapturedEvent(T0, Tid("T1"), build),
            new EvidenceCapturedEvent(T0, Tid("T1"), test),
            Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Failed, "La verificación falló en: test"));
        await WaitForAsync(() => monitor.Tasks[0].FailureLine is not null);

        var task = monitor.Tasks[0];
        Assert.Equal("La verificación falló en: test", task.ReasonText);
        Assert.Equal("No test projects were found.", task.FailureLine);
        Assert.True(task.ShowsFailureLine);
        Assert.Equal([test.Id], store.Opened);

        // Tried again, the last failure is still said; once completed, nothing is.
        run.Emit(Move("T1", WorkTaskStatus.Failed, WorkTaskStatus.Pending, "Reintento automático."), Move("T1", WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada."));
        await WaitForAsync(() => monitor.Tasks[0].Status == WorkTaskStatus.Running);
        Assert.Equal("Intento anterior: La verificación falló en: test", task.ReasonText);
        Assert.True(task.ShowsFailureLine);

        run.Emit(Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Completed, "Completada: hecho."));
        run.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Hecho"));
        await monitor.Listening;
        Assert.Null(task.ReasonText);
        Assert.Null(task.FailureLine);
        Assert.False(task.ShowsFailureLine);
    }

    [Fact]
    public async Task Without_the_evidence_store_a_failure_says_its_reason_alone()
    {
        var monitor = NewMonitor();
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, Project);

        run.Emit(Move("T1", WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada."), Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Failed, "El paso lint falló."));
        run.Finish(new RunResult(Run, RunPhase.Failed, FakeRun.Healthy, null, null, "Falló."));
        await monitor.Listening;

        Assert.Equal("El paso lint falló.", monitor.Tasks[0].ReasonText);
        Assert.Null(monitor.Tasks[0].FailureLine);
    }

    [Fact]
    public async Task A_task_blocked_for_a_person_says_why_and_one_waiting_for_quota_leaves_it_to_its_status()
    {
        var monitor = NewMonitor();
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1"), FakeRun.View("T2")));
        monitor.Attach(run, Project);

        run.CurrentSnapshot = FakeRun.SnapshotOf(
            RunPhase.AwaitingHuman,
            FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "AwaitingHuman"),
            FakeRun.View("T2", status: WorkTaskStatus.Blocked, blockReason: "RateLimitedUntil(2026-01-01T10:15:00Z)"));
        run.Emit(
            Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "El agente necesita a una persona: ¿qué firma quieres?"),
            Move("T2", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "Límite de uso del proveedor hasta 2026-01-01T10:15:00.0000000+00:00."));
        run.Finish(new RunResult(Run, RunPhase.AwaitingHuman, FakeRun.Healthy, null, null, "Esperando."));
        await monitor.Listening;

        Assert.Equal("El agente necesita a una persona: ¿qué firma quieres?", monitor.Tasks[0].ReasonText);
        Assert.Null(monitor.Tasks[1].ReasonText);
        Assert.StartsWith("En pausa por el límite de uso hasta las", monitor.Tasks[1].StatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inspected_run_is_never_active_takes_no_command_raises_no_finished_and_is_delivered_as_a_live_one()
    {
        var delivery = new FakeDeliveryService();
        var monitor = NewMonitor(delivery);
        var evidence = new MemoryEvidenceStore().Add("Pruebas (test)", EvidenceKind.TestRun, StepLog("stdout| Passed!"));
        var run = new FakeRun(new RunSnapshot(Run, RunPhase.Succeeded, [FakeRun.View("T1", status: WorkTaskStatus.Completed) with { Evidence = [evidence.Id] }], FakeRun.Healthy, 1));
        run.Emit(
            Move("T1", WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada."),
            new EvidenceCapturedEvent(T0, Tid("T1"), evidence),
            Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Completed, "Completada: hecho."));
        run.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, "c0ffee0000000000000000000000000000000000", "veredicto-1", "Hecho."));
        var active = new List<bool>();
        monitor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunMonitorViewModel.IsActive))
            {
                active.Add(monitor.IsActive);
            }
        };
        var finished = 0;
        monitor.Finished += (_, _) => finished++;

        monitor.Inspect(run, Project);
        await monitor.Listening;
        await monitor.DeliveryCheck;

        Assert.True(monitor.IsInspecting);
        Assert.True(monitor.IsAttached);
        Assert.False(monitor.IsActive);
        Assert.DoesNotContain(true, active);
        Assert.Equal(0, finished);
        Assert.Equal("Completada", monitor.PhaseLabel);
        Assert.NotNull(monitor.InspectionText);
        Assert.Equal(1, monitor.Tasks[0].EvidenceCount);
        Assert.Single(monitor.Evidence);
        Assert.Contains(monitor.Activity, a => a.Text.Contains("abierta desde el historial", StringComparison.Ordinal));
        Assert.False(monitor.PauseCommand.CanExecute(null));
        Assert.False(monitor.CancelCommand.CanExecute(null));
        Assert.False(monitor.RetryBlockedCommand.CanExecute(monitor.Tasks[0]));

        // The same check and the same delivery as for a run followed live.
        Assert.Equal([(Project, Run)], delivery.Checks);
        Assert.True(monitor.DeliverCommand.CanExecute(null));
        await monitor.DeliverCommand.ExecuteAsync(null);
        Assert.Equal([(Project, Run, DeliveryMode.FastForwardOnly)], delivery.Deliveries);

        // «Cerrar» asks the document; putting it away leaves the monitor free.
        var closeAsked = 0;
        monitor.CloseRequested += (_, _) => closeAsked++;
        Assert.True(monitor.CloseInspectionCommand.CanExecute(null));
        monitor.CloseInspectionCommand.Execute(null);
        Assert.Equal(1, closeAsked);
        await monitor.DetachAsync();
        Assert.False(monitor.IsAttached);
        Assert.False(monitor.IsInspecting);
        Assert.Null(monitor.InspectionText);
    }

    /// <summary>A run that succeeded, as the inspector reads it, delivered once to <c>main</c> when <paramref name="delivered"/>.</summary>
    private static FakeRun Succeeded(bool delivered = false)
    {
        var run = new FakeRun(new RunSnapshot(Run, RunPhase.Succeeded, [FakeRun.View("T1", status: WorkTaskStatus.Completed)], FakeRun.Healthy, 1));
        run.Emit(Move("T1", WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada."), Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Completed, "Completada: hecho."));
        if (delivered)
        {
            run.Emit(new RunDeliveredEvent(T0.AddHours(1), "refs/heads/main", "c0ffee0000000000000000000000000000000000"));
        }

        run.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, "c0ffee0000000000000000000000000000000000", "veredicto-1", "Hecho."));
        return run;
    }

    [Fact]
    public async Task Looking_at_a_run_writes_nothing_and_delivering_it_audits_it_first_as_the_end_of_a_run_does()
    {
        var delivery = new FakeDeliveryService();
        var auditor = new AgentStudio.Tests.Desktop.Debate.FakeOrchestratorAuditService();
        var monitor = NewMonitor(delivery);
        monitor.Review = new RunReviewServices(Auditor: auditor);

        monitor.Inspect(Succeeded(), Project);
        await monitor.Listening;
        await monitor.DeliveryCheck;

        Assert.Empty(auditor.Audited);
        Assert.Null(monitor.AuditSummary);
        Assert.Contains("verla no ejecuta ni anota nada", monitor.InspectionText, StringComparison.Ordinal);

        await monitor.DeliverCommand.ExecuteAsync(null);

        Assert.Equal([Run], auditor.Audited);
        Assert.NotNull(monitor.AuditSummary);
        Assert.Equal([(Project, Run, DeliveryMode.FastForwardOnly)], delivery.Deliveries);

        // A live run is audited when it ends, as before.
        var live = NewMonitor(new FakeDeliveryService());
        live.Review = new RunReviewServices(Auditor: auditor);
        var running = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        live.Attach(running, Project);
        running.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Hecho."));
        await live.Listening;
        await live.AuditCheck;
        Assert.Equal([Run, Run], auditor.Audited);
    }

    [Fact]
    public async Task A_run_delivered_before_says_where_and_when_and_what_delivering_it_again_does()
    {
        var monitor = NewMonitor(new FakeDeliveryService());

        monitor.Inspect(Succeeded(delivered: true), Project);
        await monitor.Listening;

        Assert.Equal(
            "Ya se entregó a «main» (c0ffee0000) el "
            + TimeZoneInfo.ConvertTime(T0.AddHours(1), new FakeTimeProvider(T0).LocalTimeZone).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            + ". Si la entregas otra vez, la misma integración irá a la rama que tengas activa ahora.",
            monitor.PreviousDelivery);
        Assert.Contains(monitor.Activity, a => a.Text == "Entregada a main (c0ffee0000)");

        await monitor.DetachAsync();
        Assert.Null(monitor.PreviousDelivery);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The monitor did not apply the events in time.");
    }
}
