using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

// The rules of the analyzer about the power of each agent (docs/17 D-22 to D-24): the level comes from the local catalog, and
// what happened from the verified log. A model the catalog does not classify, or with fewer than three references, says nothing.

/// <summary>
/// <see cref="FindingCodes.ModelOversized"/>: an agent of level «máximo» writes a task that only touches documentation or tests
/// (every entry of its scope, <see cref="DraftFacts.IsDocsOrTests"/>). Suggestion. Its fix gives the task to an agent that writes
/// at level «equilibrado» and whose folders hold its scope (of the same vendor first); none when there is no such agent.
/// Nodes: the task and its agent. Arguments: the task id, the agent id, its model.
/// </summary>
public sealed class ModelOversizedRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.ModelOversized];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Plan is not { } plan || draft.Catalog is not { } catalog)
        {
            return [];
        }

        var agents = DraftFacts.Agents(draft);
        var findings = new List<Finding>();
        foreach (var task in plan.Tasks.Where(t => DraftFacts.Writes(t) && t.WriteScope.Count > 0 && t.WriteScope.All(DraftFacts.IsDocsOrTests)))
        {
            if (task.Agent is not { } id || !agents.TryGetValue(id, out var agent) || DraftFacts.TierOf(catalog, agent) != ModelTier.Maximum)
            {
                continue;
            }

            var balanced = (draft.Context.Roster ?? [])
                .Where(a => a.Id != agent.Id && TeamRoles.Writes(a.Role) && DraftFacts.TierOf(catalog, a) == ModelTier.Balanced && AgentWriteArea.Outside(a, task.WriteScope).Count == 0)
                .OrderBy(a => a.Model.Vendor == agent.Model.Vendor ? 0 : 1)
                .FirstOrDefault();
            findings.Add(Finding.Of(
                FindingCodes.ModelOversized,
                FindingSeverity.Suggestion,
                [task.Id.Value, agent.Id.Value],
                balanced is null ? null : new ReassignTaskFix(task.Id, balanced.Id),
                task.Id.Value,
                agent.Id.Value,
                agent.Model.Model ?? string.Empty));
        }

        return findings;
    }
}

/// <summary>
/// <see cref="FindingCodes.ModelUndersized"/>: an agent of level «ligero» whose model had retries or rejections of the judge in
/// this project (known: at least three tasks). With a plan, only the agents that have a task in it. Suggestion. Its fix moves
/// the agent to the first model of level «equilibrado» of its CLI in the catalog. Nodes: the agent. Arguments: the agent id,
/// its model, the retries, the rejections.
/// </summary>
public sealed class ModelUndersizedRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.ModelUndersized];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Catalog is not { } catalog)
        {
            return [];
        }

        var inPlan = draft.Plan?.Tasks.Where(t => t.Kind is WorkTaskKind.AgentWork or WorkTaskKind.Review).Select(t => t.Agent).OfType<Common.AgentId>().ToHashSet();
        var findings = new List<Finding>();
        foreach (var agent in (draft.Context.Roster ?? []).DistinctBy(a => a.Id))
        {
            if ((inPlan is not null && !inPlan.Contains(agent.Id)) || catalog.Find(agent.Model.Provider, agent.Model.Model) is not { Tier: ModelTier.Light } entry)
            {
                continue;
            }

            var performance = (draft.Learned?.Models ?? []).FirstOrDefault(p => p.Provider == agent.Model.Provider && p.IsKnown && (entry.IsNamed(p.Model) || string.Equals(p.Model, agent.Model.Model, StringComparison.OrdinalIgnoreCase)));
            if (performance is null || (performance.Retries == 0 && performance.Rejections == 0))
            {
                continue;
            }

            var stronger = catalog.Entries.FirstOrDefault(e => e.Provider == agent.Model.Provider && e.Tier == ModelTier.Balanced);
            findings.Add(Finding.Of(
                FindingCodes.ModelUndersized,
                FindingSeverity.Suggestion,
                [agent.Id.Value],
                stronger is null ? null : new ChangeModelFix(agent.Id, stronger.Name),
                agent.Id.Value,
                agent.Model.Model ?? string.Empty,
                performance.Retries,
                performance.Rejections));
        }

        return findings;
    }
}
