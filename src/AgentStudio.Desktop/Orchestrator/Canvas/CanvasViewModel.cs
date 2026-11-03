using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

public enum NoticeKind
{
    Info,
    Error,
}

/// <summary>One button of the node palette; Debate is listed but disabled until Phase 5.</summary>
public sealed record PaletteItem(WorkTaskKind Kind)
{
    public bool IsAvailable => NodeKinds.IsAvailable(Kind);

    public string Label => IsAvailable ? NodeKinds.Label(Kind) : $"{NodeKinds.Label(Kind)} · {NodeKinds.UnavailableBadge}";

    public string Hint => IsAvailable
        ? $"Añadir un nodo «{NodeKinds.Label(Kind)}»"
        : $"Aplazado: {NodeKinds.UnavailableReason}. El validador del plan rechaza los nodos de debate.";
}

/// <summary>One entry of the status legend under the canvas.</summary>
public sealed record StatusLegendItem(WorkTaskStatus Status)
{
    public string Label => TaskStatusText.Label(Status);
}

/// <summary>
/// The orchestrator canvas as plain state: nodes, wires, selection, viewport and the messages next to the nodes. The view
/// (<c>Views/Orchestrator/CanvasView.xaml</c>) is only bindings to Nodify's <c>NodifyEditor</c>; nothing here references a
/// control, so a different canvas library would only replace the view (ADR-0015).
/// </summary>
public sealed partial class CanvasViewModel : ObservableObject
{
    /// <summary>The size of a node until the view measured it: the node template is 230 wide at least, and about 150 high.</summary>
    public static readonly Size DefaultNodeSize = new(260, 150);

    /// <summary>The smallest zoom «Ajustar vista» leaves: below it the words of a node cannot be read.</summary>
    public const double MinFitZoom = 0.6;

    /// <summary>The room «Ajustar vista» leaves around the nodes, and a new node from the edge of the visible area.</summary>
    private const double ViewMargin = 40;

    /// <summary>The least room between two nodes: a new node never goes closer to another one.</summary>
    private const double NodeGap = 24;

    private const double ColumnWidth = 320;
    private const double RowHeight = 190;

    private readonly TimeProvider _time;
    private int _nextNumber = 1;
    private bool _loading;

    public CanvasViewModel(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        PendingConnection = new PendingConnectionViewModel(this);
        Palette = [.. Enum.GetValues<WorkTaskKind>().Select(kind => new PaletteItem(kind))];
        StatusLegend = [.. Enum.GetValues<WorkTaskStatus>().Select(status => new StatusLegendItem(status))];
        Nodes.CollectionChanged += OnNodesChanged;
        Connections.CollectionChanged += (_, _) => RaisePlanChanged();
        SelectedNodes.CollectionChanged += OnSelectedNodesChanged;
        SelectedConnections.CollectionChanged += (_, _) => DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised when the plan drawn on the canvas changes: a node, a wire or a field of a node (not a position).</summary>
    public event EventHandler? PlanChanged;

    /// <summary>Raised when the person creates a node from the palette (not when a plan is loaded).</summary>
    public event EventHandler<NodeViewModel>? NodeCreated;

    /// <summary>Raised when the person deletes the selection («Eliminar selección» or Supr), not when a plan is loaded or cleared.</summary>
    public event EventHandler? SelectionDeleted;

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    /// <summary>Bound to NodifyEditor.SelectedItems. Never changed from its own CollectionChanged (Nodify issue #292).</summary>
    public ObservableCollection<NodeViewModel> SelectedNodes { get; } = [];

    /// <summary>Bound to NodifyEditor.SelectedConnections.</summary>
    public ObservableCollection<ConnectionViewModel> SelectedConnections { get; } = [];

    public PendingConnectionViewModel PendingConnection { get; }

    public IReadOnlyList<PaletteItem> Palette { get; }

    public IReadOnlyList<StatusLegendItem> StatusLegend { get; }

    /// <summary>The node the properties panel shows: the selection when it is exactly one node.</summary>
    [ObservableProperty]
    private NodeViewModel? _selectedNode;

    /// <summary>True while a run is followed: the plan is the one that runs, so nothing can be added, wired or removed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectConnectorCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveConnectionCommand))]
    private bool _isReadOnly;

    public bool IsEmpty => Nodes.Count == 0;

    /// <summary>Top-left of the visible area in graph coordinates (two-way with the editor).</summary>
    [ObservableProperty]
    private Point _viewportLocation;

    /// <summary>Visible area in graph coordinates, that is pixels divided by zoom (the editor writes it).</summary>
    [ObservableProperty]
    private Size _viewportSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _viewportZoom = 1d;

    public string ZoomText => $"{ViewportZoom:P0}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _noticeText;

    [ObservableProperty]
    private NoticeKind _noticeKind;

    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);

    /// <summary>Where positions are saved; null keeps them in memory only.</summary>
    public ICanvasLayoutStore? LayoutStore { get; set; }

    /// <summary>
    /// The project the canvas belongs to; positions are saved only when it is set. The orchestrator document sets it once
    /// the project's storage opened, and clears it when opening it fails.
    /// </summary>
    public string? ProjectRoot { get; set; }

    // ---------- Wires: rules, creation and removal ----------

    /// <summary>
    /// Whether two connectors may be joined, in either drag direction. Cheap structural rules first; cycles and branch
    /// rules are asked of the domain <see cref="TaskGraph"/>, so the canvas and the plan validator cannot disagree.
    /// </summary>
    public ConnectionCheck Check(ConnectorViewModel first, ConnectorViewModel second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var structure = CheckStructure(first, second);
        if (!structure.IsAllowed)
        {
            return structure;
        }

        var (output, input) = Orient(first, second);
        return PlanGraph.CheckWire(Nodes, Connections, output, input, _time.GetUtcNow());
    }

    private ConnectionCheck CheckStructure(ConnectorViewModel first, ConnectorViewModel second)
    {
        if (IsReadOnly)
        {
            return new ConnectionCheck(ConnectionVerdict.ReadOnly, "El plan se está ejecutando: no se puede cambiar.");
        }

        if (first.Flow == second.Flow)
        {
            return new ConnectionCheck(ConnectionVerdict.WrongDirection, "Une una salida con una entrada.");
        }

        var (output, input) = Orient(first, second);
        if (output.Node.IsDisabled || input.Node.IsDisabled)
        {
            return new ConnectionCheck(ConnectionVerdict.NotAvailable, $"Debate: {NodeKinds.UnavailableReason}.");
        }

        if (Connections.Any(c => c.Source == output && c.Target == input))
        {
            return new ConnectionCheck(ConnectionVerdict.Duplicate, "Esa conexión ya existe.");
        }

        return ConnectionCheck.Ok;
    }

    private static (ConnectorViewModel Output, ConnectorViewModel Input) Orient(ConnectorViewModel first, ConnectorViewModel second) =>
        first.Flow == ConnectorFlow.Output ? (first, second) : (second, first);

    /// <summary>
    /// Bound to NodifyEditor.ConnectionCompletedCommand, whose parameter is a <c>(object Source, object? Target)</c> value
    /// tuple. CanExecute applies only the cheap structural rules: if it also refused cycles, Nodify would drop the wire
    /// silently and the user would get no message. Execute runs the full check and explains a rejection.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect(object? ends)
    {
        if (!TryGetEnds(ends, out var first, out var second))
        {
            return;
        }

        var check = Check(first, second);
        if (!check.IsAllowed)
        {
            Notify($"Conexión rechazada. {check.Message}", NoticeKind.Error);
            return;
        }

        var (output, input) = Orient(first, second);
        Connections.Add(new ConnectionViewModel(output, input));
        ClearNotice();
    }

    private bool CanConnect(object? ends) =>
        !IsReadOnly && TryGetEnds(ends, out var first, out var second) && CheckStructure(first, second).IsAllowed;

    private static bool TryGetEnds(object? ends, out ConnectorViewModel first, out ConnectorViewModel second)
    {
        if (ends is ValueTuple<object, object?> { Item1: ConnectorViewModel a, Item2: ConnectorViewModel b })
        {
            first = a;
            second = b;
            return true;
        }

        first = second = null!;
        return false;
    }

    /// <summary>Bound to NodifyEditor.DisconnectConnectorCommand (Alt+click on a connector, or Delete on a focused one).</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void DisconnectConnector(ConnectorViewModel? connector)
    {
        if (connector is null)
        {
            return;
        }

        foreach (var connection in Connections.Where(c => c.Source == connector || c.Target == connector).ToList())
        {
            Detach(connection);
        }
    }

    /// <summary>Bound to NodifyEditor.RemoveConnectionCommand (Alt+click on a wire).</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void RemoveConnection(ConnectionViewModel? connection)
    {
        if (connection is not null)
        {
            Detach(connection);
        }
    }

    private bool IsEditable() => !IsReadOnly;

    private void Detach(ConnectionViewModel connection)
    {
        Connections.Remove(connection);
        SelectedConnections.Remove(connection);
        connection.Detach();
    }

    // ---------- Nodes ----------

    /// <summary>
    /// Palette command: adds a node of <paramref name="kind"/> in a free place (<see cref="FreeSpot"/>), selects it and brings it
    /// into view.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddNode))]
    private void AddNode(WorkTaskKind kind)
    {
        var node = Add(NewId(), kind, $"{NodeKinds.Label(kind)} nuevo", FreeSpot());
        SelectedNodes.Clear();
        SelectedNodes.Add(node);
        BringIntoView(node);
        Notify($"Creado {node.Id} · {node.KindLabel}.", NoticeKind.Info);
        PersistLayout();
        NodeCreated?.Invoke(this, node);
    }

    /// <summary>
    /// Where a new node goes: to the right of the last node, or under it, at the first place of a grid around it where it
    /// overlaps no node (nor comes closer than a small gap); on an empty canvas, at the top left of the visible area.
    /// </summary>
    public Point FreeSpot()
    {
        if (Nodes.Count == 0)
        {
            return new Point(ViewportLocation.X + ViewMargin, ViewportLocation.Y + ViewMargin);
        }

        var origin = Nodes[^1].Location;
        var candidates =
            from column in Enumerable.Range(0, 12)
            from row in Enumerable.Range(0, 12)
            where column > 0 || row > 0
            orderby Math.Max(column, row), row, column
            select new Point(origin.X + (column * ColumnWidth), origin.Y + (row * RowHeight));
        foreach (var candidate in candidates)
        {
            var place = new Rect(candidate, DefaultNodeSize);
            if (!Nodes.Any(n => Overlaps(place, n)))
            {
                return candidate;
            }
        }

        // A canvas full all around: past the right edge of every node.
        return new Point(Nodes.Max(n => n.Location.X + SizeOf(n).Width) + NodeGap, origin.Y);
    }

    /// <summary>«Ajustar vista»: frames every node, between <see cref="MinFitZoom"/> and 100 %; a plan too big for that is shown from its top left.</summary>
    [RelayCommand]
    private void FitView()
    {
        if (Nodes.Count == 0)
        {
            return;
        }

        var bounds = Bounds();
        var pixels = new Size(ViewportSize.Width * ViewportZoom, ViewportSize.Height * ViewportZoom);
        if (pixels.Width <= 0 || pixels.Height <= 0)
        {
            ViewportLocation = new Point(bounds.X - ViewMargin, bounds.Y - ViewMargin);
            return;
        }

        var needed = new Size(bounds.Width + (2 * ViewMargin), bounds.Height + (2 * ViewMargin));
        var zoom = Math.Clamp(Math.Min(pixels.Width / needed.Width, pixels.Height / needed.Height), MinFitZoom, 1d);
        var visible = new Size(pixels.Width / zoom, pixels.Height / zoom);
        ViewportZoom = zoom;
        ViewportLocation = new Point(
            needed.Width <= visible.Width ? bounds.X + (bounds.Width / 2) - (visible.Width / 2) : bounds.X - ViewMargin,
            needed.Height <= visible.Height ? bounds.Y + (bounds.Height / 2) - (visible.Height / 2) : bounds.Y - ViewMargin);
    }

    /// <summary>Moves the visible area so <paramref name="node"/> is in it (centred), when the view gave its size and the node is not in it yet.</summary>
    public void BringIntoView(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            return;
        }

        var area = new Rect(node.Location, SizeOf(node));
        if (new Rect(ViewportLocation, ViewportSize).Contains(area))
        {
            return;
        }

        ViewportLocation = new Point(area.X + (area.Width / 2) - (ViewportSize.Width / 2), area.Y + (area.Height / 2) - (ViewportSize.Height / 2));
    }

    /// <summary>The area the nodes cover, with the size the view measured for each one.</summary>
    private Rect Bounds()
    {
        var bounds = Rect.Empty;
        foreach (var node in Nodes)
        {
            bounds.Union(new Rect(node.Location, SizeOf(node)));
        }

        return bounds;
    }

    private static Size SizeOf(NodeViewModel node) => node.Size is { Width: > 0, Height: > 0 } measured ? measured : DefaultNodeSize;

    private static bool Overlaps(Rect place, NodeViewModel node)
    {
        var taken = new Rect(node.Location, SizeOf(node));
        taken.Inflate(NodeGap, NodeGap);
        return taken.IntersectsWith(place);
    }

    private bool CanAddNode(WorkTaskKind kind) => !IsReadOnly && NodeKinds.IsAvailable(kind);

    /// <summary>Deletes the selected wires and nodes; deleting a node removes every wire attached to it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelection()
    {
        var nodes = SelectedNodes.ToList();
        var attached = Connections.Where(c => nodes.Contains(c.Source.Node) || nodes.Contains(c.Target.Node));
        var wires = SelectedConnections.Union(attached).ToList();

        foreach (var wire in wires)
        {
            Detach(wire);
        }

        foreach (var node in nodes)
        {
            Remove(node);
        }

        Notify($"Eliminados {nodes.Count} nodos y {wires.Count} conexiones.", NoticeKind.Info);
        PersistLayout();
        SelectionDeleted?.Invoke(this, EventArgs.Empty);
    }

    private bool HasSelection() => !IsReadOnly && (SelectedNodes.Count > 0 || SelectedConnections.Count > 0);

    [RelayCommand]
    private void DismissNotice() => ClearNotice();

    /// <summary>Bound to NodifyEditor.ItemsDragCompletedCommand: the user finished moving nodes.</summary>
    [RelayCommand]
    private void SaveLayout() => PersistLayout();

    /// <summary>Selects one node, for instance from the list of problems.</summary>
    public void Select(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        SelectedConnections.Clear();
        SelectedNodes.Clear();
        SelectedNodes.Add(node);
    }

    // ---------- Building blocks ----------

    public NodeViewModel Add(TaskId id, WorkTaskKind kind, string title, Point location)
    {
        var node = new NodeViewModel(id, kind, title, location);
        Nodes.Add(node);
        return node;
    }

    /// <summary>Adds a wire after the full check; returns null (and leaves the canvas untouched) when it is refused.</summary>
    public ConnectionViewModel? Link(ConnectorViewModel output, ConnectorViewModel input)
    {
        if (!Check(output, input).IsAllowed)
        {
            return null;
        }

        var connection = new ConnectionViewModel(output, input);
        Connections.Add(connection);
        return connection;
    }

    public void Clear()
    {
        SelectedNodes.Clear();
        SelectedConnections.Clear();
        foreach (var connection in Connections.ToList())
        {
            Detach(connection);
        }

        foreach (var node in Nodes.ToList())
        {
            Remove(node);
        }

        _nextNumber = 1;
        ClearNotice();
    }

    public TaskId NewId()
    {
        TaskId id;
        do
        {
            id = new TaskId($"T{_nextNumber++}");
        }
        while (Nodes.Any(n => n.Id == id));

        return id;
    }

    public void Notify(string text, NoticeKind kind)
    {
        NoticeKind = kind;
        NoticeText = text;
    }

    public void ClearNotice() => NoticeText = null;

    // ---------- The plan behind the drawing ----------

    /// <summary>
    /// The tasks drawn on the canvas, in canvas order. Each one depends on the wires that end on it (with the branch of
    /// the output they leave from) and on the dependencies a loaded plan had that no wire can draw.
    /// </summary>
    public IReadOnlyList<TaskSpec> BuildTasks() =>
        [.. Nodes.Select(node => new TaskSpec(
            node.Id,
            node.Title,
            node.Kind,
            node.Agent,
            node.Objective,
            node.Instructions,
            [
                .. Connections
                    .Where(c => ReferenceEquals(c.Target.Node, node))
                    .Select(c => new DependencySpec(c.Source.Node.Id, c.Branch)),
                .. node.ExtraDependencies,
            ],
            node.Join,
            node.Covers,
            node.WriteScope,
            node.VerificationSteps,
            node.Condition,
            node.ToolId,
            node.MaxRetries,
            node.Timeout,
            node.Workspace))];

    /// <summary>
    /// Replaces the canvas with <paramref name="tasks"/> (a proposed plan or the plan of a run). Wires are drawn without
    /// the cycle probe so that a broken plan can be shown as it is: the validator, not the canvas, reports it. A
    /// dependency no connector can carry (unknown task, wrong branch, repeated) is kept on its node. Nodes go where
    /// <paramref name="layout"/> put them, the others in columns by dependency depth.
    /// </summary>
    public void LoadTasks(IReadOnlyList<TaskSpec> tasks, CanvasLayout layout)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(layout);
        _loading = true;
        try
        {
            Clear();
            var automatic = LayeredPositions(tasks);
            var created = new List<(TaskSpec Spec, NodeViewModel Node)>(tasks.Count);
            foreach (var (spec, index) in tasks.Select((t, i) => (t, i)))
            {
                var location = layout.Positions.TryGetValue(spec.Id.Value, out var saved) ? saved : automatic[index];
                var node = new NodeViewModel(spec.Id, spec.Kind, spec.Title, location)
                {
                    Agent = spec.Agent,
                    Objective = spec.Objective,
                    Instructions = spec.Instructions,
                    Join = spec.Join,
                    Covers = [.. spec.Covers],
                    WriteScope = [.. spec.WriteScope],
                    VerificationSteps = [.. spec.VerificationSteps],
                    Condition = spec.Condition,
                    ToolId = spec.ToolId,
                    MaxRetries = spec.MaxRetries,
                    Timeout = spec.Timeout,
                    Workspace = spec.Workspace,
                };
                Nodes.Add(node);
                created.Add((spec, node));
            }

            foreach (var (spec, node) in created)
            {
                var extra = new List<DependencySpec>();
                foreach (var dependency in spec.DependsOn)
                {
                    var output = Nodes.FirstOrDefault(n => n.Id == dependency.Prerequisite)?.Outputs
                        .FirstOrDefault(o => string.Equals(o.Branch, dependency.Branch, StringComparison.Ordinal));
                    if (output is null || Connections.Any(c => c.Source == output && c.Target == node.Inputs[0]))
                    {
                        extra.Add(dependency);
                        continue;
                    }

                    Connections.Add(new ConnectionViewModel(output, node.Inputs[0]));
                }

                node.ExtraDependencies = extra;
            }
        }
        finally
        {
            _loading = false;
        }

        RaisePlanChanged();
        PersistLayout();
    }

    /// <summary>Shows <paramref name="problems"/> next to their nodes; a node without an entry shows none.</summary>
    public void ShowProblems(IReadOnlyDictionary<TaskId, IReadOnlyList<string>> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        foreach (var node in Nodes)
        {
            var wanted = problems.TryGetValue(node.Id, out var messages) ? messages : [];
            if (node.Problems.SequenceEqual(wanted, StringComparer.Ordinal))
            {
                continue;
            }

            node.Problems.Clear();
            foreach (var message in wanted)
            {
                node.Problems.Add(message);
            }
        }
    }

    public void SetStatus(TaskId task, WorkTaskStatus status)
    {
        foreach (var node in Nodes.Where(n => n.Id == task))
        {
            node.Status = status;
        }
    }

    public void ResetStatuses()
    {
        foreach (var node in Nodes)
        {
            node.Status = WorkTaskStatus.Pending;
        }
    }

    /// <summary>The positions of the nodes on the canvas, by task id.</summary>
    public CanvasLayout CaptureLayout()
    {
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            positions.TryAdd(node.Id.Value, node.Location);
        }

        return new CanvasLayout(positions);
    }

    /// <summary>Saves the positions when a store and a project are set. A failure is shown, never thrown.</summary>
    public void PersistLayout()
    {
        if (LayoutStore is not { } store || ProjectRoot is not { } root)
        {
            return;
        }

        try
        {
            store.Save(root, CaptureLayout());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"No se pudo guardar la disposición del lienzo: {ex.Message}", NoticeKind.Error);
        }
    }

    /// <summary>
    /// Columns by dependency depth (a task is one column right of the deepest task it waits for), rows by id inside a
    /// column. Unknown prerequisites are ignored and a cycle cannot push a task further than the number of tasks.
    /// </summary>
    private static List<Point> LayeredPositions(IReadOnlyList<TaskSpec> tasks)
    {
        var byId = new Dictionary<TaskId, int>();
        foreach (var (spec, index) in tasks.Select((t, i) => (t, i)))
        {
            byId.TryAdd(spec.Id, index);
        }

        var depth = new int[tasks.Count];
        for (var pass = 0; pass < tasks.Count; pass++)
        {
            var changed = false;
            for (var i = 0; i < tasks.Count; i++)
            {
                foreach (var dependency in tasks[i].DependsOn)
                {
                    if (byId.TryGetValue(dependency.Prerequisite, out var p) && p != i && depth[p] + 1 > depth[i] && depth[p] + 1 < tasks.Count)
                    {
                        depth[i] = depth[p] + 1;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        var positions = new Point[tasks.Count];
        foreach (var column in Enumerable.Range(0, tasks.Count).GroupBy(i => depth[i]))
        {
            var row = 0;
            foreach (var i in column.OrderBy(i => tasks[i].Id.Value, StringComparer.Ordinal))
            {
                positions[i] = new Point(column.Key * ColumnWidth, row++ * RowHeight);
            }
        }

        return [.. positions];
    }

    private void Remove(NodeViewModel node)
    {
        Nodes.Remove(node);
        SelectedNodes.Remove(node);
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (NodeViewModel node in e.OldItems)
            {
                node.SpecChanged -= OnNodeSpecChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (NodeViewModel node in e.NewItems)
            {
                node.SpecChanged += OnNodeSpecChanged;
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        RaisePlanChanged();
    }

    private void OnNodeSpecChanged(object? sender, EventArgs e) => RaisePlanChanged();

    private void OnSelectedNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Read the selection only: changing it from here throws inside Nodify (issue #292).
        SelectedNode = SelectedNodes.Count == 1 ? SelectedNodes[0] : null;
        DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    private void RaisePlanChanged()
    {
        if (!_loading)
        {
            PlanChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
