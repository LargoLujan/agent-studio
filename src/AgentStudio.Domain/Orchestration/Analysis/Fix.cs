using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Orchestration.Analysis;

/// <summary>
/// A fix of one click (docs/17 D-18): a typed edit of the draft, never text. Applying it is an edit that can be undone
/// (Ctrl+Z and Ctrl+Y). A fix that changes the template says so before it is applied (<see cref="ChangesRoster"/>: it withdraws
/// the approval of the plan), and one that grants a capability of high risk asks Windows Hello
/// (<see cref="GrantsHighRiskCapability"/>, D-37). The same types carry the suggestions of «Revisar el orden» (D-20) and of
/// the analysis of consumption with an agent (D-35), which the system checks again before it offers them.
/// </summary>
public abstract record Fix
{
    /// <summary>Whether applying it changes the template of agents: its hash changes and the approval of the plan is withdrawn.</summary>
    public abstract bool ChangesRoster { get; }

    /// <summary>Whether applying it grants an agent a capability that changes the threat model: it asks Windows Hello.</summary>
    public virtual bool GrantsHighRiskCapability => false;
}

/// <summary>Adds an «Integración» task at the end, behind the leaves of the graph (<see cref="FindingCodes.NoIntegration"/>).</summary>
public sealed record AddIntegrationFix : Fix
{
    public override bool ChangesRoster => false;
}

/// <summary>Adds the verification step <paramref name="BuildStep"/> before the test steps of <paramref name="Task"/> (<see cref="FindingCodes.TestWithoutBuild"/>).</summary>
public sealed record AddBuildStepFix(TaskId Task, string BuildStep) : Fix
{
    public override bool ChangesRoster => false;
}

/// <summary>Makes <paramref name="Task"/> depend on <paramref name="DependsOn"/>: «T4 después de T2».</summary>
public sealed record AddDependencyFix(TaskId Task, TaskId DependsOn) : Fix
{
    public override bool ChangesRoster => false;
}

/// <summary>Gives <paramref name="Task"/> to <paramref name="Agent"/>.</summary>
public sealed record ReassignTaskFix(TaskId Task, AgentId Agent) : Fix
{
    public override bool ChangesRoster => false;
}

/// <summary>Has the review <paramref name="ReviewTask"/> done by the judge <paramref name="Judge"/>, of the other vendor.</summary>
public sealed record SwitchJudgeFix(TaskId ReviewTask, AgentId Judge) : Fix
{
    public override bool ChangesRoster => false;
}

/// <summary>Adds a judge of the vendor that did not produce the work, copying the template of the role (docs/17 D-09).</summary>
public sealed record AddJudgeFix(ProviderId Provider, VendorId Vendor) : Fix
{
    public override bool ChangesRoster => true;
}

/// <summary>Adds <paramref name="Folder"/> to the folders where <paramref name="Agent"/> may write: more folders is a capability of high risk (D-37).</summary>
public sealed record WidenAgentFoldersFix(AgentId Agent, string Folder) : Fix
{
    public override bool ChangesRoster => true;

    public override bool GrantsHighRiskCapability => true;
}

/// <summary>Raises the context budget of <paramref name="Agent"/> to <paramref name="Characters"/> (<see cref="FindingCodes.PackTooSmall"/>).</summary>
public sealed record RaiseContextBudgetFix(AgentId Agent, int Characters) : Fix
{
    public override bool ChangesRoster => true;
}

/// <summary>Raises the budget of the run, in agent runs (<see cref="FindingCodes.CostOverBudget"/>).</summary>
public sealed record RaiseRunBudgetFix(int MaxAgentRuns) : Fix
{
    public override bool ChangesRoster => false;
}

/// <summary>Changes the model of <paramref name="Agent"/>, and its effort when <paramref name="Effort"/> is given.</summary>
public sealed record ChangeModelFix(AgentId Agent, string Model, ReasoningEffort? Effort = null) : Fix
{
    public override bool ChangesRoster => true;
}

/// <summary>Sets the memory policy of <paramref name="Agent"/> (<see cref="FindingCodes.ContextThin"/>, <see cref="FindingCodes.WindowTight"/>).</summary>
public sealed record SetMemoryFix(AgentId Agent, MemoryPolicy Memory) : Fix
{
    public override bool ChangesRoster => true;
}

/// <summary>Deactivates <paramref name="Skills"/> for <paramref name="Agent"/> (<see cref="FindingCodes.WindowTight"/>): the activations are the person's, through their store.</summary>
public sealed record RemoveSkillsFix(AgentId Agent, IReadOnlyList<string> Skills) : Fix
{
    public override bool ChangesRoster => false;
}

/// <summary>Takes <paramref name="Tool"/> from <paramref name="Agent"/> (<see cref="FindingCodes.HighRiskCapability"/>): lowering a capability never asks Hello.</summary>
public sealed record RemoveCapabilityFix(AgentId Agent, string Tool) : Fix
{
    public override bool ChangesRoster => true;
}
