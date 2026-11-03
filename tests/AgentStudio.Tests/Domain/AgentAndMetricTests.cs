using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class AgentDefinitionTests
{
    private static AgentDefinition Agent(
        AgentRole role = AgentRole.Executor,
        string name = "Agent",
        ExecutionBudget? budget = null,
        int maxRounds = 3,
        params PathGrant[] grants) =>
        new(
            Aid("a1"),
            name,
            role,
            Model(VendorId.Anthropic),
            "instructions",
            ["obj"],
            [],
            grants,
            budget ?? new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
            maxRounds,
            MemoryPolicy.None,
            new ReviewPolicy(false, false));

    [Fact]
    public void A_well_formed_definition_has_no_problems()
    {
        Assert.Empty(Agent(grants: new PathGrant(@"C:\repo", FileAccessLevel.ReadWrite)).Validate());
        Agent().EnsureValid();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void The_name_is_required(string name)
    {
        Assert.NotEmpty(Agent(name: name).Validate());
        Violation(() => Agent(name: name).EnsureValid());
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    public void The_budget_must_be_positive(int turns, int minutes)
    {
        var agent = Agent(budget: new ExecutionBudget(turns, TimeSpan.FromMinutes(minutes)));

        Assert.NotEmpty(agent.Validate());
        Violation(agent.EnsureValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void MaxRounds_must_be_positive(int rounds)
    {
        Assert.NotEmpty(Agent(maxRounds: rounds).Validate());
    }

    [Theory]
    [InlineData(AgentRole.Judge)]
    [InlineData(AgentRole.Auditor)]
    public void Judges_and_auditors_cannot_have_write_access(AgentRole role)
    {
        var writer = Agent(role, grants: [new PathGrant(@"C:\repo", FileAccessLevel.Read), new PathGrant(@"C:\out", FileAccessLevel.ReadWrite)]);
        var reader = Agent(role, grants: new PathGrant(@"C:\repo", FileAccessLevel.Read));

        Assert.NotEmpty(writer.Validate());
        Violation(writer.EnsureValid);
        Assert.Empty(reader.Validate());
    }

    [Theory]
    [InlineData(AgentRole.Executor)]
    [InlineData(AgentRole.Orchestrator)]
    [InlineData(AgentRole.Tester)]
    public void Other_roles_may_have_write_access(AgentRole role)
    {
        Assert.Empty(Agent(role, grants: new PathGrant(@"C:\repo", FileAccessLevel.ReadWrite)).Validate());
    }

    [Fact]
    public void Every_problem_is_reported()
    {
        var agent = Agent(AgentRole.Judge, "", new ExecutionBudget(0, TimeSpan.Zero), 0, new PathGrant(@"C:\r", FileAccessLevel.ReadWrite));

        Assert.Equal(4, agent.Validate().Count);
    }
}

public sealed class MetricValueTests
{
    [Fact]
    public void Exact_Estimated_and_Unavailable_carry_their_provenance_and_source()
    {
        var exact = MetricValue<long>.Exact(5, "provider usage");
        var estimated = MetricValue<long>.Estimated(5, "4 chars/token");
        var unavailable = MetricValue<long>.Unavailable("not exposed");

        Assert.Equal((MetricProvenance.Exact, 5L, true), (exact.Provenance, exact.Value, exact.HasValue));
        Assert.Equal(MetricProvenance.Estimated, estimated.Provenance);
        Assert.Equal(MetricProvenance.Unavailable, unavailable.Provenance);
        Assert.Null(unavailable.Value);
        Assert.False(unavailable.HasValue);
        Assert.Equal("not exposed", unavailable.Source);
    }

    [Fact]
    public void A_source_is_always_required()
    {
        Assert.ThrowsAny<ArgumentException>(() => MetricValue<long>.Exact(1, " "));
        Assert.ThrowsAny<ArgumentException>(() => MetricValue<long>.Estimated(1, ""));
        Assert.ThrowsAny<ArgumentException>(() => MetricValue<long>.Unavailable(" "));
    }

    [Fact]
    public void An_unavailable_value_is_not_zero()
    {
        Assert.NotEqual(MetricValue<long>.Exact(0, "x").Value, MetricValue<long>.Unavailable("x").Value);
    }

    [Theory]
    [InlineData(MetricProvenance.Exact, MetricProvenance.Exact, MetricProvenance.Exact)]
    [InlineData(MetricProvenance.Exact, MetricProvenance.Estimated, MetricProvenance.Estimated)]
    [InlineData(MetricProvenance.Estimated, MetricProvenance.Exact, MetricProvenance.Estimated)]
    [InlineData(MetricProvenance.Estimated, MetricProvenance.Estimated, MetricProvenance.Estimated)]
    [InlineData(MetricProvenance.Exact, MetricProvenance.Unavailable, MetricProvenance.Unavailable)]
    [InlineData(MetricProvenance.Unavailable, MetricProvenance.Estimated, MetricProvenance.Unavailable)]
    [InlineData(MetricProvenance.Unavailable, MetricProvenance.Unavailable, MetricProvenance.Unavailable)]
    public void Combine_returns_the_weakest_provenance(MetricProvenance a, MetricProvenance b, MetricProvenance expected)
    {
        Assert.Equal(expected, MetricValue<long>.Combine(a, b));
        Assert.Equal(expected, MetricValue<long>.Combine(b, a));
    }

    [Fact]
    public void Combine_of_many_inputs_and_of_none()
    {
        Assert.Equal(MetricProvenance.Unavailable, MetricValue<long>.Combine(MetricProvenance.Exact, MetricProvenance.Estimated, MetricProvenance.Unavailable, MetricProvenance.Exact));
        Assert.Equal(MetricProvenance.Exact, MetricValue<long>.Combine(MetricProvenance.Exact));
        Assert.Equal(MetricProvenance.Unavailable, MetricValue<long>.Combine());
    }
}
