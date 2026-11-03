using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NodifySpike.ViewModels;

public enum ConnectorFlow
{
    Input,
    Output,
}

/// <summary>Branch names of the two outputs of a Condition node (the <c>branch</c> of an edge, docs/13 section 3.1).</summary>
public static class Branches
{
    public const string Yes = "yes";
    public const string No = "no";
}

/// <summary>
/// One connector of a node. The view binds <see cref="Anchor"/> one way to source (Nodify writes the position in graph
/// coordinates) and <see cref="IsConnected"/> one way; without <see cref="IsConnected"/> Nodify does not update the anchor.
/// </summary>
public sealed partial class ConnectorViewModel : ObservableObject
{
    private int _connectionCount;

    public ConnectorViewModel(NodeViewModel node, ConnectorFlow flow, string title, string? branch = null)
    {
        Node = node;
        Flow = flow;
        Title = title;
        Branch = branch;
    }

    public NodeViewModel Node { get; }

    public ConnectorFlow Flow { get; }

    public string Title { get; }

    /// <summary><see cref="Branches.Yes"/> or <see cref="Branches.No"/> for the outputs of a Condition; null otherwise.</summary>
    public string? Branch { get; }

    /// <summary>Text drawn next to the connector and on its wires: only the branch outputs are labelled.</summary>
    public string? Label => Branch is null ? null : Title;

    public string AutomationName => $"{Title} de {Node.Id}";

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
}
