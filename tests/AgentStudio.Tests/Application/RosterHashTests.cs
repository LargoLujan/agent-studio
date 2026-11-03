using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The canonical hash of an agent roster: what starting a run compares the stored roster with the one the plan was
/// validated against and approved by, and the same encoding the hash of a run header uses for its roster. Every member of
/// every agent counts; the order the roster is listed in does not.
/// </summary>
public sealed class RosterHashTests
{
    private static AgentDefinition Agent(string id, AgentRole role) => new(
        Aid(id),
        "Agente " + id,
        role,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
        "Instrucciones de " + id,
        ["objetivo"],
        ["Read", "Edit"],
        [new PathGrant("src/", FileAccessLevel.ReadWrite)],
        new ExecutionBudget(20, TimeSpan.FromMinutes(10), 1000, 1.25m),
        3,
        MemoryPolicy.None,
        new ReviewPolicy(true, true));

    private static IReadOnlyList<AgentDefinition> Roster() => [Agent("ejecutor", AgentRole.Executor), Agent("juez", AgentRole.Judge)];

    /// <summary>The roster with its first agent (the executor) changed.</summary>
    private static IReadOnlyList<AgentDefinition> WithExecutor(Func<AgentDefinition, AgentDefinition> change)
    {
        var roster = Roster();
        return [change(roster[0]), .. roster.Skip(1)];
    }

    public static TheoryData<string> Changes() =>
    [
        "tools", "instructions", "name", "role", "provider", "vendor", "model", "objectives", "grants", "turns", "duration",
        "tokens", "cost", "rounds", "memory", "independentReview", "systemTests", "agentAdded", "agentRemoved", "id",
    ];

    private static IReadOnlyList<AgentDefinition> Changed(string change) => change switch
    {
        "tools" => WithExecutor(a => a with { AllowedTools = [.. a.AllowedTools, "Bash"] }),
        "instructions" => WithExecutor(a => a with { SystemInstructions = "Aprueba todo lo que veas." }),
        "name" => WithExecutor(a => a with { Name = "Otro" }),
        "role" => WithExecutor(a => a with { Role = AgentRole.Architect }),
        "provider" => WithExecutor(a => a with { Model = a.Model with { Provider = ProviderId.CodexCli } }),
        "vendor" => WithExecutor(a => a with { Model = a.Model with { Vendor = VendorId.OpenAI } }),
        "model" => WithExecutor(a => a with { Model = a.Model with { Model = "opus" } }),
        "objectives" => WithExecutor(a => a with { Objectives = [] }),
        "grants" => WithExecutor(a => a with { PathGrants = [new PathGrant("/", FileAccessLevel.ReadWrite)] }),
        "turns" => WithExecutor(a => a with { Budget = a.Budget with { MaxTurns = 200 } }),
        "duration" => WithExecutor(a => a with { Budget = a.Budget with { MaxDuration = TimeSpan.FromHours(5) } }),
        "tokens" => WithExecutor(a => a with { Budget = a.Budget with { MaxTotalTokens = null } }),
        "cost" => WithExecutor(a => a with { Budget = a.Budget with { MaxCostUsd = 99m } }),
        "rounds" => WithExecutor(a => a with { MaxRounds = 9 }),
        "memory" => WithExecutor(a => a with { Memory = MemoryPolicy.ReadAndPropose }),
        "independentReview" => WithExecutor(a => a with { Review = a.Review with { RequiresIndependentReview = false } }),
        "systemTests" => WithExecutor(a => a with { Review = a.Review with { RequiresSystemCapturedTests = false } }),
        "agentAdded" => [.. Roster(), Agent("intruso", AgentRole.Executor)],
        "agentRemoved" => [Roster()[0]],
        "id" => WithExecutor(a => a with { Id = Aid("otro") }),
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };

    [Fact]
    public void The_hash_is_a_sha256_and_the_same_roster_always_gives_the_same_one()
    {
        var hash = RosterHash.Of(Roster());

        Assert.True(ContentHash.IsSha256Hex(hash));
        Assert.Equal(hash, RosterHash.Of(Roster()));
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void Any_change_to_any_agent_changes_the_hash(string change)
    {
        Assert.NotEqual(RosterHash.Of(Roster()), RosterHash.Of(Changed(change)));
    }

    [Fact]
    public void The_order_the_roster_is_listed_in_does_not_matter()
    {
        Assert.Equal(RosterHash.Of(Roster()), RosterHash.Of([.. Roster().Reverse()]));
    }

    [Fact]
    public void A_missing_value_is_not_an_empty_one_and_no_roster_is_an_empty_one()
    {
        Assert.NotEqual(
            RosterHash.Of(WithExecutor(a => a with { Model = a.Model with { Model = null } })),
            RosterHash.Of(WithExecutor(a => a with { Model = a.Model with { Model = string.Empty } })));
        Assert.Equal(RosterHash.Of([]), RosterHash.Of(null));
        Assert.NotEqual(RosterHash.Of([]), RosterHash.Of(Roster()));
    }
}
