using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Team;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>A cable the person asks for or removes in the Team layer: an agent and a main folder (null: the whole project).</summary>
public sealed record FolderCableRequest(AgentId Agent, string? Folder, bool Connect);

/// <summary>One place where an agent can be connected (or disconnected), as «Conectar con…» lists it; the same check as dragging a cable.</summary>
public sealed record ConnectTarget(TeamConnectorViewModel Connector, bool IsConnected, bool IsAllowed, string? Refusal)
{
    public string Label => Connector.Title;

    public override string ToString() => Label;
}

/// <summary>
/// The Team layer of the canvas as plain state (docs/17 D-07 to D-11, ADR-0048): the agents of the template and the repository as
/// nodes, and the cables «Reparte», «Revisa» and «Trabaja en». It is the template seen as a graph, not a model of its own
/// (<see cref="Graph"/> is built from the roster): editing it is editing the template, which <see cref="TeamEditorViewModel"/>
/// does. No cable of this layer is the dependency of a task: the plan, <c>BuildTasks</c> and <c>DependsOn</c>, never see them.
/// Nothing here references a control.
/// </summary>
public sealed partial class TeamCanvasViewModel : ObservableObject
{
    private const double ColumnWidth = 300;
    private const double RowHeight = 230;

    private readonly CanvasText _text;
    private readonly ContextHealthThresholds _thresholds;
    private IReadOnlyList<AgentDefinition> _roster = [];
    private RepositoryReading? _reading;
    private CanvasLayout _layout = CanvasLayout.Empty;
    private bool _loading;

    public TeamCanvasViewModel(CanvasText? text = null, ContextHealthThresholds? thresholds = null)
    {
        _text = text ?? new CanvasText();
        _thresholds = thresholds ?? new ContextHealthThresholds();
        SelectedNodes.CollectionChanged += OnSelectionChanged;
    }

    /// <summary>The selected agent changed (a click on its node or on its card): the Plan layer shines its tasks.</summary>
    public event EventHandler<AgentId?>? AgentSelected;

    /// <summary>The person drew or removed a cable «Trabaja en»: <see cref="TeamEditorViewModel"/> turns it into the folders of the agent.</summary>
    public event EventHandler<FolderCableRequest>? FolderCableRequested;

    /// <summary>The nodes Nodify draws: the agents and the node of the repository.</summary>
    public ObservableCollection<object> Nodes { get; } = [];

    public ObservableCollection<TeamConnectionViewModel> Connections { get; } = [];

    /// <summary>Bound to the selection of the editor; read only from its own change (Nodify issue #292).</summary>
    public ObservableCollection<object> SelectedNodes { get; } = [];

    public ObservableCollection<TeamConnectionViewModel> SelectedConnections { get; } = [];

    /// <summary>The agents of the strip, in the order of the template, all of them: the cards are the same objects as the nodes.</summary>
    public ObservableCollection<AgentNodeViewModel> Agents { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>The bars of quota of the account, one per program and window, in the header of the strip (D-12): each with its origin, or «no expuesto» with an empty track.</summary>
    public ObservableCollection<MetricBarReading> QuotaBars { get; } = [];

    /// <summary>The editor of the template that this layer is edited with (palette, remove, inspector); null without the builder.</summary>
    [ObservableProperty]
    private TeamEditorViewModel? _editor;

    /// <summary>The visual builder of the document (analysis band, «Revisar el orden»); null without it.</summary>
    [ObservableProperty]
    private VisualBuilder? _builder;

    public RepositoryNodeViewModel? Repository { get; private set; }

    /// <summary>The template has agents: the strip of the team is shown.</summary>
    public bool HasAgents => Agents.Count > 0;

    /// <summary>The agent of the node selected, when it is exactly one agent.</summary>
    [ObservableProperty]
    private AgentNodeViewModel? _selectedAgent;

    /// <summary>The filter «Solo la orquestación»: on by default, the architect and the critic of the debates are hidden (D-08).</summary>
    [ObservableProperty]
    private bool _showOnlyOrchestration = true;

    /// <summary>A run is followed or the tab is busy: the template cannot change here.</summary>
    [ObservableProperty]
    private bool _isReadOnly;

    /// <summary>The animations of the cables run: neither «Reducir movimiento» nor Windows turned them off (<see cref="Canvas.MotionPolicy"/>).</summary>
    [ObservableProperty]
    private bool _animationsEnabled = true;

    /// <summary>The limits of a team as the person assembles it: «1 orquestador · 2 de 8 ejecutores · 1 juez».</summary>
    [ObservableProperty]
    private string _limitsText = string.Empty;

    public IReadOnlyList<AgentDefinition> Roster => _roster;

    /// <summary>The Team layer as the graph of the domain: the template, the repository and the cables that are drawn.</summary>
    public TeamGraph Graph => new(
        [.. _roster.Select(a => new AgentNode(a))],
        Repository?.Node,
        [.. Connections.Select(c => c.Edge)]);

    /// <summary>What the last refused or done change of the layer says, next to the canvas; announced by screen readers.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>The places the selected agent can be connected to or disconnected from, for «Conectar con…» with the keyboard (D-17).</summary>
    public ObservableCollection<ConnectTarget> SelectedTargets { get; } = [];

    partial void OnShowOnlyOrchestrationChanged(bool value) => Rebuild();

    partial void OnSelectedAgentChanged(AgentNodeViewModel? value) => RefreshTargets();

    partial void OnIsReadOnlyChanged(bool value) => RefreshTargets();

    private void RefreshTargets()
    {
        SelectedTargets.Clear();
        if (SelectedAgent is { } agent && TeamRoles.Writes(agent.Definition.Role))
        {
            foreach (var target in TargetsOf(agent))
            {
                SelectedTargets.Add(target);
            }
        }
    }

    /// <summary>Bound to the cards of the strip: a click on a card selects its agent, as a click on its node does.</summary>
    [RelayCommand]
    private void SelectAgent(AgentNodeViewModel? agent)
    {
        if (agent is not null)
        {
            Select(agent);
        }
    }

    /// <summary>
    /// «Conectar con…» and «Desconectar de…» with the keyboard: one entry of <see cref="SelectedTargets"/> connects the selected
    /// agent to that place, or disconnects it when it already works there. The same check as dragging a cable.
    /// </summary>
    [RelayCommand]
    private void ToggleTarget(ConnectTarget? target)
    {
        if (target is null || SelectedAgent is not { } agent)
        {
            return;
        }

        Notice = target.IsConnected ? RequestDisconnect(agent, target.Connector) : RequestConnect(agent, target.Connector);
    }

    /// <summary>Bound to <c>ConnectionCompletedCommand</c> of the editor: a cable dragged from an agent to a folder. The refusal is said, never swallowed.</summary>
    [RelayCommand]
    private void Connect(object? ends)
    {
        if (ends is not ValueTuple<object, object?> { Item1: TeamConnectorViewModel a, Item2: TeamConnectorViewModel b })
        {
            return;
        }

        var (output, input) = a.Flow == TeamConnectorFlow.Output ? (a, b) : (b, a);
        Notice = output.Owner is AgentNodeViewModel agent && input.Flow == TeamConnectorFlow.Input
            ? RequestConnect(agent, input)
            : _text.Get("Canvas_Connect_OnlyFolders");
    }

    /// <summary>Bound to the removal of a cable (Alt+click, or Delete on a selected cable): only «Trabaja en» can be removed, as it is the one the person drew.</summary>
    [RelayCommand]
    private void RemoveConnection(TeamConnectionViewModel? connection)
    {
        if (connection is null)
        {
            return;
        }

        Notice = connection.Kind == TeamEdgeKind.WorksIn && Agents.FirstOrDefault(a => a.Id == connection.Edge.From) is { } agent
            ? RequestDisconnect(agent, connection.Target)
            : _text.Get("Canvas_Connect_NotRemovable");
    }

    /// <summary>Bound to <c>DisconnectConnectorCommand</c> (Alt+click on a connector, or Delete on a focused one): every cable «Trabaja en» that touches it.</summary>
    [RelayCommand]
    private void DisconnectConnector(TeamConnectorViewModel? connector)
    {
        if (connector is null)
        {
            return;
        }

        foreach (var connection in Connections.Where(c => c.Kind == TeamEdgeKind.WorksIn && (ReferenceEquals(c.Source, connector) || ReferenceEquals(c.Target, connector))).ToList())
        {
            RemoveConnection(connection);
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    partial void OnAnimationsEnabledChanged(bool value) => RefreshAnimations();

    /// <summary>Shows the template and the repository. Nodes go where <paramref name="layout"/> put them, the others in a column per role.</summary>
    public void Load(IReadOnlyList<AgentDefinition> roster, RepositoryReading? reading, CanvasLayout layout)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(layout);
        _roster = roster;
        _reading = reading;
        _layout = layout;
        Rebuild();
    }

    /// <summary>Keeps the positions of the Team layer that a file had, so saving the plan never loses them while the layer is not shown.</summary>
    public void Remember(CanvasLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.TeamPositions.Count == 0)
        {
            return;
        }

        var merged = new Dictionary<string, Point>(_layout.TeamPositions, StringComparer.Ordinal);
        foreach (var (id, point) in layout.TeamPositions)
        {
            merged[id] = point;
        }

        _layout = _layout with { TeamPositions = merged };
    }

    /// <summary>Changes the roster the layer shows without losing the positions or the live state: the template was edited.</summary>
    public void Refresh(IReadOnlyList<AgentDefinition> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        _layout = CaptureLayout();
        _roster = roster;
        Rebuild();
    }

    private void Rebuild()
    {
        var previous = Agents.ToDictionary(a => a.Id);
        var selected = SelectedAgent?.Id;
        _loading = true;
        try
        {
            SelectedNodes.Clear();
            foreach (var connection in Connections.ToList())
            {
                connection.Detach();
            }

            Connections.Clear();
            SelectedConnections.Clear();
            Nodes.Clear();
            Agents.Clear();

            var shown = _roster.Where(a => !ShowOnlyOrchestration || !TeamRoles.IsDebateOnly(a.Role)).ToList();
            var positions = Layout(shown);
            foreach (var definition in shown)
            {
                var location = _layout.TeamPositions.TryGetValue(definition.Id.Value, out var saved) ? saved : positions[definition.Id];
                var node = previous.TryGetValue(definition.Id, out var kept) ? kept : new AgentNodeViewModel(definition, location, _text, _thresholds);
                if (kept is not null)
                {
                    node.Definition = definition;
                    node.Location = location;
                }

                Agents.Add(node);
                Nodes.Add(node);
            }

            Repository = BuildRepository(shown, positions);
            Nodes.Add(Repository);
            DrawCables(shown);
        }
        finally
        {
            _loading = false;
        }

        SelectedAgent = selected is { } id ? Agents.FirstOrDefault(a => a.Id == id) : null;
        if (SelectedAgent is not null)
        {
            SelectedNodes.Add(SelectedAgent);
        }

        OnPropertyChanged(nameof(Repository));
        OnPropertyChanged(nameof(Graph));
        OnPropertyChanged(nameof(HasAgents));
        RefreshTargets();
        ShowProblems();
        RefreshAnimations();
    }

    private RepositoryNodeViewModel BuildRepository(IReadOnlyList<AgentDefinition> shown, IReadOnlyDictionary<AgentId, Point> positions)
    {
        var node = _reading?.Node;
        if (node is not null)
        {
            // The folders the agents work in are main folders too: the person chooses them with the folders of each agent (D-10).
            var extra = shown.SelectMany(a => a.PathGrants)
                .Where(g => g.Access == FileAccessLevel.ReadWrite)
                .Select(g => AsFolder(g.Root))
                .Where(f => f is not null && !node.MainFolders.Contains(f, StringComparer.Ordinal))
                .Select(f => f!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            node = node with { MainFolders = [.. node.MainFolders, .. extra] };
        }

        var location = _layout.TeamPositions.TryGetValue(RepositoryNodeViewModel.LayoutId, out var saved) ? saved : RepositoryDefault(positions);
        return new RepositoryNodeViewModel(node, _reading?.Problem ?? (node is null ? RepositoryProblems.Reading : null), location, _text);
    }

    private void DrawCables(IReadOnlyList<AgentDefinition> shown)
    {
        var graph = new TeamGraph([.. _roster.Select(a => new AgentNode(a))], Repository?.Node, []);
        var orchestrator = shown.FirstOrDefault(a => a.Role == AgentRole.Orchestrator);
        var writers = shown.Where(a => TeamRoles.Writes(a.Role)).ToList();
        var judges = shown.Where(a => TeamRoles.Judges(a.Role)).ToList();
        if (orchestrator is not null)
        {
            foreach (var writer in writers)
            {
                Add(new TeamEdge(TeamEdgeKind.Distributes, orchestrator.Id, writer.Id), graph, _text.Get("Canvas_Edge_Distributes"));
            }
        }

        foreach (var writer in writers)
        {
            foreach (var judge in judges)
            {
                Add(new TeamEdge(TeamEdgeKind.Reviews, writer.Id, judge.Id), graph, _text.Get("Canvas_Edge_Reviews"));
            }
        }

        foreach (var writer in writers)
        {
            var folders = writer.PathGrants.Where(g => g.Access == FileAccessLevel.ReadWrite).Select(g => AsFolder(g.Root)).Where(f => f is not null).Distinct(StringComparer.Ordinal).ToList();
            if (writer.PathGrants.Count == 0)
            {
                Add(new TeamEdge(TeamEdgeKind.WorksIn, writer.Id), graph, _text.Get("Canvas_Edge_WorksIn"));
            }

            foreach (var folder in folders)
            {
                Add(new TeamEdge(TeamEdgeKind.WorksIn, writer.Id, ToFolder: folder), graph, _text.Get("Canvas_Edge_WorksIn"));
            }
        }
    }

    private void Add(TeamEdge edge, TeamGraph graph, string text)
    {
        var from = Agents.FirstOrDefault(a => a.Id == edge.From);
        if (from is null || Repository is null)
        {
            return;
        }

        TeamConnectorViewModel? target = edge.Kind == TeamEdgeKind.WorksIn
            ? Repository.ConnectorOf(edge.ToFolder)
            : Agents.FirstOrDefault(a => a.Id == edge.ToAgent)?.Input;
        if (target is null)
        {
            return;
        }

        var shares = graph.SharesVendor(edge);
        var label = shares
            ? _text.Format("Canvas_Edge_ReviewsSameVendor", edge.From.Value, edge.ToAgent?.Value ?? string.Empty)
            : text;
        Connections.Add(new TeamConnectionViewModel(edge, from.Output, target, label, shares));
    }

    private void ShowProblems()
    {
        Problems.Clear();
        foreach (var code in Graph.Validate())
        {
            Problems.Add(_text.Get("Canvas_Problem_" + code[(code.IndexOf('.', StringComparison.Ordinal) + 1)..].Replace('-', '_')));
        }

        var orchestrators = _roster.Count(a => a.Role == AgentRole.Orchestrator);
        var executors = _roster.Count(a => a.Role == AgentRole.Executor);
        var judges = _roster.Count(a => a.Role == AgentRole.Judge);
        LimitsText = _text.Format("Canvas_Team_Limits", orchestrators, TeamLimits.MaxOrchestrators, executors, TeamLimits.MaxExecutors, judges);
    }

    // ---------- Selection linked with the Plan layer ----------

    /// <summary>Selects the agent as a click on its card does, and shows it on the Team layer.</summary>
    public void Select(AgentNodeViewModel agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        SelectedConnections.Clear();
        SelectedNodes.Clear();
        SelectedNodes.Add(agent);
    }

    /// <summary>Clears the selection.</summary>
    public void ClearSelection() => SelectedNodes.Clear();

    /// <summary>The agent of the task selected in the Plan layer shines here, and no other (D-07).</summary>
    public void Highlight(AgentId? agent)
    {
        foreach (var node in Agents)
        {
            node.IsHighlighted = agent is { } id && node.Id == id;
        }
    }

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Read only: changing the selection from here throws inside Nodify (issue #292).
        var agent = SelectedNodes.Count == 1 ? SelectedNodes[0] as AgentNodeViewModel : null;
        if (!ReferenceEquals(agent, SelectedAgent))
        {
            SelectedAgent = agent;
            AgentSelected?.Invoke(this, agent?.Id);
        }
    }

    // ---------- Live state (docs/17 D-14 and D-16) ----------

    /// <summary>
    /// Applies a grouped change of the live state: only the agents it names are given theirs (the others did not change), and
    /// the cables «Trabaja en» of the agents that write now are animated, at most <see cref="LiveProjection.MaxAnimations"/> at once.
    /// </summary>
    /// <param name="state">The live state after the change; null when no run is followed.</param>
    /// <param name="changed">The nodes that changed; null gives every agent its state.</param>
    public void ApplyLive(RunLiveState? state, IReadOnlyCollection<string>? changed = null)
    {
        foreach (var agent in Agents)
        {
            if (state is null || changed is null || changed.Contains(agent.Id.Value))
            {
                agent.ApplyLive(state is not null && state.Agents.TryGetValue(agent.Id, out var live) ? live : null, state?.MaxAgentRuns);
            }
        }

        if (Repository is { } repository)
        {
            repository.IsIntegrating = state?.Integrating == true;
        }

        RefreshAnimations();
    }

    /// <summary>The cable of a writing agent moves while it writes; with the animations off nothing moves, and the state stays in the text and the icon.</summary>
    private void RefreshAnimations()
    {
        var animated = 0;
        foreach (var connection in Connections)
        {
            var moves = AnimationsEnabled
                && connection.Kind == TeamEdgeKind.WorksIn
                && Agents.FirstOrDefault(a => a.Id == connection.Edge.From) is { IsWriting: true }
                && animated < LiveProjection.MaxAnimations;
            connection.IsAnimated = moves;
            if (moves)
            {
                animated++;
            }
        }

        if (Repository is { } repository)
        {
            repository.IsAnimated = AnimationsEnabled && repository.IsIntegrating;
        }
    }

    /// <summary>How many cables move now.</summary>
    public int AnimatedCount => Connections.Count(c => c.IsAnimated) + (Repository is { IsAnimated: true } ? 1 : 0);

    // ---------- Cables «Trabaja en» ----------

    /// <summary>
    /// Whether <paramref name="agent"/> may work in the place <paramref name="target"/> stands for (the same check as dragging a
    /// cable to it): only an agent that writes works in folders, once, and not while the layer is read only.
    /// </summary>
    public (bool IsAllowed, string? Refusal) CheckFolder(AgentNodeViewModel agent, TeamConnectorViewModel target)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(target);
        if (IsReadOnly)
        {
            return (false, _text.Get("Canvas_Team_ReadOnly"));
        }

        if (!TeamRoles.Writes(agent.Definition.Role))
        {
            return (false, _text.Get("Canvas_Connect_NotWriter"));
        }

        if (target.Owner is not RepositoryNodeViewModel)
        {
            return (false, _text.Get("Canvas_Connect_OnlyFolders"));
        }

        return Connections.Any(c => c.Kind == TeamEdgeKind.WorksIn && ReferenceEquals(c.Source, agent.Output) && ReferenceEquals(c.Target, target))
            ? (false, _text.Get("Canvas_Connect_Already"))
            : (true, null);
    }

    /// <summary>The places an agent can be connected to or disconnected from, for «Conectar con…» and «Desconectar de…» with the keyboard.</summary>
    public IReadOnlyList<ConnectTarget> TargetsOf(AgentNodeViewModel agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (Repository is null)
        {
            return [];
        }

        return
        [
            .. new[] { Repository.WholeProject }.Concat(Repository.Folders).Select(connector =>
            {
                var connected = Connections.Any(c => c.Kind == TeamEdgeKind.WorksIn && ReferenceEquals(c.Source, agent.Output) && ReferenceEquals(c.Target, connector));
                var (allowed, refusal) = connected ? (!IsReadOnly && TeamRoles.Writes(agent.Definition.Role), (string?)null) : CheckFolder(agent, connector);
                return new ConnectTarget(connector, connected, allowed, refusal);
            }),
        ];
    }

    /// <summary>
    /// Connects an agent to a folder (a cable dragged, or «Conectar con…»): it asks for the folder, and the editor saves the
    /// template. A cable the check refuses is not asked for. Returns the refusal, or null when it was asked for.
    /// </summary>
    public string? RequestConnect(AgentNodeViewModel agent, TeamConnectorViewModel target)
    {
        var (allowed, refusal) = CheckFolder(agent, target);
        if (!allowed)
        {
            return refusal;
        }

        FolderCableRequested?.Invoke(this, new FolderCableRequest(agent.Id, target.Folder, Connect: true));
        return null;
    }

    /// <summary>Disconnects an agent from a folder (Supr on its cable, or «Desconectar de…»). Returns the refusal, or null when it was asked for.</summary>
    public string? RequestDisconnect(AgentNodeViewModel agent, TeamConnectorViewModel target)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(target);
        if (IsReadOnly)
        {
            return _text.Get("Canvas_Team_ReadOnly");
        }

        FolderCableRequested?.Invoke(this, new FolderCableRequest(agent.Id, target.Folder, Connect: false));
        return null;
    }

    // ---------- Layout ----------

    /// <summary>The positions of the nodes, by agent id and <c>repository</c> for the node of the repository.</summary>
    public CanvasLayout CaptureLayout()
    {
        var positions = new Dictionary<string, Point>(_layout.TeamPositions, StringComparer.Ordinal);
        foreach (var agent in Agents)
        {
            positions[agent.Id.Value] = agent.Location;
        }

        if (Repository is { } repository)
        {
            positions[RepositoryNodeViewModel.LayoutId] = repository.Location;
        }

        return new CanvasLayout(new Dictionary<string, Point>(StringComparer.Ordinal)) { TeamPositions = positions };
    }

    /// <summary>The orchestrator on top, the executors under it in a row, the judges to the right and the repository below.</summary>
    private static Dictionary<AgentId, Point> Layout(IReadOnlyList<AgentDefinition> shown)
    {
        var positions = new Dictionary<AgentId, Point>();
        var executors = shown.Where(a => TeamRoles.Writes(a.Role)).ToList();
        var judges = shown.Where(a => TeamRoles.Judges(a.Role)).ToList();
        var others = shown.Except(executors).Except(judges).ToList();
        var width = Math.Max(1, executors.Count) * ColumnWidth;
        foreach (var agent in others)
        {
            positions[agent.Id] = new Point(Math.Max(0, (width - ColumnWidth) / 2), 0);
        }

        for (var i = 0; i < executors.Count; i++)
        {
            positions[executors[i].Id] = new Point(i * ColumnWidth, RowHeight);
        }

        for (var i = 0; i < judges.Count; i++)
        {
            positions[judges[i].Id] = new Point(width + ColumnWidth / 2, i * RowHeight * 0.9);
        }

        return positions;
    }

    private static Point RepositoryDefault(IReadOnlyDictionary<AgentId, Point> positions) =>
        new(0, (positions.Count == 0 ? 0 : positions.Values.Max(p => p.Y)) + RowHeight * 1.3);

    /// <summary>A grant of folder as a main folder: relative with <c>/</c> at the end; null when it is not one.</summary>
    internal static string? AsFolder(string root)
    {
        var trimmed = (root ?? string.Empty).Replace('\\', '/').Trim();
        var folder = trimmed.EndsWith('/') ? trimmed : trimmed + "/";
        return RepositoryNode.IsMainFolder(folder) ? folder : null;
    }
}
