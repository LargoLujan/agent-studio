using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Debate;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The run monitor (docs/13 section 8, F3-3 and F3-6): it shows what the run's events say, applied on the injected UI
/// context, and it never turns an interrupted run or a wait for quota into a deadlock alert.
/// </summary>
public sealed class RunMonitorViewModelTests
{
    private static readonly GraphHealthReport Deadlocked = new([], [], [], IsDeadlocked: true);

    private static RunMonitorViewModel NewMonitor(SynchronizationContext? context = null) => new(new FakeTimeProvider(T0), context ?? new InlineSynchronizationContext());

    private static RunResult Result(RunPhase phase, string summary = "Terminada") => new(new("20260101-abc123"), phase, FakeRun.Healthy, null, null, summary);

    /// <summary>Attaches, emits, ends the run and waits until the monitor has read and applied everything (inline context).</summary>
    private static async Task<RunMonitorViewModel> FollowAsync(FakeRun run, params OrchestrationEvent[] events)
    {
        var monitor = NewMonitor();
        monitor.Attach(run);
        run.Emit(events);
        run.Finish(Result(RunPhase.Succeeded));
        await monitor.Listening;
        return monitor;
    }

    private static async Task<RunMonitorViewModel> FollowOpenAsync(FakeRun run, params OrchestrationEvent[] events)
    {
        var context = new QueueSynchronizationContext();
        var monitor = NewMonitor(context);
        monitor.Attach(run);
        run.Emit(events);
        await WaitUntilAsync(() => context.Pending >= events.Length);
        context.RunAll();
        return monitor;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The monitor did not read the events in time.");
    }

    [Fact]
    public async Task Attaching_shows_the_snapshot_at_once()
    {
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running), FakeRun.View("T2")));

        var monitor = await FollowOpenAsync(run);

        Assert.Equal(["T1", "T2"], monitor.Tasks.Select(t => t.Id.Value));
        Assert.Equal(["En curso", "Pendiente"], monitor.Tasks.Select(t => t.StatusLabel));
        Assert.Equal("En ejecución", monitor.PhaseLabel);
        Assert.True(monitor.IsActive);
    }

    [Fact]
    public async Task Events_are_applied_on_the_injected_context_in_their_order()
    {
        var context = new QueueSynchronizationContext();
        var monitor = NewMonitor(context);
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        var forwarded = new List<TaskStatusUpdate>();
        monitor.TaskStatusChanged += (_, update) => forwarded.Add(update);
        monitor.Attach(run);

        run.Emit(
            new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "started"),
            new TaskStatusChangedEvent(T0.AddMinutes(1), Tid("T1"), WorkTaskStatus.Running, WorkTaskStatus.Completed, "done"));
        run.Finish(Result(RunPhase.Succeeded, "Todo hecho"));
        await monitor.Listening;

        // Read, but not applied: only the UI context may change what the view binds to.
        Assert.Equal(WorkTaskStatus.Pending, monitor.Tasks[0].Status);
        Assert.True(context.Pending > 0);

        context.RunAll();

        Assert.Equal(WorkTaskStatus.Completed, monitor.Tasks[0].Status);
        Assert.Equal([new TaskStatusUpdate(Tid("T1"), WorkTaskStatus.Running), new TaskStatusUpdate(Tid("T1"), WorkTaskStatus.Completed)], forwarded);
        Assert.True(monitor.IsFinished);
        Assert.Equal("Completada", monitor.PhaseLabel);
        Assert.Equal("Todo hecho", monitor.ResultSummary);
    }

    /// <summary>
    /// V35, seen live: the engine ends a run with its phase and the summary as its detail, and then with its result and the
    /// same summary, so the monitor read «Terminó bien: 3 tareas completadas y 0 omitidas.» twice. It is shown once; a summary
    /// that says more than the detail is still shown under it.
    /// </summary>
    [Fact]
    public async Task The_summary_of_a_finished_run_is_shown_once_when_the_detail_of_its_phase_already_says_it()
    {
        const string Summary = "Terminó bien: 3 tareas completadas y 0 omitidas.";
        static async Task<RunMonitorViewModel> EndAsync(RunPhase phase, string detail, string summary)
        {
            var monitor = NewMonitor();
            var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
            monitor.Attach(run);
            var result = Result(phase, summary);
            run.Emit(new RunPhaseChangedEvent(T0, phase, detail), new RunFinishedEvent(T0, result));
            run.Finish(result);
            await monitor.Listening;
            return monitor;
        }

        var same = await EndAsync(RunPhase.Succeeded, Summary, Summary);
        var other = await EndAsync(RunPhase.Failed, "Falló la verificación.", "Falló: la política de pruebas exige pruebas capturadas por el sistema.");

        Assert.Equal(Summary, same.PhaseDetail);
        Assert.Equal(Summary, same.ResultSummary);
        Assert.Null(same.ResultSummaryText);
        Assert.Equal("Falló la verificación.", other.PhaseDetail);
        Assert.Equal("Falló: la política de pruebas exige pruebas capturadas por el sistema.", other.ResultSummaryText);
    }

    [Fact]
    public async Task An_interrupted_run_is_not_blocked_and_raises_no_deadlock_alert()
    {
        var interrupted = await FollowOpenAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "Interrupted"))),
            new GraphHealthEvent(T0, Deadlocked),
            new RunPhaseChangedEvent(T0, RunPhase.Interrupted, "La aplicación se cerró con la tarea en curso."));
        var blocked = await FollowOpenAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "ProviderUnavailable"))),
            new GraphHealthEvent(T0, Deadlocked),
            new RunPhaseChangedEvent(T0, RunPhase.Blocked, null));

        Assert.Equal("Interrumpida", interrupted.PhaseLabel);
        Assert.Equal(Tone.Warning, interrupted.PhaseTone);
        Assert.False(interrupted.ShowsDeadlockAlert);
        Assert.Equal("Interrumpida", interrupted.Tasks[0].StatusLabel);

        Assert.Equal("Bloqueada", blocked.PhaseLabel);
        Assert.Equal(Tone.Error, blocked.PhaseTone);
        Assert.True(blocked.ShowsDeadlockAlert);
        Assert.Equal("Bloqueada · el programa del agente no está disponible", blocked.Tasks[0].StatusLabel);
    }

    [Fact]
    public async Task Waiting_for_quota_is_not_a_deadlock()
    {
        var monitor = await FollowOpenAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "RateLimitedUntil(2026-01-01T10:15:00+00:00)"))),
            new GraphHealthEvent(T0, Deadlocked),
            new RunPhaseChangedEvent(T0, RunPhase.WaitingForQuota, null));

        Assert.Equal("En pausa por el límite de uso", monitor.PhaseLabel);
        Assert.False(monitor.ShowsDeadlockAlert);
        Assert.Equal("En pausa por el límite de uso hasta las 10:15", monitor.Tasks[0].StatusLabel);
    }

    [Fact]
    public async Task A_task_waiting_for_a_person_says_so()
    {
        var monitor = await FollowOpenAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "AwaitingHuman"))),
            new RunPhaseChangedEvent(T0, RunPhase.AwaitingHuman, null));

        Assert.Equal("Esperando a una persona", monitor.PhaseLabel);
        Assert.Equal("Bloqueada · espera a una persona", monitor.Tasks[0].StatusLabel);
        Assert.False(monitor.ShowsDeadlockAlert);
    }

    [Fact]
    public async Task A_queued_task_shows_En_cola_until_it_starts()
    {
        var context = new QueueSynchronizationContext();
        var monitor = NewMonitor(context);
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run);

        run.Emit(new TaskQueuedEvent(T0, Tid("T1"), ProviderId.ClaudeCode));
        await WaitUntilAsync(() => context.Pending == 1);
        context.RunAll();

        Assert.True(monitor.Tasks[0].IsQueued);
        Assert.Equal("En cola · claude-code", monitor.Tasks[0].StatusLabel);

        run.CurrentSnapshot = FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running));
        run.Emit(new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "started"));
        await WaitUntilAsync(() => context.Pending == 1);
        context.RunAll();

        Assert.False(monitor.Tasks[0].IsQueued);
        Assert.Equal("En curso", monitor.Tasks[0].StatusLabel);
    }

    [Fact]
    public async Task Agent_activity_and_evidence_are_listed_with_their_task()
    {
        var evidence = new EvidenceRecord(Eid("ev-1"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Pruebas de T1", "system", T0, HashA, Green);
        var monitor = await FollowAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running))),
            new AgentActivityEvent(T0, Tid("T1"), new ToolInvoked(T0, "Edit", "src/Calc/Calc.cs")),
            new AgentActivityEvent(T0, Tid("T1"), new MessageEmitted(T0, "parcial", IsPartial: true)),
            new AgentActivityEvent(T0, Tid("T1"), new UsageReported(T0, new TokenUsage(
                MetricValue<long>.Exact(1200, "claude result.usage"),
                MetricValue<long>.Exact(340, "claude result.usage"),
                MetricValue<long>.Unavailable("not reported"),
                MetricValue<long>.Unavailable("not reported"),
                MetricValue<decimal>.Unavailable("subscription")))),
            new EvidenceCapturedEvent(T0, Tid("T1"), evidence));

        Assert.Equal(["Usa Edit: src/Calc/Calc.cs", "Uso: 1200 tokens de entrada y 340 de salida"], monitor.Activity.Where(a => a.Task == "T1").Select(a => a.Text));
        Assert.Equal("Uso: 1200 tokens de entrada y 340 de salida", monitor.Tasks[0].LastActivity);
        var entry = Assert.Single(monitor.Evidence);
        Assert.Equal(("T1", "Pruebas de T1", "Pruebas", "capturada por el sistema", "aaaaaaaaaaaa", "10 correctas, 0 fallidas, 0 omitidas"),
            (entry.Task, entry.Title, entry.Kind, entry.Origin, entry.ShortHash, entry.Tests));
        Assert.Equal(1, monitor.Tasks[0].EvidenceCount);
    }

    [Fact]
    public async Task Health_lists_stalled_unreachable_and_exhausted_tasks()
    {
        var monitor = await FollowOpenAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1"), FakeRun.View("T2"), FakeRun.View("T3"))),
            new GraphHealthEvent(T0, new GraphHealthReport([Tid("T1")], [Tid("T2"), Tid("T3")], [Tid("T3")], false)));

        Assert.Equal(["Abandonadas: T1", "Inalcanzables: T2, T3", "Sin reintentos: T3"], monitor.HealthLines);
    }

    [Fact]
    public async Task A_pending_approval_is_shown_on_its_task()
    {
        var request = new ApprovalRequest("Fusionar en main", "Entrega del resultado", RiskLevel.Critical, null, ["src/"]);
        var monitor = await FollowOpenAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T5", WorkTaskKind.HumanApproval, WorkTaskStatus.AwaitingApproval))),
            new ApprovalPendingEvent(T0, Tid("T5"), request));

        Assert.Equal("Fusionar en main", monitor.Tasks[0].PendingApproval);
        Assert.Contains(monitor.Activity, a => a.Text == "Espera tu aprobación: Fusionar en main");
    }

    [Fact]
    public async Task Commands_reach_the_run_and_follow_its_phase()
    {
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "ProviderUnavailable"), FakeRun.View("T2")));
        var context = new QueueSynchronizationContext();
        var monitor = NewMonitor(context);
        monitor.Attach(run);

        Assert.True(monitor.PauseCommand.CanExecute(null));
        Assert.False(monitor.ResumeCommand.CanExecute(null));
        Assert.True(monitor.RetryBlockedCommand.CanExecute(monitor.Tasks[0]));
        Assert.False(monitor.RetryBlockedCommand.CanExecute(monitor.Tasks[1]));

        await monitor.PauseCommand.ExecuteAsync(null);
        await monitor.RetryBlockedCommand.ExecuteAsync(monitor.Tasks[0]);
        Assert.Equal(RunPhase.Running, monitor.Phase); // the run says when it is paused; the monitor does not guess

        run.Emit(new RunPhaseChangedEvent(T0, RunPhase.Paused, null));
        await WaitUntilAsync(() => context.Pending > 0);
        context.RunAll();
        Assert.False(monitor.PauseCommand.CanExecute(null));
        Assert.True(monitor.ResumeCommand.CanExecute(null));

        await monitor.ResumeCommand.ExecuteAsync(null);
        await monitor.CancelCommand.ExecuteAsync(null);
        run.Finish(Result(RunPhase.Cancelled));
        await monitor.Listening;
        context.RunAll();

        Assert.Equal(["pause", "retry:T1", "resume", "cancel:Cancelada por el usuario desde el monitor."], run.Calls);
        Assert.False(monitor.CancelCommand.CanExecute(null));
        Assert.False(monitor.ResumeCommand.CanExecute(null));
        Assert.Equal("Cancelada", monitor.PhaseLabel);
    }

    // ---- «Entregar a mi rama» --------------------------------------------------------------------------------------

    private static readonly ProjectContext Project = new("p-1", @"C:\proyectos\calc");

    private static RunResult Succeeded() => new(new("20260101-abc123"), RunPhase.Succeeded, FakeRun.Healthy, "c0ffee0000000000000000000000000000000000", "veredicto-1", "Hecho");

    /// <summary>A monitor with the delivery service, following a run until it ends with <paramref name="result"/> (and the delivery check is done).</summary>
    private static async Task<RunMonitorViewModel> FinishedWithDeliveryAsync(FakeDeliveryService delivery, RunResult result, ProjectContext? project = null)
    {
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext(), delivery);
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, project ?? Project);
        Assert.False(monitor.DeliverCommand.CanExecute(null));
        run.Finish(result);
        await monitor.Listening;
        await monitor.DeliveryCheck;
        return monitor;
    }

    [Fact]
    public async Task Delivering_is_offered_only_once_the_delivery_service_finds_the_finished_run_ready()
    {
        var delivery = new FakeDeliveryService();

        var monitor = await FinishedWithDeliveryAsync(delivery, Succeeded());

        Assert.Equal([(Project, new RunId("20260101-abc123"))], delivery.Checks);
        Assert.True(monitor.DeliverCommand.CanExecute(null));
        Assert.Equal("El juez aprobó la integración c0ffee0000.", monitor.DeliveryMessage);
        Assert.Empty(delivery.Deliveries);
    }

    [Fact]
    public async Task Delivering_is_not_offered_while_the_run_is_in_progress()
    {
        var delivery = new FakeDeliveryService();
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext(), delivery);

        monitor.Attach(new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1"))), Project);

        Assert.True(monitor.IsActive);
        Assert.False(monitor.DeliverCommand.CanExecute(null));
        Assert.Empty(delivery.Checks);
    }

    [Theory]
    [InlineData(RunPhase.Failed)]
    [InlineData(RunPhase.Cancelled)]
    [InlineData(RunPhase.Interrupted)]
    public async Task A_run_that_did_not_succeed_is_never_offered_for_delivery(RunPhase phase)
    {
        var delivery = new FakeDeliveryService();

        var monitor = await FinishedWithDeliveryAsync(delivery, Result(phase));

        Assert.False(monitor.DeliverCommand.CanExecute(null));
        Assert.Empty(delivery.Checks);
    }

    [Fact]
    public async Task A_succeeded_run_the_service_does_not_find_ready_is_not_offered_and_the_reason_is_shown()
    {
        var delivery = new FakeDeliveryService { Readiness = new(false, null, "El veredicto del juez es EscalateToHuman: solo se entrega lo que un juez aprobó.") };

        var monitor = await FinishedWithDeliveryAsync(delivery, Succeeded());

        Assert.False(monitor.DeliverCommand.CanExecute(null));
        Assert.Equal("El veredicto del juez es EscalateToHuman: solo se entrega lo que un juez aprobó.", monitor.DeliveryMessage);
    }

    [Fact]
    public async Task Delivering_asks_the_service_for_the_runs_project_with_fast_forward_and_once_delivered_is_not_offered_again()
    {
        var delivery = new FakeDeliveryService();
        var monitor = await FinishedWithDeliveryAsync(delivery, Succeeded());

        await monitor.DeliverCommand.ExecuteAsync(null);

        Assert.Equal([(Project, new RunId("20260101-abc123"), DeliveryMode.FastForwardOnly)], delivery.Deliveries);
        Assert.Equal("Entregado: main está en c0ffee0000.", monitor.DeliveryMessage);
        Assert.True(monitor.IsDelivered);
        Assert.False(monitor.DeliverCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refused_delivery_says_why_and_can_be_asked_again()
    {
        var delivery = new FakeDeliveryService { Result = () => new(false, null, "main", "Entrega denegada por user: main no se ha tocado.") };
        var monitor = await FinishedWithDeliveryAsync(delivery, Succeeded());

        await monitor.DeliverCommand.ExecuteAsync(null);

        Assert.Equal("Entrega denegada por user: main no se ha tocado.", monitor.DeliveryMessage);
        Assert.False(monitor.IsDelivered);
        Assert.True(monitor.DeliverCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_delivery_that_fails_is_shown_and_not_thrown()
    {
        var delivery = new FakeDeliveryService { Result = () => throw new InvalidOperationException("git no está.") };
        var monitor = await FinishedWithDeliveryAsync(delivery, Succeeded());

        await monitor.DeliverCommand.ExecuteAsync(null);

        Assert.Equal("No se pudo entregar: git no está.", monitor.ErrorMessage);
        Assert.False(monitor.IsDelivered);
        Assert.True(monitor.DeliverCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_a_delivery_service_or_a_project_delivering_is_never_offered()
    {
        var withoutService = NewMonitor();
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        withoutService.Attach(run);
        run.Finish(Succeeded());
        await withoutService.Listening;
        await withoutService.DeliveryCheck;

        var delivery = new FakeDeliveryService();
        var withoutProject = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext(), delivery);
        var other = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        withoutProject.Attach(other);
        other.Finish(Succeeded());
        await withoutProject.Listening;
        await withoutProject.DeliveryCheck;

        Assert.False(withoutService.DeliverCommand.CanExecute(null));
        Assert.False(withoutProject.DeliverCommand.CanExecute(null));
        Assert.Empty(delivery.Checks);
    }

    [Fact]
    public async Task Detaching_forgets_what_was_found_about_delivering()
    {
        var delivery = new FakeDeliveryService();
        var monitor = await FinishedWithDeliveryAsync(delivery, Succeeded());

        await monitor.DetachAsync();

        Assert.False(monitor.DeliverCommand.CanExecute(null));
        Assert.Null(monitor.DeliveryMessage);
        Assert.False(monitor.IsDelivered);
    }

    // ---- Context health of each task (docs/14 D-09 to D-12) --------------------------------------------------------

    private static ContextObserved Context(long? used, long? window, MetricOrigin windowOrigin = MetricOrigin.ReportedByProvider, string? model = "claude-sonnet-5-5") =>
        new(
            T0,
            model,
            used is { } tokens ? MetricValue<long>.Exact(tokens, "claude assistant.usage") : MetricValue<long>.NotExposed("codex exec does not report it"),
            window is { } size ? MetricValue<long>.Of(windowOrigin, size, "claude modelUsage.contextWindow") : MetricValue<long>.Missing("no window known yet"));

    private static FakeRun RunningAgentTask() => new(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running)));

    [Fact]
    public async Task A_running_task_shows_its_context_with_the_percentage_its_origin_tokens_window_model_and_level()
    {
        var monitor = await FollowOpenAsync(RunningAgentTask(), new AgentActivityEvent(T0, Tid("T1"), Context(30_822, 1_000_000)));

        var task = monitor.Tasks[0];
        Assert.Equal("Contexto: 3 % · exacto · informado por el CLI · 30.822 de 1.000.000 tokens · claude-sonnet-5-5 · sano", task.Context);
        Assert.Equal(ContextHealthLevel.Healthy, task.ContextLevel);
        Assert.Equal(Tone.Success, task.ContextTone);

        // A step is not activity worth a line of its own: the task's context line says it.
        Assert.DoesNotContain(monitor.Activity, a => a.Text.Contains("Contexto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_window_seen_in_an_earlier_run_is_shown_as_an_estimate_never_as_exact()
    {
        var monitor = await FollowOpenAsync(RunningAgentTask(), new AgentActivityEvent(T0, Tid("T1"), Context(140_000, 192_000, MetricOrigin.EstimatedBySystem)));

        var context = monitor.Tasks[0].Context;
        Assert.Equal("Contexto: ~73 % · estimado por Agent Studio · 140.000 de ~192.000 tokens · claude-sonnet-5-5 · aviso", context);
        Assert.DoesNotContain("exacto", context, StringComparison.Ordinal);
        Assert.Equal(Tone.Warning, monitor.Tasks[0].ContextTone);
    }

    [Fact]
    public async Task Without_a_window_the_occupancy_goes_in_tokens_and_is_not_judged()
    {
        // A model neither the log of the project nor the base table knows: only its tokens (docs/14 D-10).
        var monitor = await FollowOpenAsync(RunningAgentTask(), new AgentActivityEvent(T0, Tid("T1"), Context(30_822, null, model: "modelo-desconocido")));

        Assert.Equal("Contexto: 30.822 tokens · exacto · informado por el CLI · sin porcentaje: la ventana no se conoce todavía · modelo-desconocido · sin evaluar", monitor.Tasks[0].Context);
        Assert.Equal(Tone.Neutral, monitor.Tasks[0].ContextTone);
        Assert.False(monitor.Tasks[0].ContextBar!.HasValue);
        Assert.Equal("30.822 tokens", monitor.Tasks[0].ContextBar!.ValueText);
    }

    [Fact]
    public async Task With_the_base_table_the_first_step_of_a_new_project_already_has_an_estimated_percentage()
    {
        // P36: Claude reports the window only with the result; until then the base table gives it, as an estimate.
        var monitor = await FollowOpenAsync(RunningAgentTask(), new AgentActivityEvent(T0, Tid("T1"), Context(470_000, null)));

        Assert.Equal("Contexto: ~47 % · estimado por Agent Studio · 470.000 de ~1.000.000 tokens · claude-sonnet-5-5 · sano", monitor.Tasks[0].Context);
        var bar = monitor.Tasks[0].ContextBar!;
        Assert.True(bar.IsEstimate);
        Assert.Equal(47, bar.Value!.Value, 6);
        Assert.Contains("tabla base", bar.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_simulated_stream_raises_the_live_percentage_during_the_attempt_and_the_result_makes_it_exact()
    {
        // P36: the tracker of the attempt follows the stream step by step; nothing waits for the end of the attempt.
        var context = new QueueSynchronizationContext();
        var monitor = NewMonitor(context);
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run);
        var percentages = new List<double?>();

        async Task StepAsync(params OrchestrationEvent[] events)
        {
            var before = context.Pending;
            run.Emit(events);
            await WaitUntilAsync(() => context.Pending >= before + events.Length);
            context.RunAll();
            percentages.Add(monitor.Tasks[0].ContextBar?.Value);
        }

        await StepAsync(new TaskQueuedEvent(T0, Tid("T1"), ProviderId.ClaudeCode), new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada"));
        Assert.Equal(MetricBars.NoObservationYet, monitor.Tasks[0].ContextBar!.ValueText);

        await StepAsync(new AgentActivityEvent(T0, Tid("T1"), Context(100_000, null)));
        await StepAsync(new AgentActivityEvent(T0, Tid("T1"), Context(300_000, null)));
        await StepAsync(new AgentActivityEvent(T0, Tid("T1"), Context(720_000, null)));

        Assert.Equal([null, 10, 30, 72], percentages.Select(p => p is { } v ? Math.Round(v) : (double?)null));
        Assert.True(monitor.Tasks[0].ContextBar!.IsEstimate);
        Assert.Equal(ContextHealthLevel.Warning, monitor.Tasks[0].ContextLevel);

        var usage = new TokenUsage(
            MetricValue<long>.Exact(1_000, "claude result.usage"),
            MetricValue<long>.Exact(50, "claude result.usage"),
            MetricValue<long>.Exact(0, "claude result.usage"),
            MetricValue<long>.NotExposed("per model"),
            MetricValue<decimal>.Estimated(0.01m, "claude total_cost_usd"))
        {
            PerModel = [new ModelUsage("claude-sonnet-5-5", MetricValue<long>.Exact(1, "m"), MetricValue<long>.Exact(1, "m"), MetricValue<long>.Exact(0, "m"), MetricValue<long>.Exact(0, "m"), MetricValue<decimal>.Estimated(0m, "m"), MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), "list")],
        };
        await StepAsync(new AgentActivityEvent(T0, Tid("T1"), new UsageReported(T0, usage)));

        var exact = monitor.Tasks[0].ContextBar!;
        Assert.False(exact.IsEstimate);
        Assert.Equal("exacto · informado por el CLI", exact.Origin);
        Assert.Equal("Tokens del último intento: 1.000 de entrada y 50 de salida · exacto · informado por el CLI", monitor.Tasks[0].TokensText);
    }

    [Fact]
    public async Task A_judge_on_codex_is_not_exposed_from_the_start_of_its_review()
    {
        // The engine queues a review on the provider of its judge before it runs it, as it queues agent work
        // (ApprovalReviewAndIntegrationTests.The_review_is_queued_on_the_provider_of_its_judge_before_it_runs).
        var monitor = await FollowOpenAsync(
            new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", WorkTaskKind.Review))),
            new TaskQueuedEvent(T0, Tid("T1"), ProviderId.CodexCli),
            new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Arranca el intento 1."));

        Assert.Equal("Contexto: no expuesto", monitor.Tasks[0].Context);
        Assert.Equal("no expuesto", monitor.Tasks[0].ContextBar!.ValueText);
        Assert.False(monitor.Tasks[0].ContextBar!.HasValue);
    }

    [Fact]
    public async Task A_window_the_project_learned_wins_over_the_base_table_for_the_next_attempt()
    {
        var context = new QueueSynchronizationContext();
        var monitor = NewMonitor(context);
        monitor.ContextWindows = global::AgentStudio.Tests.Observability.Context.ModelWindowCatalogTestData.Learned(("claude-sonnet-5-5", 400_000));
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run);
        OrchestrationEvent[] events =
        [
            new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada"),
            new AgentActivityEvent(T0, Tid("T1"), Context(300_000, null)),
        ];
        run.Emit(events);
        await WaitUntilAsync(() => context.Pending >= events.Length);
        context.RunAll();

        Assert.Equal(75, monitor.Tasks[0].ContextBar!.Value!.Value, 6);
        Assert.Contains("ventana vista en una ejecución anterior", monitor.Tasks[0].ContextBar!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_budget_of_the_run_is_a_bar_of_the_agent_runs_used_against_its_maximum()
    {
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")) with { AgentRunsUsed = 3, MaxAgentRuns = 12 });

        var monitor = await FollowOpenAsync(run);

        Assert.Equal("3 de 12", monitor.BudgetBar!.ValueText);
        Assert.Equal(0.25, monitor.BudgetBar!.Fraction!.Value, 6);
        Assert.Equal("exacto · medido por Agent Studio", monitor.BudgetBar!.Origin);
    }

    [Fact]
    public async Task What_codex_exec_does_not_expose_is_said_as_such()
    {
        var monitor = await FollowOpenAsync(RunningAgentTask(), new AgentActivityEvent(T0, Tid("T1"), Context(null, null, model: null) with { ContextWindow = MetricValue<long>.NotExposed("codex exec") }));

        Assert.Equal("Contexto: no expuesto", monitor.Tasks[0].Context);
    }

    [Fact]
    public async Task An_agent_task_that_started_says_there_is_no_observation_yet_and_one_that_did_not_start_says_nothing()
    {
        var monitor = await FollowOpenAsync(new FakeRun(FakeRun.SnapshotOf(
            RunPhase.Running,
            FakeRun.View("T1", status: WorkTaskStatus.Running),
            FakeRun.View("T2"),
            FakeRun.View("T3", WorkTaskKind.ToolRun, WorkTaskStatus.Running))));

        Assert.Equal("Contexto: sin observaciones todavía", monitor.Tasks[0].Context);
        Assert.Null(monitor.Tasks[1].Context);
        Assert.Null(monitor.Tasks[2].Context);
    }

    [Fact]
    public async Task The_thresholds_of_the_settings_decide_the_level_and_a_change_applies_to_what_is_shown()
    {
        var monitor = await FollowOpenAsync(RunningAgentTask(), new AgentActivityEvent(T0, Tid("T1"), Context(55, 100)));
        Assert.Equal(ContextHealthLevel.Healthy, monitor.Tasks[0].ContextLevel);

        monitor.ContextThresholds = new ContextHealthThresholds(0.50, 0.60, 0.90);

        Assert.Equal(ContextHealthLevel.Warning, monitor.Tasks[0].ContextLevel);
        Assert.EndsWith("· aviso", monitor.Tasks[0].Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_latest_step_is_the_occupancy_shown()
    {
        var monitor = await FollowOpenAsync(
            RunningAgentTask(),
            new AgentActivityEvent(T0, Tid("T1"), Context(100_000, 1_000_000)),
            new AgentActivityEvent(T0, Tid("T1"), Context(960_000, 1_000_000)));

        Assert.Equal(ContextHealthLevel.Critical, monitor.Tasks[0].ContextLevel);
        Assert.Equal(Tone.Error, monitor.Tasks[0].ContextTone);
    }

    [Fact]
    public async Task A_compaction_is_counted_on_its_task_and_listed_as_a_possible_loss()
    {
        var monitor = await FollowOpenAsync(
            RunningAgentTask(),
            new AgentActivityEvent(T0, Tid("T1"), Context(180_000, 1_000_000)),
            new AgentActivityEvent(T0, Tid("T1"), new CompactionObserved(T0, "auto", MetricValue<long>.Exact(180_000, "claude compact_boundary.pre_tokens"))),
            new AgentActivityEvent(T0, Tid("T1"), Context(20_000, 1_000_000)));

        Assert.Equal("Contexto: 2 % · exacto · informado por el CLI · 20.000 de 1.000.000 tokens · claude-sonnet-5-5 · sano · 1 compactación del contexto (posible pérdida de información)", monitor.Tasks[0].Context);
        Assert.Contains(monitor.Activity, a => a.Text == "El programa del agente compactó el contexto (auto): 180.000 tokens antes · exacto · informado por el CLI");
    }

    [Fact]
    public async Task A_run_that_fails_is_shown_and_not_thrown()
    {
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        var monitor = NewMonitor();
        monitor.Attach(run);

        run.Fail(new InvalidOperationException("The engine stopped."));
        await monitor.Listening;

        Assert.True(monitor.IsFinished);
        Assert.Equal("La ejecución terminó con un error: The engine stopped.", monitor.ErrorMessage);
        Assert.False(monitor.CancelCommand.CanExecute(null));
    }

    // ---- The review of a finished run (docs/15 D-33 to D-35, D-40 and D-43) ---------------------------------------

    private static readonly OrchestratorFinding OmittedReview = new("orchestrator.review-omitted", AgentStudio.Domain.Judging.Severity.High, [Tid("T3")], [12], "La tarea T3 exigía revisión independiente y no la tuvo.");

    private static readonly OrchestratorFinding Incomplete = new("orchestrator.instructions", AgentStudio.Domain.Judging.Severity.Medium, [Tid("T2")], [], "Las instrucciones de T2 no citan el requisito R2.");

    private static OrchestratorAuditReport AuditOf(RunId run, params OrchestratorFinding[] findings) =>
        new(run, DecisionLogVerification.Valid, findings, findings.Length == 0 ? [] : [40, 41], ReplanRequested: findings.Any(f => f.Severity >= AgentStudio.Domain.Judging.Severity.High));

    /// <summary>A monitor with the delivery and the review, following a run until it ends with <paramref name="result"/> and every check is applied.</summary>
    private static async Task<RunMonitorViewModel> ReviewedAsync(FakeDeliveryService delivery, RunReviewServices review, RunResult? result = null)
    {
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext(), delivery) { Review = review };
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, Project);
        run.Finish(result ?? Succeeded());
        await monitor.Listening;
        await monitor.AuditCheck;
        await monitor.DeliveryCheck;
        await monitor.ReviewCheck;
        return monitor;
    }

    [Fact]
    public async Task The_auditor_checks_a_finished_run_before_the_delivery_is_asked_and_lists_its_findings()
    {
        var delivery = new FakeDeliveryService();
        var checksWhenAudited = -1;
        var auditor = new FakeOrchestratorAuditService
        {
            Report = run =>
            {
                checksWhenAudited = delivery.Checks.Count;
                return AuditOf(run, OmittedReview, Incomplete);
            },
        };

        var monitor = await ReviewedAsync(delivery, new RunReviewServices(auditor));

        Assert.Equal([new RunId("20260101-abc123")], auditor.Audited);
        Assert.Equal(0, checksWhenAudited);
        Assert.Single(delivery.Checks);
        Assert.Equal("Auditor del orquestador: 2 hallazgos, 1 de gravedad alta o crítica (2 alertas nuevas en el historial).", monitor.AuditSummary);
        Assert.Equal(Tone.Error, monitor.AuditTone);
        Assert.Equal(
            [
                new AuditFindingRow("Alta", Tone.Error, "Revisión obligatoria omitida", "orchestrator.review-omitted", "tareas T3 · entradas #12", "La tarea T3 exigía revisión independiente y no la tuvo."),
                new AuditFindingRow("Media", Tone.Warning, "Instrucciones completas", "orchestrator.instructions", "tareas T2", "Las instrucciones de T2 no citan el requisito R2."),
            ],
            monitor.AuditFindings);
        Assert.True(monitor.ReplanPending);
        Assert.Equal("El auditor pide planificar de nuevo. Prepara una ejecución de seguimiento o descarta la petición. Hasta que decidas, la entrega espera.", monitor.ReplanText);
        Assert.True(monitor.HasReviewSection);
    }

    [Fact]
    public async Task A_clean_run_has_no_findings_and_nothing_to_replan()
    {
        var monitor = await ReviewedAsync(new FakeDeliveryService(), new RunReviewServices(new FakeOrchestratorAuditService()));

        Assert.Equal("El auditor del orquestador no halló nada en el historial verificado de la ejecución.", monitor.AuditSummary);
        Assert.Equal(Tone.Success, monitor.AuditTone);
        Assert.Empty(monitor.AuditFindings);
        Assert.False(monitor.ReplanPending);
        Assert.Null(monitor.ReplanText);
        Assert.False(monitor.DismissReplanCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_log_that_does_not_verify_is_the_report_and_nothing_is_asked_to_replan()
    {
        var auditor = new FakeOrchestratorAuditService { Report = run => new(run, new DecisionLogVerification(false, 7, "hash roto"), [], [], false) };

        var monitor = await ReviewedAsync(new FakeDeliveryService(), new RunReviewServices(auditor));

        Assert.Equal("El historial de la ejecución no se pudo verificar desde la entrada 7: hash roto. El auditor no escribió nada.", monitor.AuditSummary);
        Assert.Equal(Tone.Error, monitor.AuditTone);
        Assert.False(monitor.ReplanPending);
    }

    [Fact]
    public async Task Dismissing_the_request_to_replan_asks_the_gate_in_the_run_and_asks_the_delivery_again()
    {
        var delivery = new FakeDeliveryService();
        var gate = new FakeApprovalGate();
        var auditor = new FakeOrchestratorAuditService { Report = run => AuditOf(run, OmittedReview) };
        var monitor = await ReviewedAsync(delivery, new RunReviewServices(auditor, Approvals: gate));

        await monitor.DismissReplanCommand.ExecuteAsync(null);
        await monitor.DeliveryCheck;

        var request = Assert.Single(gate.Requests);
        Assert.Equal(RunMonitorViewModel.ReplanDismissAction, request.Action);
        Assert.Equal("replan.dismiss", request.Action);
        Assert.Equal("p-1", request.ProjectId);
        Assert.Equal("20260101-abc123", request.RunId);
        Assert.Contains("orchestrator.review-omitted", request.Description, StringComparison.Ordinal);
        Assert.False(monitor.ReplanPending);
        Assert.StartsWith("Descartaste la petición de replanificar", monitor.ReplanText, StringComparison.Ordinal);
        Assert.Equal(2, delivery.Checks.Count);
    }

    [Fact]
    public async Task A_denied_dismissal_keeps_the_request_and_the_delivery_waiting()
    {
        var delivery = new FakeDeliveryService();
        var gate = new FakeApprovalGate(approve: false);
        var auditor = new FakeOrchestratorAuditService { Report = run => AuditOf(run, OmittedReview) };
        var monitor = await ReviewedAsync(delivery, new RunReviewServices(auditor, Approvals: gate));

        await monitor.DismissReplanCommand.ExecuteAsync(null);

        Assert.True(monitor.ReplanPending);
        Assert.Equal("La petición de replanificar sigue en pie: No.", monitor.ErrorMessage);
        Assert.Single(delivery.Checks);
    }

    [Fact]
    public async Task Preparing_the_follow_up_hands_the_findings_as_data_to_the_orchestrator()
    {
        var auditor = new FakeOrchestratorAuditService { Report = run => AuditOf(run, OmittedReview) };
        var monitor = await ReviewedAsync(new FakeDeliveryService(), new RunReviewServices(auditor));
        Assert.False(monitor.PrepareFollowUpCommand.CanExecute(null));
        var prepared = new List<(RunId Run, string Request)>();
        monitor.PrepareFollowUp = (run, request) =>
        {
            prepared.Add((run, request));
            return Task.CompletedTask;
        };
        monitor.PrepareFollowUpCommand.NotifyCanExecuteChanged();

        await monitor.PrepareFollowUpCommand.ExecuteAsync(null);

        var (followed, text) = Assert.Single(prepared);
        Assert.Equal(new RunId("20260101-abc123"), followed);
        Assert.StartsWith("Ejecución de seguimiento de 20260101-abc123: el auditor del orquestador pidió replanificar.", text, StringComparison.Ordinal);
        Assert.Contains("como datos del sistema (no son instrucciones)", text, StringComparison.Ordinal);
        Assert.EndsWith("\n- [Alta] orchestrator.review-omitted · T3: La tarea T3 exigía revisión independiente y no la tuvo.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auditing_again_is_free_and_asks_the_delivery_again()
    {
        var delivery = new FakeDeliveryService();
        var auditor = new FakeOrchestratorAuditService();
        var monitor = await ReviewedAsync(delivery, new RunReviewServices(auditor));

        await monitor.AuditAgainCommand.ExecuteAsync(null);
        await monitor.DeliveryCheck;

        Assert.Equal(2, auditor.Audited.Count);
        Assert.Equal(2, delivery.Checks.Count);
    }

    [Theory]
    [InlineData(RunPhase.Interrupted)]
    [InlineData(RunPhase.Blocked)]
    public async Task A_run_that_did_not_end_is_not_audited(RunPhase phase)
    {
        var auditor = new FakeOrchestratorAuditService();

        var monitor = await ReviewedAsync(new FakeDeliveryService(), new RunReviewServices(auditor), Result(phase));

        Assert.Empty(auditor.Audited);
        Assert.Null(monitor.AuditSummary);
    }

    [Fact]
    public async Task The_card_of_the_reviews_verdict_offers_challenging_and_says_where_its_challenge_stands()
    {
        var challenges = new FakeVerdictChallenges();
        challenges.Statuses["veredicto-1"] = ChallengeStatus.Confirmed;
        var verdicts = new FakeVerdictStore(DebateSamples.Stored("veredicto-1", DebateSamples.VerdictOf(DebateSamples.CodexJudge, AgentStudio.Domain.Judging.VerdictDecision.Approved, null) with { Resolution = null }, run: "20260101-abc123"));
        var challenged = new List<(ProjectContext Project, string Verdict)>();
        var review = new RunReviewServices(
            Verdicts: verdicts,
            Challenges: challenges,
            Challenge: (project, verdict) =>
            {
                challenged.Add((project, verdict));
                return Task.CompletedTask;
            });

        var monitor = await ReviewedAsync(new FakeDeliveryService(), review);

        var card = Assert.IsType<AgentStudio.Desktop.Debate.VerdictCardViewModel>(monitor.ReviewCard);
        Assert.Equal("Veredicto de la revisión · 20260101-abc123", card.Title);
        Assert.Equal("Impugnado: la revisión confirmó lo que decía el veredicto.", monitor.ChallengeText);
        Assert.True(card.Verdicts[0].CanBeChallenged);
        await card.Verdicts[0].ChallengeCommand.ExecuteAsync(null);
        Assert.Equal([(Project, "veredicto-1")], challenged);
        Assert.Null(monitor.AuditSummary);
        Assert.True(monitor.HasReviewSection);
    }

    [Fact]
    public async Task A_verdict_whose_challenge_is_in_course_is_not_offered_for_challenge_again()
    {
        var challenges = new FakeVerdictChallenges();
        challenges.Statuses["veredicto-1"] = ChallengeStatus.Pending;
        var verdicts = new FakeVerdictStore(DebateSamples.Stored("veredicto-1", DebateSamples.VerdictOf(DebateSamples.CodexJudge, AgentStudio.Domain.Judging.VerdictDecision.Approved, null), run: "20260101-abc123"));

        var monitor = await ReviewedAsync(new FakeDeliveryService(), new RunReviewServices(Verdicts: verdicts, Challenges: challenges, Challenge: (_, _) => Task.CompletedTask));

        Assert.False(monitor.ReviewCard!.Verdicts[0].CanBeChallenged);
        Assert.Equal("Impugnado: la revisión no se ha cerrado y la entrega espera.", monitor.ChallengeText);
        Assert.Equal(Tone.Warning, monitor.ChallengeTone);
    }

    [Fact]
    public async Task A_revision_that_turned_towards_an_approval_is_accepted_here_and_the_delivery_asked_again()
    {
        var delivery = new FakeDeliveryService();
        var challenges = new FakeVerdictChallenges();
        challenges.Statuses["veredicto-1"] = ChallengeStatus.AwaitingAcceptance;
        var original = DebateSamples.Stored("veredicto-1", DebateSamples.VerdictOf(DebateSamples.CodexJudge, AgentStudio.Domain.Judging.VerdictDecision.Rejected, null), run: "20260101-abc123");
        var revision = DebateSamples.Stored("veredicto-2", DebateSamples.VerdictOf(DebateSamples.ClaudeJudge, AgentStudio.Domain.Judging.VerdictDecision.Approved, null), run: "20260101-abc123", supersedes: "veredicto-1");
        var monitor = await ReviewedAsync(delivery, new RunReviewServices(Verdicts: new FakeVerdictStore(original, revision), Challenges: challenges));
        Assert.Equal("veredicto-2", monitor.RevisionVerdictId);
        Assert.True(monitor.AcceptRevisionCommand.CanExecute(null));

        challenges.Statuses["veredicto-1"] = ChallengeStatus.Accepted;
        await monitor.AcceptRevisionCommand.ExecuteAsync(null);

        Assert.Equal(["veredicto-2"], challenges.Accepted);
        Assert.Equal("Impugnado: aceptaste la revisión que da la vuelta hacia una aprobación.", monitor.ChallengeText);
        Assert.Null(monitor.RevisionVerdictId);
        Assert.Equal(2, delivery.Checks.Count);
    }

    [Fact]
    public async Task Without_the_review_services_nothing_of_the_review_is_shown()
    {
        var delivery = new FakeDeliveryService();
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext(), delivery);
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, Project);
        run.Finish(Succeeded());
        await monitor.Listening;
        await monitor.DeliveryCheck;

        Assert.False(monitor.HasReviewSection);
        Assert.Null(monitor.ReviewCard);
        Assert.Empty(monitor.AuditFindings);
        Assert.False(monitor.AuditAgainCommand.CanExecute(null));
        Assert.False(monitor.DismissReplanCommand.CanExecute(null));
        Assert.False(monitor.AcceptRevisionCommand.CanExecute(null));
        Assert.Single(delivery.Checks);
    }
}
