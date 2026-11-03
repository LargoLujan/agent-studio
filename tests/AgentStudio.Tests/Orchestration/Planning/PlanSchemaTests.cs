using System.Text;
using System.Text.Json;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Planning;

public sealed class PlanSchemaTests
{
    private static JsonElement TaskProperty(string schema, string name)
    {
        var root = JsonDocument.Parse(schema).RootElement;
        return root.GetProperty("properties").GetProperty("tasks").GetProperty("items").GetProperty("properties").GetProperty(name);
    }

    private static IReadOnlyList<string> Enum(JsonElement schema) =>
        [.. schema.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!)];

    private static JsonElement NonNullBranch(JsonElement nullable)
    {
        var branches = nullable.GetProperty("anyOf").EnumerateArray().ToList();
        Assert.Equal(2, branches.Count);
        Assert.Contains(branches, b => b.GetProperty("type").GetString() == "null");
        return branches.Single(b => b.GetProperty("type").GetString() != "null");
    }

    [Fact]
    public void The_schema_for_the_F3_8_world_is_portable_to_strict_mode_and_fits_inline()
    {
        var schema = PlanSchema.Build(Context());

        Assert.Empty(StrictSchemaChecker.Problems(schema));
        Assert.True(PlanSchema.Fits(schema));
        Assert.True(Encoding.UTF8.GetByteCount(schema) <= 16 * 1024);
        Assert.DoesNotContain('\n', schema);
    }

    [Fact]
    public void The_size_limit_is_the_inline_limit_of_the_Claude_adapter()
    {
        Assert.Equal(ClaudeCodeArguments.MaxJsonSchemaBytes, PlanSchema.MaxBytes);
    }

    [Fact]
    public void Agents_offered_are_everyone_in_the_roster_but_the_orchestrator()
    {
        var schema = PlanSchema.Build(Context());

        var agents = Enum(NonNullBranch(TaskProperty(schema, "agent")));

        Assert.Equal(["ejecutor", "ejecutor2", "ejecutorCodex", "juez"], agents);
    }

    [Fact]
    public void Requirements_steps_and_tools_are_enums_of_the_frozen_set_and_the_profile()
    {
        var schema = PlanSchema.Build(Context());

        Assert.Equal(["RQ-01", "RQ-02"], Enum(TaskProperty(schema, "covers").GetProperty("items")));
        Assert.Equal(["build", "lint", "test"], Enum(TaskProperty(schema, "verificationSteps").GetProperty("items")));
        Assert.Equal(["build", "lint", "test"], Enum(NonNullBranch(TaskProperty(schema, "toolId"))));
    }

    [Fact]
    public void Debate_nodes_are_offered_only_when_the_limits_allow_them()
    {
        var phase3 = Enum(TaskProperty(PlanSchema.Build(Context()), "kind"));
        var phase5 = Enum(TaskProperty(PlanSchema.Build(Context(new PlanLimits(DebateNodesAvailable: true))), "kind"));

        Assert.DoesNotContain("Debate", phase3);
        Assert.Equal(["AgentWork", "Condition", "ToolRun", "Review", "HumanApproval", "Integration"], phase3);
        Assert.Contains("Debate", phase5);
    }

    [Fact]
    public void Optional_values_are_nullable_and_every_task_field_is_required()
    {
        var schema = PlanSchema.Build(Context());
        var task = JsonDocument.Parse(schema).RootElement.GetProperty("properties").GetProperty("tasks").GetProperty("items");

        var required = task.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(16, required.Count);
        foreach (var nullable in new[] { "agent", "condition", "toolId", "timeoutMinutes" })
        {
            NonNullBranch(TaskProperty(schema, nullable));
        }

        var predicate = NonNullBranch(TaskProperty(schema, "condition"));
        Assert.Equal(["TaskSucceeded", "TestsPassed", "OutputEquals", "PathsChanged"], Enum(predicate.GetProperty("properties").GetProperty("type")));
        var branch = NonNullBranch(TaskProperty(schema, "dependsOn").GetProperty("items").GetProperty("properties").GetProperty("branch"));
        Assert.Equal(["yes", "no"], Enum(branch));
    }

    [Fact]
    public void A_world_at_generous_limits_still_fits()
    {
        var roster = Enumerable.Range(1, 40)
            .Select(i => Agent($"ejecutor-{i:D2}", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic))
            .Append(Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic))
            .ToList();
        var requirements = FrozenRequirementSet.Freeze(
            Enumerable.Range(1, 80).Select(i => new Requirement(new RequirementId($"RQ-{i:D3}"), "statement", RequirementPriority.Desirable, [])),
            Start);
        var steps = Enumerable.Range(1, 20).Select(i => $"step-{i:D2}").ToHashSet(StringComparer.Ordinal);
        var context = new PlanContext(requirements, roster, steps, steps, new PlanLimits());

        var schema = PlanSchema.Build(context);

        Assert.Empty(StrictSchemaChecker.Problems(schema));
        Assert.True(PlanSchema.Fits(schema), $"{Encoding.UTF8.GetByteCount(schema)} bytes");
    }

    [Fact]
    public void A_world_too_large_for_the_inline_limit_does_not_fit()
    {
        var requirements = FrozenRequirementSet.Freeze(
            Enumerable.Range(1, 2000).Select(i => new Requirement(new RequirementId($"RQ-{i:D5}"), "statement", RequirementPriority.Desirable, [])),
            Start);
        var context = Context() with { Requirements = requirements };

        var schema = PlanSchema.Build(context);

        Assert.False(PlanSchema.Fits(schema));
    }

    [Fact]
    public void A_profile_without_steps_still_gives_a_portable_schema()
    {
        var context = Context() with { VerificationSteps = new HashSet<string>(), ToolIds = new HashSet<string>() };

        var schema = PlanSchema.Build(context);

        Assert.Empty(StrictSchemaChecker.Problems(schema));
        Assert.Equal("null", TaskProperty(schema, "toolId").GetProperty("type").GetString());
    }

    [Fact]
    public void Tasks_with_no_agent_to_assign_still_give_a_portable_schema()
    {
        var context = Context(roster: [Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic)]);

        var schema = PlanSchema.Build(context);

        Assert.Empty(StrictSchemaChecker.Problems(schema));
        Assert.Equal("null", TaskProperty(schema, "agent").GetProperty("type").GetString());
    }

    [Fact]
    public void The_checker_rejects_what_strict_mode_rejects()
    {
        // A self-test of the checker: without it, a checker that accepts everything would make the tests above vacuous.
        Assert.NotEmpty(StrictSchemaChecker.Problems("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}"""));
        Assert.NotEmpty(StrictSchemaChecker.Problems("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}},"required":["a"],"additionalProperties":false}"""));
        Assert.NotEmpty(StrictSchemaChecker.Problems("""{"type":"object","properties":{"a":{"type":"string","pattern":"x"}},"required":["a"],"additionalProperties":false}"""));
        Assert.NotEmpty(StrictSchemaChecker.Problems("""{"anyOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false},{"type":"null"}]}"""));
        Assert.NotEmpty(StrictSchemaChecker.Problems("""{"type":"object","properties":{"a":{"type":"string","enum":[]}},"required":["a"],"additionalProperties":false}"""));
        Assert.Empty(StrictSchemaChecker.Problems("""{"type":"object","properties":{"a":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["a"],"additionalProperties":false}"""));
    }
}
