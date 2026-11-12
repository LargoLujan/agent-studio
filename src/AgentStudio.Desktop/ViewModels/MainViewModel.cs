using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Controls.Editor;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Help;
using AgentStudio.Desktop.Documents;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Repository;
using AgentStudio.Desktop.Work;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Panels;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Skills;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>ContentIds of the dockable panels.</summary>
public static class PanelIds
{
    public const string Explorer = "panel:explorer";
    public const string AutoDoctor = "panel:autodoctor";
    public const string Journal = "panel:journal";
    public const string Terminal = "panel:terminal";
    public const string Problems = "panel:problems";
    public const string Output = "panel:output";
    public const string Tests = "panel:tests";
}

/// <summary>State of the IDE shell: project, documents, panels, recents and the provider summary.</summary>
public sealed partial class MainViewModel : ObservableObject, IReviewNavigation
{
    /// <summary>
    /// The shortcut that shows or hides the terminal (MainWindow binds it): Ctrl and the key to the right of «L» (VK_OEM_3),
    /// which is «Ñ» on a Spanish keyboard and «`» on a US one, the key Visual Studio Code uses for its terminal.
    /// </summary>
    public static string TerminalShortcut => Texts.Get("Main_Shortcut_Terminal");

    /// <summary>The shortcuts the window binds for «Ir al fichero», «Debates» and «Auditar proyecto completo», as the tooltips say them.</summary>
    public const string GoToFileShortcut = "Ctrl+P";

    /// <inheritdoc cref="GoToFileShortcut"/>
    public static string DebatesShortcut => Texts.Get("Main_Shortcut_Debates");

    /// <inheritdoc cref="GoToFileShortcut"/>
    public static string AuditShortcut => Texts.Get("Main_Shortcut_Audit");

    /// <summary>The shortcut of the «Terminal» entry as its tooltip says it: on a US keyboard the same key is «`».</summary>
    private static string TerminalShortcutHint => Texts.Get("Main_Shortcut_TerminalHint");

    private readonly IAppSettingsStore _settingsStore;
    private readonly IFolderPicker _folderPicker;
    private readonly IUnsavedChangesPrompt _unsavedPrompt;
    private readonly HashSet<DocumentViewModel> _closing = [];
    private readonly IOrchestratorDocumentFactory _orchestratorFactory;

    /// <summary>The «Orquestador» document of each project opened in this session; a closed tab keeps its canvas and its run.</summary>
    private readonly Dictionary<string, OrchestratorDocumentViewModel> _orchestrators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the run each orchestrator follows was going on the last time it was seen: an end is a true-to-false change.</summary>
    private readonly Dictionary<RunMonitorViewModel, bool> _runsActive = [];
    private readonly ISkillsDocumentFactory _skillsFactory;
    private readonly UsageDashboardViewModel? _usage;
    private readonly MemoryViewModel? _memory;
    private readonly ProjectMemoryUpkeep? _upkeep;
    private readonly DebateScreens? _debates;
    private readonly RunReviewServices? _review;
    private readonly BackgroundWorkRegistry? _work;
    private readonly IConsentedResumes? _resumes;
    private readonly SettingsDocumentViewModel? _settingsDocument;
    private readonly ContextWindowCatalogs? _contextWindows;
    private readonly SynchronizationContext? _ui;

    /// <summary>The «Debates» documents whose debates the registry of the work follows, with the debate each one follows now.</summary>
    private readonly Dictionary<DebatesDocumentViewModel, IDisposable?> _trackedDebates = [];

    /// <summary>
    /// The projects a pass of the consented resume went over in this session (docs/16 D-39): one whole pass per project. A pass
    /// held back (the tray had not started, or the managed exit held the resumes) does not count, and is asked for again.
    /// </summary>
    private readonly HashSet<string> _resumeChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The projects opened in this session, whose pass is wanted until it went over them.</summary>
    private readonly HashSet<string> _resumeWanted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The projects whose pass is going on now: a release meanwhile does not start a second one.</summary>
    private readonly HashSet<string> _resumePasses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The projects asked for again while their pass was going on: asked once more if that pass was held back.</summary>
    private readonly HashSet<string> _resumeAgain = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors of runs the scheduler resumed while the monitor of their document followed another run: the tray follows them.</summary>
    private readonly List<RunMonitorViewModel> _backgroundMonitors = [];

    /// <summary>The project of each of <see cref="_backgroundMonitors"/>, whose learned windows reach it.</summary>
    private readonly Dictionary<RunMonitorViewModel, string> _backgroundRoots = [];
    private AppSettings _settings = AppSettings.Default;
    private WelcomeDocumentViewModel? _welcome;
    private SkillsDocumentViewModel? _skills;

    /// <summary>The project «Consumo» and «Memoria» last showed; null until they are first opened.</summary>
    private ProjectShown? _usageShown;
    private ProjectShown? _memoryShown;

    /// <param name="orchestrators">Creates the «Orquestador» documents; without it they open without an engine.</param>
    /// <param name="skills">Creates the «Skills» documents; without it the screen says which ports are missing.</param>
    /// <param name="usage">The «Consumo» document of the session; without it the activity is not offered.</param>
    /// <param name="memory">The «Memoria» document of the session; without it the activity is not offered.</param>
    /// <param name="upkeep">Derives the memory after each run and when a project is opened; without it nothing is derived.</param>
    /// <param name="debates">The «Debates» and «Auditoría» documents of each project; without it the activity is not offered.</param>
    /// <param name="review">The review of a run in its monitor (auditor, verdicts, challenge); without it the monitor offers none.</param>
    /// <param name="work">
    /// The registry of the work the tray, its panel, the managed exit and «mantener despierto» follow (docs/16 D-14 to D-16,
    /// ADR-0036): the shell tracks there the monitor of each «Orquestador», each debate and the projects of the other
    /// documents. Without it nothing is tracked.
    /// </param>
    /// <param name="resumes">
    /// The consented resume after a restart (docs/16 D-39): a pass for each project opened, and the runs it resumed attached to
    /// the monitor of their «Orquestador». Without it nothing resumes by itself.
    /// </param>
    /// <param name="settingsDocument">The «Ajustes» document of the session; without it the activity is not offered.</param>
    /// <param name="statusStrip">
    /// The bars of the status bar (docs/18 P3): the context of the tasks in course, the quota and the budget. Without it the shell
    /// makes one over what it has.
    /// </param>
    /// <param name="contextWindows">
    /// The context windows each project learned (P36), which every monitor judges the live context with; without it, the base
    /// table alone.
    /// </param>
    /// <param name="monitors">What the monitors the shell creates for itself are given (docs/04 section 21.10); without it they have none of it.</param>
    /// <param name="setup">Creates the configurator of a project (docs/17 section 4.6); without it «Configurar proyecto» is not offered.</param>
    /// <param name="setupSuggestions">Which projects the configurator was already suggested for; without it nothing is suggested.</param>
    /// <param name="headTests">What composes «Ejecutar las pruebas de HEAD» of the panel «Pruebas»; without it, or with a part missing, the button is not offered.</param>
    /// <param name="clientDocuments">The «Documentación para clientes» document of the session; without it the activity is not offered.</param>
    public MainViewModel(
        IAppSettingsStore settingsStore,
        IFolderPicker folderPicker,
        IUnsavedChangesPrompt unsavedPrompt,
        ProviderDiagnosticsViewModel diagnostics,
        ExplorerViewModel explorer,
        JournalViewModel journal,
        QuickOpenViewModel quickOpen,
        IOrchestratorDocumentFactory? orchestrators = null,
        ISkillsDocumentFactory? skills = null,
        UsageDashboardViewModel? usage = null,
        MemoryViewModel? memory = null,
        ProjectMemoryUpkeep? upkeep = null,
        DebateScreens? debates = null,
        RunReviewServices? review = null,
        BackgroundWorkRegistry? work = null,
        IConsentedResumes? resumes = null,
        SettingsDocumentViewModel? settingsDocument = null,
        StatusStripViewModel? statusStrip = null,
        ContextWindowCatalogs? contextWindows = null,
        MonitorServices? monitors = null,
        SetupViewModelFactory? setup = null,
        SetupSuggestions? setupSuggestions = null,
        HelpDocumentViewModel? help = null,
        ClientDocumentsViewModel? clientDocuments = null,
        RepositoryViewModel? repository = null,
        WorkDocumentViewModel? workDocument = null,
        HeadTestsServices? headTests = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(unsavedPrompt);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(explorer);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(quickOpen);
        _settingsStore = settingsStore;
        _folderPicker = folderPicker;
        _unsavedPrompt = unsavedPrompt;
        Diagnostics = diagnostics;
        Explorer = explorer;
        Journal = journal;
        QuickOpen = quickOpen;

        // Without a composed factory (tests, a shell built by hand) the orchestrator still opens, without an engine.
        _orchestratorFactory = orchestrators ?? OrchestratorDocumentFactory.WithoutEngine();

        // Without a composed factory the «Skills» screen still opens, over no ports, and says what is missing.
        _skillsFactory = skills ?? new SkillsDocumentFactory(SkillsBackend.None, TimeProvider.System);
        _usage = usage;
        _memory = memory;
        _clientDocuments = clientDocuments;
        _workDocument = workDocument;
        _upkeep = upkeep;
        _debates = debates;
        _review = review;
        _work = work;
        _resumes = resumes;
        _settingsDocument = settingsDocument;
        _contextWindows = contextWindows;
        _ui = SynchronizationContext.Current;
        StatusStrip = statusStrip ?? new StatusStripViewModel(usage?.Quota, TimeProvider.System, _ui);
        if (resumes is not null)
        {
            resumes.Resumed += OnRunResumed;
            resumes.Available += OnResumesAvailable;
        }

        if (_usage is not null)
        {
            _usage.Preferences.Saved += OnObservabilityPreferencesSaved;
        }

        Explorer.FileActivated += (_, path) => _ = OpenFileAsync(path);
        Diagnostics.Providers.CollectionChanged += OnProvidersChanged;
        Diagnostics.PropertyChanged += OnDiagnosticsPropertyChanged;
        UpdateProviderSummary();

        InitializeHelp(help);
        InitializeRepository(repository);
        ConnectRepositoryToWork();
        Activities =
        [
            new("explorer", Texts.Get("Main_Panel_Explorer"), "", null, PanelIds.Explorer, TogglePanelCommand),
            new("orchestrator", Texts.Get("Main_Activity_Orchestrator"), "\uE9F5", null, null, OpenOrchestratorCommand),
            .. AgentActivities(),
            new("skills", Texts.Get("Main_Activity_Skills"), "", null, null, OpenSkillsCommand),
            .. BuilderToolActivities(),
            .. ObservabilityActivities(),
            .. RepositoryActivities(),
            .. ClientDocumentActivities(),
            .. WorkActivities(),
            .. DebateActivities(),
            new("terminal", Texts.Get("Main_Panel_Terminal"), "", null, PanelIds.Terminal, TogglePanelCommand) { Shortcut = TerminalShortcutHint },
            new("autodoctor", Texts.Get("Main_Panel_AutoDoctor"), "", null, PanelIds.AutoDoctor, TogglePanelCommand),
            new("history", Texts.Get("Main_Activity_History"), "", null, PanelIds.Journal, TogglePanelCommand),
            .. HelpActivities(),
            .. SettingsActivities(),
        ];
        Tests.HeadTests = headTests?.For(() => ProjectPath);
        InitializeBuilderShell(monitors, setup, setupSuggestions);
    }

    public ProviderDiagnosticsViewModel Diagnostics { get; }

    /// <summary>
    /// The bars of the status bar of the window (docs/18 P3): the context of the tasks in course, the quota of the account and
    /// the budget of the runs, always in view, in real time.
    /// </summary>
    public StatusStripViewModel StatusStrip { get; }

    public ExplorerViewModel Explorer { get; }

    public JournalViewModel Journal { get; }

    /// <summary>"Go to file" (Ctrl+P): the search list over the files of the open project.</summary>
    public QuickOpenViewModel QuickOpen { get; }

    public IReadOnlyList<ActivityItemViewModel> Activities { get; }

    /// <summary>«Debates» (the room, the history and «Auditar proyecto completo»), only when its screens were composed.</summary>
    private IEnumerable<ActivityItemViewModel> DebateActivities()
    {
        if (_debates is not null)
        {
            yield return new ActivityItemViewModel("debates", Texts.Get("Main_Activity_Debates"), "", null, null, OpenDebatesCommand) { Shortcut = DebatesShortcut };
        }
    }

    /// <summary>«Ajustes», last, only when its document was composed.</summary>
    private IEnumerable<ActivityItemViewModel> SettingsActivities()
    {
        if (_settingsDocument is not null)
        {
            yield return new ActivityItemViewModel("settings", Texts.Get("SettingsDoc_Title"), "", null, null, OpenSettingsCommand);
        }
    }

    /// <summary>Opens «Ajustes»: one tab for the session, with every preference of the app and one «Guardar».</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        if (_settingsDocument is not null)
        {
            AddDocument(_settingsDocument);
        }
    }

    /// <summary>«Consumo» and «Memoria», each only when its screen was composed.</summary>
    private IEnumerable<ActivityItemViewModel> ObservabilityActivities()
    {
        if (_usage is not null)
        {
            yield return new ActivityItemViewModel("usage", Texts.Get("Main_Activity_Usage"), "", null, null, OpenUsageCommand);
        }

        if (_memory is not null)
        {
            yield return new ActivityItemViewModel("memory", Texts.Get("Main_Activity_Memory"), "", null, null, OpenMemoryCommand);
        }
    }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];

    /// <summary>Currently active content of the dock (a document or a panel's view).</summary>
    [ObservableProperty]
    private object? _activeContent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    [NotifyPropertyChangedFor(nameof(ProjectName))]
    [NotifyPropertyChangedFor(nameof(ProjectAccessibleName))]
    [NotifyPropertyChangedFor(nameof(ProjectPathDisplay))]
    [NotifyPropertyChangedFor(nameof(GoToFileToolTip))]
    [NotifyCanExecuteChangedFor(nameof(GoToFileCommand))]
    private string? _projectPath;

    public bool HasProject => ProjectPath is not null;

    /// <summary>Tooltip of the "go to file" button; says why it is disabled when no project is open.</summary>
    public string GoToFileToolTip => HasProject
        ? Texts.Get("Main_GoToFile_Hint")
        : Texts.Get("Main_GoToFile_NoProject");

    public string ProjectName => ProjectPath is null ? Texts.Get("Main_Project_None") : RecentProjectItem.From(ProjectPath).Name;

    /// <summary>What a screen reader says for the name of the project in the top bar: that one is open, and which.</summary>
    public string ProjectAccessibleName => ProjectPath is null ? Texts.Get("Main_Project_NoneName") : Texts.Format("Main_Project_OpenNamed", ProjectName);

    public string ProjectPathDisplay => ProjectPath is null ? Texts.Get("Main_Project_Start") : PathDisplay.CollapseHome(ProjectPath);

    /// <summary>
    /// The version of the app in the status bar: the version and the commit it was built from («v1.0.0 · 87f7a07»), so the
    /// build that is installed can be told apart.
    /// </summary>
    public string AppVersion { get; } = FormatVersion(
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// «v{version} · {commit}» from the informational version the SDK writes («1.0.0+{sha}»): the version alone when it names
    /// no commit, and an empty text when there is none.
    /// </summary>
    public static string FormatVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        var parts = informationalVersion.Trim().Split('+', 2);
        var commit = parts.Length == 2 ? parts[1].Trim() : string.Empty;
        return commit.Length >= 7 ? $"v{parts[0]} · {commit[..7]}" : $"v{parts[0]}";
    }

    /// <summary>The welcome tab follows the open project: with one, it offers the ways into it.</summary>
    partial void OnProjectPathChanged(string? value)
    {
        _welcome?.ShowProject(value is null ? null : ProjectName, value is null ? null : ProjectPathDisplay);
        ShowSetupSuggestion(value);
        AttachPanels();
    }

    /// <summary>Transient message for the status bar (errors opening a project or file).</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>One-line state of the providers for the top bar (text plus semantic colour).</summary>
    [ObservableProperty]
    private ProviderSummary _providerSummary = new(string.Empty, StatusKind.Warning);

    /// <summary>Completes once the last pass of the consented resume asked for (when a project is opened) ended.</summary>
    public Task ConsentedResumeCheck { get; private set; } = Task.CompletedTask;

    /// <summary>Raised before the project changes, with the path that is about to be left (null if none).</summary>
    public event EventHandler<string?>? ProjectChanging;

    /// <summary>Raised once the new project is loaded (null when the project was closed).</summary>
    public event EventHandler<string?>? ProjectChanged;

    /// <summary>Raised when an activity-bar button asks to show or hide a panel (argument: its ContentId).</summary>
    public event EventHandler<string>? PanelToggleRequested;

    /// <summary>Loads settings, shows the welcome tab and reopens the last project if its folder still exists.</summary>
    public async Task InitializeAsync()
    {
        _settings = _settingsStore.Load();
        RefreshRecents();
        ShowWelcome();

        if (_settings.LastProjectPath is { } last)
        {
            if (Directory.Exists(last))
            {
                await OpenProjectAsync(last);
            }
            else
            {
                _settings = _settings.WithProjectRemoved(last);
                TrySaveSettings();
                RefreshRecents();
            }
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var folder = _folderPicker.PickFolder(Texts.Get("Main_OpenFolder_Dialog"));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await OpenProjectAsync(folder);
        }
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenProjectAsync(path);
        }
    }

    [RelayCommand]
    private void ShowWelcome()
    {
        if (_welcome is null)
        {
            _welcome = new WelcomeDocumentViewModel(RecentProjects, OpenFolderCommand, OpenRecentCommand, WelcomeActions());
            _welcome.ShowProject(HasProject ? ProjectName : null, HasProject ? ProjectPathDisplay : null);
            ShowSetupSuggestion(ProjectPath);
        }

        AddDocument(_welcome);
    }

    /// <summary>
    /// The ways into the open project the welcome tab offers, with their shortcuts: only the screens that were composed, as in
    /// the activity bar.
    /// </summary>
    private List<WelcomeActionViewModel> WelcomeActions()
    {
        var actions = new List<WelcomeActionViewModel>
        {
            new(Texts.Get("Main_Activity_Orchestrator"), Texts.Get("Welcome_Action_Orchestrator"), null, OpenOrchestratorCommand),
            new(Texts.Get("Main_GoToFile"), Texts.Get("Welcome_Action_GoToFile"), GoToFileShortcut, GoToFileCommand),
        };

        if (_debates is not null)
        {
            actions.Add(new(Texts.Get("Main_Activity_Debates"), Texts.Get("Welcome_Action_Debates"), DebatesShortcut, OpenDebatesCommand));
            actions.Add(new(Texts.Get("Main_Audit"), Texts.Get("Welcome_Action_Audit"), AuditShortcut, OpenAuditCommand));
        }

        if (_usage is not null)
        {
            actions.Add(new(Texts.Get("Main_Activity_Usage"), Texts.Get("Welcome_Action_Usage"), null, OpenUsageCommand));
        }

        if (_memory is not null)
        {
            actions.Add(new(Texts.Get("Main_Activity_Memory"), Texts.Get("Welcome_Action_Memory"), null, OpenMemoryCommand));
        }

        if (_setupFactory is not null)
        {
            actions.Add(new(Texts.Get("Shell_Setup_Action"), Texts.Get("Shell_Setup_ActionHelp"), null, OpenSetupCommand));
        }

        actions.Add(new(Texts.Get("Main_Panel_Terminal"), Texts.Get("Welcome_Action_Terminal"), TerminalShortcut, ToggleTerminalCommand));
        return actions;
    }

    /// <summary>Ctrl+P: search the files of the open project and open the one chosen in a tab.</summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private Task GoToFileAsync() =>
        ProjectPath is { } root ? QuickOpen.OpenAsync(root, Texts.Get("Main_GoToFile"), OpenFileAsync) : Task.CompletedTask;

    /// <summary>
    /// Opens the «Orquestador» tab of the open project: one document per project, created the first time and kept for the
    /// session, so closing the tab loses neither the canvas nor a run being followed.
    /// </summary>
    [RelayCommand]
    private async Task OpenOrchestratorAsync()
    {
        if (ProjectPath is not { } root)
        {
            StatusMessage = Texts.Get("Main_NeedProject_Orchestrator");
            return;
        }

        if (!_orchestrators.TryGetValue(root, out var document))
        {
            document = _orchestratorFactory.Create(root);
            _orchestrators[root] = document;
            Follow(document);
        }

        AddDocument(document);
        await document.OpenAsync();
    }

    /// <summary>
    /// Opens the «Skills» tab: the catalog with the activations of the open project, or of none. A tab of another project
    /// is replaced, because its activations and its recommendation belong to that project.
    /// </summary>
    [RelayCommand]
    private async Task OpenSkillsAsync()
    {
        if (_skills is not null && !SameProject(_skills.ProjectRoot, ProjectPath))
        {
            if (Documents.Contains(_skills))
            {
                RemoveDocument(_skills);
            }

            _skills = null;
        }

        _skills ??= _skillsFactory.Create(ProjectPath);
        AddDocument(_skills);
        await _skills.OpenAsync();
    }

    /// <summary>Opens «Consumo»: one tab for the session, which follows the open project (or shows every recent one).</summary>
    [RelayCommand]
    private async Task OpenUsageAsync()
    {
        if (_usage is null)
        {
            return;
        }

        AddDocument(_usage);
        if (_usageShown is null || !SameProject(_usageShown.Path, ProjectPath))
        {
            _usageShown = new ProjectShown(ProjectPath);
            await _usage.SetProjectAsync(ProjectPath);
        }
    }

    /// <summary>Opens «Memoria»: one tab for the session, which follows the open project.</summary>
    [RelayCommand]
    private async Task OpenMemoryAsync()
    {
        if (_memory is null)
        {
            return;
        }

        AddDocument(_memory);
        if (_memoryShown is null || !SameProject(_memoryShown.Path, ProjectPath))
        {
            _memoryShown = new ProjectShown(ProjectPath);
            await _memory.SetProjectAsync(ProjectPath);
        }
    }

    /// <summary>
    /// The open project changed: «Consumo» and «Memoria» show it at once when their tab is open, or the next time they are
    /// opened; a «Skills» tab of the project left is closed, like its files.
    /// </summary>
    private async Task ShowProjectInScreensAsync(string projectPath)
    {
        // The «Debates» and «Auditoría» of the project left go with its files; the screens keep them, so a debate going on
        // keeps going and coming back shows it.
        foreach (var document in Documents.Where(d =>
            (d is DebatesDocumentViewModel debates && !SameProject(debates.ProjectRoot, projectPath))
            || (d is ProjectAuditDocumentViewModel audit && !SameProject(audit.ProjectRoot, projectPath))).ToList())
        {
            RemoveDocument(document);
        }

        if (_skills is not null && !SameProject(_skills.ProjectRoot, projectPath))
        {
            if (Documents.Contains(_skills))
            {
                RemoveDocument(_skills);
            }

            _skills = null;
        }

        if (_usage is not null)
        {
            _usageShown = null;
            if (Documents.Contains(_usage))
            {
                _usageShown = new ProjectShown(projectPath);
                await _usage.SetProjectAsync(projectPath);
            }
        }

        if (_memory is not null)
        {
            _memoryShown = null;
            if (Documents.Contains(_memory))
            {
                _memoryShown = new ProjectShown(projectPath);
                await _memory.SetProjectAsync(projectPath);
            }
        }

        await ShowProjectInRepositoryAsync(projectPath);
        await ShowProjectInDocumentsAsync(projectPath);
        await ShowProjectInWorkAsync(projectPath);
    }

    /// <summary>
    /// Follows the run of a new «Orquestador» document: its context is judged with the user's thresholds (docs/14 D-11),
    /// and the start and the end of its runs reach the other screens.
    /// </summary>
    private void Follow(OrchestratorDocumentViewModel document)
    {
        if (_usage is not null)
        {
            document.Monitor.ContextThresholds = _usage.Preferences.Thresholds;
        }

        _runsActive[document.Monitor] = document.Monitor.IsActive;
        document.Monitor.PropertyChanged += OnMonitorPropertyChanged;
        document.Monitor.Finished += (_, _) => OnRunFinished(document.ProjectRoot);
        document.RunHandedOff += OnRunHandedOff;
        document.ConfigureProjectRequested += OnConfigureProjectRequested;
        if (SameProject(document.ProjectRoot, ProjectPath))
        {
            AttachPanels();
        }

        // The live bars (docs/18 P3 and P36): the status bar follows this monitor and the planning of this document (V33), and
        // its attempts are judged with the windows the project learned.
        StatusStrip.Track(document.Monitor);
        StatusStrip.TrackPlanning(document);
        LearnContextWindows(document.ProjectRoot, document.Monitor);

        // The tray, its panel, the managed exit and «mantener despierto» follow the runs of this document (ADR-0036), with the
        // project of the run followed (the scheduler may attach one before the tab opened the project) or of the tab.
        _work?.TrackRun(document.ProjectRoot, document.Monitor, () => document.Monitor.Project ?? document.Context?.Project);
        if (_review is not null)
        {
            // The review of the run (docs/15 D-35 and D-38): its auditor, its verdicts and «Impugnar», which opens the
            // challenge in the «Debates» of the project; «Crear tareas correctivas» and the follow-up go to this orchestrator.
            var debates = _debates;
            document.Monitor.Review = _review with
            {
                Challenge = debates is null ? null : (project, verdict) => debates.ChallengeAsync(project.RootPath, verdict),
                CorrectiveTasks = (project, request) => CreateCorrectiveTasksAsync(project.RootPath, request),
            };
            document.Monitor.PrepareFollowUp = document.PrepareFollowUpAsync;
        }
    }

    /// <summary>Opens «Debates» of the open project: the room, the history and «Nuevo debate» (docs/15 section 4.10).</summary>
    [RelayCommand]
    private async Task OpenDebatesAsync()
    {
        if (_debates is null)
        {
            return;
        }

        if (ProjectPath is not { } root)
        {
            StatusMessage = Texts.Get("Main_NeedProject_Debate");
            return;
        }

        var document = _debates.Debates(root);
        AddDocument(document);
        await document.OpenAsync();
    }

    /// <summary>«Auditar proyecto completo» (F5-5): the audit of the commit HEAD of the open project.</summary>
    [RelayCommand]
    private async Task OpenAuditAsync()
    {
        if (_debates is null)
        {
            return;
        }

        if (ProjectPath is not { } root)
        {
            StatusMessage = Texts.Get("Main_NeedProject_Audit");
            return;
        }

        var document = _debates.Audit(root);
        AddDocument(document);
        await document.OpenAsync();
    }

    /// <summary>Shows a document of the debates (a challenge opened from the monitor or an audit) and activates it.</summary>
    public void Show(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AddDocument(document);
    }

    /// <summary>
    /// «Crear tareas correctivas»: the «Orquestador» of the project, with <paramref name="request"/> as its request. Nothing
    /// is planned nor run: the user asks for the plan, approves it and runs it.
    /// </summary>
    public async Task CreateCorrectiveTasksAsync(string projectRoot, string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (!SameProject(ProjectPath, projectRoot))
        {
            StatusMessage = Texts.Get("Main_Corrective_OtherProject");
            return;
        }

        await OpenOrchestratorAsync();
        if (_orchestrators.TryGetValue(projectRoot, out var document) && !document.Monitor.IsActive)
        {
            document.RequestText = request;
        }
    }

    private void OnObservabilityPreferencesSaved(object? sender, EventArgs e)
    {
        if (_usage is null)
        {
            return;
        }

        foreach (var document in _orchestrators.Values)
        {
            document.Monitor.ContextThresholds = _usage.Preferences.Thresholds;
        }
    }

    /// <summary>A run started or stopped being followed in an orchestrator: «Memoria» withholds or offers its export again.</summary>
    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RunMonitorViewModel monitor || e.PropertyName != nameof(RunMonitorViewModel.IsActive))
        {
            return;
        }

        var wasActive = _runsActive.TryGetValue(monitor, out var before) && before;
        _runsActive[monitor] = monitor.IsActive;
        if (wasActive != monitor.IsActive)
        {
            _memory?.RefreshRunActivity();
        }

        // A run that starts in the monitor of a closed tab (resumed by its consent) is in view in the status bar again, and is
        // let go once it ends while its tab stays closed.
        if (monitor.IsActive)
        {
            StatusStrip.Track(monitor);
            if (_orchestrators.Values.FirstOrDefault(d => ReferenceEquals(d.Monitor, monitor)) is { } document && !Documents.Contains(document))
            {
                StatusStrip.Release(monitor);
            }
        }
    }

    /// <summary>
    /// A run of an orchestrator finished: its memory is derived from the log and exported (D-18 and D-22), «Memoria» offers
    /// its export again and shows what was derived, and the monitors of the project learn the windows its attempts reported.
    /// The quota of Codex is not read here: it is read after each run of Codex itself, whatever started it (an orchestration, a
    /// debate, an audit), with the opt-in (docs/18 P37, <see cref="QuotaViewModel.CodexRunFinishedAsync"/>).
    /// </summary>
    private async void OnRunFinished(string projectRoot)
    {
        _memory?.RefreshRunActivity();
        if (_contextWindows is { } windows)
        {
            // Its attempts may have reported windows the project had not seen: the next attempts are judged with them.
            var monitors = _orchestrators.Values.Where(d => SameProject(d.ProjectRoot, projectRoot)).Select(d => d.Monitor)
                .Concat(_backgroundRoots.Where(b => SameProject(b.Value, projectRoot)).Select(b => b.Key))
                .ToList();
            _ = ReadContextWindowsAsync(windows, projectRoot, monitors);
        }

        if (await KeepMemoryAsync(projectRoot, afterRun: true) && _memory is not null && Documents.Contains(_memory) && SameProject(_memoryShown?.Path, projectRoot))
        {
            await _memory.RefreshCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Derives the memory of the project (and, after a run, exports it); what it says goes to the status bar. True when it
    /// ran; a failure is shown, never thrown into the dispatcher.
    /// </summary>
    private async Task<bool> KeepMemoryAsync(string projectRoot, bool afterRun)
    {
        if (_upkeep is not { IsAvailable: true } upkeep)
        {
            return false;
        }

        try
        {
            var line = afterRun
                ? await upkeep.AfterRunAsync(projectRoot, CancellationToken.None)
                : await upkeep.OnProjectOpenedAsync(projectRoot, CancellationToken.None);
            if (line is not null)
            {
                StatusMessage = line;
            }

            return true;
        }
#pragma warning disable CA1031 // The memory is behind ports: a failure is shown in the status bar, never thrown into the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = Texts.Format("Main_Memory_UpdateFailed", ex.Message);
            return false;
        }
    }

    private static bool SameProject(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The project a session-wide screen last showed; <see cref="Path"/> is null when it showed none.</summary>
    private sealed record ProjectShown(string? Path);

    [RelayCommand]
    private void TogglePanel(ActivityItemViewModel? activity)
    {
        if (activity?.PanelId is { } panelId)
        {
            PanelToggleRequested?.Invoke(this, panelId);
        }
    }

    /// <summary><see cref="TerminalShortcut"/> and «Terminal» in the welcome tab: shows the terminal, or hides it when it is in view.</summary>
    [RelayCommand]
    private void ToggleTerminal() => PanelToggleRequested?.Invoke(this, PanelIds.Terminal);

    /// <summary>The provider summary of the top bar: shows Auto Doctor, with the detail of each provider, or hides it when it is in view.</summary>
    [RelayCommand]
    private void ToggleAutoDoctor() => PanelToggleRequested?.Invoke(this, PanelIds.AutoDoctor);

    public async Task OpenProjectAsync(string path)
    {
        string normalized;
        try
        {
            normalized = AppSettings.NormalizePath(path);
        }
        catch (ArgumentException ex)
        {
            StatusMessage = Texts.Format("Main_Path_Invalid", ex.Message);
            return;
        }

        if (!Directory.Exists(normalized))
        {
            StatusMessage = Texts.Format("Main_Folder_Gone", PathDisplay.CollapseHome(normalized));
            _settings = _settings.WithProjectRemoved(normalized);
            TrySaveSettings();
            RefreshRecents();
            return;
        }

        // Ask before anything changes: cancelling (or a failed save) leaves the current project untouched.
        if (!await ConfirmDiscardOrSaveAsync(Documents.OfType<FileDocumentViewModel>()))
        {
            return;
        }

        QuickOpen.Close();
        ProjectChanging?.Invoke(this, ProjectPath);

        // File tabs belong to the project being left; so does its orchestrator tab, unless it follows a run in progress
        // (the run goes on, and its tab stays where the user can see it).
        foreach (var document in Documents.Where(d => d is FileDocumentViewModel || IsIdleOrchestratorOfAnotherProject(d, normalized)).ToList())
        {
            RemoveDocument(document);
        }

        ProjectPath = normalized;
        Explorer.Load(normalized);
        _settings = _settings.WithProjectOpened(normalized);
        TrySaveSettings();
        RefreshRecents();
        StatusMessage = null;
        await Journal.SetProjectAsync(normalized);
        await KeepMemoryAsync(normalized, afterRun: false);
        await ShowProjectInScreensAsync(normalized);
        ProjectChanged?.Invoke(this, normalized);
        CheckConsentedResumes(normalized);
    }

    /// <summary>
    /// The first time a project is opened in this session (at startup, the last one), one pass of the consented resume
    /// (docs/16 D-39): the scheduler resumes the runs its consent allows, and each one comes back through
    /// <see cref="OnRunResumed"/>. Nothing resumes without the consent of its header, nothing before the managed exit exists
    /// (the tray started), and nothing while it holds the resumes (<see cref="ConsentedResumes.Suspend"/>): a pass held back is
    /// asked for again once they may run (<see cref="OnResumesAvailable"/>). It runs apart: opening the project does not wait.
    /// </summary>
    private void CheckConsentedResumes(string projectRoot)
    {
        if (_resumes is null)
        {
            return;
        }

        _resumeWanted.Add(projectRoot);
        if (_resumeChecked.Contains(projectRoot))
        {
            return;
        }

        if (!_resumePasses.Add(projectRoot))
        {
            // A pass of the project is going on: if it turns out held back, it is asked for again once it ends.
            _resumeAgain.Add(projectRoot);
            return;
        }

        ConsentedResumeCheck = PassAsync(_resumes, projectRoot);
    }

    /// <summary>The passes may run again: the ones held back are asked for again, on the UI thread.</summary>
    private void OnResumesAvailable(object? sender, EventArgs e)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            CheckWantedResumes();
        }
        else
        {
            _ui.Post(_ => CheckWantedResumes(), null);
        }
    }

    private void CheckWantedResumes()
    {
        foreach (var root in _resumeWanted.Where(r => !_resumeChecked.Contains(r)).ToList())
        {
            CheckConsentedResumes(root);
        }
    }

    private async Task PassAsync(IConsentedResumes resumes, string projectRoot)
    {
        ConsentedPass pass;
        try
        {
            pass = await resumes.ResumeAsync(projectRoot, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The engine is behind a port: a failure is shown in the status bar, never thrown into the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A failure does not go away by asking again in this session: the project counts as checked.
            _resumeChecked.Add(projectRoot);
            _resumeAgain.Remove(projectRoot);
            StatusMessage = Texts.Format("Main_Resume_CheckFailed", ex.Message);
            return;
        }
        finally
        {
            _resumePasses.Remove(projectRoot);
        }

        if (pass.Complete)
        {
            _resumeChecked.Add(projectRoot);
            _resumeAgain.Remove(projectRoot);
        }
        else if (_resumeAgain.Remove(projectRoot))
        {
            // Held back, and the passes were let go while it ran: once more.
            CheckConsentedResumes(projectRoot);
        }

        var outcome = pass.Outcome;
        var resumed = outcome.Where(o => o.Resumed is not null).ToList();
        var failed = outcome.Where(o => o.Cause is not null && o.Resumed is null).ToList();
        if (resumed.Count > 0)
        {
            StatusMessage = resumed.Count == 1
                ? Texts.Format("Main_Resumed_One", resumed[0].Run, OrchestratorCodedText.Painted(resumed[0].ReasonText, resumed[0].Reason))
                : Texts.Format("Main_Resumed_Many", resumed.Count);
        }
        else if (failed.Count > 0)
        {
            StatusMessage = Texts.Format("Main_Resume_Failed", failed[0].Run, OrchestratorCodedText.Painted(failed[0].ReasonText, failed[0].Reason));
        }
    }

    /// <summary>
    /// A run the scheduler resumed, on its own thread and before the suspension the managed exit asks for is handed over: it goes
    /// into the registry of the work at once, without the UI thread (which the end of the session blocks), so the exit lists it
    /// whatever the dispatcher runs first; then the monitor of its «Orquestador» takes it on the UI thread.
    /// </summary>
    private void OnRunResumed(object? sender, ScheduledResumeEventArgs e)
    {
        var announced = new ResumedRunWork(AppSettings.NormalizePath(e.Project.RootPath), e.Project, e.Run);
        _work?.TrackFromAnyThread(announced);
        if (_ui is null)
        {
            FollowResumed(e.Project, e.Run, announced);
        }
        else
        {
            _ui.Post(_ => FollowResumed(e.Project, e.Run, announced), null);
        }
    }

    /// <summary>
    /// A run the scheduler resumed by the consent of its header: the monitor of the «Orquestador» document of its project follows
    /// it, the document created if the tab was never opened, so the run has its place in the tray, its pause and its choice on
    /// exit (ADR-0036). Its events wait for the monitor, their one reader. When that monitor is busy or follows another run, a
    /// monitor of its own, which the tray follows, takes it. Only once a monitor took it is its announcement forgotten: if none
    /// could, the announcement stays in the registry of the work, so the tray shows the run, and the exit asks about it, pauses
    /// it and writes its choice.
    /// </summary>
    private void FollowResumed(ProjectContext project, IOrchestrationRun run, ResumedRunWork announced)
    {
        try
        {
            var root = AppSettings.NormalizePath(project.RootPath);
            if (!_orchestrators.TryGetValue(root, out var document))
            {
                document = _orchestratorFactory.Create(root);
                _orchestrators[root] = document;
                Follow(document);
            }

            if (!document.FollowResumed(run, project))
            {
                FollowInBackground(root, project, run, resumeConsent: null);
                StatusMessage = Texts.Format("Main_Resumed_Busy", run.Id);
            }
        }
#pragma warning disable CA1031 // A run no monitor could take keeps its announcement in the registry: it is said, never thrown into the dispatcher.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (!IsFollowed(run.Id))
            {
                StatusMessage = Texts.Format("Main_Resumed_NotFollowed", run.Id);
                return;
            }
        }

        // A monitor follows it now (the registry has it through that monitor): the announcement is forgotten.
        _work?.Untrack(announced);
    }

    /// <summary>Whether a monitor of the shell (of a document, or of its own) follows the run.</summary>
    private bool IsFollowed(RunId run) =>
        _orchestrators.Values.Any(d => d.Monitor.RunId == run) || _backgroundMonitors.Any(m => m.RunId == run);

    /// <summary>
    /// A run whose document cannot follow it now (its monitor is busy or follows another run): a monitor of its own takes it,
    /// which the tray, its panel, the managed exit and «mantener despierto» follow (ADR-0036).
    /// </summary>
    private void FollowInBackground(string root, ProjectContext project, IOrchestrationRun run, bool? resumeConsent)
    {
        var monitor = new RunMonitorViewModel(TimeProvider.System, _ui);
        EquipBackgroundMonitor(monitor);
        if (_usage is not null)
        {
            monitor.ContextThresholds = _usage.Preferences.Thresholds;
        }

        LearnContextWindows(root, monitor);
        monitor.Attach(run, project);

        // No document gives this monitor the template or the plan: they are read from what the run stored (P52), never from a screen.
        if (_monitors is { } services)
        {
            _ = services.GiveLiveContextAsync(monitor, project, run.Id);
        }

        monitor.ResumeConsent = resumeConsent;
        _runsActive[monitor] = monitor.IsActive;
        monitor.PropertyChanged += OnMonitorPropertyChanged;
        monitor.Finished += (_, _) => OnRunFinished(root);
        _backgroundMonitors.Add(monitor);
        _backgroundRoots[monitor] = root;
        _work?.TrackRun(root, monitor, () => project);
        StatusStrip.Track(monitor);
    }

    /// <summary>
    /// The windows a project learned reach its monitor (P36): the ones known now at once, and the ones read again from its log in
    /// the background; a monitor never waits for them, and in the meantime it has the base table.
    /// </summary>
    private void LearnContextWindows(string projectRoot, RunMonitorViewModel monitor)
    {
        if (_contextWindows is not { } windows)
        {
            return;
        }

        monitor.ContextWindows = windows.Current(projectRoot);
        _ = ReadContextWindowsAsync(windows, projectRoot, [monitor]);
    }

    /// <summary>Reads the log of the project again and gives its windows to <paramref name="monitors"/>, on the UI thread.</summary>
    private static async Task ReadContextWindowsAsync(ContextWindowCatalogs windows, string projectRoot, IReadOnlyList<RunMonitorViewModel> monitors)
    {
        var catalog = await windows.RefreshAsync(projectRoot).ConfigureAwait(true);
        foreach (var monitor in monitors)
        {
            monitor.ContextWindows = catalog;
        }
    }

    /// <summary>
    /// A run the person started or resumed in a document whose monitor took another run meanwhile: a monitor of its own takes it,
    /// so it is never left without one.
    /// </summary>
    private void OnRunHandedOff(object? sender, HandedOffRun handed)
    {
        FollowInBackground(AppSettings.NormalizePath(handed.Project.RootPath), handed.Project, handed.Run, handed.ResumeConsent);
        StatusMessage = Texts.Format("Main_Run_HandedOff", handed.Run.Id);
    }

    private static bool IsIdleOrchestratorOfAnotherProject(DocumentViewModel document, string projectPath) =>
        document is OrchestratorDocumentViewModel { Monitor.IsActive: false } orchestrator
        && !string.Equals(orchestrator.ProjectRoot, projectPath, StringComparison.OrdinalIgnoreCase);

    public async Task OpenFileAsync(string path)
    {
        var id = FileDocumentViewModel.CreateId(path);
        if (Documents.FirstOrDefault(d => d.ContentId == id) is { } existing)
        {
            ActiveContent = existing;
            return;
        }

        var document = new FileDocumentViewModel(path);
        AddDocument(document);
        await document.LoadAsync();
    }

    private void AddDocument(DocumentViewModel document)
    {
        Track(document);
        if (document is OrchestratorDocumentViewModel orchestrator)
        {
            // Its tab is open again: the status bar follows its run and its planning (they were let go when it closed).
            orchestrator.OnTabReopened();
            StatusStrip.Track(orchestrator.Monitor);
            StatusStrip.TrackPlanning(orchestrator);
        }

        if (!Documents.Contains(document))
        {
            document.CloseRequested += OnDocumentCloseRequested;
            if (document is FileDocumentViewModel file)
            {
                file.CompareWithFileRequested += OnCompareWithFileRequested;
            }

            Documents.Add(document);
        }

        ActiveContent = document;
    }

    /// <summary>
    /// The registry of the work follows what a document of the debates holds (ADR-0036): the debate a «Debates» document
    /// follows (opened, challenged or resumed) and the activity of the project of a «Debates» or «Auditoría» document.
    /// </summary>
    private void Track(DocumentViewModel document)
    {
        if (_work is not { } work)
        {
            return;
        }

        switch (document)
        {
            case DebatesDocumentViewModel debates when !_trackedDebates.ContainsKey(debates):
                _trackedDebates[debates] = null;
                debates.DebateFollowed += OnDebateFollowed;
                work.TrackProject(debates.ProjectRoot, () => debates.Project);
                break;
            case ProjectAuditDocumentViewModel audit:
                work.TrackProject(audit.ProjectRoot, () => audit.Project);
                break;
        }
    }

    /// <summary>A «Debates» document follows a new debate: the registry follows it instead of the one before (which ended: one at a time).</summary>
    private void OnDebateFollowed(object? sender, FollowedDebate followed)
    {
        if (sender is not DebatesDocumentViewModel debates || _work is not { } work)
        {
            return;
        }

        if (_trackedDebates.TryGetValue(debates, out var previous))
        {
            previous?.Dispose();
        }

        _trackedDebates[debates] = work.TrackDebate(debates.ProjectRoot, followed.Project, followed.Run);
    }

    private void RemoveDocument(DocumentViewModel document)
    {
        document.CloseRequested -= OnDocumentCloseRequested;
        if (document is FileDocumentViewModel file)
        {
            file.CompareWithFileRequested -= OnCompareWithFileRequested;
        }

        Documents.Remove(document);
        document.NotifyClosed();
        if (document is OrchestratorDocumentViewModel orchestrator)
        {
            // The status bar lets go of its run and its planning, at once or once what runs in them ends.
            StatusStrip.Release(orchestrator.Monitor);
            StatusStrip.ReleasePlanning(orchestrator);
        }

        if (ReferenceEquals(ActiveContent, document))
        {
            ActiveContent = Documents.LastOrDefault();
        }
    }

    /// <summary>True when any open document has unsaved changes.</summary>
    public bool HasUnsavedChanges => Documents.Any(d => d.IsDirty);

    private async void OnDocumentCloseRequested(object? sender, EventArgs e)
    {
        if (sender is DocumentViewModel document)
        {
            await CloseDocumentAsync(document);
        }
    }

    /// <summary>A tab asks to be compared with another file: offer the project files and compare with the chosen one.</summary>
    private async void OnCompareWithFileRequested(object? sender, EventArgs e)
    {
        if (sender is not FileDocumentViewModel document || ProjectPath is not { } root)
        {
            return;
        }

        await QuickOpen.OpenAsync(
            root,
            Texts.Format("QuickOpen_CompareTitle", document.Title),
            path => document.CompareWithFileAsync(path, Path.GetRelativePath(root, path)));
    }

    /// <summary>
    /// Closes a tab, asking first when it has unsaved changes. Returns false when the tab stays open
    /// (cancelled, save failed or a close for the same tab is already in progress).
    /// </summary>
    public async Task<bool> CloseDocumentAsync(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_closing.Add(document))
        {
            return false;
        }

        try
        {
            if (!await ConfirmDiscardOrSaveAsync([document]))
            {
                return false;
            }

            RemoveDocument(document);
            return true;
        }
        finally
        {
            _closing.Remove(document);
        }
    }

    /// <summary>
    /// Asks once about the dirty documents among <paramref name="documents"/>. Returns true when the caller may go on
    /// (nothing was dirty, the user discarded, or everything was saved) and false when it must stop
    /// (cancelled or a save failed; <see cref="StatusMessage"/> says why in the latter case). A question withdrawn by
    /// <paramref name="cancellationToken"/> (the interface locked while it was open, docs/16 D-13) is a cancel: an answer given as
    /// it was withdrawn is not taken.
    /// </summary>
    public async Task<bool> ConfirmDiscardOrSaveAsync(IEnumerable<DocumentViewModel> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var dirty = documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            return true;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var canSave = dirty.All(d => d.CanSave);
        var choice = _unsavedPrompt.Ask(dirty.Select(d => d.Title).ToList(), canSave, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return choice switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save when canSave => await TrySaveAllAsync(dirty),
            _ => false,
        };
    }

    private async Task<bool> TrySaveAllAsync(IReadOnlyList<DocumentViewModel> dirty)
    {
        foreach (var document in dirty)
        {
            try
            {
                await document.SaveAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                StatusMessage = ex is FileChangedOnDiskException
                    ? Texts.Format("Main_Save_ChangedOnDisk", document.Title)
                    : Texts.Format("Main_Save_Failed", document.Title, ex.Message);
                return false;
            }

            if (document.IsDirty)
            {
                // A save that returns without clearing the flag did not persist the edits (or they changed meanwhile).
                StatusMessage = Texts.Format("Main_Save_Pending", document.Title);
                return false;
            }
        }

        return true;
    }

    private void RefreshRecents()
    {
        RecentProjects.Clear();
        foreach (var path in _settings.RecentProjects)
        {
            RecentProjects.Add(RecentProjectItem.From(path));
        }
    }

    private void TrySaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (IOException ex)
        {
            StatusMessage = Texts.Format("Main_Settings_SaveFailed", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = Texts.Format("Main_Settings_SaveFailed", ex.Message);
        }
    }

    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateProviderSummary();

    private void OnDiagnosticsPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateProviderSummary();

    private void UpdateProviderSummary()
    {
        ProviderSummary = ProviderSummary.From(Diagnostics);
    }
}
