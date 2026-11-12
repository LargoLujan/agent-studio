using System.ComponentModel;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Panels;
using AgentStudio.Desktop.Setup;
using AgentStudio.Desktop.Tools;
using AgentStudio.Domain.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

// What the shell shows of the visual builder at the end of Phase 13 (docs/17 section 9.3, «Integración (el lead)»; docs/03 section 5.5):
// «Agentes», «Herramientas» and «Pruebas» in the activity bar, the panels «Problemas», «Salida» and «Pruebas» of the bottom zone, the
// counter of the agents in the top bar and the configurator of the project. Every text comes from the resources.
public sealed partial class MainViewModel
{
    private MonitorServices? _monitors;
    private SetupViewModelFactory? _setupFactory;
    private SetupSuggestions? _setupSuggestions;
    private readonly Dictionary<string, SetupDocumentViewModel> _setups = new(StringComparer.OrdinalIgnoreCase);
    private ToolsDocumentViewModel? _tools;
    private RunMonitorViewModel? _idleMonitor;
    private RunMonitorViewModel? _counted;
    private OrchestratorDocumentViewModel? _panelsOf;

    /// <summary>The panel «Problemas» of the bottom zone: what the analyzer and the validator see, and why each task failed.</summary>
    public ProblemsPanelViewModel Problems { get; } = new();

    /// <summary>The panel «Salida» of the bottom zone: the output of the run, of a task or of an agent.</summary>
    public OutputPanelViewModel Output { get; } = new();

    /// <summary>The panel «Pruebas» of the bottom zone: the verifications of the run. The button of the tests of HEAD is shown when the shell composes the service (HeadTestsServices).</summary>
    public TestsPanelViewModel Tests { get; } = new();

    /// <summary>
    /// The counter of the agents of the run followed in the open project (docs/17 D-43): «Agentes: 2 trabajando · 1 te pregunta».
    /// Empty while no run is followed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAgentCounter))]
    private string _agentCounter = string.Empty;

    public bool HasAgentCounter => AgentCounter.Length > 0;

    /// <summary>What a screen reader says of the counter.</summary>
    public string AgentCounterName => Texts.Get("Shell_Counter_Name");

    private void InitializeBuilderShell(MonitorServices? monitors, SetupViewModelFactory? setup, SetupSuggestions? suggestions)
    {
        _monitors = monitors;
        _setupFactory = setup;
        _setupSuggestions = suggestions;
        AttachPanels();
    }

    /// <summary>«Agentes», «Herramientas» and «Pruebas»: the entries of the bar that belong to the visual builder.</summary>
    private IEnumerable<ActivityItemViewModel> AgentActivities()
    {
        yield return new ActivityItemViewModel("agents", Texts.Get("Shell_Activity_Agents"), "", null, null, OpenAgentsCommand);
    }

    private IEnumerable<ActivityItemViewModel> BuilderToolActivities()
    {
        yield return new ActivityItemViewModel("tools", Texts.Get("Shell_Activity_Tools"), "", null, null, OpenToolsCommand);
        yield return new ActivityItemViewModel("tests", Texts.Get("Shell_Activity_Tests"), "", null, PanelIds.Tests, TogglePanelCommand);
    }

    private OrchestratorDocumentViewModel? CurrentOrchestrator =>
        ProjectPath is { } root && _orchestrators.TryGetValue(root, out var document) ? document : null;

    /// <summary>
    /// «Agentes» (docs/17 D-42, F13-17): the orchestrator of the open project on the layer Team, where agents are added, configured and
    /// removed, with the inspector of each one.
    /// </summary>
    [RelayCommand]
    private async Task OpenAgentsAsync()
    {
        await OpenOrchestratorAsync();
        CurrentOrchestrator?.ShowTeamLayer();
    }

    /// <summary>«Herramientas» (docs/17 D-46): what each agent of the template may use, from the template and the profile of the open project.</summary>
    [RelayCommand]
    private async Task OpenToolsAsync()
    {
        if (ProjectPath is not { } root)
        {
            StatusMessage = Texts.Get("Shell_Setup_NoProject");
            return;
        }

        var orchestrator = await EnsureOrchestratorAsync(root);
        if (_tools is null)
        {
            _tools = new ToolsDocumentViewModel(new ToolsViewModel());
            _tools.Tools.GiveToAgentRequested += OnGiveToAgentRequested;
        }

        _tools.Tools.Load(orchestrator.Context?.Roster ?? [], orchestrator.Context?.Verification);
        AddDocument(_tools);
    }

    /// <summary>«Dar a un agente…» of «Herramientas»: the inspector of the agents on the layer Team, where the change is made and approved as always.</summary>
    private async void OnGiveToAgentRequested(object? sender, string tool)
    {
        await OpenOrchestratorAsync();
        if (CurrentOrchestrator is not { } document)
        {
            return;
        }

        document.ShowTeamLayer();
        if (document.Canvas.Team.SelectedAgent is null && document.Canvas.Team.Agents.FirstOrDefault() is { } first)
        {
            document.Canvas.Team.Select(first);
        }

        StatusMessage = Texts.Format("Shell_Tools_Give", tool);
    }

    /// <summary>«Configurar proyecto» of the band of preparation of an orchestrator: the shell opens the configurator of that project.</summary>
    private async void OnConfigureProjectRequested(object? sender, EventArgs e)
    {
        if (sender is not OrchestratorDocumentViewModel document)
        {
            return;
        }

        try
        {
            await OpenSetupForAsync(document.ProjectRoot);
        }
#pragma warning disable CA1031 // The stores are behind ports: a failure is said in the status bar, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = Texts.Format("Shell_Setup_ReadAgainFailed", ex.Message);
        }
    }

    /// <summary>The orchestrator document of a project, created and read when it did not exist; not shown.</summary>
    private async Task<OrchestratorDocumentViewModel> EnsureOrchestratorAsync(string root)
    {
        if (!_orchestrators.TryGetValue(root, out var document))
        {
            document = _orchestratorFactory.Create(root);
            _orchestrators[root] = document;
            Follow(document);
        }

        await document.OpenAsync();
        return document;
    }

    // ---------- «Configurar proyecto» ----------

    /// <summary>«Configurar proyecto»: the configurator of the open project in a tab; it only opens when the person asks.</summary>
    [RelayCommand]
    private async Task OpenSetupAsync()
    {
        if (ProjectPath is not { } root)
        {
            StatusMessage = Texts.Get("Shell_Setup_NoProject");
            return;
        }

        await OpenSetupForAsync(root);
    }

    private async Task OpenSetupForAsync(string root)
    {
        if (_setupFactory is not { } factory)
        {
            return;
        }

        var orchestrator = await EnsureOrchestratorAsync(root);
        if (!_setups.TryGetValue(root, out var document))
        {
            var setup = factory.Create();
            setup.Applied += (_, result) => _ = OnSetupAppliedAsync(orchestrator, result);
            document = new SetupDocumentViewModel(root, setup, RecentProjectItem.From(root).Name);
            _setups[root] = document;
        }

        AddDocument(document);
        await document.Setup.LoadAsync(orchestrator.Context?.Project);
    }

    private async Task OnSetupAppliedAsync(OrchestratorDocumentViewModel orchestrator, SetupApplyResult result)
    {
        try
        {
            await orchestrator.OnSetupAppliedAsync(result);
        }
#pragma warning disable CA1031 // Reading the template again goes through the stores: a failure is said in the status bar, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = Texts.Format("Shell_Setup_ReadAgainFailed", ex.Message);
        }
    }

    /// <summary>
    /// The entry of the welcome tab: with the project open, «Configurar proyecto» is always there, and the first time a project is opened
    /// the tab also says it is a good moment for it. It is noted as suggested at once and never opens by itself.
    /// </summary>
    private void ShowSetupSuggestion(string? projectRoot)
    {
        if (_welcome is null || _setupFactory is null)
        {
            return;
        }

        if (projectRoot is null || _setupSuggestions is not { } suggestions || suggestions.WasSuggested(projectRoot))
        {
            _welcome.SetupSuggestion = null;
            return;
        }

        _welcome.SetupSuggestion = Texts.Get("Shell_Setup_Suggestion");
        suggestions.MarkSuggested(projectRoot);
    }

    // ---------- Panels, counter and tray of a document ----------

    /// <summary>
    /// The panels of the bottom zone and the counter follow the monitor of the orchestrator of the open project; without one, an idle
    /// monitor, so they never show the run of another project.
    /// </summary>
    private void AttachPanels()
    {
        var document = CurrentOrchestrator;
        var monitor = document?.Monitor ?? (_idleMonitor ??= new RunMonitorViewModel(TimeProvider.System, _ui));
        Problems.Attach(monitor);
        Output.Attach(monitor);
        Tests.Attach(monitor);
        Output.SelectTask(null);
        TrackCounter(monitor);
        FollowSelection(document);
    }

    /// <summary>The panels read what the canvas selects and the findings of its band, from the document of the open project only.</summary>
    private void FollowSelection(OrchestratorDocumentViewModel? document)
    {
        if (ReferenceEquals(_panelsOf, document))
        {
            return;
        }

        if (_panelsOf is not null)
        {
            _panelsOf.Canvas.PropertyChanged -= OnPanelsCanvasChanged;
            _panelsOf.Canvas.Team.AgentSelected -= OnPanelsAgentSelected;
            if (_panelsOf.Builder is { } before)
            {
                before.Band.AnalysisChanged -= OnPanelsAnalysisChanged;
            }
        }

        _panelsOf = document;
        Problems.SetMontage(null);
        if (document is null)
        {
            return;
        }

        document.Canvas.PropertyChanged += OnPanelsCanvasChanged;
        document.Canvas.Team.AgentSelected += OnPanelsAgentSelected;
        if (document.Builder is { } builder)
        {
            builder.Band.AnalysisChanged += OnPanelsAnalysisChanged;
            Problems.SetMontage(builder.Band.Analysis);
        }
    }

    private void OnPanelsCanvasChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasViewModel.SelectedNode) && _panelsOf is { } document)
        {
            Output.SelectTask(document.Canvas.SelectedNode?.Id);
        }
    }

    private void OnPanelsAgentSelected(object? sender, AgentId? agent) => Output.SelectAgent(agent);

    private void OnPanelsAnalysisChanged(object? sender, EventArgs e) => Problems.SetMontage(_panelsOf?.Builder?.Band.Analysis);

    private void TrackCounter(RunMonitorViewModel monitor)
    {
        if (_counted is not null)
        {
            _counted.Changed -= OnCounterChanged;
            _counted.PropertyChanged -= OnCounterMonitorChanged;
        }

        _counted = monitor;
        monitor.Changed += OnCounterChanged;
        monitor.PropertyChanged += OnCounterMonitorChanged;
        RefreshCounter();
    }

    private void OnCounterChanged(object? sender, RunLiveChangedEventArgs e) => RefreshCounter();

    private void OnCounterMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunMonitorViewModel.IsAttached) or nameof(RunMonitorViewModel.IsActive))
        {
            RefreshCounter();
        }
    }

    private void RefreshCounter() =>
        AgentCounter = _counted?.Current is { } state ? AgentLiveTexts.Counter(state) : string.Empty;

    /// <summary>A monitor the shell creates for a run nobody else follows gets the same sources as the others.</summary>
    private void EquipBackgroundMonitor(RunMonitorViewModel monitor)
    {
        if (_monitors?.Equip(monitor) is not { } release)
        {
            return;
        }

        // Its service of windows lets go of the lock and of the monitor when its run ends.
        monitor.Finished += (_, _) => release.Dispose();
    }
}
