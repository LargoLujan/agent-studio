using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Orchestration.Analysis.Rules;

/// <summary>
/// The typed fixes of an order an agent suggested (docs/17 D-20), checked by the system: whether the plan already keeps it, and
/// the dependency that would keep it when <see cref="Work.TaskGraph"/> admits it (no cycle, both tasks in the plan, not on a
/// condition). Nothing is applied here: the person applies the fix, as any other, and can undo it.
/// </summary>
public static class OrderFix
{
    /// <summary>Whether <paramref name="later"/> already comes after <paramref name="earlier"/> in <paramref name="plan"/>, through any path.</summary>
    public static bool Keeps(PlanDefinition plan, TaskId later, TaskId earlier)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return DraftFacts.Ancestors(plan) is { } ancestors && ancestors.TryGetValue(later, out var before) && before.Contains(earlier);
    }

    /// <summary>The fix that makes <paramref name="task"/> wait for <paramref name="dependsOn"/>, or null when it cannot be offered (<see cref="DraftFacts.DependencyFix"/>).</summary>
    public static AddDependencyFix? For(PlanDefinition plan, TaskId task, TaskId dependsOn)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return DraftFacts.DependencyFix(plan, task, dependsOn);
    }
}
