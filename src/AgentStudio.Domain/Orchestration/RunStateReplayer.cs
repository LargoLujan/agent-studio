using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration;

/// <summary>
/// A fact about a run that changes the state of its graph. The engine records these in the decision log as it
/// applies them; <see cref="RunStateReplayer"/> reads them back to rebuild the graph after a restart.
/// </summary>
public abstract record RunEvent(DateTimeOffset At);

/// <summary>
/// A task moved from <paramref name="From"/> to <paramref name="To"/>. <paramref name="Evidence"/> is what completed it.
/// A skip is recorded like any other transition, but replay recomputes it instead of applying it.
/// </summary>
public sealed record TaskTransitioned(
    DateTimeOffset At,
    TaskId Task,
    WorkTaskStatus From,
    WorkTaskStatus To,
    string Reason,
    IReadOnlyList<EvidenceId> Evidence) : RunEvent(At);

/// <summary>
/// The system evaluated a condition and chose a branch. It is recorded just before the condition's own transition to
/// completed; the branch takes effect with that transition.
/// </summary>
public sealed record ConditionResolved(DateTimeOffset At, TaskId Task, string Branch) : RunEvent(At);

/// <summary>
/// The graph rebuilt from a log. A run whose replay reports any <paramref name="Problems"/> cannot be resumed: the
/// log does not describe a run the domain rules allow, so someone must look at it. The graph is the state just before
/// the first problem.
/// </summary>
public sealed record RunReplay(TaskGraph Graph, IReadOnlyList<string> Problems)
{
    public bool IsResumable => Problems.Count == 0;
}

/// <summary>
/// Rebuilds the state of a run by applying its events, in order, through the same public API the engine uses, so a
/// log that the domain rules would not have allowed cannot be replayed. Nothing else is stored: the state of a run is
/// derived from the (hash-chained) decision log.
/// </summary>
/// <remarks>
/// <para>Besides the rules of <see cref="WorkTask"/> and <see cref="TaskGraph"/>, replay checks that:</para>
/// <list type="bullet">
/// <item>each transition starts from the status the graph has (so a missing, repeated or reordered event shows);</item>
/// <item>a task only leaves <c>Pending</c> for running, approval or blocking when its edges are satisfied;</item>
/// <item>a condition completes with the branch recorded just before, and only with that one;</item>
/// <item>a recorded skip is justified: the skips are recomputed with <see cref="TaskGraph.PropagateSkips"/> and
/// compared with the log. A skip that the graph would also have made but the log never recorded is not an error (the
/// app may have died between recording two skips of the same pass): it is deterministic and the engine repeats it.</item>
/// </list>
/// </remarks>
public static class RunStateReplayer
{
    /// <param name="plan">The plan the run started with.</param>
    /// <param name="createdAt">When the run started (the graph is built with this instant).</param>
    /// <param name="events">The run's events in log order.</param>
    /// <param name="expectedPlanHash">If given, the hash recorded for the run; a plan with another hash is refused.</param>
    public static RunReplay Replay(PlanDefinition plan, DateTimeOffset createdAt, IEnumerable<RunEvent> events, string? expectedPlanHash = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(events);

        if (expectedPlanHash is not null && !string.Equals(expectedPlanHash, plan.ContentHash, StringComparison.Ordinal))
        {
            return new RunReplay(new TaskGraph(), [$"The plan does not match the run: its hash is {plan.ContentHash}, the run recorded {expectedPlanHash}."]);
        }

        TaskGraph graph;
        try
        {
            graph = plan.BuildGraph(createdAt);
        }
        catch (DomainRuleViolationException ex)
        {
            return new RunReplay(new TaskGraph(), [$"The plan cannot be built: {ex.Message}"]);
        }

        var announced = new Dictionary<TaskId, string>();
        var position = 0;
        foreach (var runEvent in events)
        {
            position++;
            string? problem;
            try
            {
                problem = Apply(graph, runEvent, announced);
            }
            catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
            {
                // The log is data read back from storage: a rule it breaks (or a blank field) is a finding, not a crash.
                problem = ex.Message;
            }

            if (problem is not null)
            {
                return new RunReplay(graph, [$"Event {position}: {problem}"]);
            }
        }

        return new RunReplay(graph, []);
    }

    private static string? Apply(TaskGraph graph, RunEvent? runEvent, Dictionary<TaskId, string> announced)
    {
        switch (runEvent)
        {
            case ConditionResolved resolved:
                return Announce(graph, resolved, announced);
            case TaskTransitioned transition:
                return Transition(graph, transition, announced);
            case null:
                return "The event is missing.";
            default:
                return $"Unknown kind of event: {runEvent.GetType().Name}.";
        }
    }

    private static string? Announce(TaskGraph graph, ConditionResolved resolved, Dictionary<TaskId, string> announced)
    {
        if (!TryFind(graph, resolved.Task, out var task))
        {
            return $"Task '{resolved.Task}' is not in the plan.";
        }

        if (task.Kind != WorkTaskKind.Condition)
        {
            return $"Task '{task.Id}' is a {task.Kind}, not a condition, but the log says it chose a branch.";
        }

        if (task.Status != WorkTaskStatus.Running)
        {
            return $"Condition '{task.Id}' chose a branch while it was {task.Status}, not Running.";
        }

        if (string.IsNullOrWhiteSpace(resolved.Branch))
        {
            return $"Condition '{task.Id}' chose a blank branch.";
        }

        announced[task.Id] = resolved.Branch;
        return null;
    }

    private static string? Transition(TaskGraph graph, TaskTransitioned transition, Dictionary<TaskId, string> announced)
    {
        if (!TryFind(graph, transition.Task, out var task))
        {
            return $"Task '{transition.Task}' is not in the plan.";
        }

        if (transition.To == WorkTaskStatus.Skipped)
        {
            return Skip(graph, task, transition);
        }

        if (task.Status != transition.From)
        {
            return $"Task '{task.Id}' is {task.Status} in the replay, but the log says it left {transition.From} (to {transition.To}).";
        }

        if (task.Status == WorkTaskStatus.Pending
            && transition.To is WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval or WorkTaskStatus.Blocked
            && graph.ReadyTasks().All(t => t.Id != task.Id))
        {
            return $"Task '{task.Id}' went from Pending to {transition.To} before its prerequisites were satisfied.";
        }

        var hadBranch = announced.Remove(task.Id, out var branch);
        if (transition.To == WorkTaskStatus.Completed && task.Kind == WorkTaskKind.Condition)
        {
            if (!hadBranch)
            {
                return $"Condition '{task.Id}' completed without a recorded branch.";
            }

            graph.CompleteCondition(task.Id, branch!, transition.At, transition.Reason);
        }
        else if (transition.To == WorkTaskStatus.Completed)
        {
            task.Complete(transition.Evidence ?? [], transition.At, transition.Reason);
        }
        else if (transition.From == WorkTaskStatus.Failed && transition.To == WorkTaskStatus.Pending)
        {
            task.Retry(transition.At, transition.Reason);
        }
        else
        {
            task.TransitionTo(transition.To, transition.At, transition.Reason);
        }

        return null;
    }

    private static string? Skip(TaskGraph graph, WorkTask task, TaskTransitioned transition)
    {
        if (task.Status == WorkTaskStatus.Skipped)
        {
            return null; // an earlier skip of the same pass already took it
        }

        if (task.Status != transition.From)
        {
            return $"Task '{task.Id}' is {task.Status} in the replay, but the log says it was skipped from {transition.From}.";
        }

        graph.PropagateSkips(transition.At);
        return task.Status == WorkTaskStatus.Skipped
            ? null
            : $"Task '{task.Id}' is recorded as skipped, but the state of its prerequisites does not justify skipping it.";
    }

    private static bool TryFind(TaskGraph graph, TaskId id, out WorkTask task)
    {
        task = null!;
        if (!graph.Tasks.Any(t => t.Id == id))
        {
            return false;
        }

        task = graph[id];
        return true;
    }
}
