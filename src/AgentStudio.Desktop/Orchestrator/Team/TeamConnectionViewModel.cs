using CommunityToolkit.Mvvm.ComponentModel;
using AgentStudio.Domain.Team;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>
/// A cable of the Team layer (docs/17 D-11): «Reparte», «Revisa» or «Trabaja en». Each one is made real by a rule, never by the
/// drawing: «Revisa» between companies that share a vendor is drawn as an error, with its text, and «Trabaja en» is animated
/// while the task of the agent writes. No cable of this layer ever enters the plan.
/// </summary>
public sealed partial class TeamConnectionViewModel : ObservableObject
{
    public TeamConnectionViewModel(TeamEdge edge, TeamConnectorViewModel source, TeamConnectorViewModel target, string text, bool isError)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        Edge = edge;
        Source = source;
        Target = target;
        Text = text;
        IsError = isError;
        Source.Add();
        Target.Add();
    }

    public TeamEdge Edge { get; }

    public TeamEdgeKind Kind => Edge.Kind;

    public TeamConnectorViewModel Source { get; }

    public TeamConnectorViewModel Target { get; }

    /// <summary>What the cable means in words («reparte», «revisa», «trabaja en»), or why it is an error.</summary>
    public string Text { get; }

    /// <summary>A cable «Revisa» whose judge shares the vendor of a producer: drawn in the colour of an error.</summary>
    public bool IsError { get; }

    /// <summary>The cable moves: «Trabaja en» while the task of its agent writes (docs/17 D-14); it is off with «Reducir movimiento».</summary>
    [ObservableProperty]
    private bool _isAnimated;

    public string AutomationName => Text;

    internal void Detach()
    {
        Source.Remove();
        Target.Remove();
    }

    public override string ToString() => AutomationName;
}
