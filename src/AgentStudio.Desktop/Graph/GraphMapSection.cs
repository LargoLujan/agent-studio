using System.Collections.ObjectModel;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Help;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Graph;

/// <summary>
/// What «Grafo» reads, all optional (docs/20 D-19, D-21 and D-22): without the analysis the section says it is not available, and without the proposer there is
/// no «Proponer relaciones» (never a button that does nothing).
/// </summary>
/// <param name="Analysis">The analysis of a commit, cached by commit.</param>
/// <param name="Git">The history of the repository by the hardened git layer, to know the version the project is at and whether the folder has changes.</param>
/// <param name="Inference">The relations an AI proposed, kept apart from the model.</param>
/// <param name="Tasks">The tasks that changed files of a folder, for «Ir a la tarea».</param>
/// <param name="Proposer">«Proponer relaciones»: one launch of an agent, read-only, with the OK of the person each time.</param>
/// <param name="Estimator">The estimate shown before the OK.</param>
public sealed record GraphBackend(
    ICommitAnalysisService? Analysis = null,
    IRepositoryHistoryReader? Git = null,
    IGraphInferenceStore? Inference = null,
    IGraphTaskIndex? Tasks = null,
    IGraphRelationProposer? Proposer = null,
    IUsageEstimator? Estimator = null)
{
    /// <summary>Nothing composed.</summary>
    public static GraphBackend None { get; } = new();
}

/// <summary>Where the person goes from a node of the graph (docs/20 D-22, F6-5).</summary>
public static class GraphFileTarget
{
    /// <summary>
    /// The file of the working folder that a path of the model names, or null when it is not there. A path of the model is data of a repository: it is checked
    /// again here (relative, never leading out of the folder, no link on the way, a file that exists), because the copy it came from is gone and the folder
    /// may have changed since.
    /// </summary>
    public static string? Resolve(string projectRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !RepositoryPaths.IsSafeRelative(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(projectRoot);
            var current = root;
            var segments = relativePath.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                FileSystemInfo info = i < segments.Length - 1 ? new DirectoryInfo(current) : new FileInfo(current);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                {
                    return null;
                }
            }

            return current.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? current : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// «Grafo» (docs/20 D-22, F6-3 to F6-5): the solution as a canvas of Nodify that is read-only, for the saved version of the project the analysis describes.
/// The projects are what is drawn first; a project unfolds into its namespaces. A relation read from the files and one an AI proposed are drawn differently
/// (dashed, another colour, the label «IA», and their own name for the screen reader), and the legend says so. From a node the person goes to its file or to the
/// work. «Proponer relaciones» spends quota: it opens a card with what is sent and the estimate, and nothing starts until the person says yes.
/// </summary>
public sealed partial class GraphMapSection : GraphSection
{
    /// <summary>With more nodes than this visible, a namespace is not unfolded: they stay folded in their project (docs/20 D-22).</summary>
    public const int MaxVisibleNodes = 200;

    private readonly GraphBackend _backend;
    private readonly CanvasText _text;
    private readonly TimeProvider _time;
    private CommitAnalysis? _analysis;
    private IReadOnlyList<GraphEdge> _inferred = [];
    private readonly HashSet<string> _unfolded = new(StringComparer.Ordinal);
    private ProjectContext? _pendingProject;
    private CodeGraph? _pendingGraph;
    private int _selectionGeneration;
    private int _loadGeneration;

    public GraphMapSection(GraphBackend backend, TimeProvider? time = null, IUiTextProvider? texts = null)
        : base("Info_Graph_Map")
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _time = time ?? TimeProvider.System;
        _text = new CanvasText(texts);
        Consent = new QuotaConsentCardViewModel(_text, _time);
        TaskRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTasks));
        Gaps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoGaps));

        // «Proponer relaciones» can be pressed once there is a graph and nothing is running or open: when any of that changes, the button is asked again.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy) or nameof(Graph))
            {
                ProposeCommand.NotifyCanExecuteChanged();
            }
        };
        Consent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuotaConsentCardViewModel.IsOpen))
            {
                ProposeCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public bool HasNoTasks => TaskRows.Count == 0;

    public bool HasNoGaps => Gaps.Count == 0;

    /// <summary>The row of the list the person chose: it chooses its node, so the list does what a click on the canvas does.</summary>
    [ObservableProperty]
    private GraphRowItem? _selectedRow;

    partial void OnSelectedRowChanged(GraphRowItem? value)
    {
        if (value is not null && SelectedNode?.Id != value.Id)
        {
            SelectedNode = Nodes.FirstOrDefault(n => n.Id == value.Id);
        }
    }

    /// <summary>The user opened a file of the project from the graph: the full path, already checked against the folder. The shell opens it in the editor.</summary>
    public event EventHandler<string>? FileRequested;

    /// <summary>The user went to the work of a task from a project: the shell opens «Trabajo» on its chain.</summary>
    public event EventHandler<TraceRef>? TaskRequested;

    public ObservableCollection<GraphNodeItem> Nodes { get; } = [];

    public ObservableCollection<GraphWireItem> Wires { get; } = [];

    /// <summary>The same graph as words, for the keyboard and the screen reader.</summary>
    public ObservableCollection<GraphRowItem> Rows { get; } = [];

    public ObservableCollection<GraphLegendItem> Legend { get; } = [];

    public ObservableCollection<GraphGapRow> Gaps { get; } = [];

    public ObservableCollection<GraphFileRow> Files { get; } = [];

    public ObservableCollection<GraphTaskRow> TaskRows { get; } = [];

    /// <summary>The OK of the person for «Proponer relaciones», with the estimate in front of them.</summary>
    public QuotaConsentCardViewModel Consent { get; }

    /// <summary>What the card says will be sent.</summary>
    public ObservableCollection<string> CardLines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraph))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private CodeGraph? _graph;

    public bool HasGraph => Graph is not null;

    public bool IsEmpty => HasProject && !IsBusy && Graph is null;

    /// <summary>The version of the project the graph describes, and whether the folder has changes since.</summary>
    [ObservableProperty]
    private string _versionText = string.Empty;

    /// <summary>How much was read completely: nothing partial is presented as complete.</summary>
    [ObservableProperty]
    private string _coverageText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _status;

    public bool HasStatus => Status is not null;

    [ObservableProperty]
    private bool _showPackages;

    /// <summary>A switch that hides the relations an AI proposed (docs/20 D-21).</summary>
    [ObservableProperty]
    private bool _showInferred = true;

    /// <summary>There is at least one relation proposed by an AI: the legend names them and the switch is offered.</summary>
    [ObservableProperty]
    private bool _hasInferred;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    [NotifyPropertyChangedFor(nameof(CanOpenProjectFile))]
    [NotifyPropertyChangedFor(nameof(SelectedIsProject))]
    [NotifyPropertyChangedFor(nameof(SelectedIsNamespace))]
    [NotifyPropertyChangedFor(nameof(CanExpandSelected))]
    private GraphNodeItem? _selectedNode;

    /// <summary>The node chosen is a project with namespaces to unfold.</summary>
    public bool CanExpandSelected => SelectedNode is { CanExpand: true };

    public bool HasSelection => SelectedNode is not null;

    public bool SelectedIsProject => SelectedNode?.Kind == GraphNodeKind.Project;

    public bool SelectedIsNamespace => SelectedNode?.Kind == GraphNodeKind.Namespace;

    public string SelectedTitle => SelectedNode is { } node ? Texts.Format("Graph_Selected_Title", node.KindText, node.Title) : Texts.Get("Graph_Selected_None");

    public bool CanOpenProjectFile => SelectedNode is { Kind: GraphNodeKind.Project, Node.Path: not null };

    /// <summary>«Proponer relaciones» is composed: without it the button is not offered.</summary>
    public bool HasProposer => _backend.Proposer is not null;

    public bool CanAnalyze => _backend.Analysis is not null && _backend.Git is not null;

    public string UnavailableText => Texts.Get("Graph_Unavailable");

    public bool IsAvailable => CanAnalyze;

    protected override void ProjectChanged()
    {
        // What an earlier load or proposal is still waiting for is not this project's: it will find itself stale and show nothing.
        _loadGeneration++;
        IsBusy = false;
        _analysis = null;
        _inferred = [];
        _unfolded.Clear();
        Graph = null;
        SelectedNode = null;
        ClearView();
        Gaps.Clear();
        VersionText = string.Empty;
        CoverageText = string.Empty;
        Status = null;
        Consent.Close();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Reads what is kept for the version the project is at; when nothing is, it calculates it (D-19: «al abrir la pantalla si HEAD cambió»).</summary>
    public override Task ReloadAsync(CancellationToken ct) => LoadAsync(recalculate: false, ct);

    /// <summary>
    /// The one way the graph is read. A load is stale when the project changed, or a newer load began, while it waited (the analysis can take minutes or wait for
    /// the lock of a verification): a stale load shows nothing, so the graph of a project is never painted under another.
    /// </summary>
    private async Task LoadAsync(bool recalculate, CancellationToken ct)
    {
        if (Project is not { } project || !CanAnalyze)
        {
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        var generation = ++_loadGeneration;
        bool Stale() => generation != _loadGeneration;
        IsBusy = true;
        Error = null;
        try
        {
            var state = await _backend.Git!.SummaryAsync(project, ct).ConfigureAwait(true);
            if (Stale())
            {
                return;
            }

            if (!state.Accepted || state.HeadCommit is not { } head || !TraceRef.IsFullCommitId(head))
            {
                Error = Texts.Get("Graph_Error_NoVersion");
                return;
            }

            var analysis = recalculate ? null : await _backend.Analysis!.FindAsync(project, head, ct).ConfigureAwait(true);
            if (Stale())
            {
                return;
            }

            if (analysis is null)
            {
                Status = Texts.Get("Graph_Status_Calculating");
                analysis = await _backend.Analysis!.AnalyzeAsync(project, head, ct).ConfigureAwait(true);
                if (Stale())
                {
                    return;
                }

                Status = null;
            }

            await ShowAsync(project, analysis, state.HasUncommittedChanges, Stale, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (!Stale())
            {
                Status = null;
            }

            throw;
        }
#pragma warning disable CA1031 // The analysis is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (!Stale())
            {
                Status = null;
                Error = Texts.Format("Graph_Error_Analysis", ex.Message);
            }
        }
        finally
        {
            if (!Stale())
            {
                IsBusy = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>«Actualizar»: calculates the version the project is at now, even when something is kept for it (a file that timed out on a busy machine is read again).</summary>
    [RelayCommand]
    private Task RefreshAsync(CancellationToken ct) => LoadAsync(recalculate: true, ct);

    private async Task ShowAsync(ProjectContext project, CommitAnalysis analysis, bool hasChanges, Func<bool> stale, CancellationToken ct)
    {
        var inferred = _backend.Inference is { } store ? await store.ListAsync(project, ct).ConfigureAwait(true) : [];
        if (stale())
        {
            return;
        }

        _analysis = analysis;
        _inferred = inferred;
        VersionText = Texts.Format(hasChanges ? "Graph_Version_WithChanges" : "Graph_Version", analysis.Commit[..Math.Min(7, analysis.Commit.Length)]);
        Graph = analysis.Graph;
        var coverage = analysis.Graph.Coverage;
        CoverageText = Texts.Format("Graph_Coverage", coverage.Projects, coverage.CompleteProjects, coverage.PartialProjects, coverage.Gaps);
        Gaps.Clear();
        foreach (var gap in analysis.Graph.Gaps)
        {
            Gaps.Add(GraphGapRow.From(analysis.Graph, gap));
        }

        Rebuild();
    }

    partial void OnShowPackagesChanged(bool value) => Rebuild();

    partial void OnShowInferredChanged(bool value) => Rebuild();

    /// <summary>The model with what an AI proposed laid over it: what the canvas draws. The hash of the model never moves.</summary>
    public CodeGraph? Displayed => Graph?.WithInferred(_inferred, out _);

    /// <summary>The nodes that are visible now: the projects, the namespaces of the projects that are unfolded and, if asked, the packages.</summary>
    private IReadOnlyList<GraphNode> VisibleNodes(CodeGraph graph)
    {
        var contains = graph.Edges.Where(e => e.Kind == GraphEdgeKind.Contains && e.Origin == GraphEdgeOrigin.Static && graph.Nodes.Any(n => n.Id == e.To && n.Kind == GraphNodeKind.Namespace))
            .ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
        var shown = new List<GraphNode>();
        foreach (var node in graph.Nodes)
        {
            switch (node.Kind)
            {
                case GraphNodeKind.Project:
                    shown.Add(node);
                    break;
                case GraphNodeKind.Package when ShowPackages:
                    shown.Add(node);
                    break;
            }
        }

        var ids = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var project in _unfolded.Order(StringComparer.Ordinal))
        {
            foreach (var child in contains[project])
            {
                shown.Add(ids[child]);
            }
        }

        return shown;
    }

    private void Rebuild()
    {
        var keep = SelectedNode?.Id;
        ClearView();
        if (Displayed is not { } graph)
        {
            return;
        }

        var visible = VisibleNodes(graph);
        var items = visible.ToDictionary(n => n.Id, n => new GraphNodeItem(n, _unfolded.Contains(n.Id)) { CanExpand = n.Kind == GraphNodeKind.Project && HasNamespaces(graph, n.Id) }, StringComparer.Ordinal);
        var edges = graph.Edges
            .Where(e => e.Kind != GraphEdgeKind.Contains && items.ContainsKey(e.From) && items.ContainsKey(e.To) && (ShowInferred || e.Origin == GraphEdgeOrigin.Static))
            .ToList();
        HasInferred = graph.Edges.Any(e => e.Origin == GraphEdgeOrigin.Inferred);
        var places = GraphLayout.Locations(
            [.. items.Keys],
            edges.Select(e => (e.From, e.To)),
            id => items[id].Title);
        foreach (var (id, item) in items.OrderBy(p => p.Value.Title, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            item.Location = places[id];
            Nodes.Add(item);
        }

        foreach (var edge in edges)
        {
            Wires.Add(new GraphWireItem(edge, items[edge.From], items[edge.To]));
        }

        foreach (var item in Nodes)
        {
            var uses = edges.Where(e => e.From == item.Id).Select(e => items[e.To].Title).Order(StringComparer.Ordinal).ToList();
            var proposed = edges.Any(e => e.From == item.Id && e.Origin == GraphEdgeOrigin.Inferred);
            var line = uses.Count == 0
                ? Texts.Format("Graph_Row_NoUses", item.KindText, item.Title, item.PartialText)
                : Texts.Format("Graph_Row_Uses", item.KindText, item.Title, item.PartialText, string.Join(", ", uses));
            Rows.Add(new GraphRowItem(item.Id, line.Replace("  ", " ", StringComparison.Ordinal).Trim(), proposed));
        }

        Legend.Add(new GraphLegendItem(Texts.Get("Graph_Legend_Static"), false, false));
        Legend.Add(new GraphLegendItem(Texts.Get("Graph_Legend_Partial"), false, true));
        if (HasInferred)
        {
            Legend.Add(new GraphLegendItem(Texts.Get("Graph_Legend_Inferred"), true, false));
        }

        SelectedNode = keep is not null && items.TryGetValue(keep, out var again) ? again : null;
    }

    private static bool HasNamespaces(CodeGraph graph, string projectId) =>
        graph.Edges.Any(e => e.From == projectId && e.Kind == GraphEdgeKind.Contains && graph.Nodes.Any(n => n.Id == e.To && n.Kind == GraphNodeKind.Namespace));

    private void ClearView()
    {
        Nodes.Clear();
        Wires.Clear();
        Rows.Clear();
        Legend.Clear();
        Files.Clear();
        TaskRows.Clear();
    }

    // ---- unfolding ----

    /// <summary>Unfolds the namespaces of the selected project, or folds them back (D-22: two levels).</summary>
    [RelayCommand]
    private void ToggleExpand()
    {
        if (SelectedNode is not { CanExpand: true } node || Graph is null)
        {
            return;
        }

        if (_unfolded.Remove(node.Id))
        {
            Rebuild();
            return;
        }

        _unfolded.Add(node.Id);
        if (VisibleNodes(Displayed!).Count > MaxVisibleNodes)
        {
            _unfolded.Remove(node.Id);
            Message = Texts.Format("Graph_Message_TooMany", MaxVisibleNodes);
            return;
        }

        Message = null;
        Rebuild();
    }

    // ---- from a node to its file and to its work (F6-5) ----

    partial void OnSelectedNodeChanged(GraphNodeItem? value)
    {
        Files.Clear();
        TaskRows.Clear();
        if (value is null || _analysis is null)
        {
            return;
        }

        foreach (var path in _analysis.Files.For(value.Id))
        {
            Files.Add(new GraphFileRow(path));
        }

        if (value is { Kind: GraphNodeKind.Project, Node.Path: { } projectPath })
        {
            _ = LoadTasksAsync(value.Id, projectPath);
        }
    }

    private async Task LoadTasksAsync(string nodeId, string projectPath)
    {
        if (Project is not { } project || _backend.Tasks is not { } index)
        {
            return;
        }

        var generation = ++_selectionGeneration;
        var folder = projectPath.Contains('/', StringComparison.Ordinal) ? projectPath[..projectPath.LastIndexOf('/')] : string.Empty;
        try
        {
            var tasks = await index.TasksUnderAsync(project, folder, CancellationToken.None).ConfigureAwait(true);
            if (generation == _selectionGeneration && SelectedNode?.Id == nodeId)
            {
                TaskRows.Clear();
                foreach (var task in tasks)
                {
                    TaskRows.Add(new GraphTaskRow(task));
                }
            }
        }
#pragma warning disable CA1031 // The index is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Message = Texts.Format("Graph_Message_TasksFailed", ex.Message);
        }
    }

    /// <summary>Opens the <c>.csproj</c> of the selected project in the editor.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProjectFile))]
    private void OpenProjectFile()
    {
        if (SelectedNode is { Kind: GraphNodeKind.Project, Node.Path: { } path })
        {
            OpenFile(path);
        }
    }

    /// <summary>Opens a file that declares the selected namespace in the editor.</summary>
    [RelayCommand]
    private void OpenFile(string? path)
    {
        if (Project is not { } project || path is null)
        {
            return;
        }

        if (GraphFileTarget.Resolve(project.RootPath, path) is not { } full)
        {
            Message = Texts.Format("Graph_Message_FileGone", path);
            return;
        }

        Message = null;
        FileRequested?.Invoke(this, full);
    }

    /// <summary>Goes to the work of a task that changed files of the project: «Trabajo», on its chain.</summary>
    [RelayCommand]
    private void OpenTask(GraphTaskRow? row)
    {
        if (row is null)
        {
            return;
        }

        TaskRequested?.Invoke(this, TraceRef.Task(row.Task.Run.Value, row.Task.Task.Value));
    }

    partial void OnSelectedNodeChanged(GraphNodeItem? oldValue, GraphNodeItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        OpenProjectFileCommand.NotifyCanExecuteChanged();
    }

    // ---- «Proponer relaciones» (D-21, decision 7 (a)) ----

    private bool CanPropose() => HasProposer && Graph is not null && !IsBusy && !Consent.IsOpen;

    /// <summary>Shows what would be sent and the estimate; nothing is launched until the person says yes.</summary>
    [RelayCommand(CanExecute = nameof(CanPropose))]
    private async Task ProposeAsync()
    {
        if (Project is not { } project || Graph is not { } graph || _backend.Proposer is null)
        {
            return;
        }

        var nodes = GraphInferenceService.NodesToSend(graph);
        if (nodes.Count < 2)
        {
            Message = Texts.Get("Graph_Message_NothingToRelate");
            return;
        }

        _pendingProject = project;
        _pendingGraph = graph;
        var choice = HelpAgentChoice.Default;
        CardLines.Clear();
        CardLines.Add(Texts.Format("Graph_Card_Nodes", nodes.Count));
        CardLines.Add(Texts.Format("Graph_Card_Known", graph.Edges.Count(e => e.Origin == GraphEdgeOrigin.Static && e.Kind is GraphEdgeKind.ProjectReference or GraphEdgeKind.NamespaceUse)));
        CardLines.Add(Texts.Get("Graph_Card_Agent"));
        CardLines.Add(Texts.Get("Graph_Card_Never"));
        Func<CancellationToken, Task<UsageEstimate?>>? estimate = null;
        if (_backend.Estimator is { } estimator)
        {
            estimate = async ct => await estimator.EstimateAsync(
                new UsageEstimateRequest(project, GraphRelationsAgent.Id, choice.Provider, choice.Model, AgentRunPurpose.GraphRelations),
                ct).ConfigureAwait(false);
        }

        // The OK is for this graph and this agent: the description carries their fingerprint, which the service checks.
        await Consent.OpenAsync(Texts.Get("Graph_Card_ConsentDescription") + " " + GraphInferenceService.ConsentTag(graph, choice), estimate).ConfigureAwait(true);
        ProposeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CancelProposal()
    {
        _pendingProject = null;
        _pendingGraph = null;
        CardLines.Clear();
        Consent.Close();
        ProposeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Dar mi OK y enviar»: the OK of this proposal, which the service spends once.</summary>
    [RelayCommand]
    private async Task ConfirmProposalAsync()
    {
        if (_backend.Proposer is not { } proposer || _pendingProject is not { } project || _pendingGraph is not { } graph || !Consent.IsOpen || !Consent.IsReady)
        {
            return;
        }

        var consent = Consent.Grant();
        Consent.Close();
        _pendingProject = null;
        _pendingGraph = null;
        CardLines.Clear();
        var generation = _loadGeneration;
        bool Stale() => generation != _loadGeneration;
        IsBusy = true;
        Status = Texts.Get("Graph_Status_Proposing");
        try
        {
            // What the person paid for is kept by the service whatever happens here; if the project changed meanwhile, the screen shows nothing of it.
            var reply = await proposer.ProposeAsync(new GraphProposalRequest(project, graph, HelpAgentChoice.Default, _text.Provider.Culture.Name, consent), null, CancellationToken.None).ConfigureAwait(true);
            if (Stale())
            {
                return;
            }

            if (reply.Error is { } error)
            {
                Error = Texts.Format("Graph_Error_Proposal", error);
            }
            else
            {
                Message = Texts.Format("Graph_Message_Proposed", reply.Accepted.Count, reply.Discarded);
                var inferred = _backend.Inference is { } store ? await store.ListAsync(project, CancellationToken.None).ConfigureAwait(true) : _inferred;
                if (!Stale())
                {
                    _inferred = inferred;
                    Rebuild();
                }
            }
        }
#pragma warning disable CA1031 // The service is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (!Stale())
            {
                Error = Texts.Format("Graph_Error_Proposal", ex.Message);
            }
        }
        finally
        {
            if (!Stale())
            {
                IsBusy = false;
                Status = null;
            }

            ProposeCommand.NotifyCanExecuteChanged();
        }
    }
}
