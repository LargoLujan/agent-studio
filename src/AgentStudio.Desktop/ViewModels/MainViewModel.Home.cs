using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

// The navigation of the first redesign (docs/24 §3.1 and §3.2): the activity bar in groups, with the title of each entry and a
// button that leaves only the icons; «Inicio» with the next step of the open project; and the way to «Requisitos» that
// «Auditar» and the orchestrator offer where they say they are missing.
public sealed partial class MainViewModel
{
    private readonly ProjectProgressReader? _progress;

    /// <summary>Counts the reads of the next step: an older one that ends later never paints over a newer one.</summary>
    private int _nextStepReads;

    /// <summary>The groups of the activity bar, in its order, without the footer.</summary>
    public IReadOnlyList<ActivitySection> ActivitySections { get; }

    /// <summary>The entries at the bottom of the activity bar: «Ayuda» and «Ajustes».</summary>
    public IReadOnlyList<ActivityItemViewModel> FooterActivities { get; }

    /// <summary>The activity bar shows only the icons; each entry keeps its name in its tooltip and for a screen reader. For the session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsActivityTitles))]
    [NotifyPropertyChangedFor(nameof(ActivityBarToggleName))]
    private bool _isActivityBarCollapsed;

    /// <summary>The activity bar shows the title of each entry and the caption of each group.</summary>
    public bool ShowsActivityTitles => !IsActivityBarCollapsed;

    /// <summary>What the button that folds the bar does now, for its tooltip and a screen reader.</summary>
    public string ActivityBarToggleName => Texts.Get(IsActivityBarCollapsed ? "Main_Bar_Expand" : "Main_Bar_Collapse");

    /// <summary>Completes once the last read of the next step of «Inicio» ended.</summary>
    public Task NextStepRead { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void ToggleActivityBar() => IsActivityBarCollapsed = !IsActivityBarCollapsed;

    /// <summary>«Configurar proyecto», only when the configurator was composed.</summary>
    private IEnumerable<ActivityItemViewModel> SetupActivities()
    {
        if (_setupFactory is not null)
        {
            yield return new ActivityItemViewModel("setup", Texts.Get("Shell_Setup_Action"), "", null, null, OpenSetupCommand) { Group = ActivityGroup.Project };
        }
    }

    private static List<ActivitySection> SectionsOf(IReadOnlyList<ActivityItemViewModel> activities) =>
    [
        .. activities
            .Where(a => a.Group != ActivityGroup.Footer)
            .GroupBy(a => a.Group)
            .OrderBy(g => g.Key)
            .Select(g => new ActivitySection(g.Key, CaptionOf(g.Key), [.. g])),
    ];

    private static string CaptionOf(ActivityGroup group) => group switch
    {
        ActivityGroup.Project => Texts.Get("Main_Group_Project"),
        ActivityGroup.Inside => Texts.Get("Main_Group_Inside"),
        ActivityGroup.Accounts => Texts.Get("Main_Group_Accounts"),
        _ => Texts.Get("Main_Group_More"),
    };

    /// <summary>
    /// «Escribir una petición»: the orchestrator of the open project with «Preparación» open and the keyboard in the box of the
    /// request (<see cref="Orchestrator.OrchestratorDocumentViewModel.FocusRequestBox"/>).
    /// </summary>
    [RelayCommand]
    private async Task WriteRequestAsync()
    {
        var root = ProjectPath;
        await OpenOrchestratorAsync();
        if (root is not null && SameProject(root, ProjectPath))
        {
            CurrentOrchestrator?.FocusRequestBox();
        }
    }

    /// <summary>
    /// «Ir a los requisitos» of «Auditar»: the orchestrator of the project with «Requisitos» in view and the keyboard in it. Only for the
    /// open project; the documents of another one are closed when it changes.
    /// </summary>
    public async Task OpenRequirementsAsync(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!SameProject(ProjectPath, projectRoot))
        {
            StatusMessage = Texts.Get("Main_Requirements_OtherProject");
            return;
        }

        await OpenOrchestratorAsync();
        if (SameProject(ProjectPath, projectRoot))
        {
            CurrentOrchestrator?.ShowRequirements();
        }
    }

    /// <summary>
    /// A tab came into view: «Inicio» reads its next step again (a run may have started, the project may have been set up), and
    /// «Auditar» reads the requirements again while it has none (they may have been frozen through «Ir a los requisitos»).
    /// </summary>
    partial void OnActiveContentChanged(object? value)
    {
        if (value is not null && ReferenceEquals(value, _welcome))
        {
            RefreshNextStep();
        }
        else if (value is ProjectAuditDocumentViewModel audit)
        {
            AuditRequirementsRead = audit.RefreshRequirementsAsync();
        }
    }

    /// <summary>Completes once the last read of the requirements of an «Auditar» tab that came back into view ended.</summary>
    public Task AuditRequirementsRead { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The context of an orchestrator changed (its requirements may have been frozen): an «Auditar» tab of that project that is open
    /// reads them again, so coming back to it does not say they are missing.
    /// </summary>
    private void OnContextChangedForAudits(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OrchestratorDocumentViewModel.Context) || sender is not OrchestratorDocumentViewModel document)
        {
            return;
        }

        foreach (var audit in Documents.OfType<ProjectAuditDocumentViewModel>().Where(a => SameProject(a.ProjectRoot, document.ProjectRoot)).ToList())
        {
            AuditRequirementsRead = audit.RefreshRequirementsAsync();
        }
    }

    /// <summary>How long «Inicio» waits before it reads the log, so a burst of changes reads it once.</summary>
    private static readonly TimeSpan NextStepDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>Reads the next step of the open project again for «Inicio», when its tab exists; a read still waiting is cancelled.</summary>
    private void RefreshNextStep()
    {
        if (_welcome is null)
        {
            return;
        }

        _nextStepCancellation?.Cancel();
        _nextStepCancellation?.Dispose();
        _nextStepCancellation = new CancellationTokenSource();
        NextStepRead = ReadNextStepAsync(_welcome, _nextStepCancellation.Token);
    }

    private CancellationTokenSource? _nextStepCancellation;

    /// <summary>A question of a run came or was answered: what «Inicio» says of the run follows it (waiting for your answer, or working).</summary>
    private void OnQuestionsChangedForHome(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshNextStep();

    /// <summary>
    /// The next step (docs/24 §3.2) from what is known without opening anything the app did not open already. A run going on comes
    /// first, since it is the most certain thing and can go on in a project never set up. A folder where Agent Studio never wrote was
    /// never set up. For a project its orchestrator has open: set up when the configurator was applied or when it has agents and a
    /// verification profile (the orchestrator's own notion, so the two never disagree), and then without frozen requirements its
    /// first request is the step. Otherwise, and for what is not known, a new request, which claims nothing.
    /// </summary>
    private async Task ReadNextStepAsync(WelcomeDocumentViewModel welcome, CancellationToken ct)
    {
        var read = ++_nextStepReads;
        if (ProjectPath is not { } root)
        {
            welcome.NextStep = null;
            return;
        }

        if (RunStep(root) is { } running)
        {
            welcome.NextStep = running;
            return;
        }

        if (_progress is null)
        {
            welcome.NextStep = NewRequestStep();
            return;
        }

        if (!ProjectProgressReader.HasStorage(root))
        {
            // Never opened here; but a versioned profile says it was prepared elsewhere (a fresh clone), so it is not one to set up.
            welcome.NextStep = _setupFactory is null || ProjectProgressReader.HasVersionedProfile(root) ? NewRequestStep() : SetUpStep();
            return;
        }

        if (!_orchestrators.TryGetValue(root, out var document) || document.Context is not { Project: { } project } opened)
        {
            welcome.NextStep = NewRequestStep();
            return;
        }

        var context = opened;
        bool? applied;
        try
        {
            await Task.Delay(NextStepDebounce, ct);
            applied = await _progress.SetupAppliedAsync(project, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The project, or the run, may have changed while it was read: only the last read of the open project is painted.
        if (ct.IsCancellationRequested || read != _nextStepReads || !SameProject(root, ProjectPath))
        {
            return;
        }

        // What the orchestrator has open is read again: it may have been reloaded (requirements frozen, a profile saved) meanwhile.
        if (!_orchestrators.TryGetValue(root, out var after) || after.Context is not { Project: not null } current)
        {
            welcome.NextStep = RunStep(root) ?? NewRequestStep();
            return;
        }

        context = current;

        var configured = applied == true || (context.Roster.Count > 0 && context.Verification is not null);
        welcome.NextStep = RunStep(root)
            ?? (configured && context.Requirements is null ? FirstRequestStep()
            : !configured && applied == false && _setupFactory is not null ? SetUpStep()
            : NewRequestStep());
    }

    private NextStepViewModel SetUpStep() =>
        new(NextStepKind.SetUp, Texts.Get("Welcome_Next_SetUp"), Texts.Get("Welcome_Next_SetUpAction"), OpenSetupCommand);

    private NextStepViewModel FirstRequestStep() =>
        new(NextStepKind.FirstRequest, Texts.Get("Welcome_Next_FirstRequest"), Texts.Get("Welcome_Next_FirstRequestAction"), WriteRequestCommand);

    private NextStepViewModel NewRequestStep() =>
        new(NextStepKind.NewRequest, Texts.Get("Welcome_Next_NewRequest"), Texts.Get("Welcome_Next_NewRequestAction"), WriteRequestCommand);

    /// <summary>
    /// «Ver la ejecución en marcha» when a run of the project goes on, with what it is doing in plain words: working, paused, waiting
    /// for your answer (a question), waiting for your approval (a task, with no question), or stopped for something else. In the monitor of its orchestrator, which it opens; or in a monitor of its own
    /// (a run resumed while that one followed another), which «Ejecuciones reanudadas» shows. Null otherwise.
    /// </summary>
    private NextStepViewModel? RunStep(string root)
    {
        if (_orchestrators.TryGetValue(root, out var document) && document.Monitor.IsActive)
        {
            return new NextStepViewModel(NextStepKind.RunGoingOn, RunReason(document.Monitor), Texts.Get("Welcome_Next_RunAction"), OpenOrchestratorCommand);
        }

        return _backgroundMonitors.FirstOrDefault(m => m.IsActive && _backgroundRoots.TryGetValue(m, out var where) && SameProject(where, root)) is { } background
            ? new NextStepViewModel(NextStepKind.RunGoingOn, RunReason(background), Texts.Get("Welcome_Next_RunAction"), OpenBackgroundRunsCommand)
            : null;
    }

    private static string RunReason(RunMonitorViewModel monitor) =>
        monitor.HasPendingQuestions ? Texts.Get("Welcome_Next_RunWaiting")
        : monitor.Phase == RunPhase.AwaitingHuman ? Texts.Get("Welcome_Next_RunApproval")
        : monitor.Phase == RunPhase.Paused ? Texts.Get("Welcome_Next_RunPaused")
        : monitor.Phase is null or RunPhase.Running ? Texts.Get("Welcome_Next_Run")
        : Texts.Get("Welcome_Next_RunStopped");
}
