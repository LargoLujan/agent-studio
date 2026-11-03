using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Agents;

public enum AgentRole
{
    Orchestrator,
    Executor,
    Architect,
    Tester,
    Documenter,
    Critic,
    Judge,
    Auditor,
}

public enum FileAccessLevel
{
    None = 0,
    Read = 1,
    ReadWrite = 2,
}

/// <summary>Access granted to an agent on a directory tree.</summary>
public sealed record PathGrant(string Root, FileAccessLevel Access);

/// <summary>Hard limits for one agent run. Exceeding any of them stops the run.</summary>
public sealed record ExecutionBudget(int MaxTurns, TimeSpan MaxDuration, long? MaxTotalTokens = null, decimal? MaxCostUsd = null);

public enum MemoryPolicy
{
    /// <summary>The agent gets no project memory in its context.</summary>
    None,

    /// <summary>The agent receives selected memory entries but cannot write new ones.</summary>
    ReadSelected,

    /// <summary>
    /// The agent receives selected entries and may propose new ones (stored after review). Not available: no channel lets
    /// an agent propose memory, and Phase 5 does not open one either (docs/15 §1 and D-46), so a definition with it is
    /// invalid (<c>agent.memory-proposals-not-available</c>).
    /// </summary>
    ReadAndPropose,
}

public sealed record ReviewPolicy(bool RequiresIndependentReview, bool RequiresSystemCapturedTests);

/// <summary>Configuration of one agent (section 6 of the master prompt).</summary>
public sealed record AgentDefinition(
    AgentId Id,
    string Name,
    AgentRole Role,
    ModelBinding Model,
    string SystemInstructions,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<PathGrant> PathGrants,
    ExecutionBudget Budget,
    int MaxRounds,
    MemoryPolicy Memory,
    ReviewPolicy Review)
{
    /// <summary>Returns every rule this definition breaks; an empty list means it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("agent.name-required");
        }

        if (Budget.MaxTurns <= 0 || Budget.MaxDuration <= TimeSpan.Zero)
        {
            problems.Add("agent.budget-must-be-positive");
        }

        if (MaxRounds <= 0)
        {
            problems.Add("agent.max-rounds-must-be-positive");
        }

        // Judges and auditors are read-only by construction (master prompt, section 8).
        if (Role is AgentRole.Judge or AgentRole.Auditor && PathGrants.Any(g => g.Access == FileAccessLevel.ReadWrite))
        {
            problems.Add("agent.judge-must-be-read-only");
        }

        // A verdict rests on the dossier alone: judges and auditors never receive project memory (docs/14 D-23).
        if (Role is AgentRole.Judge or AgentRole.Auditor && Memory != MemoryPolicy.None)
        {
            problems.Add("agent.judge-memory-none");
        }

        // No channel lets an agent propose memory (docs/14 D-25; Phase 5 opens none either, docs/15 D-46): the policy is
        // refused, not simulated.
        if (Memory == MemoryPolicy.ReadAndPropose)
        {
            problems.Add("agent.memory-proposals-not-available");
        }

        return problems;
    }

    public void EnsureValid()
    {
        var problems = Validate();
        if (problems.Count > 0)
        {
            throw new DomainRuleViolationException(problems[0], $"Agent '{Id}' is invalid: {string.Join(", ", problems)}.");
        }
    }
}
