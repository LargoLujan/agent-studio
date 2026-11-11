using System.Windows;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Orchestration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

public enum ConnectorFlow
{
    Input,
    Output,
}

/// <summary>
/// One connector of a node. The view binds <see cref="Anchor"/> one way to source (Nodify writes the position in graph
/// coordinates) and <see cref="IsConnected"/> one way: Nodify only updates the anchor of a connected connector.
/// </summary>
public sealed partial class ConnectorViewModel : ObservableObject
{
    private int _connectionCount;

    public ConnectorViewModel(NodeViewModel node, ConnectorFlow flow, string title, string? branch = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Node = node;
        Flow = flow;
        Title = title;
        Branch = branch;
    }

    public NodeViewModel Node { get; }

    public ConnectorFlow Flow { get; }

    public string Title { get; }

    /// <summary><see cref="ConditionBranches.Yes"/> or <see cref="ConditionBranches.No"/> on the outputs of a condition; null otherwise.</summary>
    public string? Branch { get; }

    /// <summary>Text drawn next to the connector and on its wires: only the branch outputs are labelled.</summary>
    public string? Label => Branch is null ? null : Title;

    public string AutomationName => Texts.Format("Orchestrator_Connector_Name", Title, Node.Id);

    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;

    internal void AddConnection()
    {
        _connectionCount++;
        IsConnected = true;
    }

    internal void RemoveConnection()
    {
        _connectionCount = Math.Max(0, _connectionCount - 1);
        IsConnected = _connectionCount > 0;
    }

    /// <summary>What a screen reader says of a connector of a node (V34): its title and its node.</summary>
    public override string ToString() => AutomationName;
}

/// <summary>
/// A wire from an output connector (its node is the prerequisite) to an input connector (its node depends on it).
/// Creating it marks both connectors as connected; <see cref="Detach"/> undoes that.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    public ConnectionViewModel(ConnectorViewModel source, ConnectorViewModel target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Flow != ConnectorFlow.Output || target.Flow != ConnectorFlow.Input)
        {
            throw new ArgumentException("A connection goes from an output connector to an input connector.", nameof(source));
        }

        Source = source;
        Target = target;
        Source.AddConnection();
        Target.AddConnection();
    }

    /// <summary>The output connector: its node is the prerequisite.</summary>
    public ConnectorViewModel Source { get; }

    /// <summary>The input connector: its node depends on <see cref="Source"/>'s node.</summary>
    public ConnectorViewModel Target { get; }

    /// <summary>
    /// The dependency was just met and its next task started: a short pulse on the cable (docs/17 D-14). The grouped live state
    /// sets it and clears it with the next change; the animation is a finite <c>Storyboard</c> of the template and is off with
    /// «Reducir movimiento».
    /// </summary>
    [ObservableProperty]
    private bool _isPulsing;

    /// <summary>The branch of the condition the dependent waits for, or null for a plain dependency.</summary>
    public string? Branch => Source.Branch;

    public string AutomationName => Branch is null
        ? Texts.Format("Orchestrator_Connector_Link", Source.Node.Id, Target.Node.Id)
        : Texts.Format("Orchestrator_Connector_LinkWithBranch", Source.Node.Id, Source.Title, Target.Node.Id);

    internal void Detach()
    {
        Source.RemoveConnection();
        Target.RemoveConnection();
    }

    /// <summary>What a screen reader says of a wire of the canvas (V34): the task it goes from and the one it goes to.</summary>
    public override string ToString() => AutomationName;
}
