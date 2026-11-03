using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

/// <summary>
/// A valid plan and the context it is valid in. Each validator test breaks exactly one thing in it.
/// <code>
///  T1 AgentWork  ejecutor   (write src/Calc/, tests/Calc.Tests/)   covers R1
///  T2 AgentWork  ejecutor2  (write src/Other/)                     covers R2
///  T3 Integration           depends on T1, T2
///  T4 Condition TestsPassed(T3), depends on T3
///  T5 HumanApproval         depends on T4 (yes)
///  T6 AgentWork  ejecutor   (write docs/)                          depends on T4 (no)
///  T7 Review     juez       depends on T5, T6 (AnyCompleted)
///  T8 ToolRun    lint       depends on T7
/// </code>
/// </summary>
internal static class PlanFixtures
{
    public static AgentDefinition Agent(string id, AgentRole role, VendorId vendor) =>
        new(
            Aid(id),
            id,
            role,
            new ModelBinding(new ProviderId("p-" + vendor.Value), vendor),
            "system",
            [],
            [],
            [],
            new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
            3,
            MemoryPolicy.None,
            new ReviewPolicy(false, false));

    public static IReadOnlyList<AgentDefinition> Roster() =>
    [
        Agent("orquestador", AgentRole.Orchestrator, VendorId.Anthropic),
        Agent("ejecutor", AgentRole.Executor, VendorId.Anthropic),
        Agent("ejecutor2", AgentRole.Executor, VendorId.Anthropic),
        Agent("ejecutorG", AgentRole.Executor, VendorId.Google),
        Agent("ejecutorO", AgentRole.Executor, VendorId.OpenAI),
        Agent("juez", AgentRole.Judge, VendorId.OpenAI),
        Agent("juezA", AgentRole.Judge, VendorId.Anthropic),
        Agent("juez2", AgentRole.Judge, VendorId.Google),
        Agent("auditor", AgentRole.Auditor, VendorId.OpenAI),
    ];

    public static FrozenRequirementSet Requirements() =>
        FrozenRequirementSet.Freeze(
            [
                Req("R1", RequirementPriority.Mandatory, "Add Calc.Multiply", Crit("c1", VerificationMethod.AutomatedTest)),
                Req("R2", RequirementPriority.Desirable, "Document it", Crit("c2", VerificationMethod.HumanReview)),
            ],
            T0);

    /// <summary>The context of the valid plan: the profile has a build, a test step (the one test run) and a lint tool.</summary>
    public static PlanContext Context(PlanLimits? limits = null, IReadOnlyList<AgentDefinition>? roster = null) =>
        new(
            Requirements(),
            roster ?? Roster(),
            new HashSet<string> { "build", "test" },
            new HashSet<string> { "lint" },
            limits ?? new PlanLimits(),
            new HashSet<string> { "test" });

    public static TaskSpec Spec(string id, WorkTaskKind kind = WorkTaskKind.AgentWork) =>
        new(
            Tid(id),
            $"Task {id}",
            kind,
            Agent: null,
            Objective: kind == WorkTaskKind.AgentWork ? $"Do {id}" : string.Empty,
            Instructions: string.Empty,
            DependsOn: [],
            Join: JoinPolicy.AllCompleted,
            Covers: [],
            WriteScope: [],
            VerificationSteps: [],
            Condition: null,
            ToolId: null,
            MaxRetries: 1,
            Timeout: null,
            Workspace: WorkspaceMode.None);

    public static DependencySpec Dep(string prerequisite, string? branch = null) => new(Tid(prerequisite), branch);

    public static IReadOnlyList<TaskSpec> ValidTasks() =>
    [
        Spec("T1") with
        {
            Agent = Aid("ejecutor"),
            Workspace = WorkspaceMode.Write,
            WriteScope = ["src/Calc/", "tests/Calc.Tests/"],
            Covers = [Rid("R1")],
            VerificationSteps = ["build", "test"],
        },
        Spec("T2") with
        {
            Agent = Aid("ejecutor2"),
            Workspace = WorkspaceMode.Write,
            WriteScope = ["src/Other/"],
            Covers = [Rid("R2")],
            VerificationSteps = ["build"],
        },
        Spec("T3", WorkTaskKind.Integration) with { DependsOn = [Dep("T1"), Dep("T2")], VerificationSteps = ["build", "test"] },
        Spec("T4", WorkTaskKind.Condition) with { DependsOn = [Dep("T3")], Condition = new TestsPassed(Tid("T3")) },
        Spec("T5", WorkTaskKind.HumanApproval) with { DependsOn = [Dep("T4", "yes")], MaxRetries = 0 },
        Spec("T6") with
        {
            Agent = Aid("ejecutor"),
            Workspace = WorkspaceMode.Write,
            WriteScope = ["docs/"],
            DependsOn = [Dep("T4", "no")],
        },
        Spec("T7", WorkTaskKind.Review) with
        {
            Agent = Aid("juez"),
            Workspace = WorkspaceMode.ReadOnly,
            DependsOn = [Dep("T5"), Dep("T6")],
            Join = JoinPolicy.AnyCompleted,
            MaxRetries = 0,
        },
        Spec("T8", WorkTaskKind.ToolRun) with { ToolId = "lint", DependsOn = [Dep("T7")] },
    ];

    public static PlanDefinition Plan(IReadOnlyList<TaskSpec>? tasks = null, PlanSource source = PlanSource.OrchestratorAgent, string? author = "orquestador") =>
        PlanDefinition.Create(
            "Add Calc.Multiply with tests",
            tasks ?? ValidTasks(),
            ["The build may be slow"],
            source,
            author is null ? null : Aid(author));

    /// <summary>The valid tasks with <paramref name="id"/> replaced by <paramref name="changed"/>.</summary>
    public static IReadOnlyList<TaskSpec> Replace(string id, Func<TaskSpec, TaskSpec> changed) =>
        [.. ValidTasks().Select(t => t.Id == Tid(id) ? changed(t) : t)];
}
