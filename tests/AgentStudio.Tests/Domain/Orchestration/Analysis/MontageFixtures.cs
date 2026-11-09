using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration.Analysis;

/// <summary>
/// A clean draft the analyzer finds nothing in, for each test to break in one way:
/// <code>
///  orquestador  Claude · opus (máximo)          plans
///  ejecutor     Claude · sonnet (equilibrado)   memory, writes src/Calc/ in T1
///  ejecutor2    Claude · haiku (ligero)
///  juez         Codex                           reviews R1
///  T1 AgentWork ejecutor (src/Calc/, test) → I1 Integration (test) → R1 Review juez
/// </code>
/// The profile: <c>build</c> builds, <c>test</c> runs tests and requires <c>build</c>.
/// </summary>
internal static class MontageFixtures
{
    public static AgentDefinition Agent(string id, AgentRole role, ProviderId provider, VendorId vendor, string? model = null, MemoryPolicy memory = MemoryPolicy.None, IReadOnlyList<string>? tools = null) =>
        new(Aid(id), id, role, new ModelBinding(provider, vendor, model), "system", [], tools ?? [], [], new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 3, memory, new ReviewPolicy(false, false));

    public static IReadOnlyList<AgentDefinition> Roster() =>
    [
        Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic, "opus"),
        Agent("ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet", MemoryPolicy.ReadSelected),
        Agent("ejecutor2", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
        Agent("juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI),
    ];

    /// <summary>The roster with <paramref name="id"/> changed by <paramref name="change"/>.</summary>
    public static IReadOnlyList<AgentDefinition> With(this IReadOnlyList<AgentDefinition> roster, string id, Func<AgentDefinition, AgentDefinition> change) =>
        [.. roster.Select(a => a.Id == Aid(id) ? change(a) : a)];

    public static ModelCatalog Catalog() =>
        new(
            1,
            [
                new ModelCatalogEntry(ProviderId.ClaudeCode, "2.1.290", ["claude-opus", "opus"], ModelTier.Maximum, [ReasoningEffort.High]),
                new ModelCatalogEntry(ProviderId.ClaudeCode, "2.1.290", ["claude-sonnet", "sonnet"], ModelTier.Balanced, [ReasoningEffort.Medium]),
                new ModelCatalogEntry(ProviderId.ClaudeCode, "2.1.290", ["claude-haiku", "haiku"], ModelTier.Light, []),
            ],
            ModelRecommendationRules.Default);

    public static IReadOnlyList<VerificationStepFacts> Steps(bool testRequiresBuild = true) =>
    [
        new("build", Builds: true, RunsTests: false, Requires: []),
        new("test", Builds: false, RunsTests: true, Requires: testRequiresBuild ? ["build"] : []),
    ];

    public static TaskSpec Task(string id, WorkTaskKind kind = WorkTaskKind.AgentWork, string? agent = null) =>
        new(
            Tid(id),
            "Tarea " + id,
            kind,
            agent is null ? null : Aid(agent),
            kind == WorkTaskKind.AgentWork ? "Objetivo de " + id : string.Empty,
            string.Empty,
            [],
            JoinPolicy.AllCompleted,
            [],
            [],
            [],
            null,
            null,
            kind is WorkTaskKind.Review or WorkTaskKind.HumanApproval ? 0 : 1,
            null,
            kind switch { WorkTaskKind.AgentWork => WorkspaceMode.Write, WorkTaskKind.Review => WorkspaceMode.ReadOnly, _ => WorkspaceMode.None });

    public static TaskSpec Writing(string id, string agent, params string[] scope) =>
        Task(id, WorkTaskKind.AgentWork, agent) with { WriteScope = scope, VerificationSteps = ["test"] };

    public static TaskSpec After(this TaskSpec task, params string[] prerequisites) =>
        task with { DependsOn = [.. prerequisites.Select(p => new DependencySpec(Tid(p)))] };

    public static IReadOnlyList<TaskSpec> Tasks() =>
    [
        Writing("T1", "ejecutor", "src/Calc/") with { Covers = [Rid("R1")] },
        Task("I1", WorkTaskKind.Integration).After("T1") with { VerificationSteps = ["test"] },
        Task("R1", WorkTaskKind.Review, "juez").After("I1"),
    ];

    /// <summary>A writer behind a review, with no integration: T1 → R1 (review) → T2.</summary>
    public static IReadOnlyList<TaskSpec> WriterBehindReview() =>
    [
        Writing("T1", "ejecutor", "src/Calc/") with { Covers = [Rid("R1")] },
        Task("R1", WorkTaskKind.Review, "juez").After("T1"),
        Writing("T2", "ejecutor2", "src/Other/").After("R1"),
    ];

    public static PlanDefinition Plan(IReadOnlyList<TaskSpec>? tasks = null) =>
        PlanDefinition.Create("Añadir Calc.Multiply", tasks ?? Tasks(), [], PlanSource.OrchestratorAgent, Aid("orquestador"));

    public static PlanContext Context(IReadOnlyList<AgentDefinition>? roster = null) =>
        new(
            FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Mandatory, "Add Calc.Multiply", Crit("c1"))], T0),
            roster ?? Roster(),
            new HashSet<string>(StringComparer.Ordinal) { "build", "test" },
            new HashSet<string>(StringComparer.Ordinal),
            new PlanLimits(),
            new HashSet<string>(StringComparer.Ordinal) { "test" });

    public static TaskContextFacts Facts(string task, long preamble = 1_000, long window = 200_000, int mandatory = 500, int budget = 24_000, bool memory = true, bool brief = false, bool upstream = false) =>
        new(Tid(task), preamble, MetricValue<long>.Exact(window, "test"), mandatory, budget, memory, brief, upstream);

    public static RepositoryNode Repository() =>
        new("C:\\repo", "main", "a1b2c3d", [], false, "dotnet", ["src/", "tests/", "docs/"]);

    /// <summary>The clean draft, with the team of <paramref name="roster"/> and the plan of <paramref name="tasks"/> (null: no plan).</summary>
    public static MontageDraft Draft(IReadOnlyList<AgentDefinition>? roster = null, IReadOnlyList<TaskSpec>? tasks = null, bool withPlan = true) =>
        Draft(roster ?? Roster(), withPlan ? Plan(tasks) : null);

    public static MontageDraft Draft(IReadOnlyList<AgentDefinition> roster, PlanDefinition? plan) =>
        new(
            7,
            new TeamGraph([.. roster.Select(a => new AgentNode(a))], Repository(), []),
            plan,
            Context(roster),
            new MontageRunFacts(3, 12, new Dictionary<ProviderId, int>()),
            plan is null ? [] : [.. plan.Tasks.Where(t => t.Kind == WorkTaskKind.AgentWork).Select(t => Facts(t.Id.Value))],
            [],
            OrderNotes.None,
            Catalog(),
            MontageLearnedFacts.None,
            new ContextHealthThresholds())
        {
            Steps = Steps(),
        };
}
