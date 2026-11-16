using AgentStudio.Desktop.Localization;
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
            StatusMessage = Texts.Get("Main_Corrective_OtherProject");
            return;
        }

        await OpenOrchestratorAsync();
        if (SameProject(ProjectPath, projectRoot))
        {
            CurrentOrchestrator?.ShowRequirements();
        }
    }

    /// <summary>«Inicio» became the tab in view: its next step is read again (a run may have started, the project may have been set up).</summary>
    partial void OnActiveContentChanged(object? value)
    {
        if (value is not null && ReferenceEquals(value, _welcome))
        {
            RefreshNextStep();
        }
    }

    /// <summary>Reads the next step of the open project again for «Inicio», when its tab exists.</summary>
    private void RefreshNextStep()
    {
        if (_welcome is null)
        {
            return;
        }

        NextStepRead = ReadNextStepAsync(_welcome);
    }

    /// <summary>
    /// The next step (docs/24 §3.2) from what can be read: a run going on comes first, since it is the most certain thing and can
    /// go on in a project that was never set up; then a project never set up; then one set up without frozen requirements; and
    /// otherwise a new request, which claims nothing (nor what could not be read).
    /// </summary>
    private async Task ReadNextStepAsync(WelcomeDocumentViewModel welcome)
    {
        var read = ++_nextStepReads;
        if (ProjectPath is not { } root)
        {
            welcome.NextStep = null;
            return;
        }

        if (RunGoingOnStep(root) is { } running)
        {
            welcome.NextStep = running;
            return;
        }

        ProjectProgress progress;
        try
        {
            progress = _progress is null ? ProjectProgress.Unknown : await _progress.ReadAsync(root, CancellationToken.None);
        }
#pragma warning disable CA1031 // The reader is behind ports: a failure is a step that claims nothing, never thrown into the dispatcher.
        catch (Exception)
#pragma warning restore CA1031
        {
            progress = ProjectProgress.Unknown;
        }

        // The project, or the run, may have changed while it was read: only the last read of the open project is painted.
        if (read != _nextStepReads || !SameProject(root, ProjectPath))
        {
            return;
        }

        welcome.NextStep = RunGoingOnStep(root) ?? progress switch
        {
            { SetupApplied: false } when _setupFactory is not null =>
                new NextStepViewModel(NextStepKind.SetUp, Texts.Get("Welcome_Next_SetUp"), Texts.Get("Welcome_Next_SetUpAction"), OpenSetupCommand),
            { SetupApplied: true, HasFrozenRequirements: false } =>
                new NextStepViewModel(NextStepKind.FirstRequest, Texts.Get("Welcome_Next_FirstRequest"), Texts.Get("Welcome_Next_FirstRequestAction"), WriteRequestCommand),
            _ => new NextStepViewModel(NextStepKind.NewRequest, Texts.Get("Welcome_Next_NewRequest"), Texts.Get("Welcome_Next_NewRequestAction"), WriteRequestCommand),
        };
    }

    /// <summary>
    /// «Ver la ejecución en marcha» when a run of the project goes on: in the monitor of its orchestrator, which it opens; or in a
    /// monitor of its own (a run resumed while that one followed another), which «Ejecuciones reanudadas» shows. Null otherwise.
    /// </summary>
    private NextStepViewModel? RunGoingOnStep(string root)
    {
        if (_orchestrators.TryGetValue(root, out var document) && document.Monitor.IsActive)
        {
            return new NextStepViewModel(NextStepKind.RunGoingOn, Texts.Get("Welcome_Next_Run"), Texts.Get("Welcome_Next_RunAction"), OpenOrchestratorCommand);
        }

        return _backgroundMonitors.Any(m => m.IsActive && _backgroundRoots.TryGetValue(m, out var where) && SameProject(where, root))
            ? new NextStepViewModel(NextStepKind.RunGoingOn, Texts.Get("Welcome_Next_Run"), Texts.Get("Welcome_Next_RunAction"), OpenBackgroundRunsCommand)
            : null;
    }
}
