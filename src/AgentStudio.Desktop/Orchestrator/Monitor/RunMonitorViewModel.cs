using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>A task changed status in the run being followed (the canvas recolours its node).</summary>
public sealed record TaskStatusUpdate(TaskId Task, WorkTaskStatus Status);

/// <summary>
/// What the monitor needs to show the review of a finished run (docs/15 D-43, D-35 and D-40), each piece only when it is
/// composed: the deterministic auditor of the orchestrator, the stored verdicts (the card of the review), the challenges
/// (where the challenge of that verdict stands, and accepting a revision that turned towards an approval), the approval
/// gate («Descartar la petición», <c>replan.dismiss</c>), the frozen requirements (the card names each one), and the moves
/// that leave the monitor: «Impugnar» (the challenge in «Debates») and «Crear tareas correctivas» (the orchestrator).
/// </summary>
public sealed record RunReviewServices(
    IOrchestratorAuditService? Auditor = null,
    IVerdictStore? Verdicts = null,
    IVerdictChallenges? Challenges = null,
    IApprovalGate? Approvals = null,
    IRequirementStore? Requirements = null,
    Func<ProjectContext, string, Task>? Challenge = null,
    Func<ProjectContext, string, Task>? CorrectiveTasks = null);

/// <summary>A finding of the auditor of the orchestrator, as the monitor lists it.</summary>
public sealed record AuditFindingRow(string Severity, Tone Tone, string Check, string Code, string Where, string Message);

/// <summary>
/// Follows one <see cref="IOrchestrationRun"/>: phase, tasks, queue, agent activity, evidence and graph health. The
/// events are read on a background task and applied on the UI context given to the constructor (the pattern of
/// <c>TerminalViewModel</c>), in the order they come. Nothing is shown that the run did not say, and the commands only
/// ask the run: a pause is shown when the run reports it.
/// </summary>
/// <remarks>
/// F3-3: an interrupted run, a run waiting for quota and a run waiting for a person all make
/// <see cref="TaskGraph.Analyze"/> report a deadlock (blocked tasks do not progress), but none of them is one. The
/// deadlock alert is shown only when the engine classified the run as <see cref="RunPhase.Blocked"/>.
/// <para>
/// Once a run has finished <see cref="RunPhase.Succeeded"/>, the delivery service (when there is one) is asked whether
/// it can be delivered, and «Entregar a mi rama» is offered only when it says so; why it cannot is shown instead.
/// </para>
/// <para>
/// Each task that runs an agent shows the context occupancy of its latest step (docs/14 D-12), from the
/// <see cref="ContextObserved"/> events of its agent, judged with <see cref="ContextThresholds"/>, and the compactions of
/// the CLI. The origin of the figures goes with them: an estimate is never shown as exact.
/// </para>
/// <para>
/// Phase 5 (docs/15 D-43): once a run has finished, the deterministic auditor of the orchestrator (<see cref="Review"/>)
/// checks its log before the delivery is asked, because a request to replan blocks the delivery until you prepare a
/// follow-up run or dismiss it (D-35). The card of the review's verdict offers «Impugnar», and where its challenge stands
/// is shown; a revision that turned towards an approval only counts once you accept it here (D-40). Every section is
/// hidden while its port or its result is missing.
/// </para>
/// <para>
/// A run read back from its log (<see cref="Inspect"/>, <see cref="IRunInspector"/>) is shown the same way, in read-only:
/// it is never active, it takes no command, and its end raises no <see cref="Finished"/> (nothing ended now); the review
/// of a run that succeeded, and «Entregar a mi rama», are asked exactly as for a run followed live.
/// </para>
/// <para>
/// Each task says the reason of its last transition when it explains it (failed, blocked, waiting for a person, cancelled
/// or skipped), and the reason of its last failure while it is tried again. When a verification step failed, the main error
/// line of that step's evidence (<see cref="StepLogText.KeyLine"/>) is read from the evidence store and shown under it.
/// </para>
/// </remarks>
public sealed partial class RunMonitorViewModel : ObservableObject
{
    public const string CancelReason = "Cancelada por el usuario desde el monitor.";

    /// <summary>The approval that dismisses the auditor's request to replan (docs/15 D-35), recorded in the run.</summary>
    public const string ReplanDismissAction = "replan.dismiss";

    private const int MaxActivity = 300;

    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly IDeliveryService? _delivery;
    private readonly IEvidenceStore? _evidence;
    private IOrchestrationRun? _run;
    private ProjectContext? _project;
    private int _generation;
    private ContextHealthThresholds _contextThresholds = new();
    private RunPhase? _finishedPhase;
    private IReadOnlyList<OrchestratorFinding> _findings = [];

    /// <param name="time">Its local time zone formats the times shown.</param>
    /// <param name="uiContext">Where the events are applied; defaults to the creating thread's context.</param>
    /// <param name="delivery">Delivers a finished run to the user's branch; without it, delivering is never offered.</param>
    /// <param name="evidence">Reads the evidence of a failed verification step for its main error line; without it, only the reason is shown.</param>
    public RunMonitorViewModel(TimeProvider time, SynchronizationContext? uiContext = null, IDeliveryService? delivery = null, IEvidenceStore? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        _delivery = delivery;
        _evidence = evidence;
    }

    /// <summary>Raised on the UI context for every status change, so the canvas can recolour the node.</summary>
    public event EventHandler<TaskStatusUpdate>? TaskStatusChanged;

    /// <summary>Raised on the UI context once the run has finished (or its stream failed).</summary>
    public event EventHandler? Finished;

    public ObservableCollection<MonitorTaskViewModel> Tasks { get; } = [];

    /// <summary>What the agents and the run did, newest last; only the latest entries are kept.</summary>
    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public ObservableCollection<EvidenceEntry> Evidence { get; } = [];

    /// <summary>Abandoned, unreachable and retry-exhausted tasks, one line per list that is not empty.</summary>
    public ObservableCollection<string> HealthLines { get; } = [];

    /// <summary>Completes once every event of the run has been read and its completion observed.</summary>
    public Task Listening { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAttached))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryBlockedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private RunId? _runId;

    /// <summary>
    /// The run on screen was read back from its log (<see cref="Inspect"/>): it does not run here, it is never active and it
    /// takes no command. «Cerrar» asks the orchestrator document to put it away (<see cref="CloseRequested"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(InspectionText))]
    [NotifyCanExecuteChangedFor(nameof(CloseInspectionCommand))]
    private bool _isInspecting;

    /// <summary>Says that the run on screen is only being looked at; null for a run followed live.</summary>
    public string? InspectionText => IsInspecting
        ? "Vista de solo lectura, reconstruida del registro de decisiones: al verla no se ejecuta ni se anota nada. «Entregar a mi rama» la audita antes, como al terminar una ejecución. «Cerrar» vuelve a tu borrador."
        : null;

    /// <summary>
    /// The last delivery of the run its log records (an inspected run), where and when, and what delivering it again does; null
    /// when it was never delivered.
    /// </summary>
    [ObservableProperty]
    private string? _previousDelivery;

    /// <summary>The run looked at was audited for its delivery (<see cref="DeliverAsync"/>): once is enough.</summary>
    private bool _auditedForDelivery;

    /// <summary>Raised when the person asks to put away the run read back from its log («Cerrar»); the orchestrator document does it.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>«Cerrar»: the orchestrator document puts the inspected run away and brings back the draft that was on the canvas.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseInspection))]
    private void CloseInspection() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private bool CanCloseInspection() => IsInspecting && !IsDelivering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseLabel))]
    [NotifyPropertyChangedFor(nameof(PhaseTone))]
    [NotifyPropertyChangedFor(nameof(ShowsDeadlockAlert))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private RunPhase? _phase;

    [ObservableProperty]
    private string? _phaseDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryBlockedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeliverCommand))]
    private bool _isFinished;

    /// <summary>What the delivery service found about delivering the finished run (null until it was asked).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeliverCommand))]
    private DeliveryReadiness? _deliveryReadiness;

    /// <summary>Why the run can or cannot be delivered, or what the last delivery did; null when there is nothing to say.</summary>
    [ObservableProperty]
    private string? _deliveryMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeliverCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseInspectionCommand))]
    private bool _isDelivering;

    /// <summary>The run was delivered to the user's branch.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeliverCommand))]
    private bool _isDelivered;

    /// <summary>Completes once the delivery service was asked whether the finished run can be delivered (and its answer applied).</summary>
    public Task DeliveryCheck { get; private set; } = Task.CompletedTask;

    // ---- the review of a finished run (docs/15 D-43) ----

    /// <summary>
    /// The auditor, the verdicts, the challenges and the moves the review of a finished run needs. The shell assigns it
    /// once, like <see cref="ContextThresholds"/>; without it (or with a piece missing) that part is not shown.
    /// </summary>
    public RunReviewServices? Review { get; set; }

    /// <summary>
    /// «Preparar ejecución de seguimiento» (D-35): the orchestrator puts on its canvas an empty plan that follows the run,
    /// with the findings as data. Given by the orchestrator document; without it the action is not offered.
    /// </summary>
    public Func<RunId, string, Task>? PrepareFollowUp { get; set; }

    /// <summary>Completes once the auditor checked the finished run (and its report was applied).</summary>
    public Task AuditCheck { get; private set; } = Task.CompletedTask;

    /// <summary>Completes once the card of the review's verdict and its challenge were read (and applied).</summary>
    public Task ReviewCheck { get; private set; } = Task.CompletedTask;

    /// <summary>The findings of the auditor, one row each.</summary>
    public ObservableCollection<AuditFindingRow> AuditFindings { get; } = [];

    /// <summary>What the auditor found, in one line; null until it ran (and while it is not composed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewSection))]
    private string? _auditSummary;

    [ObservableProperty]
    private Tone _auditTone = Tone.Neutral;

    /// <summary>The auditor asks to replan: what that means for the delivery and what you can do; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewSection))]
    private string? _replanText;

    /// <summary>A request to replan stands and was not dismissed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DismissReplanCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrepareFollowUpCommand))]
    private bool _replanPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AuditAgainCommand))]
    [NotifyCanExecuteChangedFor(nameof(DismissReplanCommand))]
    private bool _isReviewing;

    /// <summary>The card of the review's verdict; null when there is none or the verdicts are not composed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewSection))]
    private VerdictCardViewModel? _reviewCard;

    /// <summary>Where the challenge of the review's verdict stands; null when it was never challenged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewSection))]
    private string? _challengeText;

    [ObservableProperty]
    private Tone _challengeTone = Tone.Neutral;

    /// <summary>The revision verdict that turned towards an approval and waits for you to accept it; null otherwise.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptRevisionCommand))]
    private string? _revisionVerdictId;

    /// <summary>Something of the review of the run is on screen (the auditor, the card or the challenge).</summary>
    public bool HasReviewSection => AuditSummary is not null || ReplanText is not null || ReviewCard is not null || ChallengeText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDeadlockAlert))]
    [NotifyPropertyChangedFor(nameof(HealthSummary))]
    private GraphHealthReport? _health;

    [ObservableProperty]
    private int _agentRunsUsed;

    [ObservableProperty]
    private string? _resultSummary;

    [ObservableProperty]
    private string? _integrationCommit;

    [ObservableProperty]
    private string? _verdictId;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsAttached => RunId is not null;

    /// <summary>The project of the run followed; null while none is followed or it was attached without one.</summary>
    public ProjectContext? Project => _project;

    /// <summary>
    /// Whether the run followed has the consent of its header to resume by itself (docs/16 D-39, ADR-0034): true or false as
    /// its header says, and null while that is not known (no run, or no header to read). The document that attached the run
    /// gives it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResumeConsentText))]
    private bool? _resumeConsent;

    /// <summary>What the consent of the run followed means, in a line; null while it is not known.</summary>
    public string? ResumeConsentText => ResumeConsent switch
    {
        true => "Con tu consentimiento para reanudarse sola: espera el reinicio de la cuota y, si un cierre que no pediste la corta, se reanuda al volver a abrir la app.",
        false => "Sin consentimiento para reanudarse sola: una espera larga la deja en pausa y, tras un cierre, la reanudas tú.",
        _ => null,
    };

    /// <summary>
    /// The thresholds the context of the tasks is judged with (docs/14 D-11): the user's, from the settings; 70, 85 and
    /// 95 % until the composition gives them. A change applies at once to what is shown.
    /// </summary>
    public ContextHealthThresholds ContextThresholds
    {
        get => _contextThresholds;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _contextThresholds = value;
            foreach (var task in Tasks)
            {
                task.JudgeContext(value);
            }
        }
    }

    /// <summary>A run is being followed and has not finished: the plan on the canvas is the one running. An inspected run never is.</summary>
    public bool IsActive => IsAttached && !IsFinished && !IsInspecting;

    public string PhaseLabel => Phase is { } phase ? RunPhaseText.Label(phase) : "Sin ejecución";

    public Tone PhaseTone => Phase is { } phase ? RunPhaseText.ToneOf(phase) : Tone.Neutral;

    /// <summary>The graph cannot progress and the engine says the run is blocked (see the remarks of the class).</summary>
    public bool ShowsDeadlockAlert => Health is { IsDeadlocked: true } && Phase == RunPhase.Blocked;

    public string DeadlockText => "Bloqueo: el grafo no puede avanzar sin una decisión tuya. Reintenta o cancela las tareas bloqueadas.";

    public string HealthSummary => Health is null
        ? "Salud del grafo: sin datos"
        : HealthLines.Count == 0 && !ShowsDeadlockAlert ? "Salud del grafo: sin problemas" : "Salud del grafo: con avisos";

    /// <summary>Starts following <paramref name="run"/>: its snapshot at once, then its events as they come.</summary>
    /// <param name="project">The project the run belongs to; without it, delivering is never offered.</param>
    public void Attach(IOrchestrationRun run, ProjectContext? project = null) => Follow(run, project, inspecting: false);

    /// <summary>
    /// Shows a run read back from its log (<see cref="IRunInspector.InspectAsync"/>), in read-only: the state its log left,
    /// then its history in order, then its end, with the review and the delivery of a run that succeeded as for a run
    /// followed live. It is never active and raises no <see cref="Finished"/>.
    /// </summary>
    /// <param name="project">The project of the run; the review and the delivery are asked for it.</param>
    public void Inspect(IOrchestrationRun run, ProjectContext project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Follow(run, project, inspecting: true);
    }

    private void Follow(IOrchestrationRun run, ProjectContext? project, bool inspecting)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (IsAttached)
        {
            throw new InvalidOperationException("A run is already attached; detach it (once it has finished) before attaching another one.");
        }

        Reset();
        _run = run;
        _project = project;
        IsInspecting = inspecting;
        var generation = ++_generation;

        // An inspected run tells its history from the start, every evidence included: its tasks count them as they come.
        ApplySnapshot(run.Snapshot(), countEvidence: !inspecting);
        RunId = run.Id;
        Log(_time.GetUtcNow(), null, inspecting ? $"Ejecución {run.Id} abierta desde el registro, en solo lectura" : $"Siguiendo la ejecución {run.Id}");
        Listening = ListenAsync(run, generation);
    }

    /// <summary>Forgets a finished run and releases it. A run in progress is never released from here.</summary>
    public async Task DetachAsync()
    {
        if (_run is not { } run)
        {
            return;
        }

        if (IsActive)
        {
            throw new InvalidOperationException("The run is still in progress.");
        }

        _generation++;
        _run = null;
        Reset();
        await run.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Why a pause did not apply, as the monitor says it: the run had ended, or its loop stopped before the pause was in the log
    /// (<see cref="IOrchestrationRun.PauseAsync"/> fails then, docs/16 D-39).
    /// </summary>
    public const string PauseNotApplied = "la ejecución ya había terminado o se paró antes de aplicar la pausa, y el registro no la recoge.";

    /// <summary>Why a retry did not apply, as the monitor says it: the run had ended, or the task was no longer blocked.</summary>
    public const string RetryNotApplied = "la ejecución ya había terminado o la tarea ya no estaba bloqueada. Si terminó, reanúdala para reintentar sus tareas.";

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => TryPauseAsync();

    /// <summary>
    /// «Pausar», as the monitor, the tray and the managed exit ask it: nothing new is dispatched once the pause is in the log.
    /// False when the run did not take it (it had ended, or stopped before), and <see cref="ErrorMessage"/> says why in Spanish.
    /// </summary>
    public Task<bool> TryPauseAsync() => SendAsync("pausar la ejecución", run => run.PauseAsync(), PauseNotApplied);

    private bool CanPause() => IsActive && Phase is RunPhase.Running or RunPhase.WaitingForQuota;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => TryResumeAsync();

    /// <summary>«Reanudar» the dispatch; false when the run did not take it, and <see cref="ErrorMessage"/> says why.</summary>
    public Task<bool> TryResumeAsync() => SendAsync("reanudar la ejecución", run => run.ResumeDispatchAsync());

    private bool CanResume() => IsActive && Phase == RunPhase.Paused;

    /// <summary>Puts a blocked task back in the queue: the user's decision, and it may spend quota.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryBlockedAsync(MonitorTaskViewModel? task) =>
        task is null ? Task.CompletedTask : SendAsync($"reintentar {task.Id}", run => run.RetryBlockedAsync(task.Id), RetryNotApplied);

    private bool CanRetry(MonitorTaskViewModel? task) => IsActive && task?.Status == WorkTaskStatus.Blocked;

    [RelayCommand(CanExecute = nameof(IsActive))]
    private Task CancelAsync() => TryCancelAsync();

    /// <summary>«Cancelar ejecución»; false when the run did not take it, and <see cref="ErrorMessage"/> says why.</summary>
    public Task<bool> TryCancelAsync() => SendAsync("cancelar la ejecución", run => run.CancelAsync(CancelReason));

    /// <summary>
    /// «Entregar a mi rama»: the run's approved integration to the branch the user has checked out, with
    /// <c>--ff-only</c>. Offered only for a finished run the delivery service found ready (a verified log, Succeeded, and a
    /// valid approval of exactly that integration); the service asks the approval gate (critical risk, naming the hook
    /// files that change) and checks everything again before git moves the branch.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeliver))]
    private async Task DeliverAsync()
    {
        if (_delivery is not { } delivery || _project is not { } project || RunId is not { } run)
        {
            return;
        }

        IsDelivering = true;
        ErrorMessage = null;
        try
        {
            // A run read back from its log was not audited by opening it: its delivery is, as the end of a run is (D-34), so
            // a request to replan of the auditor stops it here as it would have stopped it then (D-35).
            if (IsInspecting && !_auditedForDelivery && Review?.Auditor is { } auditor && _finishedPhase is { } ended && RunPhaseText.IsFinal(ended))
            {
                AuditCheck = AuditAsync(auditor, project, run, _generation);
                await AuditCheck.ConfigureAwait(true);
                _auditedForDelivery = true;

                // The audit may have asked to replan: the delivery is asked again, so «Entregar a mi rama» says so and stays
                // off afterwards (the service refuses it anyway, before the critical approval).
                RecheckDelivery();
            }

            var result = await delivery.DeliverAsync(project, run, DeliveryMode.FastForwardOnly, CancellationToken.None).ConfigureAwait(true);
            DeliveryMessage = result.Message;
            IsDelivered = result.Delivered;
        }
#pragma warning disable CA1031 // The delivery is behind a port: whatever it throws is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo entregar: {ex.Message}";
        }
        finally
        {
            IsDelivering = false;
        }
    }

    private bool CanDeliver() =>
        _delivery is not null && _project is not null && IsFinished && DeliveryReadiness is { Ready: true } && !IsDelivering && !IsDelivered;

    /// <summary>
    /// Asks the delivery service whether the finished run can be delivered, and applies the answer on the UI context; after
    /// <paramref name="after"/> (the auditor), whose request to replan the service must see.
    /// </summary>
    private async Task CheckDeliveryAsync(IDeliveryService delivery, ProjectContext project, RunId run, int generation, Task? after = null)
    {
        if (after is not null)
        {
            await after.ConfigureAwait(false);
        }

        DeliveryReadiness readiness;
        try
        {
            readiness = await delivery.CheckAsync(project, run, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The delivery is behind a port: a failure to check is shown as the reason it is not offered.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            readiness = new DeliveryReadiness(false, null, $"No se pudo comprobar si la ejecución se puede entregar: {ex.Message}");
        }

        Post(generation, () =>
        {
            DeliveryReadiness = readiness;
            DeliveryMessage = readiness.Reason;
        });
    }

    /// <summary>
    /// Sends a command to the run followed and says what came of it: true when the run took it. A refusal of the run
    /// (<see cref="InvalidOperationException"/>: it had ended, or stopped before applying the command) is said with
    /// <paramref name="notApplied"/>, in Spanish, when there is one; anything else, with what the engine said.
    /// </summary>
    private async Task<bool> SendAsync(string what, Func<IOrchestrationRun, Task> command, string? notApplied = null)
    {
        if (_run is not { } run)
        {
            return false;
        }

        try
        {
            ErrorMessage = null;
            await command(run).ConfigureAwait(true);
            return true;
        }
        catch (InvalidOperationException) when (notApplied is not null)
        {
            ErrorMessage = $"No se pudo {what}: {notApplied}";
            return false;
        }
#pragma warning disable CA1031 // The engine is behind a port: whatever it throws is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo {what}: {ex.Message}";
            return false;
        }
    }

    private async Task ListenAsync(IOrchestrationRun run, int generation)
    {
        try
        {
            await foreach (var orchestrationEvent in run.Events.ConfigureAwait(false))
            {
                Post(generation, () => Apply(orchestrationEvent));
            }

            var result = await run.Completion.ConfigureAwait(false);
            Post(generation, () => Finish(result));
        }
        catch (OperationCanceledException)
        {
            Post(generation, () => Fail("La ejecución se detuvo antes de terminar."));
        }
#pragma warning disable CA1031 // A failure of the run must be shown in the monitor, never thrown on a background task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Post(generation, () => Fail($"La ejecución terminó con un error: {ex.Message}"));
        }
    }

    private void Post(int generation, Action action)
    {
        void Run()
        {
            if (generation == _generation)
            {
                action();
            }
        }

        if (_ui is null)
        {
            Run();
        }
        else
        {
            _ui.Post(_ => Run(), null);
        }
    }

    private void Apply(OrchestrationEvent orchestrationEvent)
    {
        switch (orchestrationEvent)
        {
            case TaskStatusChangedEvent change:
                var changed = TaskFor(change.Task);
                changed.Status = change.To;
                changed.Transitioned(change.To, change.Reason);
                if (change.To == WorkTaskStatus.Failed)
                {
                    ReadFailureLine(changed, change.Reason);
                }

                if (change.To != WorkTaskStatus.Pending)
                {
                    changed.QueuedOn = null;
                }

                if (change.To != WorkTaskStatus.AwaitingApproval)
                {
                    changed.PendingApproval = null;
                }

                RefreshDetails(changed);
                RetryBlockedCommand.NotifyCanExecuteChanged();
                Log(change.At, change.Task, $"{TaskStatusText.Label(change.From)} → {TaskStatusText.Label(change.To)}");
                TaskStatusChanged?.Invoke(this, new TaskStatusUpdate(change.Task, change.To));
                break;

            case TaskQueuedEvent queued:
                var waiting = TaskFor(queued.Task);
                if (waiting.Status == WorkTaskStatus.Pending)
                {
                    waiting.QueuedOn = queued.Provider.Value;
                }

                Log(queued.At, queued.Task, $"En cola de {queued.Provider}");
                break;

            case AgentActivityEvent activity:
                var working = TaskFor(activity.Task);
                if (activity.Event is ContextObserved observed)
                {
                    working.ObserveContext(observed, ContextThresholds);
                }
                else if (activity.Event is CompactionObserved)
                {
                    working.CountCompaction();
                }

                if (AgentActivityText.Describe(activity.Event, _time.LocalTimeZone) is { } text)
                {
                    working.LastActivity = text;
                    Log(activity.At, activity.Task, text);
                }

                break;

            case EvidenceCapturedEvent captured:
                var evidence = captured.Evidence;
                Evidence.Add(new EvidenceEntry(
                    captured.Task?.Value,
                    evidence.Title,
                    EvidenceText.Kind(evidence.Kind),
                    EvidenceText.Origin(evidence.Origin),
                    evidence.ContentSha256[..12],
                    EvidenceText.Tests(evidence.TestRun)));
                if (captured.Task is { } owner)
                {
                    var holder = TaskFor(owner);
                    holder.EvidenceCount++;
                    holder.Captured(evidence);
                }

                break;

            case GraphHealthEvent health:
                Health = health.Report;
                break;

            case ApprovalPendingEvent approval:
                TaskFor(approval.Task).PendingApproval = approval.Request.Action;
                Log(approval.At, approval.Task, $"Espera tu aprobación: {approval.Request.Action}");
                break;

            case RunPhaseChangedEvent phase:
                Phase = phase.Phase;
                PhaseDetail = phase.Detail;
                Log(phase.At, null, phase.Detail is null ? $"Fase: {RunPhaseText.Label(phase.Phase)}" : $"Fase: {RunPhaseText.Label(phase.Phase)} · {phase.Detail}");
                break;

            case RunFinishedEvent finished:
                Finish(finished.Result);
                break;

            case RunDeliveredEvent delivered:
                var branch = delivered.TargetRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? delivered.TargetRef["refs/heads/".Length..] : delivered.TargetRef;
                var deliveredAt = TimeZoneInfo.ConvertTime(delivered.At, _time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
                var commit = delivered.Commit is { Length: >= 10 } head ? $" ({head[..10]})" : string.Empty;
                PreviousDelivery = $"Ya se entregó a «{branch}»{commit} el {deliveredAt}. Si la entregas otra vez, la misma integración irá a la rama que tengas activa ahora.";
                Log(delivered.At, null, $"Entregada a {branch}{commit}");
                break;
        }
    }

    private void Finish(RunResult result)
    {
        Phase = result.Phase;
        Health = result.Health;
        ResultSummary = result.Summary;
        IntegrationCommit = result.IntegrationCommit;
        VerdictId = result.VerdictId;
        if (!IsFinished)
        {
            IsFinished = true;
            _finishedPhase = result.Phase;
            Log(_time.GetUtcNow(), null, IsInspecting ? $"Estado final según el registro: {RunPhaseText.Label(result.Phase)}" : $"Ejecución terminada: {RunPhaseText.Label(result.Phase)}");

            // The auditor checks the log of every run that ended (D-34), before the delivery is asked: its request to
            // replan blocks the delivery until you decide (D-35). A run only looked at is not: opening it writes nothing in
            // its log, and its delivery audits it first (DeliverAsync).
            if (RunPhaseText.IsFinal(result.Phase) && Review?.Auditor is { } auditor && _project is { } audited && RunId is { } auditedRun && !IsInspecting)
            {
                AuditCheck = AuditAsync(auditor, audited, auditedRun, _generation);
            }

            // Only a run that succeeded can be delivered; the service decides whether this one can.
            if (result.Phase == RunPhase.Succeeded && _delivery is { } delivery && _project is { } project && RunId is { } run)
            {
                DeliveryCheck = CheckDeliveryAsync(delivery, project, run, _generation, AuditCheck);
            }

            if (result.VerdictId is { } verdict && Review is { } review && _project is { } reviewed && RunId is { } reviewedRun)
            {
                ReviewCheck = LoadReviewAsync(review, reviewed, reviewedRun, verdict, _generation);
            }

            AuditAgainCommand.NotifyCanExecuteChanged();
            RefreshReviewCommand.NotifyCanExecuteChanged();

            // Nothing ended now when the run was only read back: the memory, the quota and the list of runs need nothing.
            if (!IsInspecting)
            {
                Finished?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // ---- the review of the finished run (docs/15 D-33 to D-35, D-40 and D-43) ----

    /// <summary>«Auditar de nuevo»: the auditor checks the log again (free and idempotent: a finding already noted is not noted twice).</summary>
    [RelayCommand(CanExecute = nameof(CanAuditAgain))]
    private async Task AuditAgainAsync()
    {
        if (Review?.Auditor is not { } auditor || _project is not { } project || RunId is not { } run)
        {
            return;
        }

        AuditCheck = AuditAsync(auditor, project, run, _generation);
        await AuditCheck.ConfigureAwait(true);
        RecheckDelivery();
    }

    private bool CanAuditAgain() => IsFinished && !IsReviewing && Review?.Auditor is not null && _project is not null && RunId is not null;

    /// <summary>
    /// «Descartar la petición» (D-35): asks your approval (<see cref="ReplanDismissAction"/>, in the run) and, with it, the
    /// request to replan no longer holds the delivery; the findings stay in the log.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDismissReplan))]
    private async Task DismissReplanAsync()
    {
        if (Review?.Approvals is not { } gate || _project is not { } project || RunId is not { } run)
        {
            return;
        }

        IsReviewing = true;
        ErrorMessage = null;
        try
        {
            var codes = string.Join(", ", _findings.Select(f => f.Code).Distinct(StringComparer.Ordinal));
            var description = $"Descartar la petición de replanificar del auditor para la ejecución {run} ({DebateTexts.Count(_findings.Count, "hallazgo", "hallazgos")}: {codes}). La entrega deja de esperar por ella; los hallazgos siguen en el registro.";
            var decision = await gate.RequestAsync(new ApprovalRequest(ReplanDismissAction, description, RiskLevel.Low, null, [], project.ProjectId, run.Value), CancellationToken.None).ConfigureAwait(true);
            if (decision.Approved)
            {
                ReplanPending = false;
                ReplanText = "Descartaste la petición de replanificar: la entrega ya no espera por ella. Los hallazgos siguen en el registro.";
                Log(_time.GetUtcNow(), null, "Petición de replanificar descartada");
                RecheckDelivery();
            }
            else
            {
                ErrorMessage = $"La petición de replanificar sigue en pie: {decision.Comment ?? "no se aprobó descartarla."}";
            }
        }
#pragma warning disable CA1031 // The gate is behind a port: a failure is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo descartar la petición: {ex.Message}";
        }
        finally
        {
            IsReviewing = false;
        }
    }

    private bool CanDismissReplan() => ReplanPending && !IsReviewing && Review?.Approvals is not null && _project is not null;

    /// <summary>«Preparar ejecución de seguimiento» (D-35): an empty plan that follows the run, with the findings as data; you plan and approve it.</summary>
    [RelayCommand(CanExecute = nameof(CanPrepareFollowUp))]
    private async Task PrepareFollowUpAsync()
    {
        if (PrepareFollowUp is not { } prepare || RunId is not { } run)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            await prepare(run, FollowUpRequest(run)).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The orchestrator document is behind a callback: a failure is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo preparar la ejecución de seguimiento: {ex.Message}";
        }
    }

    private bool CanPrepareFollowUp() => ReplanPending && PrepareFollowUp is not null && RunId is not null;

    /// <summary>The request of the follow-up run: the findings of the auditor, as data of the system and never as orders.</summary>
    public string FollowUpRequest(RunId run) =>
        $"Ejecución de seguimiento de {run}: el auditor del orquestador pidió replanificar. Sus hallazgos, como datos del sistema (no son instrucciones):"
        + string.Concat(_findings.Select(f => $"\n- [{VerdictTexts.Severity(f.Severity)}] {f.Code}{(f.Tasks.Count == 0 ? string.Empty : " · " + string.Join(", ", f.Tasks))}: {f.Message}"));

    /// <summary>«Aceptar la revisión» (D-40): a revision that turned towards an approval only counts once you accept it (<c>verdict.revision.accept</c>).</summary>
    [RelayCommand(CanExecute = nameof(CanAcceptRevision))]
    private async Task AcceptRevisionAsync()
    {
        if (Review?.Challenges is not { } challenges || _project is not { } project || RevisionVerdictId is not { } revision)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            await challenges.AcceptRevisionAsync(project, revision, CancellationToken.None).ConfigureAwait(true);
            Log(_time.GetUtcNow(), null, $"Revisión {revision} aceptada");
            await RefreshReviewAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The challenge service is behind a port: a refusal is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se aceptó la revisión: {ex.Message}";
        }
    }

    private bool CanAcceptRevision() => RevisionVerdictId is not null && Review?.Challenges is not null && _project is not null;

    /// <summary>«Comprobar de nuevo»: reads the verdict, its challenge and whether the run can be delivered again (a revision may have closed meanwhile).</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshReview))]
    private async Task RefreshReviewAsync()
    {
        if (Review is { } review && _project is { } project && RunId is { } run && VerdictId is { } verdict)
        {
            ReviewCheck = LoadReviewAsync(review, project, run, verdict, _generation);
            await ReviewCheck.ConfigureAwait(true);
        }

        RecheckDelivery();
        await DeliveryCheck.ConfigureAwait(true);
    }

    private bool CanRefreshReview() => IsFinished && _project is not null && RunId is not null && (Review is not null || _delivery is not null);

    /// <summary>The delivery is asked again (only for a run that succeeded): the auditor, a dismissal or a challenge may have changed its answer.</summary>
    private void RecheckDelivery()
    {
        if (_finishedPhase == RunPhase.Succeeded && _delivery is { } delivery && _project is { } project && RunId is { } run)
        {
            DeliveryCheck = CheckDeliveryAsync(delivery, project, run, _generation, AuditCheck);
        }
    }

    private async Task AuditAsync(IOrchestratorAuditService auditor, ProjectContext project, RunId run, int generation)
    {
        Post(generation, () => IsReviewing = true);
        OrchestratorAuditReport? report = null;
        string? failure = null;
        try
        {
            report = await auditor.AuditRunAsync(project, run, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The auditor is behind a port: a failure is shown in the monitor, never thrown on a background task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = $"No se pudo auditar la ejecución: {ex.Message}";
        }

        Post(generation, () =>
        {
            ApplyAudit(report, failure);
            IsReviewing = false;
        });
    }

    private void ApplyAudit(OrchestratorAuditReport? report, string? failure)
    {
        AuditFindings.Clear();
        _findings = [];
        if (report is null)
        {
            AuditSummary = failure;
            AuditTone = Tone.Error;
            return;
        }

        if (!report.Log.IsValid)
        {
            var from = report.Log.FirstInvalidSequence is { } sequence ? $" desde la entrada {sequence.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
            AuditSummary = $"El registro de la ejecución no verifica{from}: {report.Log.Problem}. El auditor no escribió nada.";
            AuditTone = Tone.Error;
            ReplanPending = false;
            ReplanText = null;
            return;
        }

        _findings = report.Findings;
        foreach (var finding in report.Findings)
        {
            var where = new List<string>();
            if (finding.Tasks.Count > 0)
            {
                where.Add("tareas " + string.Join(", ", finding.Tasks));
            }

            if (finding.Entries.Count > 0)
            {
                where.Add("entradas " + string.Join(", ", finding.Entries.Select(e => "#" + e.ToString(CultureInfo.InvariantCulture))));
            }

            AuditFindings.Add(new AuditFindingRow(
                VerdictTexts.Severity(finding.Severity),
                VerdictTexts.ToneOf(finding.Severity),
                AuditFindingText.Check(finding.Code),
                finding.Code,
                where.Count == 0 ? "toda la ejecución" : string.Join(" · ", where),
                finding.Message));
        }

        var serious = report.Findings.Count(f => f.Severity >= Severity.High);
        var recorded = report.Recorded.Count switch
        {
            0 => string.Empty,
            1 => " (1 alerta nueva en el registro)",
            var count => string.Create(CultureInfo.InvariantCulture, $" ({count} alertas nuevas en el registro)"),
        };
        AuditSummary = report.Findings.Count == 0
            ? "Auditor del orquestador: sin hallazgos en el registro verificado de la ejecución."
            : $"Auditor del orquestador: {DebateTexts.Count(report.Findings.Count, "hallazgo", "hallazgos")}, {serious.ToString(CultureInfo.InvariantCulture)} de gravedad alta o crítica{recorded}.";
        AuditTone = report.Findings.Count == 0 ? Tone.Success : serious > 0 ? Tone.Error : Tone.Warning;
        var replan = report.ReplanRequested || serious > 0;
        if (replan && ReplanText?.StartsWith("Descartaste", StringComparison.Ordinal) != true)
        {
            ReplanPending = true;
            ReplanText = "El auditor pide replanificar: prepara una ejecución de seguimiento o descarta la petición. Hasta que decidas, la entrega espera.";
        }
        else if (!replan)
        {
            ReplanPending = false;
            ReplanText = null;
        }
    }

    private async Task LoadReviewAsync(RunReviewServices review, ProjectContext project, RunId run, string verdictId, int generation)
    {
        IReadOnlyList<StoredVerdict> verdicts = [];
        StoredVerdict? stored = null;
        FrozenRequirementSet? requirements = null;
        ChallengeStatus? status = null;
        string? failure = null;
        try
        {
            if (review.Verdicts is { } store)
            {
                verdicts = await store.ListAsync(project.ProjectId, run.Value, CancellationToken.None).ConfigureAwait(false);
                stored = verdicts.LastOrDefault(v => v.Id == verdictId);
            }

            if (stored is not null && review.Requirements is { } requirementStore)
            {
                requirements = await requirementStore.FindAsync(project.ProjectId, stored.Verdict.RequirementSetHash, CancellationToken.None).ConfigureAwait(false);
            }

            if (review.Challenges is { } challenges)
            {
                status = await challenges.StatusAsync(project, verdictId, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The stores are behind ports: a failure is shown in the monitor, never thrown on a background task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = $"No se pudo leer el veredicto de la revisión: {ex.Message}";
        }

        Post(generation, () => ApplyReview(review, project, run, verdictId, verdicts, stored, requirements, status, failure));
    }

    private void ApplyReview(
        RunReviewServices review,
        ProjectContext project,
        RunId run,
        string verdictId,
        IReadOnlyList<StoredVerdict> verdicts,
        StoredVerdict? stored,
        FrozenRequirementSet? requirements,
        ChallengeStatus? status,
        string? failure)
    {
        if (failure is not null)
        {
            ErrorMessage = failure;
        }

        // A verdict can be challenged unless a challenge of it is in course or waits for your acceptance.
        var challengeable = review.Challenge is not null && status is not (ChallengeStatus.Pending or ChallengeStatus.AwaitingAcceptance);
        ReviewCard = stored is null
            ? null
            : new VerdictCardViewModel(
                [new JudgedVerdict(stored.Id, stored.Verdict, stored.Issues, Corrected: false)],
                null,
                new VerdictCardContext($"Veredicto de la revisión · {run}", Requirements: requirements),
                _time.LocalTimeZone,
                new VerdictCardActions(
                    challengeable ? id => review.Challenge!(project, id) : null,
                    null,
                    review.CorrectiveTasks is { } corrective ? request => corrective(project, request) : null),
                stored.Supersedes is { } previous ? new Dictionary<string, string> { [stored.Id] = previous } : null);
        ChallengeText = status is { } known && known != ChallengeStatus.None ? VerdictTexts.Challenge(known) : null;
        ChallengeTone = status is { } tone ? VerdictTexts.ToneOf(tone) : Tone.Neutral;
        RevisionVerdictId = status == ChallengeStatus.AwaitingAcceptance && Head(verdicts, verdictId) is { } head && head != verdictId ? head : null;
    }

    /// <summary>The verdict at the end of the chain that starts at <paramref name="verdictId"/>: the one no other verdict of the run replaces.</summary>
    private static string? Head(IReadOnlyList<StoredVerdict> verdicts, string verdictId)
    {
        var current = verdictId;
        var seen = new HashSet<string>(StringComparer.Ordinal) { current };
        while (verdicts.LastOrDefault(v => v.Supersedes == current) is { } next && seen.Add(next.Id))
        {
            current = next.Id;
        }

        return current;
    }

    /// <summary>Completes once the main error line of the last failure the monitor read was applied (<see cref="ReadFailureLine"/>).</summary>
    public Task FailureLineRead { get; private set; } = Task.CompletedTask;

    /// <summary>The most text of a step's evidence read for its error line: a log longer than this is read only up to here.</summary>
    private const int MaxStepLogChars = 4 * 1024 * 1024;

    /// <summary>
    /// The main error line of the verification step that failed <paramref name="task"/> (<see cref="StepLogText.KeyLine"/>):
    /// read off the UI thread from the evidence of the step its <paramref name="reason"/> names among what its attempt captured,
    /// and applied on the UI context while that failure is still the task's last one. Nothing without the evidence store, the
    /// project or such evidence: the reason alone says why.
    /// </summary>
    private void ReadFailureLine(MonitorTaskViewModel task, string reason)
    {
        if (_evidence is not { } store || _project is not { } project || task.FailedStepEvidence(reason) is not { } evidence)
        {
            return;
        }

        FailureLineRead = ReadFailureLineAsync(store, project, evidence, task, task.Failures, _generation);
    }

    private async Task ReadFailureLineAsync(IEvidenceStore store, ProjectContext project, EvidenceRecord evidence, MonitorTaskViewModel task, int failure, int generation)
    {
        string? line = null;
        try
        {
            var stream = await store.OpenContentAsync(project.ProjectId, evidence.Id, CancellationToken.None).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = new StringBuilder();
                var chunk = new char[64 * 1024];
                int read;
                while (text.Length < MaxStepLogChars && (read = await reader.ReadAsync(chunk, CancellationToken.None).ConfigureAwait(false)) > 0)
                {
                    text.Append(chunk, 0, Math.Min(read, MaxStepLogChars - text.Length));
                }

                line = StepLogText.KeyLine(text.ToString());
            }
        }
#pragma warning disable CA1031 // The evidence store is behind a port: without the line, the reason alone says why the task failed.
        catch (Exception)
#pragma warning restore CA1031
        {
            line = null;
        }

        Post(generation, () => task.ShowFailureLine(failure, line));
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        if (!IsFinished)
        {
            IsFinished = true;
            if (!IsInspecting)
            {
                Finished?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    partial void OnHealthChanged(GraphHealthReport? value)
    {
        HealthLines.Clear();
        if (value is null)
        {
            return;
        }

        foreach (var (title, tasks) in new[]
        {
            ("Abandonadas", value.StalledTasks),
            ("Inalcanzables", value.UnreachableTasks),
            ("Sin reintentos", value.RetryExhaustedTasks),
        })
        {
            if (tasks.Count > 0)
            {
                HealthLines.Add($"{title}: {string.Join(", ", tasks)}");
            }
        }
    }

    private void ApplySnapshot(RunSnapshot snapshot, bool countEvidence = true)
    {
        foreach (var view in snapshot.Tasks)
        {
            var task = new MonitorTaskViewModel(view.Id, view.Title, view.Kind, _time.LocalTimeZone)
            {
                Status = view.Status,
                EvidenceCount = countEvidence ? view.Evidence.Count : 0,
                FromLog = IsInspecting,
            };
            task.Refresh(view);
            Tasks.Add(task);
        }

        Phase = snapshot.Phase;
        Health = snapshot.Health;
        AgentRunsUsed = snapshot.AgentRunsUsed;
    }

    /// <summary>
    /// The block reason, attempt and agent of a task come from the run's snapshot (the events only carry the status). The
    /// snapshot may already be ahead of the event being applied; then only what still matches the event is taken.
    /// </summary>
    private void RefreshDetails(MonitorTaskViewModel task)
    {
        RunSnapshot snapshot;
        try
        {
            snapshot = _run?.Snapshot() ?? throw new InvalidOperationException("No run.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        AgentRunsUsed = snapshot.AgentRunsUsed;
        if (snapshot.Tasks.FirstOrDefault(t => t.Id == task.Id) is { } view)
        {
            if (view.Status == task.Status)
            {
                task.Refresh(view);
            }
            else if (task.Status != WorkTaskStatus.Blocked)
            {
                task.BlockReason = null;
            }
        }
    }

    private MonitorTaskViewModel TaskFor(TaskId id)
    {
        if (Tasks.FirstOrDefault(t => t.Id == id) is { } known)
        {
            return known;
        }

        // A task the first snapshot did not have: take what the run says about it now.
        var view = SafeSnapshot()?.Tasks.FirstOrDefault(t => t.Id == id);
        var task = new MonitorTaskViewModel(id, view?.Title ?? id.Value, view?.Kind ?? WorkTaskKind.AgentWork, _time.LocalTimeZone) { FromLog = IsInspecting };
        if (view is not null)
        {
            task.Status = view.Status;
            task.Refresh(view);
        }

        Tasks.Add(task);
        return task;
    }

    private RunSnapshot? SafeSnapshot()
    {
        try
        {
            return _run?.Snapshot();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void Log(DateTimeOffset at, TaskId? task, string text)
    {
        var local = TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);
        Activity.Add(new ActivityEntry(at, local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), task?.Value, text));
        while (Activity.Count > MaxActivity)
        {
            Activity.RemoveAt(0);
        }
    }

    private void Reset()
    {
        Tasks.Clear();
        Activity.Clear();
        Evidence.Clear();
        RunId = null;
        IsInspecting = false;
        PreviousDelivery = null;
        _auditedForDelivery = false;
        Phase = null;
        PhaseDetail = null;
        Health = null;
        IsFinished = false;
        AgentRunsUsed = 0;
        ResultSummary = null;
        IntegrationCommit = null;
        VerdictId = null;
        ErrorMessage = null;
        _project = null;
        ResumeConsent = null;
        DeliveryCheck = Task.CompletedTask;
        DeliveryReadiness = null;
        DeliveryMessage = null;
        IsDelivering = false;
        IsDelivered = false;
        _finishedPhase = null;
        _findings = [];
        AuditCheck = Task.CompletedTask;
        ReviewCheck = Task.CompletedTask;
        AuditFindings.Clear();
        AuditSummary = null;
        AuditTone = Tone.Neutral;
        ReplanText = null;
        ReplanPending = false;
        IsReviewing = false;
        ReviewCard = null;
        ChallengeText = null;
        ChallengeTone = Tone.Neutral;
        RevisionVerdictId = null;
    }
}

/// <summary>One line of the activity log of a run.</summary>
public sealed record ActivityEntry(DateTimeOffset At, string Time, string? Task, string Text);

/// <summary>One piece of evidence the run captured; <see cref="ShortHash"/> is the start of its SHA-256.</summary>
public sealed record EvidenceEntry(string? Task, string Title, string Kind, string Origin, string ShortHash, string? Tests);

/// <summary>One task as the monitor shows it.</summary>
public sealed partial class MonitorTaskViewModel : ObservableObject
{
    private readonly TimeZoneInfo _zone;

    /// <summary>The evidence the current attempt captured, newest last: a failed verification is explained from it.</summary>
    private readonly List<EvidenceRecord> _attemptEvidence = [];
    private ContextObserved? _lastContext;
    private ContextReading? _context;
    private int _compactions;

    public MonitorTaskViewModel(TaskId id, string title, WorkTaskKind kind, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        Id = id;
        Title = title;
        Kind = kind;
        _zone = zone;
    }

    public TaskId Id { get; }

    public string Title { get; }

    public WorkTaskKind Kind { get; }

    public string KindLabel => NodeKinds.Label(Kind);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    [NotifyPropertyChangedFor(nameof(Context))]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    [NotifyPropertyChangedFor(nameof(ShowsFailureLine))]
    private WorkTaskStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    private string? _blockReason;

    /// <summary>The reason of the last transition of the task, as the run recorded it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    private string? _reason;

    /// <summary>The reason of the last failure of the task, kept while it is tried again; null once it completed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasonText))]
    [NotifyPropertyChangedFor(nameof(ShowsFailureLine))]
    private string? _failureReason;

    /// <summary>
    /// The main error line of the verification step behind the last failure (<see cref="StepLogText.KeyLine"/>), read from its
    /// evidence; null while it is not read or there is none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFailureLine))]
    private string? _failureLine;

    /// <summary>How many times the task failed: a line read for an earlier failure is not shown for a later one.</summary>
    public int Failures { get; private set; }

    /// <summary>
    /// Why the task is where it is, in a line, when its status alone does not say it: the reason of a failure (and of the last
    /// one while it is tried again), of a task waiting for a person, cancelled or skipped, or blocked for another reason than
    /// an interruption or a wait for quota (its status says those). Null otherwise.
    /// </summary>
    public string? ReasonText => Status switch
    {
        WorkTaskStatus.Failed => FailureReason ?? Reason,
        WorkTaskStatus.Pending or WorkTaskStatus.Running when FailureReason is not null => "Intento anterior: " + FailureReason,
        WorkTaskStatus.Blocked when BlockReason is { } block
            && (block.StartsWith("Interrupted", StringComparison.OrdinalIgnoreCase) || block.StartsWith("RateLimitedUntil", StringComparison.OrdinalIgnoreCase)) => null,
        WorkTaskStatus.Blocked or WorkTaskStatus.AwaitingApproval or WorkTaskStatus.Cancelled or WorkTaskStatus.Skipped => Reason,
        _ => null,
    };

    /// <summary>The error line of the last failure is shown: the task failed, or is being tried again after it.</summary>
    public bool ShowsFailureLine => FailureLine is not null && FailureReason is not null
        && Status is WorkTaskStatus.Failed or WorkTaskStatus.Pending or WorkTaskStatus.Running;

    /// <summary>Takes a transition: its reason, a new attempt (its evidence starts again), a failure or a completion.</summary>
    internal void Transitioned(WorkTaskStatus to, string? reason)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        switch (to)
        {
            case WorkTaskStatus.Running:
                _attemptEvidence.Clear();
                break;
            case WorkTaskStatus.Failed:
                Failures++;
                FailureLine = null;
                FailureReason = Reason;
                break;
            case WorkTaskStatus.Completed:
                FailureReason = null;
                FailureLine = null;
                break;
        }
    }

    /// <summary>Notes an evidence the current attempt captured.</summary>
    internal void Captured(EvidenceRecord evidence) => _attemptEvidence.Add(evidence);

    /// <summary>Shows the error line read for failure number <paramref name="failure"/>, while it is still the last one.</summary>
    internal void ShowFailureLine(int failure, string? line)
    {
        if (failure == Failures && FailureReason is not null)
        {
            FailureLine = line;
        }
    }

    /// <summary>
    /// The evidence of the verification step a failure names (<c>La verificación falló en: build, test</c>, <c>El paso test
    /// falló.</c>), as its attempt captured it: the system's output of a step, whose title ends with the step's id in brackets
    /// (<c>Pruebas (test)</c>, <c>Compilar (build) · sin aislamiento</c>). The first step named that has one; null otherwise.
    /// </summary>
    internal EvidenceRecord? FailedStepEvidence(string? reason)
    {
        var steps = StepLogText.FailedSteps(reason);
        var outputs = _attemptEvidence
            .Where(e => e.Origin == EvidenceOrigin.CapturedBySystem && e.Kind is EvidenceKind.TestRun or EvidenceKind.BuildLog or EvidenceKind.CommandOutput)
            .ToList();
        foreach (var step in steps)
        {
            var named = $"({step})";
            if (outputs.LastOrDefault(e => e.Title.EndsWith(named, StringComparison.Ordinal) || e.Title.Contains(named + " · ", StringComparison.Ordinal)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The provider whose queue the task waits in, while it is pending.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    private string? _queuedOn;

    [ObservableProperty]
    private string? _agent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttemptText))]
    private int _attempt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttemptText))]
    private int _retryCount;

    [ObservableProperty]
    private string? _lastActivity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvidenceText))]
    private int _evidenceCount;

    /// <summary>«1 evidencia», «3 evidencias».</summary>
    public string EvidenceText => EvidenceCount == 1 ? "1 evidencia" : $"{EvidenceCount.ToString(CultureInfo.InvariantCulture)} evidencias";

    /// <summary>
    /// The task belongs to a run read back from its log: what its agent did step by step is not there, so no line says that
    /// nothing was observed yet.
    /// </summary>
    internal bool FromLog { get; set; }

    /// <summary>The action waiting for the user's approval, while there is one.</summary>
    [ObservableProperty]
    private string? _pendingApproval;

    /// <summary>
    /// The context of the latest step of the task's agent (docs/14 D-12): occupancy with its origin, tokens and window,
    /// model, level and the compactions of the CLI. A task that runs an agent and has started without any observation
    /// says so; any other task has no line.
    /// </summary>
    public string? Context
    {
        get
        {
            var compactions = _compactions switch
            {
                0 => string.Empty,
                1 => " · 1 compactación del CLI (posible pérdida de información)",
                var count => $" · {count} compactaciones del CLI (posible pérdida de información)",
            };
            if (_context is { } reading)
            {
                return "Contexto: " + reading.Text + compactions;
            }

            return RunsAnAgent && !FromLog && (Status != WorkTaskStatus.Pending || _compactions > 0) ? "Contexto: sin observaciones todavía" + compactions : null;
        }
    }

    /// <summary>The level of the latest step against the thresholds; null without an observation.</summary>
    public ContextHealthLevel? ContextLevel => _context?.Level;

    /// <summary>
    /// The latest step of the task's agent as it came (<see cref="ContextObserved"/>), the figures <see cref="Context"/> is
    /// read from; null without an observation. The status panel of the tray draws its bar from it (docs/16 D-15).
    /// </summary>
    public ContextObserved? LastContext => _lastContext;

    /// <summary>The tone of the dot next to the context line: an unknown level is never «healthy».</summary>
    public Tone ContextTone => ContextLevel switch
    {
        ContextHealthLevel.Healthy => Tone.Success,
        ContextHealthLevel.Warning or ContextHealthLevel.CompressionRecommended => Tone.Warning,
        ContextHealthLevel.Critical => Tone.Error,
        _ => Tone.Neutral,
    };

    private bool RunsAnAgent => Kind is WorkTaskKind.AgentWork or WorkTaskKind.Review or WorkTaskKind.Debate;

    internal void ObserveContext(ContextObserved observed, ContextHealthThresholds thresholds)
    {
        _lastContext = observed;
        JudgeContext(thresholds);
    }

    internal void CountCompaction()
    {
        _compactions++;
        OnPropertyChanged(nameof(Context));
    }

    /// <summary>Judges the latest step again (new thresholds, or a new step).</summary>
    internal void JudgeContext(ContextHealthThresholds thresholds)
    {
        _context = _lastContext is { } step ? ContextText.Read(step.Model, step.PromptTokens, step.ContextWindow, thresholds) : null;
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(ContextLevel));
        OnPropertyChanged(nameof(ContextTone));
    }

    public string StatusLabel => Status switch
    {
        WorkTaskStatus.Pending when QueuedOn is not null => $"En cola · {QueuedOn}",
        WorkTaskStatus.Blocked => BlockReasonText.Label(BlockReason, _zone),
        _ => TaskStatusText.Label(Status),
    };

    public bool IsQueued => QueuedOn is not null && Status == WorkTaskStatus.Pending;

    public bool IsBlocked => Status == WorkTaskStatus.Blocked;

    public string AttemptText => RetryCount switch
    {
        0 => $"Intento {Attempt}",
        1 => $"Intento {Attempt} · 1 reintento",
        _ => $"Intento {Attempt} · {RetryCount} reintentos",
    };

    internal void Refresh(TaskView view)
    {
        BlockReason = view.BlockReason;
        Agent = view.Agent?.Value;
        Attempt = view.Attempt;
        RetryCount = view.RetryCount;
    }
}
