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
/// <param name="Block">
/// Phase 13: the text form of the reason of a move to <c>Blocked</c>, as the log carries it (<c>AwaitingAnswer</c>,
/// <c>Interrupted</c>…), so replay tells a task waiting for the answer to its question from any other block. Null when the
/// reader does not give it: then the questions of the task are not checked against its blocks.
/// </param>
public sealed record TaskTransitioned(
    DateTimeOffset At,
    TaskId Task,
    WorkTaskStatus From,
    WorkTaskStatus To,
    string Reason,
    IReadOnlyList<EvidenceId> Evidence,
    string? Block = null) : RunEvent(At);

/// <summary>
/// Phase 13 (docs/17 D-29): an agent asked the person the question <paramref name="Question"/> (its id in the run). The
/// engine records it while the attempt that asked is still <c>Running</c>, just before its task goes to
/// <c>Blocked(AwaitingAnswer)</c>, which makes it pending. <paramref name="Task"/> is null for the orchestrator asking before
/// it plans: no task waits for it.
/// </summary>
public sealed record QuestionPosed(DateTimeOffset At, TaskId? Task, string Question) : RunEvent(At);

/// <summary>
/// Phase 13 (docs/17 D-29): the pending question <paramref name="Question"/> got its answer (from the person, or from the
/// system under the consent of the run). It is recorded before its task leaves <c>Blocked(AwaitingAnswer)</c> for
/// <c>Pending</c>.
/// </summary>
public sealed record QuestionResolved(DateTimeOffset At, TaskId? Task, string Question) : RunEvent(At);

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

    /// <summary>
    /// Phase 13 (docs/17 D-29): the questions the person still has to answer, by the task that waits for each one. A task
    /// waiting for its answer keeps waiting after a restart. Empty when none is pending.
    /// </summary>
    public IReadOnlyDictionary<TaskId, string> PendingQuestions { get; init; } = new Dictionary<TaskId, string>();

    /// <summary>Phase 13: the questions of the orchestrator, asked before it planned, still to be answered; no task waits for them.</summary>
    public IReadOnlyList<string> PendingRunQuestions { get; init; } = [];

    /// <summary>Phase 13: every question that got its answer, in the order of the answers.</summary>
    public IReadOnlyList<string> AnsweredQuestions { get; init; } = [];
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
/// <item>Phase 13 (docs/17 D-29): a question is asked while its attempt runs, its task waits for the answer only after
/// one, goes on only after the answer, and an answer matches the question pending. What is still pending is in
/// <see cref="RunReplay.PendingQuestions"/>, so a task waiting for its answer keeps waiting after a restart.</item>
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
        var questions = new Questions();
        var position = 0;
        foreach (var runEvent in events)
        {
            position++;
            string? problem;
            try
            {
                problem = Apply(graph, runEvent, announced, questions);
            }
            catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
            {
                // The log is data read back from storage: a rule it breaks (or a blank field) is a finding, not a crash.
                problem = ex.Message;
            }

            if (problem is not null)
            {
                return questions.Of(new RunReplay(graph, [$"Event {position}: {problem}"]));
            }
        }

        return questions.Of(new RunReplay(graph, []));
    }

    /// <summary>The text form of the block of a task waiting for the answer to its question (docs/17 D-29), as the log carries it.</summary>
    public const string AwaitingAnswerBlock = "AwaitingAnswer";

    private static string? Apply(TaskGraph graph, RunEvent? runEvent, Dictionary<TaskId, string> announced, Questions questions)
    {
        switch (runEvent)
        {
            case ConditionResolved resolved:
                return Announce(graph, resolved, announced);
            case QuestionPosed posed:
                return questions.Pose(graph, posed);
            case QuestionResolved answered:
                return questions.Resolve(graph, answered);
            case TaskTransitioned transition:
                return questions.Before(transition) ?? Transition(graph, transition, announced);
            case null:
                return "The event is missing.";
            default:
                return $"Unknown kind of event: {runEvent.GetType().Name}.";
        }
    }

    /// <summary>
    /// The questions of a run as replay follows them (docs/17 D-29): a question is announced while the attempt that asked is
    /// running, pending once its task is blocked waiting for the answer, and answered before the task goes on. A task never
    /// waits for an answer without a question, never goes on while its question is unanswered, and an answer always matches
    /// the question pending. An announced question whose task moves otherwise (the app stopped before the block was recorded,
    /// say) was never shown, and is forgotten.
    /// </summary>
    private sealed class Questions
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<TaskId, string> _asked = [];
        private readonly Dictionary<TaskId, string> _pending = [];
        private readonly List<string> _runPending = [];
        private readonly List<string> _answered = [];

        public RunReplay Of(RunReplay replay) => replay with
        {
            PendingQuestions = new Dictionary<TaskId, string>(_pending),
            PendingRunQuestions = [.. _runPending],
            AnsweredQuestions = [.. _answered],
        };

        public string? Pose(TaskGraph graph, QuestionPosed posed)
        {
            if (string.IsNullOrWhiteSpace(posed.Question))
            {
                return "A question was asked without an id.";
            }

            if (!_seen.Add(posed.Question))
            {
                return $"The question '{posed.Question}' was asked twice: its id must be unique in the run.";
            }

            if (posed.Task is not { } id)
            {
                _runPending.Add(posed.Question);
                return null;
            }

            if (!TryFind(graph, id, out var task))
            {
                return $"Task '{id}' is not in the plan, but the log says its agent asked the question '{posed.Question}'.";
            }

            if (task.Status != WorkTaskStatus.Running)
            {
                return $"The agent of task '{id}' asked the question '{posed.Question}' while the task was {task.Status}, not Running.";
            }

            if (_asked.ContainsKey(id) || _pending.ContainsKey(id))
            {
                return $"The agent of task '{id}' asked the question '{posed.Question}' while another question of the task was still open.";
            }

            _asked[id] = posed.Question;
            return null;
        }

        public string? Resolve(TaskGraph graph, QuestionResolved answered)
        {
            if (answered.Task is not { } id)
            {
                if (!_runPending.Remove(answered.Question))
                {
                    return $"The question '{answered.Question}' was answered, but no question of the orchestrator with that id was pending.";
                }

                _answered.Add(answered.Question);
                return null;
            }

            if (!_pending.TryGetValue(id, out var pending) || !string.Equals(pending, answered.Question, StringComparison.Ordinal))
            {
                return $"The question '{answered.Question}' of task '{id}' was answered, but the task was not waiting for the answer to it.";
            }

            if (!TryFind(graph, id, out var task) || task.Status != WorkTaskStatus.Blocked)
            {
                return $"The question '{answered.Question}' of task '{id}' was answered while the task was not blocked waiting for it.";
            }

            _pending.Remove(id);
            _answered.Add(answered.Question);
            return null;
        }

        /// <summary>What a transition does to the questions of its task, checked before the graph applies it.</summary>
        public string? Before(TaskTransitioned transition)
        {
            var id = transition.Task;
            var awaiting = transition.To == WorkTaskStatus.Blocked && string.Equals(transition.Block, AwaitingAnswerBlock, StringComparison.Ordinal);
            if (_asked.Remove(id, out var asked))
            {
                if (awaiting && transition.From == WorkTaskStatus.Running)
                {
                    _pending[id] = asked;
                    return null;
                }
            }
            else if (awaiting)
            {
                return $"Task '{id}' was blocked waiting for an answer, but its agent asked no question just before.";
            }

            if (_pending.TryGetValue(id, out var pending) && transition.From == WorkTaskStatus.Blocked)
            {
                if (transition.To == WorkTaskStatus.Pending)
                {
                    return $"Task '{id}' went on while the question '{pending}' was still waiting for its answer.";
                }

                _pending.Remove(id); // cancelled, skipped or settled otherwise: the question is no longer waited for
            }

            return null;
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
