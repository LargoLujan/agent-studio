using AgentStudio.Domain.Work;

namespace NodifySpike.ViewModels;

/// <summary>
/// The eight task statuses the canvas colours. <see cref="WorkTaskStatus"/> has seven values until P0 adds
/// <c>Skipped</c> (docs/13 section 3.1); WP6 then drops this copy and binds the domain enum directly.
/// </summary>
public enum NodeStatus
{
    Pending,
    Running,
    Blocked,
    AwaitingApproval,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}

/// <summary>One entry of the status legend (which doubles as the "apply status" picker of the harness).</summary>
public sealed record StatusChoice(NodeStatus Value, string Label);

public static class NodeStatusInfo
{
    public static NodeStatus From(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Pending => NodeStatus.Pending,
        WorkTaskStatus.Running => NodeStatus.Running,
        WorkTaskStatus.Blocked => NodeStatus.Blocked,
        WorkTaskStatus.AwaitingApproval => NodeStatus.AwaitingApproval,
        WorkTaskStatus.Completed => NodeStatus.Completed,
        WorkTaskStatus.Failed => NodeStatus.Failed,
        WorkTaskStatus.Cancelled => NodeStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown task status."),
    };

    public static string Label(NodeStatus status) => status switch
    {
        NodeStatus.Pending => "Pendiente",
        NodeStatus.Running => "En curso",
        NodeStatus.Blocked => "Bloqueada",
        NodeStatus.AwaitingApproval => "Espera aprobación",
        NodeStatus.Completed => "Completada",
        NodeStatus.Failed => "Fallida",
        NodeStatus.Cancelled => "Cancelada",
        NodeStatus.Skipped => "Omitida",
        _ => status.ToString(),
    };
}

public static class NodeKindInfo
{
    public static string Label(WorkTaskKind kind) => kind switch
    {
        WorkTaskKind.AgentWork => "Trabajo de agente",
        WorkTaskKind.Condition => "Condición",
        WorkTaskKind.ToolRun => "Herramienta",
        WorkTaskKind.Review => "Revisión",
        WorkTaskKind.Debate => "Debate",
        WorkTaskKind.HumanApproval => "Aprobación humana",
        WorkTaskKind.Integration => "Integración",
        _ => kind.ToString(),
    };

    /// <summary>The plan validator rejects Debate nodes until Phase 5 (docs/13 section 1, "Se aplaza").</summary>
    public static bool IsAvailable(WorkTaskKind kind) => kind != WorkTaskKind.Debate;

    public const string UnavailableBadge = "Fase 5";
}
