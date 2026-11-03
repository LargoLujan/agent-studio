using System.Windows;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NodifySpike.ViewModels;

/// <summary>A task node of the canvas: one of the seven <see cref="WorkTaskKind"/> values, with its connectors.</summary>
public sealed partial class NodeViewModel : ObservableObject
{
    public NodeViewModel(TaskId id, WorkTaskKind kind, string title, Point location, NodeStatus status = NodeStatus.Pending)
    {
        Id = id;
        Kind = kind;
        _title = title;
        _location = location;
        _status = status;

        Inputs = [new ConnectorViewModel(this, ConnectorFlow.Input, "Entrada")];
        Outputs = kind == WorkTaskKind.Condition
            ? [new ConnectorViewModel(this, ConnectorFlow.Output, "Sí", Branches.Yes), new ConnectorViewModel(this, ConnectorFlow.Output, "No", Branches.No)]
            : [new ConnectorViewModel(this, ConnectorFlow.Output, "Salida")];
    }

    public TaskId Id { get; }

    public WorkTaskKind Kind { get; }

    public string KindLabel => NodeKindInfo.Label(Kind);

    /// <summary>True for a kind the plan validator rejects (Debate until Phase 5): shown dimmed, not connectable.</summary>
    public bool IsDisabled => !NodeKindInfo.IsAvailable(Kind);

    public IReadOnlyList<ConnectorViewModel> Inputs { get; }

    public IReadOnlyList<ConnectorViewModel> Outputs { get; }

    [ObservableProperty]
    private string _title;

    /// <summary>Top-left corner in graph coordinates; Nodify writes it back when the node is dragged.</summary>
    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private NodeStatus _status;

    public string StatusLabel => IsDisabled ? NodeKindInfo.UnavailableBadge : NodeStatusInfo.Label(Status);

    /// <summary>What a screen reader announces for the node (Nodify ships no automation peers).</summary>
    public string AutomationName => $"{Id}, {KindLabel}, {Title}, {StatusLabel}";

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}
