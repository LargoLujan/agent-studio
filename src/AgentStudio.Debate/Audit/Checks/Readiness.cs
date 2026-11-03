using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Whether a task of the approved plan may be dispatched, by the rule <see cref="TaskGraph"/> documents, applied to the
/// statuses the log recorded rather than to a graph the domain rebuilds (one the domain refuses further on must not hide
/// the order of what follows; that refusal is <c>orchestrator.replay</c>):
/// <list type="bullet">
/// <item>an edge is satisfied when its prerequisite completed (on the branch it waits for, as <c>ConditionEvaluated</c>
/// recorded it), dead when the prerequisite was skipped or chose another branch, and open otherwise;</item>
/// <item>a task is ready when every edge is satisfied or, with <see cref="JoinPolicy.AnyCompleted"/>, when none is open
/// and one is satisfied;</item>
/// <item>the statuses are those the engine sees when it dispatches: it propagates the skips first, so every pending or
/// blocked task whose edges can no longer be satisfied counts as skipped even before the log records it (a resume after a
/// crash between two skips of the same pass never records the rest).</item>
/// </list>
/// The property test over random runs of the engine is what tells when this copy of the rule drifts from the domain.
/// </summary>
internal static class Readiness
{
    /// <summary>The status of every task of the trusted plan, with the skips the engine propagates before it dispatches.</summary>
    public static Dictionary<TaskId, WorkTaskStatus> Effective(AuditedRun run, TaskStatuses statuses)
    {
        var effective = run.Specs.Keys.ToDictionary(t => t, statuses.Of);
        foreach (var task in run.TopologicalOrder)
        {
            if (effective[task] is WorkTaskStatus.Pending or WorkTaskStatus.Blocked && SkipDue(run.Specs[task], effective, statuses))
            {
                effective[task] = WorkTaskStatus.Skipped;
            }
        }

        return effective;
    }

    /// <summary>The edges that keep the task from being ready, with why; empty when it is ready.</summary>
    public static List<(TaskId Prerequisite, string Why)> Unmet(TaskSpec spec, IReadOnlyDictionary<TaskId, WorkTaskStatus> effective, TaskStatuses statuses)
    {
        var edges = spec.DependsOn.Select(edge => (Edge: edge, State: State(edge, effective, statuses))).ToList();
        var satisfied = edges.Count(e => e.State.Kind == EdgeKind.Satisfied);
        var dead = edges.Count(e => e.State.Kind == EdgeKind.Dead);
        var ready = edges.Count == 0
            || (spec.Join == JoinPolicy.AnyCompleted ? satisfied > 0 && satisfied + dead == edges.Count : satisfied == edges.Count);
        if (ready)
        {
            return [];
        }

        var blocking = spec.Join == JoinPolicy.AnyCompleted && edges.Any(e => e.State.Kind == EdgeKind.Open)
            ? edges.Where(e => e.State.Kind == EdgeKind.Open)
            : edges.Where(e => e.State.Kind != EdgeKind.Satisfied);
        return [.. blocking.Select(e => (e.Edge.Prerequisite, e.State.Why))];
    }

    private static bool SkipDue(TaskSpec spec, IReadOnlyDictionary<TaskId, WorkTaskStatus> effective, TaskStatuses statuses)
    {
        if (spec.DependsOn.Count == 0)
        {
            return false;
        }

        var states = spec.DependsOn.Select(edge => State(edge, effective, statuses).Kind).ToList();
        return spec.Join == JoinPolicy.AnyCompleted ? states.All(s => s == EdgeKind.Dead) : states.Any(s => s == EdgeKind.Dead);
    }

    private static (EdgeKind Kind, string Why) State(DependencySpec edge, IReadOnlyDictionary<TaskId, WorkTaskStatus> effective, TaskStatuses statuses)
    {
        var prerequisite = edge.Prerequisite;
        var status = effective.TryGetValue(prerequisite, out var known) ? known : statuses.Of(prerequisite);
        if (status == WorkTaskStatus.Completed)
        {
            var taken = statuses.TakenBranch(prerequisite);
            return edge.Branch is null || string.Equals(edge.Branch, taken, StringComparison.Ordinal)
                ? (EdgeKind.Satisfied, string.Empty)
                : (EdgeKind.Dead, taken is null ? $"'{prerequisite}' chose no branch, not '{edge.Branch}'" : $"'{prerequisite}' chose '{taken}', not '{edge.Branch}'");
        }

        return status == WorkTaskStatus.Skipped
            ? (EdgeKind.Dead, $"'{prerequisite}' was skipped")
            : (EdgeKind.Open, $"'{prerequisite}' is {status}");
    }

    private enum EdgeKind
    {
        Satisfied,
        Dead,
        Open,
    }
}
