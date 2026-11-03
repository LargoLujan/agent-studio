using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Context;
using AgentStudio.Orchestration.Verification;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>A validator message in the plan panel; selecting it selects its node.</summary>
public sealed record PlanProblem(TaskId? Task, string Text)
{
    /// <summary>What a screen reader says of the entry (A1-bis): its text, never the members of the record.</summary>
    public override string ToString() => Text;
}

/// <summary>A previous run of the project, as the engine lists it.</summary>
public sealed partial class RunListItem(RunSummary summary, string when) : ObservableObject
{
    public RunSummary Summary { get; } = summary;

    public string When { get; } = when;

    public string PhaseLabel => RunPhaseText.Label(Summary.Phase);

    public Tone PhaseTone => RunPhaseText.ToneOf(Summary.Phase);

    /// <summary>Only an interrupted run can be resumed (docs/13 section 3.7); resuming spends quota, so it is the user's call.</summary>
    public bool CanResume => Summary.Phase == RunPhase.Interrupted;

    /// <summary>
    /// The run can be opened in the monitor, read back from its log («Ver»): one that finished or was interrupted. A run in
    /// progress is in this process, and the monitor that follows it (the tab's or the tray's) shows it.
    /// </summary>
    public bool CanView => Summary.Phase is RunPhase.Succeeded or RunPhase.Failed or RunPhase.Cancelled or RunPhase.Interrupted;

    /// <summary>The monitor shows this run, read back from its log.</summary>
    [ObservableProperty]
    private bool _isViewed;

    /// <summary>What a screen reader says of the entry (A1-bis): its state, when and its id, never the name of the type.</summary>
    public override string ToString() => $"{PhaseLabel} · {When} · {Summary.Id}";
}

/// <summary>
/// A run the person started or resumed in a document whose monitor followed another run by then: the shell follows it with a
/// monitor of its own, which the tray follows (docs/16 D-39, ADR-0036), so a started run is never left without one.
/// </summary>
public sealed record HandedOffRun(ProjectContext Project, IOrchestrationRun Run, bool? ResumeConsent);

public enum ValidationState
{
    NoContext,
    Valid,
    Invalid,
}

/// <summary>
/// The «Orquestador» document of one project: the preparation (request, frozen requirements, agents and verification
/// profile), the canvas, the review of the plan (validation, approval, the plan the orchestrator agent proposes) and the
/// monitor of the run. Planning and running go through <see cref="OrchestrationSession"/>; without an engine the canvas
/// still works and the screen says «Motor no disponible».
/// </summary>
public sealed partial class OrchestratorDocumentViewModel : DocumentViewModel
{
    public const string IdPrefix = "orchestrator:";

    private static readonly PlanLimits Limits = new();

    private readonly OrchestrationSession _session;
    private readonly ICanvasLayoutStore _layout;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly ReinforcedConfirmation? _confirmation;
    private CancellationTokenSource? _planning;

    /// <summary>Why Windows Hello was not there when the plan was approved without it (the warning of <see cref="HelloNotice"/>).</summary>
    private UserPresenceAvailability? _helloAbsent;
    private bool _opened;
    private bool _preparationShownOnce;
    private string? _approvedHash;

    /// <summary>The roster the plan was validated against and shown with when the user approved it: what the run asks for.</summary>
    private IReadOnlyList<AgentDefinition>? _approvedRoster;

    /// <summary>The warning about writers at medium integrity shown when the plan was approved (empty when there was none).</summary>
    private string? _approvedWarning;

    /// <summary>The run whose resume was refused for its writers at medium integrity, with the hash of its plan (<see cref="FollowUpOf"/>).</summary>
    private FollowUp? _followUp;

    /// <summary>
    /// The run the orchestrator's auditor asked to replan, prepared here as a follow-up (docs/15 D-35): the next run started
    /// in this tab follows it, whatever plan it runs, until one starts.
    /// </summary>
    private RunId? _replanOf;

    /// <summary>Monitors of runs handed off with nobody to take them (a document without its shell): never left without one.</summary>
    private readonly List<RunMonitorViewModel> _handedOff = [];

    /// <param name="confirmation">
    /// Windows Hello for the critical approvals (docs/16 D-13, F8-11): «Aprobar plan» follows its rule, as a critical question
    /// of the approval gate does. Without it the button alone approves, as before Phase 8.
    /// </param>
    /// <param name="drafts">
    /// Keeps the request and the plan being prepared on this machine (<see cref="PlanDraft"/>), and brings them back when the
    /// tab opens again. Without it nothing is kept.
    /// </param>
    public OrchestratorDocumentViewModel(
        string projectRoot,
        OrchestrationSession session,
        ICanvasLayoutStore layout,
        TimeProvider time,
        SynchronizationContext? uiContext = null,
        ReinforcedConfirmation? confirmation = null,
        IPlanDraftStore? drafts = null)
        : base(CreateId(projectRoot), $"Orquestador · {RecentProjectItem.From(projectRoot).Name}")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(time);
        ProjectRoot = projectRoot;
        _session = session;
        _layout = layout;
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        _confirmation = confirmation;
        _drafts = drafts;

        // The canvas gets the project only once its storage opened (OnContextChanged): until then, or when opening it was
        // refused, the positions stay in memory and nothing is written in the project.
        Canvas = new CanvasViewModel(time) { LayoutStore = layout };
        Monitor = new RunMonitorViewModel(time, _ui, session.Delivery, session.Evidence);
        Monitor.CloseRequested += (_, _) => CloseRunViewCommand.Execute(null);
        Requirements = new RequirementsEditorViewModel(session, time, _ => ReloadAsync());
        Requirements.PropertyChanged += OnRequirementsPropertyChanged;
        Roster = new RosterEditorViewModel(
            session.CanSaveAgents ? (project, agent) => session.SaveAgentAsync(project, agent, CancellationToken.None) : null,
            OnRosterSavedAsync);
        Canvas.PlanChanged += (_, _) =>
        {
            if (!Canvas.IsEmpty)
            {
                _planEmptiedByUser = false;
            }

            Revalidate();
            DraftChanged();
        };
        Canvas.PropertyChanged += OnCanvasPropertyChanged;
        Canvas.NodeCreated += (_, _) => CollapsePreparationWhenReady();
        Canvas.SelectionDeleted += (_, _) => _planEmptiedByUser = Canvas.IsEmpty;

        // What waits to be kept is kept when the tab closes and when the app does (its dispatcher shuts down on this thread).
        Closed += (_, _) => FlushDraft();
        if (System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher)
        {
            dispatcher.ShutdownStarted += (_, _) => FlushDraft();
        }
        Monitor.TaskStatusChanged += (_, update) => Canvas.SetStatus(update.Task, update.Status);
        Monitor.Finished += (_, _) => OnRunFinished();
        Monitor.PropertyChanged += OnMonitorPropertyChanged;
        Revalidate();
    }

    public static string CreateId(string projectRoot) => IdPrefix + Path.GetFullPath(projectRoot).ToUpperInvariant();

    /// <summary>
    /// Raised on the UI thread with a run the person started or resumed here that the monitor could not take, because it followed
    /// another run by then: the shell follows it with a monitor of its own, which the tray follows. Without a handler, the
    /// document keeps it in a monitor of its own.
    /// </summary>
    public event EventHandler<HandedOffRun>? RunHandedOff;

    public string ProjectRoot { get; }

    public CanvasViewModel Canvas { get; }

    public RunMonitorViewModel Monitor { get; }

    /// <summary>The «Requisitos» panel: the requirements of the project, frozen through the requirement store.</summary>
    public RequirementsEditorViewModel Requirements { get; }

    /// <summary>
    /// The model and the «Memoria del proyecto» of each agent of the template (G1; docs/14 section 9, WP5): saved with the roster
    /// store; a change withdraws the approval of a plan that waits to run, and nothing changes while the tab is busy or follows
    /// a run.
    /// </summary>
    public RosterEditorViewModel Roster { get; }

    /// <summary>The preparation band (request, requirements, agents, profile) is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreparationToggleText))]
    private bool _isPreparationExpanded = true;

    public string PreparationToggleText => IsPreparationExpanded ? "Ocultar" : "Mostrar";

    /// <summary>The preparation in one line, which stays on screen while the band is hidden.</summary>
    public string PreparationSummary => string.Join(
        " · ",
        string.IsNullOrWhiteSpace(RequestText) ? "Sin petición" : "Petición escrita",
        Requirements.HasPendingChanges
            ? "requisitos con cambios sin congelar"
            : Context?.Requirements is { } frozen ? $"requisitos congelados ({Short(frozen.ContentHash)})" : "sin requisitos congelados",
        (Context?.Roster.Count ?? 0) switch
        {
            0 => "sin agentes",
            1 => "1 agente",
            var count => count.ToString(CultureInfo.InvariantCulture) + " agentes",
        },
        Context switch
        {
            { Verification: { } profile } => $"perfil «{profile.Name}» ({Short(profile.ContentHash)})",
            { VerificationProposal: not null } => "perfil propuesto sin aceptar",
            _ => "sin perfil de verificación",
        });

    [RelayCommand]
    private void TogglePreparation() => IsPreparationExpanded = !IsPreparationExpanded;

    /// <summary>Nothing is missing in the preparation: the request is written, the requirements frozen without changes, and the project has agents and a profile.</summary>
    public bool IsPreparationComplete =>
        !string.IsNullOrWhiteSpace(RequestText) && !Requirements.HasPendingChanges
        && Context is { Requirements: not null, Verification: not null, Roster.Count: > 0 };

    /// <summary>
    /// Folds the preparation once nothing is missing there, when the work moves to the canvas (a node created, a plan asked for
    /// or proposed, a run started or opened): the canvas, where the plan is, gets the room. «Mostrar» opens it again.
    /// </summary>
    private void CollapsePreparationWhenReady()
    {
        if (IsPreparationExpanded && IsPreparationComplete)
        {
            IsPreparationExpanded = false;
        }
    }

    /// <summary>The agents of the project, one line each: id, name, CLI and vendor, model if any, and tools.</summary>
    public IReadOnlyList<string> RosterLines => [.. (Context?.Roster ?? []).Select(RosterText.Line)];

    /// <summary>Said once the project got the default roster while opening; null otherwise.</summary>
    public string? RosterNotice => Context?.RosterCreated == true
        ? "El proyecto no tenía agentes: se creó la plantilla por defecto (Claude orquesta y ejecuta, Codex juzga)."
        : null;

    /// <summary>The profile is missing and a proposal is on screen, waiting for «Usar perfil propuesto».</summary>
    public bool ShowsProfileProposal => Context is { Verification: null, VerificationProposal: not null };

    /// <summary>The project's profile (name, hash and steps), the proposal, or that there is none.</summary>
    public string ProfileText => Context switch
    {
        { Verification: { } profile } => $"«{profile.Name}» · hash {Short(profile.ContentHash)} · {Steps(profile)}. Es lo que Agent Studio ejecuta para verificar; cada ejecución congela esta versión.",
        { VerificationProposal: { } proposal } => $"Propuesta «{proposal.Name}» · hash {Short(proposal.ContentHash)} · {Steps(proposal)}. Todavía no es el perfil del proyecto: revisa sus comandos, que son los que se ejecutarán en tu máquina.",
        _ => "Sin perfil de verificación.",
    };

    /// <summary>
    /// The steps of the profile on screen (the project's or the proposed one), as the user should read them before they run
    /// on the machine: id, title, command line and, for a step without isolation, that the agents' code runs with the
    /// user's own rights (<see cref="VerificationStepText.Line"/>).
    /// </summary>
    public IReadOnlyList<string> ProfileSteps => [.. ((Context?.Verification ?? Context?.VerificationProposal)?.Steps ?? []).Select(VerificationStepText.Line)];

    /// <summary>
    /// Shown next to «Aprobar plan»: the writers at medium integrity of the plan as drawn now, each with its task and why
    /// (an agent with Bash, an executor that writes with Codex or with a CLI or a version whose confinement was not
    /// measured, a step the plan runs without isolation...), and what that means for the run (ADR-0018, decision 3). It
    /// is read with the roster and the project's profile on screen and the confinement probe's answer, exactly as the
    /// engine reads the check of the user's repository and the approval request it records, which carries this same
    /// text. Recomputed on every change of the plan or the context; null when there is no writer of that kind.
    /// </summary>
    public string? IsolationWarning { get; private set; }

    public bool HasIsolationWarning => IsolationWarning is not null;

    /// <summary>
    /// What the validator warns about a valid plan before it is approved (B10): writing that no integration reviewed by the
    /// judge takes in, so the run could not be delivered (<see cref="PlanValidator.Warnings"/>). The engine says the same when
    /// the run starts; here it is seen before spending quota. Null when the plan has violations (they come first) or nothing
    /// to warn about.
    /// </summary>
    public string? DeliveryWarning { get; private set; }

    public bool HasDeliveryWarning => DeliveryWarning is not null;

    /// <summary>«Usar perfil propuesto»: saves the proposal as the project's profile (only ever on the user's word).</summary>
    [RelayCommand(CanExecute = nameof(CanUseProposedProfile))]
    private async Task UseProposedProfileAsync()
    {
        if (Context is not { Project: { } project, VerificationProposal: { } proposal })
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            await _session.SaveVerificationProfileAsync(project, proposal, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The profile is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo guardar el perfil de verificación: {ex.Message}";
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    private bool CanUseProposedProfile() => !IsBusy && ShowsProfileProposal && Context?.Project is not null;

    /// <summary>The properties panel of the selected node, or null.</summary>
    [ObservableProperty]
    private NodeEditorViewModel? _editor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Missing))]
    [NotifyPropertyChangedFor(nameof(HasMissing))]
    private SessionContext? _context;

    public bool HasEngine => _session.HasEngine;

    public string EngineStatus => HasEngine ? "Motor de orquestación conectado" : "Motor no disponible";

    public Tone EngineTone => HasEngine ? Tone.Success : Tone.Warning;

    /// <summary>What the screen cannot do and why, one line each (empty when everything is available).</summary>
    public IReadOnlyList<string> Missing => Context?.Missing ?? (HasEngine ? [] : ["Motor no disponible: el lienzo funciona, pero no se puede pedir un plan ni ejecutarlo."]);

    public bool HasMissing => Missing.Count > 0;

    /// <summary>
    /// Why the project's decision log cannot be trusted, in Spanish (ADR-0010); null when it can. Nothing is planned, run
    /// or resumed on it: the engine refuses, and the screen says so.
    /// </summary>
    public string? IntegrityProblem => Context?.IntegrityProblem;

    public bool HasIntegrityProblem => IntegrityProblem is not null;

    /// <summary>The problem is a history no anchor recognises here, which the person may accept («Aceptar este historial como nuevo origen»).</summary>
    public bool CanAcceptHistory => Context is { Project: not null, Unanchored: not null } && _session.CanAcceptHistory;

    /// <summary>
    /// «Aceptar este historial como nuevo origen»: the approval dialog asks the person, and only their yes records the
    /// acceptance and anchors the history; then the project is read again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAcceptHistoryNow))]
    private async Task AcceptHistoryAsync()
    {
        if (Context is not { Project: { } project, Unanchored: { } history })
        {
            return;
        }

        IsBusy = true;
        BusyText = "Esperando tu respuesta sobre el historial de decisiones…";
        ErrorMessage = null;
        bool accepted;
        try
        {
            accepted = await _session.AcceptHistoryAsync(project, history, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The recorder is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo aceptar el historial: {ex.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }

        if (!accepted)
        {
            ErrorMessage = "No aceptaste el historial: el registro de decisiones sigue bloqueado.";
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    private bool CanAcceptHistoryNow() => !IsBusy && CanAcceptHistory;

    // ---------- The plan under review ----------

    /// <summary>The user's request, literally: what the orchestrator plans for and what the run records.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyPropertyChangedFor(nameof(ExecuteBlockers))]
    [NotifyPropertyChangedFor(nameof(PreparationSummary))]
    private string _requestText = string.Empty;

    [ObservableProperty]
    private string _planSummary = string.Empty;

    /// <summary>The risks the orchestrator listed with its plan (a plan drawn by hand has none).</summary>
    public ObservableCollection<string> Risks { get; } = [];

    /// <summary>The last plan the orchestrator agent proposed, as it came.</summary>
    [ObservableProperty]
    private PlanProposal? _proposal;

    [ObservableProperty]
    private string? _proposalStatus;

    /// <summary>What the orchestrator agent did while planning.</summary>
    public ObservableCollection<string> PlanningActivity { get; } = [];

    /// <summary>
    /// The context of the orchestrator agent while it plans (docs/18 P3), followed step by step from its events (P36) with the
    /// windows the project knows: a bar with its origin and level, an empty track that says why when there is no percentage.
    /// Null until a plan is asked.
    /// </summary>
    [ObservableProperty]
    private MetricBarReading? _planningContext;

    /// <summary>Follows the context of the planning in course (or the last one); null before the first.</summary>
    private ContextHealthTracker? _planningTracker;

    /// <summary>The validator's verdict on the plan as drawn now (recomputed on every change).</summary>
    public IReadOnlyList<PlanViolation> Violations { get; private set; } = [];

    /// <summary>The validator's messages, in Spanish; shown in the panel and next to the nodes after «Validar».</summary>
    public ObservableCollection<PlanProblem> Problems { get; } = [];

    [ObservableProperty]
    private ValidationState _validationState;

    /// <summary>True once «Validar» was pressed: from then on the messages follow every change.</summary>
    [ObservableProperty]
    private bool _showValidation;

    /// <summary>The hash of the plan as drawn now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowUpOf))]
    private string _currentHash = string.Empty;

    /// <summary>
    /// The user's last decision on the plan: approval (what <see cref="RunRequest.PlanApproval"/> carries, with how the
    /// person was confirmed) or rejection.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApproved))]
    [NotifyPropertyChangedFor(nameof(ApprovalText))]
    [NotifyPropertyChangedFor(nameof(HelloNotice))]
    [NotifyPropertyChangedFor(nameof(HasHelloNotice))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private ApprovalDecision? _planDecision;

    /// <summary>The plan as drawn now is the one the user approved (an edit voids the approval).</summary>
    public bool IsApproved => PlanDecision is { Approved: true } && string.Equals(_approvedHash, CurrentHash, StringComparison.Ordinal);

    /// <summary>
    /// The decision on the plan, in a line. While the monitor shows a run, it says the plan that run follows, approved when it
    /// started (docs/03 section 3), and never what the draft of the canvas would need.
    /// </summary>
    public string ApprovalText => Monitor.RunId is { } shown ? FollowedPlanText(shown) : PlanDecision switch
    {
        { Approved: true } when IsApproved && FollowUpOf is { } followed => $"Plan aprobado ({Short(CurrentHash)}): «Ejecutar» empieza una ejecución de seguimiento de {followed}.",
        { Approved: true, Verification: ApprovalVerification.WindowsHello } when IsApproved => $"Plan aprobado ({Short(CurrentHash)}), confirmado con Windows Hello.",
        { Approved: true } when IsApproved => $"Plan aprobado ({Short(CurrentHash)}).",
        { Approved: true } => "El plan cambió después de aprobarlo: apruébalo otra vez.",
        { Approved: false } => "Plan rechazado.",
        _ => "Plan sin aprobar.",
    };

    /// <summary>The plan of the run the monitor shows: every run started on a plan the person approved.</summary>
    private string FollowedPlanText(RunId run)
    {
        var plan = _followedPlanHash is { } hash ? $"el plan {Short(hash)}" : "el plan con el que empezó";
        return Monitor switch
        {
            { IsInspecting: true } => $"Viendo la ejecución {run}: siguió {plan}, aprobado al iniciarla.",
            { IsActive: true } => $"La ejecución {run} sigue {plan}, aprobado al iniciarla.",
            _ => $"La ejecución {run} terminó con {plan}, aprobado al iniciarla.",
        };
    }

    /// <summary>
    /// What «Aprobar plan» says of Windows Hello (docs/16 D-13): the warning when Windows said Hello is not there and the button
    /// approved the plan alone, while that approval stands; null otherwise. <c>HelloNoticeView</c> shows it in the review.
    /// </summary>
    public string? HelloNotice => IsApproved && PlanDecision is { Verification: ApprovalVerification.WindowsHelloUnavailable }
        ? $"Windows Hello no está disponible en este equipo ({HelloAnswers.WhyAbsent(_helloAbsent ?? UserPresenceAvailability.NotConfiguredForUser)}): "
          + "el plan lo aprobó solo el botón, y el registro lo dice."
        : null;

    public bool HasHelloNotice => HelloNotice is not null;

    /// <summary>The notice of «Aprobar plan» is only ever a warning.</summary>
    public Tone HelloNoticeTone => Tone.Warning;

    public string ValidationText => ValidationState switch
    {
        ValidationState.NoContext => "Sin contexto de validación: faltan los requisitos congelados o el perfil de verificación.",
        ValidationState.Valid => "Plan válido.",
        _ => Violations.Count == 1 ? "1 problema en el plan." : $"{Violations.Count} problemas en el plan.",
    };

    public Tone ValidationTone => ValidationState switch
    {
        ValidationState.Valid => Tone.Success,
        ValidationState.Invalid => Tone.Error,
        _ => Tone.Warning,
    };

    /// <summary>Why «Ejecutar» is disabled, in a line; empty when it is enabled.</summary>
    public string ExecuteBlockers => string.Join(" · ", Blockers());

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseProposedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(AcceptHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ViewRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseRunViewCommand))]
    [NotifyPropertyChangedFor(nameof(ExecuteBlockers))]
    [NotifyPropertyChangedFor(nameof(CanChooseResumeConsent))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<RunListItem> PreviousRuns { get; } = [];

    /// <summary>The label of the consent of the next run, next to «Ejecutar» (docs/16 D-39).</summary>
    public const string ResumeConsentLabel = "Reanudar sola si un límite o un cierre la interrumpe";

    /// <summary>What the consent allows, as its tooltip says it.</summary>
    public const string ResumeConsentToolTip =
        "Con la casilla marcada, una tarea que llega al límite de uso espera al reinicio de la cuota más 3 minutos, por largo que "
        + "sea, y sigue sola; y si la app se cierra sin que lo pidas (o Windows cierra la sesión), la ejecución se reanuda al "
        + "volver a abrirla, sin preguntarte. Nunca si la pausaste o la dejaste al salir, y nunca pasa del presupuesto aprobado. "
        + "Vale solo para la próxima ejecución y queda en su cabecera.";

    /// <summary>
    /// The consent of the run «Ejecutar» starts to wait for the quota and to resume by itself after a restart (docs/16 D-39,
    /// decision 4 (a); ADR-0034): it goes into its <see cref="RunOptions.AutoResumeConsent"/>, and so into its header and the
    /// hash <c>RunStarted</c> anchors. Off by default and again after each start: every run is consented on its own.
    /// </summary>
    [ObservableProperty]
    private bool _autoResumeConsent;

    /// <summary>The consent can be changed now: no run is on screen and nothing is in course.</summary>
    public bool CanChooseResumeConsent => !Monitor.IsAttached && !IsBusy;

    /// <summary>
    /// Completes once the header of a run the scheduler resumed by its consent was read (whether it has the consent, and its
    /// plan on the canvas): <see cref="FollowResumed"/>.
    /// </summary>
    public Task ResumedCheck { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The run «Ejecutar» starts a follow-up of (<see cref="RunRequest.Follows"/>): one whose resume the engine refused
    /// because the writers at medium integrity of its plan are not the ones it was approved with (G-02), while its plan is
    /// the one on the canvas. Null otherwise: «Rechazar», another proposal, «Editar el plan» or a run started leave none,
    /// and an edited plan is not a follow-up of it.
    /// </summary>
    public RunId? FollowUpOf => _followUp is { } followUp && string.Equals(followUp.PlanHash, CurrentHash, StringComparison.Ordinal) ? followUp.Run : _replanOf;

    /// <summary>
    /// «Preparar ejecución de seguimiento», after the orchestrator's auditor asked to replan <paramref name="run"/> (docs/15
    /// D-35): the canvas is emptied and editable, <paramref name="request"/> (the request with the auditor's findings) is
    /// the request, and the next run started here follows <paramref name="run"/>, which attends the request. Nothing is
    /// planned nor started: the plan is asked for, approved and run as any other.
    /// </summary>
    public async Task PrepareFollowUpAsync(RunId run, string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (Monitor.IsActive)
        {
            return;
        }

        await Monitor.DetachAsync().ConfigureAwait(true);
        ForgetInspection();
        Canvas.IsReadOnly = false;
        Canvas.Clear();
        Proposal = null;
        ProposalStatus = null;
        _approvedHash = null;
        _approvedRoster = null;
        _approvedWarning = null;
        PlanDecision = null;
        SetFollowUp(null);
        _replanOf = run;
        RequestText = request;
        OnPropertyChanged(nameof(FollowUpOf));
        OnPropertyChanged(nameof(ApprovalText));
        RebuildEditor();
        RefreshCommands();
    }

    /// <summary>Loads the project's context, runs and layout once; called when the tab is opened.</summary>
    public async Task OpenAsync()
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Reads requirements, agents, profile and runs again (after they were changed elsewhere).</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        BusyText = "Abriendo el proyecto para orquestar…";
        try
        {
            Context = await _session.OpenAsync(ProjectRoot, CancellationToken.None).ConfigureAwait(true);
            await RefreshRunsAsync().ConfigureAwait(true);
            await RestoreDraftAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    partial void OnContextChanging(SessionContext? oldValue, SessionContext? newValue)
    {
        // The approval was given against the frozen requirements on screen: another version needs a new approval.
        if (!string.Equals(oldValue?.Requirements?.ContentHash, newValue?.Requirements?.ContentHash, StringComparison.Ordinal) && PlanDecision is not null)
        {
            _approvedHash = null;
            _approvedRoster = null;
            _approvedWarning = null;
            PlanDecision = null;
        }
    }

    partial void OnContextChanged(SessionContext? value)
    {
        // The first context decides whether the preparation is shown (something to do there) or hidden (the project is
        // ready); after that it is the user's choice.
        if (!_preparationShownOnce && value is not null)
        {
            _preparationShownOnce = true;
            IsPreparationExpanded = value is not { Requirements: not null, Verification: not null } || value.Roster.Count == 0;
        }

        // Positions are saved only in a storage the workspace opened (ADR-0010, addenda of 2026-10-18): a project that was
        // not opened, or whose storage was refused, gets no file from the canvas. Nor while the canvas shows a run only looked
        // at: the positions saved are those of the draft that comes back.
        Canvas.ProjectRoot = Monitor.IsInspecting ? null : value?.Project?.RootPath;
        Requirements.Load(value?.Project, value?.Requirements);
        Roster.Load(value?.Project, value?.Roster ?? []);
        UpdateRosterLock();
        RebuildEditor();
        Revalidate();
        RefreshCommands();
        OnPropertyChanged(nameof(RosterLines));
        OnPropertyChanged(nameof(RosterNotice));
        OnPropertyChanged(nameof(ShowsProfileProposal));
        OnPropertyChanged(nameof(ProfileText));
        OnPropertyChanged(nameof(ProfileSteps));
        OnPropertyChanged(nameof(PreparationSummary));
        OnPropertyChanged(nameof(IntegrityProblem));
        OnPropertyChanged(nameof(HasIntegrityProblem));
        OnPropertyChanged(nameof(CanAcceptHistory));
    }

    private void OnRequirementsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RequirementsEditorViewModel.HasPendingChanges))
        {
            RefreshCommands();
            OnPropertyChanged(nameof(PreparationSummary));
        }
    }

    partial void OnPlanSummaryChanged(string value)
    {
        Revalidate();
        DraftChanged();
    }

    partial void OnRequestTextChanged(string value) => DraftChanged();

    partial void OnIsBusyChanged(bool value) => UpdateRosterLock();

    /// <summary>The template may change only while the tab is idle and no run is on screen (the plan there belongs to that run).</summary>
    private void UpdateRosterLock() => Roster.IsLocked = IsBusy || Monitor.IsAttached;

    /// <summary>
    /// The template changed from the preparation (a model or «Memoria del proyecto»): a plan approved against the previous one is approved no more (the
    /// run would ask for a template the store no longer holds), so the user approves it again, and the template is read
    /// again. A change made elsewhere keeps the approval and the engine refuses to start on it, as before.
    /// </summary>
    private async Task OnRosterSavedAsync()
    {
        if (PlanDecision is { Approved: true })
        {
            _approvedHash = null;
            _approvedRoster = null;
            _approvedWarning = null;
            PlanDecision = null;
            Canvas.Notify("Cambiaste la plantilla de agentes: vuelve a aprobar el plan.", NoticeKind.Error);
            RefreshCommands();
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    partial void OnShowValidationChanged(bool value) => Revalidate();

    // ---------- Commands of the plan ----------

    /// <summary>«Validar»: shows the validator's messages next to the nodes and in the panel.</summary>
    [RelayCommand]
    private void Validate()
    {
        ShowValidation = true;
        Revalidate();
        Canvas.Notify(ValidationText, ValidationState == ValidationState.Valid ? NoticeKind.Info : NoticeKind.Error);
    }

    /// <summary>
    /// «Aprobar plan». The approval of a plan is a critical question (the engine records it so), so with Windows Hello on it
    /// follows the rule of every critical question (<see cref="ReinforcedConfirmation"/>, docs/16 D-13, F8-11): Windows Hello
    /// confirms the person after the click, and only a confirmation approves; when Windows says Hello is not there, the click
    /// decides alone and <see cref="HelloNotice"/> warns. A yes Hello does not confirm approves nothing, so no run can start,
    /// and <see cref="ErrorMessage"/> says why. «Rechazar» never asks Hello.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        var hash = CurrentHash;
        var roster = Context?.Roster;
        var warning = IsolationWarning ?? string.Empty;
        var plan = ConfirmationPlan.AnswerAlone;
        if (_confirmation is not null)
        {
            ErrorMessage = null;
            plan = await _confirmation.PlanAsync(RiskLevel.Critical, CancellationToken.None).ConfigureAwait(true);
            if (plan.AsksHello)
            {
                var hello = await _confirmation.ConfirmAsync(HelloAnswers.PlanMessage(Short(hash)), CancellationToken.None).ConfigureAwait(true);
                if (!hello.Confirmed)
                {
                    ErrorMessage = hello.Problem is { } problem
                        ? $"No se pudo pedir Windows Hello ({problem}): el plan no se aprueba."
                        : $"Windows Hello no confirmó que eres tú ({HelloAnswers.WhyNotConfirmed(hello.Result ?? UserPresenceResult.Canceled)}): el plan no se aprueba.";
                    return;
                }
            }

            // What Windows confirmed is the plan as it was drawn when the button was pressed.
            if (!string.Equals(hash, CurrentHash, StringComparison.Ordinal))
            {
                ErrorMessage = "El plan cambió mientras se confirmaba: apruébalo otra vez.";
                return;
            }
        }

        _approvedHash = hash;
        _approvedRoster = roster;
        _approvedWarning = warning;
        _helloAbsent = plan.Availability;
        PlanDecision = new ApprovalDecision(true, ApprovalPromptService.DecidedBy, _time.GetUtcNow(), $"Plan {Short(hash)} aprobado en el lienzo.", Verification: plan.Confirmation);
        RefreshCommands();
    }

    private bool CanApprove() => !Monitor.IsAttached && ValidationState == ValidationState.Valid && !Canvas.IsEmpty;

    [RelayCommand(CanExecute = nameof(CanReject))]
    private void Reject()
    {
        _approvedHash = null;
        _approvedRoster = null;
        _approvedWarning = null;
        PlanDecision = new ApprovalDecision(false, ApprovalPromptService.DecidedBy, _time.GetUtcNow(), $"Plan {Short(CurrentHash)} rechazado en el lienzo.");
        if (Proposal is not null)
        {
            ProposalStatus = "Rechazaste el plan del orquestador: corrígelo en el lienzo o pide otro.";
        }

        if (_followUp is not null)
        {
            // The follow-up offered for a refused resume (G-02) is discarded, and the message that offered it with it.
            SetFollowUp(null);
            ErrorMessage = null;
        }

        RefreshCommands();
    }

    private bool CanReject() => !Monitor.IsAttached && (Proposal is not null || !Canvas.IsEmpty);

    /// <summary>«Pedir plan»: the orchestrator agent proposes a plan for the request (spends quota).</summary>
    [RelayCommand(CanExecute = nameof(CanRequestPlan))]
    private async Task RequestPlanAsync()
    {
        if (Context is not { Project: { } project, Requirements: { } requirements, Verification: { } profile, Orchestrator: { } orchestrator } context)
        {
            return;
        }

        using var planning = new CancellationTokenSource();
        _planning = planning;
        CancelPlanningCommand.NotifyCanExecuteChanged();
        CollapsePreparationWhenReady();
        IsBusy = true;
        BusyText = "El orquestador está preparando el plan…";
        ErrorMessage = null;
        PlanningActivity.Clear();

        // Claude Code reports the context of each step; codex exec and Gemini do not, and theirs is «no expuesto» (docs/14 D-09).
        var provider = orchestrator.Model.Provider;
        _planningTracker = new ContextHealthTracker(Monitor.ContextThresholds, Monitor.ContextWindows, provider, reportsContextOccupancy: MetricBars.ReportsContext(provider));
        PlanningContext = MetricBars.Context(PlanningContextLabel, _planningTracker.Current, Monitor.ContextThresholds);
        try
        {
            var request = new PlanningRequest(project, RequestText.Trim(), requirements, context.Roster, orchestrator.Id, profile, Limits);
            var proposal = await _session.PlanAsync(request, new UiProgress(this), planning.Token).ConfigureAwait(true);
            LoadProposal(proposal);
        }
        catch (OperationCanceledException)
        {
            ProposalStatus = "Planificación cancelada.";
        }
#pragma warning disable CA1031 // The engine is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"El orquestador no pudo planificar: {ex.Message}";
        }
        finally
        {
            _planning = null;
            CancelPlanningCommand.NotifyCanExecuteChanged();
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanRequestPlan() =>
        HasEngine && !IsBusy && !Monitor.IsAttached && !string.IsNullOrWhiteSpace(RequestText) && !Requirements.HasPendingChanges
        && Context is { Project: not null, Requirements: not null, Verification: not null, Orchestrator: not null, IntegrityProblem: null };

    [RelayCommand(CanExecute = nameof(IsPlanning))]
    private void CancelPlanning() => _planning?.Cancel();

    private bool IsPlanning() => _planning is not null;

    /// <summary>«Ejecutar»: starts the approved plan. Disabled while the plan is invalid, unapproved or the engine is missing.</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (Context is not { Project: { } project, Requirements: { } requirements, Verification: { } profile } || PlanDecision is not { Approved: true } approval)
        {
            return;
        }

        // The roster goes as it was validated and shown when the user approved the plan: the engine refuses to start if the
        // store holds another one (edited after the approval, or read again since). A follow-up of a refused resume (G-02)
        // is a new run: it never takes the id of the refused run, nor that of a proposal.
        var plan = CurrentPlan();
        var follows = FollowUpOf;

        // The run of a proposal is used once: a proposal that already started a run (listed among the previous runs) runs
        // again as a new run, never with the id of that one, which the engine refuses.
        var fromProposal = follows is null && Proposal is { Plan: { } proposed } offered && string.Equals(proposed.ContentHash, plan.ContentHash, StringComparison.Ordinal)
            && IsUnused(offered.Run);
        var options = new RunOptions(AutoResumeConsent: AutoResumeConsent);
        var request = new RunRequest(project, fromProposal ? Proposal!.Run : null, RequestText.Trim(), requirements, plan, approval, profile, options, Follows: follows, ValidatedRoster: _approvedRoster, ApprovedWarning: _approvedWarning);
        IsBusy = true;
        BusyText = "Iniciando la ejecución…";
        ErrorMessage = null;
        try
        {
            var run = await _session.StartAsync(request, CancellationToken.None).ConfigureAwait(true);
            _replanOf = null;
            SetFollowUp(null);

            // Every run is consented on its own: once one started, the next one starts without it unless it is marked again.
            // A start that failed started nothing, and the box stays as the person left it.
            AutoResumeConsent = false;
            FollowOwn(run, project, options.AutoResumeConsent);
            NoteFollowedPlan(run.Id, plan.ContentHash);
            CollapsePreparationWhenReady();
        }
#pragma warning disable CA1031 // The engine is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The engine may have recorded the start before failing: the approval is never used twice, so the next «Ejecutar»
            // asks for a new one (and for Windows Hello again when it is on).
            ForgetApproval();
            ErrorMessage = $"No se pudo iniciar la ejecución: {ex.Message} Vuelve a aprobar el plan para intentarlo de nuevo.";
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool CanExecute() => Blockers().Count == 0;

    /// <summary>After a run finished: forget it and edit the plan again.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPlan))]
    private async Task EditPlanAsync()
    {
        await Monitor.DetachAsync().ConfigureAwait(true);
        Canvas.IsReadOnly = false;
        Canvas.ResetStatuses();
        _approvedHash = null;
        _approvedRoster = null;
        _approvedWarning = null;
        PlanDecision = null;
        SetFollowUp(null);
        RebuildEditor();
        RefreshCommands();
    }

    /// <summary>A run that finished here leaves its plan to edit; a run only looked at gives the canvas back with «Cerrar».</summary>
    private bool CanEditPlan() => Monitor.IsAttached && !Monitor.IsActive && !Monitor.IsInspecting;

    /// <summary>
    /// Resumes an interrupted run (spends quota; the user decides). One the engine refuses because the writers at medium
    /// integrity of its plan are not the ones it was approved with (G-02) is offered as a follow-up instead
    /// (<see cref="OfferFollowUp"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResumeRun))]
    private async Task ResumeRunAsync(RunListItem? item)
    {
        if (item is null || Context?.Project is not { } project)
        {
            return;
        }

        IsBusy = true;
        BusyText = $"Reanudando la ejecución {item.Summary.Id}…";
        ErrorMessage = null;
        PlanDefinition? plan = null;
        try
        {
            // A run only looked at gives the monitor back first: the run resumed is followed there.
            if (Monitor.IsInspecting)
            {
                await PutAwayInspectedRunAsync().ConfigureAwait(true);
            }

            // The monitor is looked at again after every wait: one that took another run meanwhile is not taken from it.
            plan = await _session.FindPlanAsync(project, item.Summary.PlanHash, CancellationToken.None).ConfigureAwait(true);
            if (!MonitorStillFree(item.Summary.Id))
            {
                return;
            }

            var header = await _session.FindRunAsync(project, item.Summary.Id, CancellationToken.None).ConfigureAwait(true);
            if (!MonitorStillFree(item.Summary.Id))
            {
                return;
            }

            var run = await _session.ResumeAsync(project, item.Summary.Id, CancellationToken.None).ConfigureAwait(true);
            if (plan is not null && !Monitor.IsAttached)
            {
                // The plan of the run is the run's: it does not become the draft.
                using (SuppressDraftSaves())
                {
                    ShowPlan(plan);
                }
            }

            SetFollowUp(null);
            FollowOwn(run, project, header?.Options.AutoResumeConsent);
            NoteFollowedPlan(run.Id, item.Summary.PlanHash);
        }
        catch (RunNeedsApprovalException refused)
        {
            OfferFollowUp(item.Summary.Id, plan, refused);
        }
#pragma warning disable CA1031 // The engine is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo reanudar la ejecución: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>
    /// The engine refused to resume <paramref name="run"/>: the writers at medium integrity of its plan are not the ones it
    /// was approved with (G-02), and nothing was recovered or launched. The screen says so, with the warning the plan has
    /// now, and puts that plan on the canvas unapproved, so the warning next to «Aprobar plan» is the one it would run
    /// with. Approving it again and «Ejecutar» start a follow-up run of <paramref name="run"/> (the engine checks that
    /// warning again); «Rechazar» discards it. The refused run stays as it is, interrupted.
    /// </summary>
    private void OfferFollowUp(RunId run, PlanDefinition? plan, RunNeedsApprovalException refused)
    {
        _approvedHash = null;
        _approvedRoster = null;
        _approvedWarning = null;
        PlanDecision = null;
        if (plan is null)
        {
            SetFollowUp(null);
        }
        else
        {
            ShowPlan(plan);
            ShowValidation = true;
            SetFollowUp(new FollowUp(run, plan.ContentHash));
        }

        ErrorMessage = ResumeRefusedText(run, refused, plan is not null);
        RefreshCommands();
    }

    /// <summary>Why a resume was refused for its writers at medium integrity, the warning its plan has now and what can be done, in Spanish.</summary>
    private static string ResumeRefusedText(RunId run, RunNeedsApprovalException refused, bool planShown)
    {
        var why = refused.WarningAnchored
            ? "los escritores con integridad Medium de su plan ya no son los que aprobaste (otra versión de Claude Code instalada, por ejemplo)"
            : "empezó antes de que el registro guardara los escritores con integridad Medium que se aprobaron, y su plan los tiene ahora";
        var now = refused.CurrentWarning is { } warning
            ? $"Aviso de ahora: «{warning}»."
            : "Ahora su plan no tiene escritores con integridad Medium.";
        var next = planShown
            ? $"Su plan está en el lienzo: si lo apruebas otra vez y pulsas «Ejecutar», empieza una ejecución de seguimiento de {run}; «Rechazar» lo descarta."
            : "Su plan no está disponible aquí para aprobarlo otra vez.";
        return $"No se reanuda la ejecución {run}: {why}. {now} {next}";
    }

    private void SetFollowUp(FollowUp? followUp)
    {
        _followUp = followUp;
        OnPropertyChanged(nameof(FollowUpOf));
        OnPropertyChanged(nameof(ApprovalText));
    }

    /// <summary>The monitor is free, or only shows a run read back from its log, which «Reanudar» puts away first.</summary>
    private bool CanResumeRun(RunListItem? item) =>
        HasEngine && !IsBusy && (!Monitor.IsAttached || (Monitor.IsInspecting && !Monitor.IsDelivering)) && item?.CanResume == true && Context?.Project is not null;

    [RelayCommand]
    private void SelectProblem(PlanProblem? problem)
    {
        if (problem?.Task is { } task && Canvas.Nodes.FirstOrDefault(n => n.Id == task) is { } node)
        {
            Canvas.Select(node);
        }
    }

    // ---------- Building blocks ----------

    /// <summary>
    /// The plan as drawn now. When it is exactly what the orchestrator proposed, the proposal itself (its source and
    /// author); otherwise a plan by the user. The hash does not depend on source or author, so the two compare.
    /// </summary>
    public PlanDefinition CurrentPlan()
    {
        var drawn = PlanDefinition.Create(PlanSummary, Canvas.BuildTasks(), [.. Risks], PlanSource.User, null);
        return Proposal?.Plan is { } proposed && string.Equals(proposed.ContentHash, drawn.ContentHash, StringComparison.Ordinal)
            ? proposed
            : drawn;
    }

    /// <summary>Shows what the orchestrator agent proposed: its plan on the canvas, with the validator's messages.</summary>
    public void LoadProposal(PlanProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Proposal = proposal;
        _approvedHash = null;
        _approvedRoster = null;
        _approvedWarning = null;
        PlanDecision = null;
        SetFollowUp(null);
        if (proposal.Plan is not { } plan)
        {
            ProposalStatus = $"El orquestador no devolvió un plan utilizable ({AgentActivityText.Outcome(proposal.Status)}){(proposal.Error is null ? "." : ": " + proposal.Error)}";
            return;
        }

        ShowPlan(plan);
        ShowValidation = true;
        Revalidate();
        var corrected = proposal.Corrected ? " Se le pidió una corrección." : string.Empty;
        ProposalStatus = (ValidationState == ValidationState.Valid
            ? "Plan propuesto por el orquestador: revísalo y apruébalo o recházalo."
            : $"Plan propuesto por el orquestador con problemas: {ValidationText}") + corrected;

        // A proposal spent quota: it is kept at once, not after the pause of the edits.
        SaveDraftNow();
        CollapsePreparationWhenReady();
    }

    private void ShowPlan(PlanDefinition plan)
    {
        Risks.Clear();
        foreach (var risk in plan.Risks)
        {
            Risks.Add(risk);
        }

        PlanSummary = plan.Summary;
        Canvas.LoadTasks(plan.Tasks, _layout.Load(ProjectRoot));
    }

    /// <summary>
    /// Follows a run the scheduler resumed by the consent of its header (docs/16 D-39, ADR-0036): the monitor attaches it at
    /// once, so the run has its place in the tray, its pause and its choice on exit, whether the tab is open or not; then its
    /// header is read (<see cref="ResumedCheck"/>): whether it has the consent, and its plan, on the canvas. False when a run is
    /// on screen already (the monitor follows one run at a time), or the tab is busy («Pedir plan», «Ejecutar», «Reanudar» and
    /// the like wait for the engine, and would take the monitor or the canvas afterwards): the caller follows it elsewhere.
    /// </summary>
    /// <param name="run">The run, going: its events wait for the monitor, their one reader.</param>
    /// <param name="project">The project of the run, which the document may not have opened yet.</param>
    public bool FollowResumed(IOrchestrationRun run, ProjectContext project)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(project);
        if (Monitor.IsAttached || IsBusy)
        {
            return false;
        }

        SetFollowUp(null);
        Follow(run, project);
        ResumedCheck = ShowResumedHeaderAsync(run.Id, project);
        return true;
    }

    /// <summary>The header of a resumed run: its consent in the monitor, and its plan on the canvas with the statuses of its tasks.</summary>
    private async Task ShowResumedHeaderAsync(RunId run, ProjectContext project)
    {
        try
        {
            var header = await _session.FindRunAsync(project, run, CancellationToken.None).ConfigureAwait(true);
            var plan = header is null ? null : await _session.FindPlanAsync(project, header.PlanHash, CancellationToken.None).ConfigureAwait(true);
            if (Monitor.RunId != run)
            {
                return;
            }

            Monitor.ResumeConsent = header?.Options.AutoResumeConsent;
            NoteFollowedPlan(run, header?.PlanHash);
            if (plan is not null)
            {
                using (SuppressDraftSaves())
                {
                    ShowPlan(plan);
                }

                Canvas.IsReadOnly = true;
                foreach (var task in Monitor.Tasks)
                {
                    Canvas.SetStatus(task.Id, task.Status);
                }

                RebuildEditor();
                RefreshCommands();
            }
        }
#pragma warning disable CA1031 // The stores are behind ports: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"La ejecución {run} se reanudó sola, pero no se pudo leer su cabecera: {ex.Message}";
        }
    }

    /// <summary>
    /// Follows a run the person started or resumed here: in the monitor of the document, or, when the monitor followed another
    /// run by then, handed to the shell (<see cref="RunHandedOff"/>), which follows it with a monitor the tray follows. A started
    /// run is never left without a monitor, and nothing is thrown at the person.
    /// </summary>
    private void FollowOwn(IOrchestrationRun run, ProjectContext project, bool? resumeConsent)
    {
        if (!Monitor.IsAttached)
        {
            Follow(run, project);
            Monitor.ResumeConsent = resumeConsent;
            return;
        }

        var handed = new HandedOffRun(project, run, resumeConsent);
        if (RunHandedOff is { } handler)
        {
            handler(this, handed);
            ErrorMessage = $"La ejecución {run.Id} empezó mientras esta pestaña seguía otra ejecución: sigue en marcha, y la sigue la bandeja.";
            return;
        }

        var monitor = new RunMonitorViewModel(_time, _ui, _session.Delivery, _session.Evidence);
        monitor.Attach(run, project);
        monitor.ResumeConsent = resumeConsent;
        _handedOff.Add(monitor);
        ErrorMessage = $"La ejecución {run.Id} empezó mientras esta pestaña seguía otra ejecución: sigue en marcha.";
    }

    /// <summary>Before the engine resumes a run: false, and why in Spanish, when the monitor took another run meanwhile.</summary>
    private bool MonitorStillFree(RunId run)
    {
        if (!Monitor.IsAttached)
        {
            return true;
        }

        ErrorMessage = $"No se reanudó la ejecución {run}: esta pestaña sigue ahora otra ejecución. Reanúdala cuando termine.";
        return false;
    }

    private void Follow(IOrchestrationRun run, ProjectContext? project = null)
    {
        Canvas.IsReadOnly = true;
        Canvas.ResetStatuses();
        Monitor.Attach(run, project ?? Context?.Project);
        foreach (var task in Monitor.Tasks)
        {
            Canvas.SetStatus(task.Id, task.Status);
        }

        RebuildEditor();
        RefreshCommands();
    }

    private void OnRunFinished()
    {
        RefreshCommands();
        _ = RefreshRunsAsync();
    }

    private async Task RefreshRunsAsync()
    {
        if (Context?.Project is not { } project || !HasEngine)
        {
            return;
        }

        try
        {
            var runs = await _session.ListRunsAsync(project, CancellationToken.None).ConfigureAwait(true);
            PreviousRuns.Clear();
            foreach (var run in runs.OrderByDescending(r => r.CreatedAt))
            {
                var local = TimeZoneInfo.ConvertTime(run.CreatedAt, _time.LocalTimeZone);
                PreviousRuns.Add(new RunListItem(run, local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))
                {
                    IsViewed = Monitor.IsInspecting && Monitor.RunId == run.Id,
                });
            }

            RefreshCommands();
        }
#pragma warning disable CA1031 // The engine is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo leer la lista de ejecuciones: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs the validator on the plan as drawn now and shows the result (next to the nodes after «Validar»), and reads its
    /// writers at medium integrity for the warning next to «Aprobar plan».
    /// </summary>
    private void Revalidate()
    {
        var plan = CurrentPlan();
        CurrentHash = plan.ContentHash;
        IsolationWarning = Context is { Verification: { } profile } opened
            ? VerificationStepText.PlanWarning(plan, opened.Roster, profile, opened.ClaudeWritesConfined)
            : null;
        // While the canvas shows the plan of a run, the approval waits on the draft that comes back: nothing is withdrawn.
        if (PlanDecision is { Approved: true } && _approvedWarning is { } approvedWarning && !Monitor.IsAttached
            && !string.Equals(approvedWarning, IsolationWarning ?? string.Empty, StringComparison.Ordinal))
        {
            // The approval was given next to another warning (ADR-0018, A-04): what runs must be what the user saw.
            _approvedHash = null;
            _approvedRoster = null;
            _approvedWarning = null;
            PlanDecision = null;
            Canvas.Notify("El aviso del plan cambió desde que lo aprobaste: revísalo y vuelve a aprobarlo.", NoticeKind.Error);
        }

        var context = Context?.PlanContext(Limits);
        var problems = new List<PlanProblem>();
        if (context is null)
        {
            Violations = [];
            ValidationState = ValidationState.NoContext;
        }
        else
        {
            Violations = PlanValidator.Validate(plan, context);
            var texts = PlanViolationText.Describe(Violations, plan, context);
            problems.AddRange(Violations.Zip(texts, (v, text) => new PlanProblem(v.Task, text)));
            ValidationState = Violations.Count == 0 ? ValidationState.Valid : ValidationState.Invalid;
        }

        // Only for a valid plan: the warnings are not computed over violations (a plan proposed with too many tasks, say).
        DeliveryWarning = ValidationState == ValidationState.Valid && plan.Tasks.Count > 0
            ? AgentStudio.Orchestration.Planning.PlanWarningText.Notice(PlanValidator.Warnings(plan))
            : null;

        Problems.Clear();
        if (ShowValidation)
        {
            foreach (var problem in problems)
            {
                Problems.Add(problem);
            }
        }

        Canvas.ShowProblems(ShowValidation
            ? problems.Where(p => p.Task is not null).GroupBy(p => p.Task!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(p => p.Text)])
            : new Dictionary<TaskId, IReadOnlyList<string>>());

        OnPropertyChanged(nameof(Violations));
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(ValidationTone));
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(ApprovalText));
        OnPropertyChanged(nameof(HelloNotice));
        OnPropertyChanged(nameof(HasHelloNotice));
        OnPropertyChanged(nameof(IsolationWarning));
        OnPropertyChanged(nameof(HasIsolationWarning));
        OnPropertyChanged(nameof(DeliveryWarning));
        OnPropertyChanged(nameof(HasDeliveryWarning));
        RefreshCommands();
    }

    private List<string> Blockers()
    {
        var blockers = new List<string>();
        if (!HasEngine)
        {
            blockers.Add("Motor no disponible");
        }

        // A run on screen, in progress or finished, holds the canvas until «Editar el plan»: its approval and its run id
        // belong to that run and must not start another one.
        if (Monitor.IsActive)
        {
            blockers.Add("Hay una ejecución en curso");
        }
        else if (Monitor.IsInspecting)
        {
            // What the draft would need is said once it is back on the canvas, not over the plan of the run looked at.
            blockers.Add("Cierra la ejecución que estás viendo para volver a tu borrador");
            return blockers;
        }
        else if (Monitor.IsAttached)
        {
            blockers.Add("Pulsa «Editar el plan» para preparar otra ejecución");
        }

        if (IsBusy)
        {
            blockers.Add("Espera a que termine la operación en curso");
        }

        if (Context?.IntegrityProblem is not null)
        {
            blockers.Add("El registro de decisiones no es íntegro");
        }

        if (Context is not { Project: not null, Requirements: not null, Verification: not null })
        {
            blockers.Add("Faltan el proyecto, los requisitos congelados o el perfil de verificación");
        }
        else if (Requirements.HasPendingChanges)
        {
            blockers.Add("Congela o descarta los cambios de los requisitos");
        }
        else if (ValidationState != ValidationState.Valid)
        {
            blockers.Add("El plan tiene problemas: pulsa «Validar»");
        }
        else if (!IsApproved)
        {
            blockers.Add("Aprueba el plan");
        }

        if (string.IsNullOrWhiteSpace(RequestText))
        {
            blockers.Add("Escribe la petición");
        }

        return blockers;
    }

    private void RefreshCommands()
    {
        UseProposedProfileCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        RequestPlanCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
        EditPlanCommand.NotifyCanExecuteChanged();
        ResumeRunCommand.NotifyCanExecuteChanged();
        ViewRunCommand.NotifyCanExecuteChanged();
        CloseRunViewCommand.NotifyCanExecuteChanged();
        AcceptHistoryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ExecuteBlockers));
    }

    private void RebuildEditor() =>
        Editor = Canvas.SelectedNode is { } node
            ? new NodeEditorViewModel(node, Context?.EditorOptions ?? EditorOptions.None, Canvas.Nodes, Canvas.IsReadOnly)
            : null;

    private void OnCanvasPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasViewModel.SelectedNode) or nameof(CanvasViewModel.IsReadOnly))
        {
            RebuildEditor();
        }
    }

    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunMonitorViewModel.RunId) && Monitor.RunId is null)
        {
            _followedPlanHash = null;
        }

        if (e.PropertyName is nameof(RunMonitorViewModel.IsActive) or nameof(RunMonitorViewModel.IsAttached) or nameof(RunMonitorViewModel.IsInspecting))
        {
            RefreshCommands();
            UpdateRosterLock();
            OnPropertyChanged(nameof(CanChooseResumeConsent));
        }

        if (e.PropertyName is nameof(RunMonitorViewModel.RunId) or nameof(RunMonitorViewModel.IsActive) or nameof(RunMonitorViewModel.IsInspecting) or nameof(RunMonitorViewModel.IsFinished))
        {
            OnPropertyChanged(nameof(ApprovalText));
        }

        if (e.PropertyName is nameof(RunMonitorViewModel.IsDelivering))
        {
            ViewRunCommand.NotifyCanExecuteChanged();
            CloseRunViewCommand.NotifyCanExecuteChanged();
        }
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private static string Steps(VerificationProfile profile) =>
        profile.Steps.Count == 1 ? "1 paso" : profile.Steps.Count.ToString(CultureInfo.InvariantCulture) + " pasos";

    /// <summary>A run whose resume was refused for its writers at medium integrity, and the hash of the plan it started with.</summary>
    private sealed record FollowUp(RunId Run, string PlanHash);

    /// <summary>What the bar of the context of the planning is called.</summary>
    private const string PlanningContextLabel = "Contexto del orquestador";

    /// <summary>An event of the planning reaches its tracker, and the bar follows it, judged with the thresholds of now.</summary>
    private void ObservePlanning(AgentEvent value)
    {
        if (_planningTracker is not { } tracker || value is not (ContextObserved or UsageReported or CompactionObserved or RunFinished))
        {
            return;
        }

        PlanningContext = MetricBars.Context(PlanningContextLabel, tracker.Observe(value), Monitor.ContextThresholds);
    }

    /// <summary>The planning progress, applied on the UI context like the monitor's events.</summary>
    private sealed class UiProgress(OrchestratorDocumentViewModel owner) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent value)
        {
            void Apply()
            {
                owner.ObservePlanning(value);
                if (AgentActivityText.Describe(value, owner._time.LocalTimeZone) is { } text)
                {
                    owner.PlanningActivity.Add(text);
                }
            }

            if (owner._ui is null)
            {
                Apply();
            }
            else
            {
                owner._ui.Post(_ => Apply(), null);
            }
        }
    }
}
