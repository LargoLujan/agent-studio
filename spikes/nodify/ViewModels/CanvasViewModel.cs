using System.Collections.ObjectModel;
using System.Windows;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NodifySpike.ViewModels;

public enum NoticeKind
{
    Info,
    Error,
}

/// <summary>One button of the node palette; Debate is listed but disabled until Phase 5.</summary>
public sealed record PaletteItem(WorkTaskKind Kind)
{
    public bool IsAvailable => NodeKindInfo.IsAvailable(Kind);

    public string Label => IsAvailable ? NodeKindInfo.Label(Kind) : $"{NodeKindInfo.Label(Kind)} · {NodeKindInfo.UnavailableBadge}";

    public string Hint => IsAvailable ? $"Añadir un nodo «{NodeKindInfo.Label(Kind)}»" : "Disponible en la Fase 5: el validador del plan rechaza los nodos Debate.";
}

/// <summary>
/// The canvas of the orchestrator as plain state: nodes, wires, selection and viewport. The view (CanvasView.xaml) is
/// only bindings to Nodify's NodifyEditor; nothing here references a control.
/// </summary>
public sealed partial class CanvasViewModel : ObservableObject
{
    private readonly Func<DateTimeOffset> _clock;
    private int _nextNumber = 1;

    public CanvasViewModel()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    /// <param name="clock">Timestamp for the throwaway domain graphs; injectable for deterministic tests.</param>
    public CanvasViewModel(Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        PendingConnection = new PendingConnectionViewModel(this);
        Palette = [.. Enum.GetValues<WorkTaskKind>().Select(kind => new PaletteItem(kind))];
        Statuses = [.. Enum.GetValues<NodeStatus>().Select(status => new StatusChoice(status, NodeStatusInfo.Label(status)))];
        SelectedNodes.CollectionChanged += (_, _) => DeleteSelectionCommand.NotifyCanExecuteChanged();
        SelectedConnections.CollectionChanged += (_, _) => DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    /// <summary>Two-way with NodifyEditor.SelectedItems.</summary>
    public ObservableCollection<NodeViewModel> SelectedNodes { get; } = [];

    /// <summary>Two-way with NodifyEditor.SelectedConnections.</summary>
    public ObservableCollection<ConnectionViewModel> SelectedConnections { get; } = [];

    public PendingConnectionViewModel PendingConnection { get; }

    public IReadOnlyList<PaletteItem> Palette { get; }

    public IReadOnlyList<StatusChoice> Statuses { get; }

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

    // ---------- Wires: rules, creation and removal ----------

    /// <summary>
    /// Whether two connectors may be joined, in either drag direction. Cheap structural rules first; the cycle rule asks
    /// the domain <see cref="TaskGraph.AddDependency"/>, so the canvas and the plan validator cannot disagree.
    /// </summary>
    public ConnectionCheck Check(ConnectorViewModel first, ConnectorViewModel second)
    {
        var structure = CheckStructure(first, second);
        if (structure.Verdict != ConnectionVerdict.Allowed)
        {
            return structure;
        }

        var (output, input) = first.Flow == ConnectorFlow.Output ? (first, second) : (second, first);
        return PlanGraph.CheckCycle(Nodes, Connections, output, input, _clock());
    }

    private ConnectionCheck CheckStructure(ConnectorViewModel first, ConnectorViewModel second)
    {
        if (first.Flow == second.Flow)
        {
            return new ConnectionCheck(ConnectionVerdict.WrongDirection, "Une una salida con una entrada.");
        }

        var (output, input) = first.Flow == ConnectorFlow.Output ? (first, second) : (second, first);
        if (output.Node.IsDisabled || input.Node.IsDisabled)
        {
            return new ConnectionCheck(ConnectionVerdict.NotAvailable, $"Debate: disponible en la {NodeKindInfo.UnavailableBadge}.");
        }

        if (Connections.Any(c => c.Source == output && c.Target == input))
        {
            return new ConnectionCheck(ConnectionVerdict.Duplicate, "Esa conexión ya existe.");
        }

        return ConnectionCheck.Ok;
    }

    /// <summary>
    /// Bound to NodifyEditor.ConnectionCompletedCommand, whose parameter is a <c>(object Source, object? Target)</c> value
    /// tuple. CanExecute applies only the cheap structural rules: if it also refused cycles, Nodify would drop the
    /// connection silently and the user would get no message. Execute runs the full check and reports a rejection.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect(object? ends)
    {
        if (!TryGetEnds(ends, out var first, out var second))
        {
            return;
        }

        var check = Check(first, second);
        if (check.Verdict != ConnectionVerdict.Allowed)
        {
            Notify($"Conexión rechazada. {check.Message}", NoticeKind.Error);
            return;
        }

        var (output, input) = first.Flow == ConnectorFlow.Output ? (first, second) : (second, first);
        Connections.Add(new ConnectionViewModel(output, input));
        ClearNotice();
    }

    private bool CanConnect(object? ends) =>
        TryGetEnds(ends, out var first, out var second) && CheckStructure(first, second).Verdict == ConnectionVerdict.Allowed;

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
    [RelayCommand]
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
    [RelayCommand]
    private void RemoveConnection(ConnectionViewModel? connection)
    {
        if (connection is not null)
        {
            Detach(connection);
        }
    }

    private void Detach(ConnectionViewModel connection)
    {
        Connections.Remove(connection);
        SelectedConnections.Remove(connection);
        connection.Detach();
    }

    // ---------- Nodes ----------

    /// <summary>Palette command: adds a node of <paramref name="kind"/> at the centre of the visible area.</summary>
    [RelayCommand(CanExecute = nameof(CanAddNode))]
    private void AddNode(WorkTaskKind kind)
    {
        var step = Nodes.Count % 6 * 24;
        var centre = new Point(
            ViewportLocation.X + (ViewportSize.Width / 2) - 105 + step,
            ViewportLocation.Y + (ViewportSize.Height / 2) - 45 + step);

        var node = Add(NewId(), kind, $"{NodeKindInfo.Label(kind)} nuevo", centre);
        SelectedNodes.Clear();
        SelectedNodes.Add(node);
        Notify($"Creado {node.Id} · {node.KindLabel}.", NoticeKind.Info);
    }

    private static bool CanAddNode(WorkTaskKind kind) => NodeKindInfo.IsAvailable(kind);

    /// <summary>Deletes the selected wires and nodes; deleting a node cascades to every wire attached to it.</summary>
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
            Nodes.Remove(node);
            SelectedNodes.Remove(node);
        }

        Notify($"Eliminados {nodes.Count} nodos y {wires.Count} conexiones.", NoticeKind.Info);
    }

    private bool HasSelection() => SelectedNodes.Count > 0 || SelectedConnections.Count > 0;

    [RelayCommand]
    private void ApplyStatus(NodeStatus status)
    {
        foreach (var node in SelectedNodes)
        {
            node.Status = status;
        }
    }

    [RelayCommand]
    private void DismissNotice() => ClearNotice();

    // ---------- Building blocks for samples and the harness ----------

    public NodeViewModel Add(TaskId id, WorkTaskKind kind, string title, Point location, NodeStatus status = NodeStatus.Pending)
    {
        var node = new NodeViewModel(id, kind, title, location, status);
        Nodes.Add(node);
        return node;
    }

    /// <summary>Adds a wire without running the cycle probe; only for generators that are acyclic by construction.</summary>
    public ConnectionViewModel LinkUnchecked(ConnectorViewModel output, ConnectorViewModel input)
    {
        var connection = new ConnectionViewModel(output, input);
        Connections.Add(connection);
        return connection;
    }

    /// <summary>Adds a wire after the full check; returns null (and leaves the canvas untouched) when it is refused.</summary>
    public ConnectionViewModel? Link(ConnectorViewModel output, ConnectorViewModel input)
    {
        if (Check(output, input).Verdict != ConnectionVerdict.Allowed)
        {
            return null;
        }

        return LinkUnchecked(output, input);
    }

    public void Clear()
    {
        SelectedNodes.Clear();
        SelectedConnections.Clear();
        foreach (var connection in Connections.ToList())
        {
            Detach(connection);
        }

        Nodes.Clear();
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
}
