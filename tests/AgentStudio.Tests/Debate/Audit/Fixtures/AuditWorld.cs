using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Debate.Audit.Fixtures;

/// <summary>
/// The world of the prepared runs the auditor examines, shaped like the F3-8 scenario: Claude orchestrates and executes
/// (the executor asks for system-captured tests and an independent review, like the roster a project gets by default) and
/// Codex judges. The default plan writes T1, integrates it in I1 and reviews it in R1.
/// </summary>
internal static class AuditWorld
{
    public const string Project = "6f1c2b0e-0000-4000-8000-0000000a0d17";

    public static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    public static readonly RunId Run = new("20261019-a0d170");

    public static readonly AgentId Orchestrator = new("orquestador");

    public static readonly AgentId Executor = new("ejecutor");

    public static readonly AgentId Judge = new("juez");

    public static readonly string BaseCommit = new('0', 40);

    public static readonly RequirementId Rq01 = new("RQ-01");

    public static readonly RequirementId Rq02 = new("RQ-02");

    public static readonly TestRunSummary Green = new(Passed: 12, Failed: 0, Skipped: 0, ExitCode: 0);

    public static readonly TestRunSummary Red = new(Passed: 10, Failed: 2, Skipped: 0, ExitCode: 1);

    public static TaskId Tid(string id) => new(id);

    public static AgentDefinition Agent(AgentId id, AgentRole role, ModelBinding model, IReadOnlyList<string> tools, ReviewPolicy? review = null, IReadOnlyList<PathGrant>? grants = null) =>
        new(
            id,
            id.Value,
            role,
            model,
            "Instrucciones de sistema de " + id.Value,
            [],
            tools,
            grants ?? [],
            new ExecutionBudget(20, TimeSpan.FromMinutes(10)),
            3,
            MemoryPolicy.None,
            review ?? new ReviewPolicy(false, false));

    public static readonly AgentId SecondExecutor = new("ejecutor2");

    public static IReadOnlyList<AgentDefinition> Roster() =>
    [
        Agent(Orchestrator, AgentRole.Orchestrator, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"), ["Read", "Grep", "Glob"]),
        Agent(Executor, AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"), ["Read", "Edit", "Write"], new ReviewPolicy(true, true)),
        Agent(Judge, AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI), ["Read", "Grep", "Glob"]),
    ];

    /// <summary>The roster whose executor has no review policy (a plan need not end in a review), with a second executor.</summary>
    public static IReadOnlyList<AgentDefinition> RosterWithoutReviewPolicy() =>
    [
        .. Roster().Select(a => a.Role == AgentRole.Executor ? a with { Review = new ReviewPolicy(false, false) } : a),
        Agent(SecondExecutor, AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet"), ["Read", "Edit", "Write"]),
    ];

    public static FrozenRequirementSet Requirements() =>
        FrozenRequirementSet.Freeze(
            [
                new Requirement(Rq01, "Añadir Calc.Multiply(a,b)", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Multiply(2,3) devuelve 6", VerificationMethod.AutomatedTest)]),
                new Requirement(Rq02, "Documentar la operación", RequirementPriority.Desirable, [new AcceptanceCriterion("c2", "README actualizado", VerificationMethod.HumanReview)]),
            ],
            T0);

    public static VerificationProfile Profile() =>
        new(
            "perfil de la auditoría",
            [
                new VerificationStep("build", "Compilar", "dotnet", ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(5)),
                new VerificationStep("test", "Pruebas", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.Trx, "results/test.trx", TimeSpan.FromMinutes(5)),
            ]);

    /// <summary>The profile of a project with no tests: a build is all its plans can ask for.</summary>
    public static VerificationProfile ProfileWithoutTests() =>
        new(
            "perfil sin pruebas",
            [new VerificationStep("build", "Compilar", "dotnet", ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(5))]);

    public static DependencySpec On(string prerequisite, string? branch = null) => new(Tid(prerequisite), branch);

    /// <summary>A task of the executor that writes <c>src/&lt;id&gt;/</c> and <c>tests/&lt;id&gt;.Tests/</c>, built and tested.</summary>
    public static TaskSpec Writer(string id, params DependencySpec[] after) =>
        new(
            Tid(id),
            "Tarea " + id,
            WorkTaskKind.AgentWork,
            Executor,
            "Objetivo de " + id + ": implementar Calc.Multiply",
            "Sigue TDD en " + id + ".",
            after,
            JoinPolicy.AllCompleted,
            [Rq01],
            [$"src/{id}/", $"tests/{id}.Tests/"],
            ["build", "test"],
            null,
            null,
            1,
            null,
            WorkspaceMode.Write);

    public static TaskSpec IntegrationTask(string id, params DependencySpec[] after) =>
        new(Tid(id), "Integración " + id, WorkTaskKind.Integration, null, string.Empty, string.Empty, after, JoinPolicy.AllCompleted, [], [], ["build", "test"], null, null, 0, null, WorkspaceMode.None);

    public static TaskSpec Review(string id, params DependencySpec[] after) =>
        new(Tid(id), "Revisión " + id, WorkTaskKind.Review, Judge, "Revisar la ejecución", "Revisa todo.", after, JoinPolicy.AllCompleted, [], [], [], null, null, 0, null, WorkspaceMode.ReadOnly);

    public static TaskSpec Condition(string id, ConditionPredicate predicate, params DependencySpec[] after) =>
        new(Tid(id), "Condición " + id, WorkTaskKind.Condition, null, string.Empty, string.Empty, after, JoinPolicy.AllCompleted, [], [], [], predicate, null, 0, null, WorkspaceMode.None);

    public static PlanDefinition Plan(params TaskSpec[] tasks) =>
        PlanDefinition.Create("Añadir Multiply con pruebas", tasks, ["Multiply puede desbordar"], PlanSource.OrchestratorAgent, Orchestrator);

    /// <summary>The F3-8 plan: T1 writes and is tested, I1 integrates it, R1 reviews it.</summary>
    public static PlanDefinition F38Plan() => Plan(Writer("T1"), IntegrationTask("I1", On("T1")), Review("R1", On("I1")));

    /// <summary>A full git object id that depends only on its name, so every prepared run has the same commits.</summary>
    public static string CommitOf(string name) => ContentHash.Sha256Hex(name)[..40];
}
