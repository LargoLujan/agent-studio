using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

// The rules of the analyzer about what each task starts with (docs/17 D-13 and D-18), from the facts the application computed
// with the packer (TaskContextFacts). Only for tasks of the plan whose facts are given, in the order of the plan.

/// <summary>
/// <see cref="FindingCodes.PackTooSmall"/>: the mandatory requirements of a task do not fit in the context budget of its
/// agent, so the packer would fail closed and the task would not start (<c>ContextBudgetTooSmall</c>). Error. No fix: the
/// budget is not a field of the template (it is the one of the prompt of the executor), so the way out is to split the task or
/// to reduce its mandatory requirements; <see cref="RaiseContextBudgetFix"/> is not offered until it is one. Nodes: the task
/// and its agent. Arguments: the task id, the characters of its mandatory requirements, the budget in characters.
/// </summary>
public sealed class PackTooSmallRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.PackTooSmall];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return ContextFacts.Of(draft)
            .Where(x => x.Facts.MandatoryCharacters > x.Facts.BudgetCharacters)
            .Select(x => Finding.Of(FindingCodes.PackTooSmall, FindingSeverity.Error, x.Nodes, null, x.Task.Id.Value, x.Facts.MandatoryCharacters, x.Facts.BudgetCharacters))
            .ToList();
    }
}

/// <summary>
/// <see cref="FindingCodes.WindowTight"/>: what goes in before the agent starts already passes the warning threshold of the
/// window of its model (70 % by default). Only with the window known. Warning. Its fix takes the memory away from the agent
/// when it has some (<see cref="SetMemoryFix"/>); otherwise none (a model with a larger window or fewer skills are the
/// person's choice: the catalog keeps no windows). Nodes: the task and its agent. Arguments: the task id, the tokens before
/// starting, the tokens of the window, the share in whole percent.
/// </summary>
public sealed class WindowTightRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.WindowTight];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var findings = new List<Finding>();
        foreach (var (task, facts, agent, nodes) in ContextFacts.Of(draft))
        {
            if (facts.Window.Value is not { } window || window <= 0)
            {
                continue;
            }

            var share = (double)facts.PreambleTokens / window;
            if (share < draft.Thresholds.Warning)
            {
                continue;
            }

            Fix? fix = agent is { Memory: not MemoryPolicy.None } ? new SetMemoryFix(agent.Id, MemoryPolicy.None) : null;
            findings.Add(Finding.Of(FindingCodes.WindowTight, FindingSeverity.Warning, nodes, fix, task.Id.Value, facts.PreambleTokens, window, (int)Math.Round(share * 100, MidpointRounding.AwayFromZero)));
        }

        return findings;
    }
}

/// <summary>
/// <see cref="FindingCodes.ContextThin"/>: a task that writes and starts with nothing of the project: no memory, no brief and
/// nothing from earlier tasks. Suggestion. Its fix gives the agent the trusted memory (<see cref="SetMemoryFix"/> with
/// <see cref="MemoryPolicy.ReadSelected"/>) when it has none and is not a judge; otherwise the way out is to write the brief.
/// Nodes: the task and its agent. Arguments: the task id.
/// </summary>
public sealed class ContextThinRule : IMontageRule
{
    public IReadOnlyList<string> Codes { get; } = [FindingCodes.ContextThin];

    public IEnumerable<Finding> Check(MontageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return ContextFacts.Of(draft)
            .Where(x => DraftFacts.Writes(x.Task) && !x.Facts.HasMemory && !x.Facts.HasBrief && !x.Facts.HasUpstream)
            .Select(x => Finding.Of(
                FindingCodes.ContextThin,
                FindingSeverity.Suggestion,
                x.Nodes,
                x.Agent is { Memory: MemoryPolicy.None } agent && !TeamRoles.Judges(agent.Role) ? new SetMemoryFix(agent.Id, MemoryPolicy.ReadSelected) : null,
                x.Task.Id.Value))
            .ToList();
    }
}

/// <summary>The facts of context of each task of the plan that has them, with its agent and the nodes of a finding about it.</summary>
internal static class ContextFacts
{
    public static IReadOnlyList<(TaskSpec Task, TaskContextFacts Facts, AgentDefinition? Agent, IReadOnlyList<string> Nodes)> Of(MontageDraft draft)
    {
        if (draft.Plan is not { } plan)
        {
            return [];
        }

        var agents = DraftFacts.Agents(draft);
        var facts = (draft.TaskContexts ?? []).GroupBy(f => f.Task).ToDictionary(g => g.Key, g => g.First());
        var result = new List<(TaskSpec, TaskContextFacts, AgentDefinition?, IReadOnlyList<string>)>();
        foreach (var task in plan.Tasks.Where(t => t.Kind is WorkTaskKind.AgentWork or WorkTaskKind.Review))
        {
            if (!facts.TryGetValue(task.Id, out var fact))
            {
                continue;
            }

            var agent = task.Agent is { } id && agents.TryGetValue(id, out var found) ? found : null;
            result.Add((task, fact, agent, agent is null ? [task.Id.Value] : [task.Id.Value, agent.Id.Value]));
        }

        return result;
    }
}
