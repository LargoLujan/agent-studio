using AgentStudio.Desktop.Localization;
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
    public static string UnavailableBadge => Texts.Get("Orchestrator_NodeKind_Badge");

    /// <summary>Why a Debate node cannot be used, as the canvas says it.</summary>
    public static string UnavailableReason => Texts.Get("Orchestrator_NodeKind_Reason");

    public static string Label(WorkTaskKind kind) => kind switch
    {
        WorkTaskKind.AgentWork => Texts.Get("Orchestrator_NodeKind_AgentWork"),
        WorkTaskKind.Condition => Texts.Get("Orchestrator_NodeKind_Condition"),
        WorkTaskKind.ToolRun => Texts.Get("Orchestrator_NodeKind_ToolRun"),
        WorkTaskKind.Review => Texts.Get("Orchestrator_NodeKind_Review"),
        WorkTaskKind.Debate => Texts.Get("Orchestrator_NodeKind_Debate"),
        WorkTaskKind.HumanApproval => Texts.Get("Orchestrator_NodeKind_HumanApproval"),
        WorkTaskKind.Integration => Texts.Get("Orchestrator_NodeKind_Integration"),
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

/// <summary>The names of the task statuses in the language of the interface; the colour of a node never carries its status alone.</summary>
public static class TaskStatusText
{
    public static string Label(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Pending => Texts.Get("Orchestrator_TaskStatus_Pending"),
        WorkTaskStatus.Running => Texts.Get("Orchestrator_TaskStatus_Running"),
        WorkTaskStatus.Blocked => Texts.Get("Orchestrator_TaskStatus_Blocked"),
        WorkTaskStatus.AwaitingApproval => Texts.Get("Orchestrator_TaskStatus_AwaitingApproval"),
        WorkTaskStatus.Completed => Texts.Get("Orchestrator_TaskStatus_Completed"),
        WorkTaskStatus.Failed => Texts.Get("Orchestrator_TaskStatus_Failed"),
        WorkTaskStatus.Cancelled => Texts.Get("Orchestrator_TaskStatus_Cancelled"),
        WorkTaskStatus.Skipped => Texts.Get("Orchestrator_TaskStatus_Skipped"),
        _ => status.ToString(),
    };
}
