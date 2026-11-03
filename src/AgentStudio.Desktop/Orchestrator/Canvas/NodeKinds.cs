using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>What the canvas shows and assumes for each <see cref="WorkTaskKind"/>.</summary>
public static class NodeKinds
{
    /// <summary>
    /// Badge of the kinds the plan validator rejects: Debate, whose nodes on the canvas were postponed (docs/15, decision 3
    /// of section 12); debates are opened from «Debates».
    /// </summary>
    public const string UnavailableBadge = "aplazado";

    /// <summary>Why a Debate node cannot be used, as the canvas says it.</summary>
    public const string UnavailableReason = "los nodos de debate del lienzo están aplazados; abre el debate desde «Debates»";

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

    /// <summary>False for Debate: <see cref="PlanValidator"/> rejects it (postponed, docs/15), so the canvas shows it disabled.</summary>
    public static bool IsAvailable(WorkTaskKind kind) => kind != WorkTaskKind.Debate;

    /// <summary>
    /// The checkout a new node starts with: agents and integrations write in a worktree of their own, reviews and tools
    /// read the code, and conditions and approvals need none.
    /// </summary>
    public static WorkspaceMode DefaultWorkspace(WorkTaskKind kind) => kind switch
    {
        WorkTaskKind.AgentWork or WorkTaskKind.Integration => WorkspaceMode.Write,
        WorkTaskKind.Review or WorkTaskKind.ToolRun => WorkspaceMode.ReadOnly,
        _ => WorkspaceMode.None,
    };

    /// <summary>
    /// Retries a new node starts with. Reviews and approvals cannot be retried (the validator requires 0), and an
    /// integration fails for good on a conflict (docs/13 section 5).
    /// </summary>
    public static int DefaultRetries(WorkTaskKind kind) => kind is WorkTaskKind.AgentWork or WorkTaskKind.ToolRun ? 2 : 0;
}

/// <summary>Spanish names of the task statuses; the colour of a node never carries its status alone.</summary>
public static class TaskStatusText
{
    public static string Label(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Pending => "Pendiente",
        WorkTaskStatus.Running => "En curso",
        WorkTaskStatus.Blocked => "Bloqueada",
        WorkTaskStatus.AwaitingApproval => "Espera aprobación",
        WorkTaskStatus.Completed => "Completada",
        WorkTaskStatus.Failed => "Fallida",
        WorkTaskStatus.Cancelled => "Cancelada",
        WorkTaskStatus.Skipped => "Omitida",
        _ => status.ToString(),
    };
}
