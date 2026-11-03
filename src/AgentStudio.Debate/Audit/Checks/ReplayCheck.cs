using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// The replay (D-33, <c>orchestrator.replay</c>, high): what keeps the run from being rebuilt from its log the way a resume
/// rebuilds it. The plan, the header or the verification profile is not the one <c>RunStarted</c> anchored (a header of
/// version 1 anchors none, which is medium: nothing says it changed, nothing proves it did not), the run starts twice, a
/// payload cannot be read, or <see cref="RunStateReplayer"/> refuses the transitions for any reason but the order: a task
/// dispatched before its prerequisites were satisfied is <c>orchestrator.dependency-order</c>, once. The entry the replayer
/// stopped at is found by replaying prefixes, so the finding names it; it does not replay past that entry.
/// </summary>
internal static class ReplayCheck
{
    /// <param name="outOfOrder">The tasks <see cref="DependencyOrderCheck"/> found dispatched out of order.</param>
    public static void Run(AuditedRun run, AuditFindings findings, IReadOnlySet<TaskId> outOfOrder)
    {
        var started = run.StartedItem.Sequence;
        if (run.PlanProblem is { } plan)
        {
            findings.Add(OrchestratorAudit.ReplayCode, Severity.High, [], [started], plan);
        }

        if (run.HeaderProblem is { } header)
        {
            findings.Add(OrchestratorAudit.ReplayCode, run.HeaderProblemSeverity, [], [started], header);
        }

        if (run.ProfileProblem is { } profile && !run.HeaderMismatch)
        {
            findings.Add(OrchestratorAudit.ReplayCode, Severity.High, [], [started], profile);
        }

        foreach (var item in run.Items.Where(i => i.Kind == DecisionKind.RunStarted && i.Sequence != started))
        {
            findings.Add(OrchestratorAudit.ReplayCode, Severity.High, [], [item.Sequence], FormattableString.Invariant(
                $"Entry {item.Sequence} starts run {run.Run} again (it started at entry {started}): a run starts once, and is resumed from its log."));
        }

        foreach (var item in run.Items.Where(i => i.Unreadable && i.Kind is not (DecisionKind.TaskStatusChanged or DecisionKind.ConditionEvaluated)))
        {
            findings.Add(OrchestratorAudit.ReplayCode, Severity.High, [], [item.Sequence], FormattableString.Invariant(
                $"Entry {item.Sequence} ({item.Kind}) has a payload that cannot be read as its kind: what it records is unknown."));
        }

        if (run.Plan is { } approved)
        {
            Replay(run, approved, findings, outOfOrder);
        }
    }

    private static void Replay(AuditedRun run, PlanDefinition plan, AuditFindings findings, IReadOnlySet<TaskId> outOfOrder)
    {
        var problems = new List<string>();
        var events = RunLog.ReplayEvents(run.Entries, problems);
        foreach (var problem in problems)
        {
            findings.Add(OrchestratorAudit.ReplayCode, Severity.High, [], [], "The run cannot be replayed from its log: " + problem);
        }

        var hash = run.Started.PlanHash;
        var replay = RunStateReplayer.Replay(plan, run.CreatedAt, events, hash);
        if (replay.IsResumable)
        {
            return;
        }

        if (RunStateReplayer.Replay(plan, run.CreatedAt, [], hash) is { IsResumable: false } before)
        {
            // Not an event: the plan itself cannot be rebuilt as a graph.
            foreach (var problem in before.Problems)
            {
                findings.Add(OrchestratorAudit.ReplayCode, Severity.High, [], [], "The run cannot be replayed from its log: " + problem);
            }

            return;
        }

        // The replay stops at its first problem, so the shortest prefix that fails ends with the event it stopped at.
        var sequences = run.Items.Where(i => i.Kind is DecisionKind.TaskStatusChanged or DecisionKind.ConditionEvaluated).Select(i => i.Sequence).ToList();
        var (low, high) = (1, events.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (RunStateReplayer.Replay(plan, run.CreatedAt, events.Take(middle), hash).IsResumable)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (events.Count == 0 || low > sequences.Count)
        {
            foreach (var problem in replay.Problems)
            {
                findings.Add(OrchestratorAudit.ReplayCode, Severity.High, [], [], "The run cannot be replayed from its log: " + problem);
            }

            return;
        }

        var stopped = events[low - 1];
        if (stopped is TaskTransitioned { From: WorkTaskStatus.Pending, To: WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval or WorkTaskStatus.Blocked } start
            && outOfOrder.Contains(start.Task)
            && RunStateReplayer.Replay(plan, run.CreatedAt, events.Take(low - 1), hash).Graph is var graph
            && graph.Tasks.Any(t => t.Id == start.Task && t.Status == WorkTaskStatus.Pending))
        {
            // Pending as the log says, so readiness is the one reason left: the order, already its own finding.
            return;
        }

        var task = stopped switch
        {
            TaskTransitioned transition => transition.Task,
            ConditionResolved condition => condition.Task,
            _ => (TaskId?)null,
        };
        var prefix = FormattableString.Invariant($"Event {low}: ");
        foreach (var problem in replay.Problems)
        {
            var text = problem.StartsWith(prefix, StringComparison.Ordinal) ? problem[prefix.Length..] : problem;
            findings.Add(OrchestratorAudit.ReplayCode, Severity.High, task is { } t ? [t] : [], [sequences[low - 1]], FormattableString.Invariant(
                $"The run cannot be replayed from its log at entry {sequences[low - 1]}: {text}"));
        }
    }
}

/// <summary>
/// A premature finish (D-33, <c>orchestrator.premature-finish</c>, high): a <c>RunFinished</c> that says the run succeeded
/// while a task of it, as the log says at that point, is neither completed nor skipped (with the trusted plan, the skips
/// the engine propagates count, recorded or not: <see cref="Readiness.Effective"/>). The tasks are those of the trusted plan
/// or, without it, those the log created.
/// </summary>
internal static class PrematureFinishCheck
{
    public static void Run(AuditedRun run, AuditFindings findings)
    {
        var statuses = new TaskStatuses();
        foreach (var item in run.Items)
        {
            if (item.Payload is RunFinishedPayload { Phase: RunPhase.Succeeded })
            {
                IReadOnlyDictionary<TaskId, WorkTaskStatus> status = run.Plan is not null
                    ? Readiness.Effective(run, statuses)
                    : run.CreatedTasks.Distinct().ToDictionary(t => t, statuses.Of);
                var tasks = run.Plan is { } plan ? plan.Tasks.Select(t => t.Id) : run.CreatedTasks.Distinct();
                var unfinished = tasks.Where(t => status[t] is not (WorkTaskStatus.Completed or WorkTaskStatus.Skipped)).ToList();
                if (unfinished.Count > 0)
                {
                    findings.Add(OrchestratorAudit.PrematureFinishCode, Severity.High, unfinished, [item.Sequence], FormattableString.Invariant(
                        $"RunFinished (entry {item.Sequence}) says the run succeeded, but {string.Join(", ", unfinished.Select(t => $"'{t}' is {status[t]}"))}."));
                }
            }

            statuses.Apply(item);
        }
    }
}
