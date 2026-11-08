using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

/// <summary>
/// What several rules of the analyzer read of a draft (docs/17 D-18): the agents by id, the tasks that write, the ancestors of
/// each task when the graph is sound, the level of each model in the catalog, and the dependency that keeps an order, decided
/// by <see cref="TaskGraph"/>. Pure: it reads nothing and changes nothing.
/// </summary>
internal static class DraftFacts
{
    /// <summary>The vendors a judge can be of, with the CLI that reaches each (ADR-0014). Gemini is parked (ADR-0009).</summary>
    public static IReadOnlyList<(ProviderId Provider, VendorId Vendor)> JudgeVendors { get; } =
    [
        (ProviderId.ClaudeCode, VendorId.Anthropic),
        (ProviderId.CodexCli, VendorId.OpenAI),
    ];

    /// <summary>The agents of the template the draft is checked against, by id (the first of a repeated id).</summary>
    public static IReadOnlyDictionary<AgentId, AgentDefinition> Agents(MontageDraft draft)
    {
        var agents = new Dictionary<AgentId, AgentDefinition>();
        foreach (var agent in draft.Context.Roster ?? [])
        {
            agents.TryAdd(agent.Id, agent);
        }

        return agents;
    }

    /// <summary>The violations of the validator on the plan of the draft (none without a plan), computed once per draft.</summary>
    public static IReadOnlyList<PlanViolation> Violations(MontageDraft draft) => DraftViolations.Of(draft);

    /// <summary>Whether a task writes code in a worktree of its own.</summary>
    public static bool Writes(TaskSpec task) => task.Kind == WorkTaskKind.AgentWork && task.Workspace == WorkspaceMode.Write;

    /// <summary>
    /// The ancestors of every task (the tasks it comes after, through any path), or null when the graph is not sound: a
    /// repeated id, an unknown or self dependency, a cycle. The validator reports those; a rule that needs the order waits.
    /// </summary>
    public static IReadOnlyDictionary<TaskId, IReadOnlySet<TaskId>>? Ancestors(PlanDefinition plan)
    {
        var byId = new Dictionary<TaskId, TaskSpec>();
        foreach (var task in plan.Tasks)
        {
            if (!byId.TryAdd(task.Id, task))
            {
                return null;
            }
        }

        if (plan.Tasks.Any(t => t.DependsOn.Any(d => d.Prerequisite == t.Id || !byId.ContainsKey(d.Prerequisite))))
        {
            return null;
        }

        var result = new Dictionary<TaskId, IReadOnlySet<TaskId>>();
        var visiting = new HashSet<TaskId>();
        bool Visit(TaskId id)
        {
            if (result.ContainsKey(id))
            {
                return true;
            }

            if (!visiting.Add(id))
            {
                return false; // a cycle
            }

            var found = new HashSet<TaskId>();
            foreach (var prerequisite in byId[id].DependsOn.Select(d => d.Prerequisite).Distinct())
            {
                if (!Visit(prerequisite))
                {
                    return false;
                }

                found.Add(prerequisite);
                found.UnionWith(result[prerequisite]);
            }

            visiting.Remove(id);
            result[id] = found;
            return true;
        }

        return plan.Tasks.All(t => Visit(t.Id)) ? result : null;
    }

    /// <summary>Whether two tasks may run at once: neither comes after the other.</summary>
    public static bool Concurrent(IReadOnlyDictionary<TaskId, IReadOnlySet<TaskId>> ancestors, TaskId a, TaskId b) =>
        a != b && !ancestors[a].Contains(b) && !ancestors[b].Contains(a);

    /// <summary>
    /// The fix that makes <paramref name="task"/> wait for <paramref name="dependsOn"/>, or null when it cannot be offered: the
    /// same task, a task not in the plan, a condition (its dependents name a branch, which the fix does not), an order the plan
    /// already keeps, or one that would make a cycle. <see cref="TaskGraph"/> decides the cycle, on the plan with the dependency
    /// added: nothing an agent says is trusted to decide it (docs/17 D-20).
    /// </summary>
    public static AddDependencyFix? DependencyFix(PlanDefinition plan, TaskId task, TaskId dependsOn)
    {
        var byId = plan.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        if (task == dependsOn || !byId.ContainsKey(task) || !byId.TryGetValue(dependsOn, out var prerequisite) || prerequisite.Kind == WorkTaskKind.Condition)
        {
            return null;
        }

        var graph = new TaskGraph();
        try
        {
            foreach (var spec in byId.Values)
            {
                graph.Add(new WorkTask(spec.Id, spec.Id.Value, spec.Kind, DateTimeOffset.UnixEpoch, null, 0, null, spec.Join));
            }

            foreach (var spec in byId.Values)
            {
                foreach (var dependency in spec.DependsOn)
                {
                    graph.AddDependency(spec.Id, dependency.Prerequisite, dependency.Branch);
                }
            }

            if (Before(graph, task).Contains(dependsOn))
            {
                return null;
            }

            graph.AddDependency(task, dependsOn);
            return new AddDependencyFix(task, dependsOn);
        }
        catch (Exception problem) when (problem is DomainRuleViolationException or KeyNotFoundException or ArgumentException)
        {
            // A cycle with the dependency added (DependencyCycleException is a DomainRuleViolationException), or a plan whose
            // graph is not sound already, which the validator reports: nothing to offer.
            return null;
        }
    }

    /// <summary>Every task <paramref name="task"/> comes after in <paramref name="graph"/>, through any path.</summary>
    private static HashSet<TaskId> Before(TaskGraph graph, TaskId task)
    {
        var found = new HashSet<TaskId>();
        var pending = new Stack<TaskId>(graph.PrerequisitesOf(task));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (found.Add(current))
            {
                foreach (var next in graph.PrerequisitesOf(current))
                {
                    pending.Push(next);
                }
            }
        }

        return found;
    }

    /// <summary>The level of the model of <paramref name="agent"/> in the catalog, or null when there is no catalog or it does not classify the model.</summary>
    public static ModelTier? TierOf(ModelCatalog? catalog, AgentDefinition agent) =>
        catalog?.Find(agent.Model.Provider, agent.Model.Model)?.Tier;

    /// <summary>The name a tool goes by without its pattern: <c>Bash(git:*)</c> is <c>Bash</c>.</summary>
    public static string ToolName(string tool)
    {
        var open = tool.IndexOf('(', StringComparison.Ordinal);
        return (open < 0 ? tool : tool[..open]).Trim();
    }

    /// <summary>
    /// Whether an entry of a write scope only touches documentation or tests: a folder or file under <c>docs/</c>,
    /// <c>doc/</c>, <c>documentation/</c>, <c>test/</c>, <c>tests/</c> or a folder whose name ends with <c>Tests</c> or
    /// <c>.Test</c>, or a file of text (<c>.md</c>, <c>.txt</c>, <c>.rst</c>, <c>.adoc</c>).
    /// </summary>
    public static bool IsDocsOrTests(string entry)
    {
        if (!WriteScope.TryNormalize(entry, out var normalized, out _))
        {
            return false;
        }

        var segments = normalized.TrimEnd('/').Split('/');
        var folders = normalized.EndsWith('/') ? segments : segments[..^1];
        if (folders.Any(s => s.Equals("docs", StringComparison.OrdinalIgnoreCase)
            || s.Equals("doc", StringComparison.OrdinalIgnoreCase)
            || s.Equals("documentation", StringComparison.OrdinalIgnoreCase)
            || s.Equals("test", StringComparison.OrdinalIgnoreCase)
            || s.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !normalized.EndsWith('/') && Path.GetExtension(normalized).ToLowerInvariant() is ".md" or ".txt" or ".rst" or ".adoc";
    }
}
