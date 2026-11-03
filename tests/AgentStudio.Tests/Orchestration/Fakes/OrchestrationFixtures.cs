using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>
/// The world of the engine tests, shaped like the F3-8 scenario: Claude (Anthropic) orchestrates and executes, Codex
/// (OpenAI) judges. <c>ejecutorCodex</c> lets a plan use two providers when it has no review. No agent has a review
/// policy (so a plan need not have verification steps or a review); the tests of the policy give one to an agent.
/// </summary>
internal static class OrchestrationFixtures
{
    public static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public static AgentDefinition Agent(string id, AgentRole role, ProviderId provider, VendorId vendor, IReadOnlyList<string>? tools = null, ReviewPolicy? review = null) =>
        new(
            new AgentId(id),
            id,
            role,
            new ModelBinding(provider, vendor, provider == ProviderId.ClaudeCode ? "haiku" : null),
            "Instrucciones de sistema de " + id,
            [],
            tools ?? [],
            [],
            new ExecutionBudget(20, TimeSpan.FromMinutes(10)),
            3,
            MemoryPolicy.None,
            review ?? new ReviewPolicy(false, false));

    public static IReadOnlyList<AgentDefinition> Roster() =>
    [
        Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob"]),
        Agent("ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write"]),
        Agent("ejecutor2", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write"]),
        Agent("ejecutorCodex", AgentRole.Executor, ProviderId.CodexCli, VendorId.OpenAI),
        Agent("juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI),
    ];

    public static FrozenRequirementSet Requirements() =>
        FrozenRequirementSet.Freeze(
            [
                new Requirement(
                    new RequirementId("RQ-01"),
                    "Añadir Calc.Multiply(a,b)",
                    RequirementPriority.Mandatory,
                    [new AcceptanceCriterion("c1", "Multiply(2,3) devuelve 6", VerificationMethod.AutomatedTest)]),
                new Requirement(
                    new RequirementId("RQ-02"),
                    "Documentar la operación",
                    RequirementPriority.Desirable,
                    [new AcceptanceCriterion("c2", "README actualizado", VerificationMethod.HumanReview)]),
            ],
            Start);

    public static VerificationProfile Profile() =>
        new(
            "perfil de prueba",
            [
                new VerificationStep("build", "Compilar", "dotnet", ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(5)),
                new VerificationStep("test", "Pruebas", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.Trx, "results/test.trx", TimeSpan.FromMinutes(5)),
                new VerificationStep("lint", "Estilo", "dotnet", ["format", "--verify-no-changes"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(5)),
            ]);

    public static PlanContext Context(PlanLimits? limits = null, IReadOnlyList<AgentDefinition>? roster = null) =>
        new(
            Requirements(),
            roster ?? Roster(),
            new HashSet<string>(Profile().Steps.Select(s => s.Id), StringComparer.Ordinal),
            new HashSet<string>(Profile().Steps.Select(s => s.Id), StringComparer.Ordinal),
            limits ?? new PlanLimits(),
            new HashSet<string>(Profile().Steps.Where(s => s.Kind == EvidenceKind.TestRun).Select(s => s.Id), StringComparer.Ordinal));

    public static TaskSpec Work(string id, string agent = "ejecutor", WorkspaceMode workspace = WorkspaceMode.Write, params string[] scope) =>
        new(
            new TaskId(id),
            "Tarea " + id,
            WorkTaskKind.AgentWork,
            new AgentId(agent),
            "Objetivo de " + id,
            "Instrucciones de " + id,
            [],
            JoinPolicy.AllCompleted,
            [new RequirementId("RQ-01")],
            workspace == WorkspaceMode.Write ? (scope.Length > 0 ? scope : [$"src/{id}/"]) : [],
            [],
            null,
            null,
            1,
            null,
            workspace);

    public static TaskSpec Node(string id, WorkTaskKind kind) =>
        new(
            new TaskId(id),
            "Nodo " + id,
            kind,
            kind == WorkTaskKind.Review ? new AgentId("juez") : null,
            string.Empty,
            string.Empty,
            [],
            JoinPolicy.AllCompleted,
            [],
            [],
            kind == WorkTaskKind.Integration ? ["build", "test"] : [], // The validator requires verification on integrations.
            null,
            kind == WorkTaskKind.ToolRun ? "lint" : null,
            kind is WorkTaskKind.Review or WorkTaskKind.HumanApproval ? 0 : 1,
            null,
            kind == WorkTaskKind.Review ? WorkspaceMode.ReadOnly : WorkspaceMode.None);

    public static DependencySpec On(string prerequisite, string? branch = null) => new(new TaskId(prerequisite), branch);

    public static TaskSpec After(this TaskSpec spec, params DependencySpec[] dependencies) => spec with { DependsOn = dependencies };

    public static TaskSpec Covering(this TaskSpec spec, params string[] requirements) =>
        spec with { Covers = [.. requirements.Select(r => new RequirementId(r))] };

    public static TaskSpec Verified(this TaskSpec spec, params string[] steps) => spec with { VerificationSteps = steps };

    public static PlanDefinition Plan(params TaskSpec[] tasks) =>
        PlanDefinition.Create("Plan de prueba", tasks, ["Riesgo de prueba"], PlanSource.OrchestratorAgent, new AgentId("orquestador"));
}
