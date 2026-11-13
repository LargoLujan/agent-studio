using System.Windows;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Graph;

/// <summary>A connector of a node of the canvas: Nodify writes its position in <see cref="Anchor"/> (one way to source) and only does it for a connector that is connected.</summary>
public sealed partial class GraphConnectorItem : ObservableObject
{
    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;

    internal int Connections { get; set; }

    internal void Add()
    {
        Connections++;
        IsConnected = true;
    }

    /// <summary>Nodify shows the text of a connector next to it: the graph has none, so a connector says nothing.</summary>
    public override string ToString() => string.Empty;
}

/// <summary>A node of the canvas of Auto Graph (docs/20 D-22): a project, or a namespace of a project that is unfolded, or a package. It only describes; the canvas is read-only.</summary>
public sealed partial class GraphNodeItem : ObservableObject
{
    internal GraphNodeItem(GraphNode node, bool isExpanded)
    {
        Node = node;
        _isExpanded = isExpanded;
        Input = [new GraphConnectorItem()];
        Output = [new GraphConnectorItem()];
    }

    public GraphNode Node { get; }

    public string Id => Node.Id;

    public GraphNodeKind Kind => Node.Kind;

    /// <summary>The name that the node shows.</summary>
    public string Title => Node.Name;

    /// <summary>What the node is, as a word: «Proyecto», «Espacio de nombres», «Paquete».</summary>
    public string KindText => Texts.Get("Graph_Kind_" + Node.Kind);

    /// <summary>The glyph of «parcial»: a shape and not only a colour, so it is told apart without seeing colours.</summary>
    public bool IsPartial => Node.Partial;

    public string PartialText => Node.Partial ? Texts.Get("Graph_Node_Partial") : string.Empty;

    /// <summary>A project has namespaces to unfold.</summary>
    public bool CanExpand { get; internal init; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private Size _size;

    [ObservableProperty]
    private bool _isSelected;

    public IReadOnlyList<GraphConnectorItem> Input { get; }

    public IReadOnlyList<GraphConnectorItem> Output { get; }

    /// <summary>What a screen reader says of the node.</summary>
    public string AutomationName => Node.Partial ? Texts.Format("Graph_Node_NamePartial", KindText, Node.Name) : Texts.Format("Graph_Node_Name", KindText, Node.Name);

    public override string ToString() => AutomationName;
}

/// <summary>A relation drawn on the canvas: from the node that uses to the one that is used. One proposed by an AI is drawn apart (docs/20 D-21, F6-4).</summary>
public sealed class GraphWireItem
{
    internal GraphWireItem(GraphEdge edge, GraphNodeItem user, GraphNodeItem used)
    {
        Edge = edge;
        User = user;
        Used = used;
        Source = user.Output[0];
        Target = used.Input[0];
        Source.Add();
        Target.Add();
    }

    public GraphEdge Edge { get; }

    public GraphNodeItem User { get; }

    public GraphNodeItem Used { get; }

    public GraphConnectorItem Source { get; }

    public GraphConnectorItem Target { get; }

    /// <summary>Proposed by an AI: dashed, in another colour and with its label «IA», and never the same thing as a relation read from the files.</summary>
    public bool IsInferred => Edge.Origin == GraphEdgeOrigin.Inferred;

    /// <summary>The label of the wire: «IA» for a proposed one, none for a read one.</summary>
    public string? Label => IsInferred ? Texts.Get("Graph_Wire_Label_Inferred") : null;

    /// <summary>Who proposed it, when it was proposed.</summary>
    public string? ProposedBy => Edge.Inference is { } inference ? Texts.Format("Graph_Wire_ProposedBy", inference.Agent, inference.Model) : null;

    public string KindText => Texts.Get("Graph_Edge_" + Edge.Kind);

    /// <summary>What a screen reader says of the wire. For a proposed relation it says «propuesta por…»: it does not depend on the colour or the dashes.</summary>
    public string AutomationName => IsInferred
        ? Texts.Format("Graph_Wire_NameInferred", User.Title, KindText, Used.Title, Edge.Inference!.Agent)
        : Texts.Format("Graph_Wire_Name", User.Title, KindText, Used.Title);

    public override string ToString() => AutomationName;
}

/// <summary>A line of the list that stands for the canvas: for the keyboard and the screen reader, the same graph as words (docs/20 D-22).</summary>
public sealed record GraphRowItem(string Id, string Text, bool IsInferred);

/// <summary>A thing of the legend.</summary>
public sealed record GraphLegendItem(string Text, bool IsDashed, bool IsPartial);

/// <summary>A gap of the model painted: what it says, where and on which node.</summary>
public sealed record GraphGapRow(string NodeName, string Text, string Where)
{
    internal static GraphGapRow From(CodeGraph graph, GraphGap gap)
    {
        var node = graph.Nodes.FirstOrDefault(n => n.Id == gap.NodeId);
        var where = gap.Line is { } line ? Texts.Format("Graph_Gap_Where", gap.Path, line) : gap.Path;
        return new GraphGapRow(node?.Name ?? gap.NodeId, Texts.Current.Render(UserText.Of(gap.Code)), where);
    }
}

/// <summary>A file that declares a namespace: where the person goes from a namespace of the graph (F6-5).</summary>
public sealed record GraphFileRow(string Path);

/// <summary>A task whose attempts changed files of a project: where the person goes from a project of the graph to the work (F6-5).</summary>
public sealed record GraphTaskRow(GraphTaskRef Task)
{
    public string Text => Texts.Format("Graph_Task_Line", Task.Title, Task.ChangedPaths.Count);

    public string AutomationName => Texts.Format("Graph_Task_Open", Task.Title);
}
