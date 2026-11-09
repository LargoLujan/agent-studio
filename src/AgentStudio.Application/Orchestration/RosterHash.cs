using System.Globalization;
using AgentStudio.Domain.Agents;
using HashFunctions = AgentStudio.Domain.Common.ContentHash;

namespace AgentStudio.Application.Orchestration;

/// <summary>
/// The canonical SHA-256 of an agent roster: every member of every agent (name, role, provider, vendor and model,
/// instructions, objectives, tools, path grants, budget, rounds, memory and review policy). It is the one encoding of a
/// roster: <see cref="RunHeaderHash"/> writes the roster of a run header with it, and starting a run compares by it the
/// roster the plan was validated against (<see cref="RunRequest.ValidatedRoster"/>) with the one the store holds.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The agents are taken in the order of their ids, so the order a store lists them in does not matter; two entries
/// with the same id stay in their order. No roster is an empty one.</item>
/// <item>Fields are length-prefixed (<see cref="HashFunctions.Canonical"/>); a value that may be missing is written as
/// <c>-</c> when missing and with a leading <c>=</c> otherwise, so a missing value is never an empty one.</item>
/// <item>Phase 13 (docs/17 D-25): the effort of an agent counts only when it is set, after all the agents, as its name and
/// its value (<see cref="Append"/>). A template without efforts hashes as it always did.</item>
/// </list>
/// </remarks>
public static class RosterHash
{
    private const string Version = "roster/1";

    public static string Of(IReadOnlyList<AgentDefinition>? roster)
    {
        var fields = new List<string?> { Version };
        Append(fields, roster);
        return HashFunctions.Sha256Hex(HashFunctions.Canonical([.. fields]));
    }

    /// <summary>
    /// Writes the fields of a roster: how many agents it has, then every member of each, in the order of their ids. Then, only
    /// for the agents that set one, the effort of Phase 13 (docs/17 D-25): <c>effort</c>, the id of the agent and the name of
    /// the effort, in the order of their ids. Each agent has a fixed number of fields, so where the agents end is known, and
    /// a roster without efforts is written exactly as before they existed: the hash of every template and every run header
    /// stored before does not change.
    /// </summary>
    internal static void Append(List<string?> fields, IReadOnlyList<AgentDefinition>? roster)
    {
        var agents = (roster ?? []).OrderBy(a => a.Id.Value, StringComparer.Ordinal).ToList();
        fields.Add(Number(agents.Count));
        foreach (var agent in agents)
        {
            AddAgent(fields, agent);
        }

        foreach (var agent in agents)
        {
            if (agent.Effort is { } effort)
            {
                fields.Add("effort");
                fields.Add(agent.Id.Value);
                fields.Add(effort.ToString());
            }
        }
    }

    private static void AddAgent(List<string?> fields, AgentDefinition agent)
    {
        fields.Add(agent.Id.Value);
        fields.Add(agent.Name);
        fields.Add(agent.Role.ToString());
        fields.Add(agent.Model.Provider.Value);
        fields.Add(agent.Model.Vendor.Value);
        fields.Add(Optional(agent.Model.Model));
        fields.Add(agent.SystemInstructions);
        AddList(fields, agent.Objectives);
        AddList(fields, agent.AllowedTools);
        var grants = agent.PathGrants ?? [];
        fields.Add(Number(grants.Count));
        foreach (var grant in grants)
        {
            fields.Add(grant.Root);
            fields.Add(grant.Access.ToString());
        }

        fields.Add(Number(agent.Budget.MaxTurns));
        fields.Add(Number(agent.Budget.MaxDuration.Ticks));
        fields.Add(Optional(agent.Budget.MaxTotalTokens?.ToString(CultureInfo.InvariantCulture)));
        fields.Add(Optional(agent.Budget.MaxCostUsd?.ToString(CultureInfo.InvariantCulture)));
        fields.Add(Number(agent.MaxRounds));
        fields.Add(agent.Memory.ToString());
        fields.Add(agent.Review.RequiresIndependentReview ? "independent-review" : "no-independent-review");
        fields.Add(agent.Review.RequiresSystemCapturedTests ? "system-tests" : "no-system-tests");
    }

    private static void AddList(List<string?> fields, IReadOnlyList<string>? values)
    {
        var list = values ?? [];
        fields.Add(Number(list.Count));
        fields.AddRange(list);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Optional(string? value) => value is null ? "-" : "=" + value;
}
