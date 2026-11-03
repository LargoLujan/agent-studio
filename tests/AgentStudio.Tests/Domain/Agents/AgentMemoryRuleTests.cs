using AgentStudio.Application.Judging;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Agents;

/// <summary>
/// docs/14 D-23 and D-25: the judge and the auditor never receive project memory, so a verdict rests on the dossier
/// alone; and no agent can propose memory until Phase 5, because there is no channel for it and nothing is simulated.
/// </summary>
public sealed class AgentMemoryRuleTests
{
    private const string JudgeMemoryNone = "agent.judge-memory-none";
    private const string ProposalsNotAvailable = "agent.memory-proposals-not-available";

    private static AgentDefinition Agent(AgentRole role, MemoryPolicy memory) =>
        new(
            Aid("a1"),
            "Agente",
            role,
            new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI),
            "instructions",
            ["obj"],
            ["Read", "Grep", "Glob"],
            [],
            new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
            1,
            memory,
            new ReviewPolicy(false, false));

    [Theory]
    [InlineData(AgentRole.Judge)]
    [InlineData(AgentRole.Auditor)]
    public void A_judge_or_an_auditor_that_would_read_memory_is_invalid(AgentRole role)
    {
        var agent = Agent(role, MemoryPolicy.ReadSelected);

        Assert.Equal([JudgeMemoryNone], agent.Validate());
        var violation = Assert.Throws<DomainRuleViolationException>(agent.EnsureValid);
        Assert.Equal(JudgeMemoryNone, violation.Rule);
    }

    [Theory]
    [InlineData(AgentRole.Judge)]
    [InlineData(AgentRole.Auditor)]
    public void A_judge_or_an_auditor_without_memory_is_valid(AgentRole role)
    {
        Assert.Empty(Agent(role, MemoryPolicy.None).Validate());
    }

    [Theory]
    [InlineData(AgentRole.Orchestrator)]
    [InlineData(AgentRole.Executor)]
    [InlineData(AgentRole.Architect)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Documenter)]
    [InlineData(AgentRole.Critic)]
    public void Agents_that_produce_the_work_may_read_selected_memory(AgentRole role)
    {
        Assert.Empty(Agent(role, MemoryPolicy.ReadSelected).Validate());
        Assert.Empty(Agent(role, MemoryPolicy.None).Validate());
    }

    [Theory]
    [InlineData(AgentRole.Orchestrator)]
    [InlineData(AgentRole.Executor)]
    [InlineData(AgentRole.Tester)]
    public void No_agent_can_propose_memory_until_phase_5(AgentRole role)
    {
        var agent = Agent(role, MemoryPolicy.ReadAndPropose);

        Assert.Equal([ProposalsNotAvailable], agent.Validate());
        Assert.Equal(ProposalsNotAvailable, Assert.Throws<DomainRuleViolationException>(agent.EnsureValid).Rule);
    }

    [Fact]
    public void A_judge_that_would_propose_memory_breaks_both_rules()
    {
        Assert.Equal([JudgeMemoryNone, ProposalsNotAvailable], Agent(AgentRole.Judge, MemoryPolicy.ReadAndPropose).Validate());
    }

    /// <summary>The judge is never launched with memory: the judge itself refuses such a definition before any run.</summary>
    [Fact]
    public void A_judge_with_memory_is_refused_before_it_could_run()
    {
        var judge = Agent(AgentRole.Judge, MemoryPolicy.ReadSelected);
        var provider = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (_, _) => throw new InvalidOperationException("the judge must never be launched"));

        var refused = Assert.Throws<ArgumentException>(() =>
            new AgentJudge(provider, new JudgeLaunch(judge, "C:\\wt\\_integration", new DossierFiles("C:\\dossiers\\p1\\r\\0", "p1", "r", [], [])), new FakeTimeProvider(T0)));

        Assert.Contains(JudgeMemoryNone, refused.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
    }
}
