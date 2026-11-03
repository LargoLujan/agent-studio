using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The roster a project gets when it has none (decision 7 of docs/13, ADR-0014): Claude Code plans and executes, Codex
/// judges, and the plan validator accepts a judge that shares no vendor with whoever produced the work.
/// </summary>
public sealed class DefaultAgentRosterTests
{
    private static readonly HashSet<string> Steps = new(StringComparer.Ordinal) { "build", "test" };

    private static FrozenRequirementSet Requirements() =>
        FrozenRequirementSet.Freeze(
            [new Requirement(new RequirementId("RQ-01"), "Añadir Calc.Multiply(a, b)", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Multiply(2, 3) devuelve 6", VerificationMethod.AutomatedTest)])],
            T0);

    private static TaskSpec Task(string id, WorkTaskKind kind, string? agent, WorkspaceMode workspace, string[] dependsOn, string[] steps, string[] scope, string[] covers) =>
        new(
            new TaskId(id),
            "Tarea " + id,
            kind,
            agent is null ? null : new AgentId(agent),
            kind == WorkTaskKind.AgentWork ? "Añadir Calc.Multiply(a, b) con su prueba" : string.Empty,
            string.Empty,
            [.. dependsOn.Select(d => new DependencySpec(new TaskId(d)))],
            JoinPolicy.AllCompleted,
            [.. covers.Select(c => new RequirementId(c))],
            scope,
            steps,
            null,
            null,
            kind == WorkTaskKind.AgentWork ? 1 : 0,
            null,
            workspace);

    /// <summary>The F3-8 plan: the executor writes, the system integrates and verifies, the judge reviews.</summary>
    private static PlanDefinition Plan() =>
        PlanDefinition.Create(
            "Añadir Calc.Multiply",
            [
                Task("T1", WorkTaskKind.AgentWork, "ejecutor", WorkspaceMode.Write, [], ["build", "test"], ["src/Calc/", "tests/Calc.Tests/"], ["RQ-01"]),
                Task("T2", WorkTaskKind.Integration, null, WorkspaceMode.None, ["T1"], ["build", "test"], [], []),
                Task("T3", WorkTaskKind.Review, "juez", WorkspaceMode.ReadOnly, ["T2"], [], [], []),
            ],
            [],
            PlanSource.OrchestratorAgent,
            DefaultAgentRoster.Orchestrator);

    private static IReadOnlyList<PlanViolation> Validate(IReadOnlyList<AgentDefinition> roster) =>
        PlanValidator.Validate(Plan(), new PlanContext(Requirements(), roster, Steps, Steps, new PlanLimits()));

    [Fact]
    public void Claude_plans_and_executes_and_codex_judges()
    {
        var roster = DefaultAgentRoster.Create();

        Assert.Equal(
            [
                ("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic),
                ("ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic),
                ("juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI),
                ("arquitecto", AgentRole.Architect, ProviderId.ClaudeCode, VendorId.Anthropic),
                ("critico", AgentRole.Critic, ProviderId.CodexCli, VendorId.OpenAI),
                ("juez-claude", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic),
                ("juez-revision", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI),
            ],
            roster.Select(a => (a.Id.Value, a.Role, a.Model.Provider, a.Model.Vendor)));
        Assert.Equal(["Read", "Grep", "Glob"], roster[0].AllowedTools);
        Assert.DoesNotContain(roster.SelectMany(a => a.AllowedTools), t => t is "Bash" or "WebFetch" or "WebSearch");
        Assert.All(roster, a => Assert.Empty(a.Validate()));
        Assert.All(roster, a => Assert.DoesNotContain(a.PathGrants, g => g.Access == FileAccessLevel.ReadWrite));
    }

    [Fact]
    public void The_debates_get_two_read_only_participants_and_a_judge_of_each_vendor_the_revision_one_with_another_model()
    {
        var roster = DefaultAgentRoster.Create().ToDictionary(a => a.Id.Value);

        // docs/15 D-09: participants read only, no memory, up to the 4 rounds of a debate; judges with no tools.
        foreach (var participant in new[] { "arquitecto", "critico" })
        {
            Assert.Equal(["Read", "Grep", "Glob"], roster[participant].AllowedTools);
            Assert.Equal(MemoryPolicy.None, roster[participant].Memory);
            Assert.Equal(4, roster[participant].MaxRounds);
            Assert.True(roster[participant].Budget.MaxTurns <= 12);
        }

        foreach (var judge in new[] { "juez", "juez-claude", "juez-revision" })
        {
            Assert.Empty(roster[judge].AllowedTools);
            Assert.Equal(MemoryPolicy.None, roster[judge].Memory);
        }

        // D-39: the revision of a verdict of «juez» is judged by Codex with another model.
        Assert.Null(roster["juez"].Model.Model);
        Assert.Equal(DefaultAgentRoster.RevisionJudgeModel, roster["juez-revision"].Model.Model);
    }

    [Fact]
    public void A_plan_on_the_default_roster_is_valid_with_an_independent_judge()
    {
        Assert.Empty(Validate(DefaultAgentRoster.Create()));
    }

    [Fact]
    public void A_judge_moved_to_the_vendor_of_the_orchestrator_and_the_executor_is_rejected()
    {
        var roster = DefaultAgentRoster.Create()
            .Select(a => a.Role == AgentRole.Judge ? a with { Model = new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic) } : a)
            .ToList();

        var violation = Assert.Single(Validate(roster));

        Assert.Equal(PlanViolationCode.JudgeVendorNotIndependent, violation.Code);
        Assert.Equal(new TaskId("T3"), violation.Task);
    }
}
