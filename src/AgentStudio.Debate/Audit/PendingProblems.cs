using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// What the report of a whole-project audit lists without a judge (docs/15 D-31), read only from the verified log: runs that
/// failed or did not finish, reviews that need a person, debates not resolved and challenges still open, requests to replan
/// nobody attended, agent text in the memory nobody reviewed and the pending tasks of the derived memory. The codes are
/// stable; the messages are in Spanish, since the report shows them.
/// </summary>
public static class PendingProblems
{
    public const string RunFailedCode = "run.failed";
    public const string RunUnfinishedCode = "run.unfinished";
    public const string ReviewNeedsHumanCode = "review.needs-human";
    public const string DebateUnfinishedCode = "debate.unfinished";
    public const string DebateUnresolvedCode = "debate.unresolved";
    public const string ChallengeOpenCode = "challenge.open";
    public const string ReplanPendingCode = "replan.pending";
    public const string MemoryUnreviewedCode = "memory.unreviewed";
    public const string MemoryPendingTaskCode = "memory.pending-task";

    // The coded texts for the person (docs/17 D-47): the code above stays as the dossier of the judge carries it, the text has its own.
    public const string TextRunUnfinished = "audit.pending.run-unfinished";
    public const string TextRunFailed = "audit.pending.run-failed";
    public const string TextReviewInvalid = "audit.pending.review-invalid";
    public const string TextReviewEscalated = "audit.pending.review-escalated";
    public const string TextReviewUnresolved = "audit.pending.review-unresolved";
    public const string TextDebateUnfinished = "audit.pending.debate-unfinished";
    public const string TextDebateUnresolved = "audit.pending.debate-unresolved";
    public const string TextChallengeOpen = "audit.pending.challenge-open";
    public const string TextReplanPending = "audit.pending.replan-pending";
    public const string TextMemoryUnreviewed = "audit.pending.memory-unreviewed";
    public const string TextMemoryPendingTask = "audit.pending.memory-pending-task";

    /// <summary>Every code of text of this class: the desktop has a text for each in both languages (a test walks them).</summary>
    public static IReadOnlyList<string> TextCodes { get; } =
    [
        TextRunUnfinished, TextRunFailed, TextReviewInvalid, TextReviewEscalated, TextReviewUnresolved, TextDebateUnfinished,
        TextDebateUnresolved, TextChallengeOpen, TextReplanPending, TextMemoryUnreviewed, TextMemoryPendingTask,
    ];

    /// <summary>
    /// The action of the approval that dismisses a request to replan (docs/15 D-35), in the run. The Debate module cannot
    /// reference Orchestration (ADR-0002): this is the value of <c>DeliveryService.ReplanDismissalAction</c>, and a test fails
    /// if they ever differ.
    /// </summary>
    public const string ReplanDismissalAction = "replan.dismiss";

    /// <summary>The pending problems of the project, in a fixed order of kinds and, within each kind, in log order.</summary>
    public static IReadOnlyList<PendingProblem> From(IReadOnlyList<DecisionEntry> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var items = log.OrderBy(e => e.Sequence).Select(AuditedRun.Parse).ToList();
        var problems = new List<PendingProblem>();
        Runs(items, problems);
        Reviews(items, problems);
        Debates(items, problems);
        Challenges(items, problems);
        Replans(items, problems);
        Memory(items, problems);
        return problems;
    }

    private static void Runs(List<RunItem> items, List<PendingProblem> problems)
    {
        foreach (var run in items.Where(i => i.Payload is RunStartedPayload).Select(i => i.Payload!.RunId).Distinct(StringComparer.Ordinal))
        {
            var finished = items.Select(i => i.Payload).OfType<RunFinishedPayload>().LastOrDefault(p => p.RunId == run);
            if (finished is null)
            {
                problems.Add(new PendingProblem(RunUnfinishedCode, run, $"La ejecución {run} no ha terminado: está interrumpida o espera a una persona.", UserText.Of(TextRunUnfinished, run)));
            }
            else if (finished.Phase == RunPhase.Failed)
            {
                problems.Add(new PendingProblem(RunFailedCode, run, $"La ejecución {run} terminó como fallida: {finished.Summary}", UserText.Of(TextRunFailed, run)));
            }
        }
    }

    /// <summary>The last verdict of each review task of a run that escalates or is invalid, and that no completion of the task followed.</summary>
    private static void Reviews(List<RunItem> items, List<PendingProblem> problems)
    {
        var runs = items.Where(i => i.Payload is RunStartedPayload).Select(i => i.Payload!.RunId).ToHashSet(StringComparer.Ordinal);
        var last = new Dictionary<(string Run, TaskId Task), (long Sequence, VerdictIssuedPayload Verdict)>();
        foreach (var item in items)
        {
            if (item.Payload is VerdictIssuedPayload { Task: { } task } verdict && runs.Contains(verdict.RunId))
            {
                last[(verdict.RunId, task)] = (item.Sequence, verdict);
            }
        }

        foreach (var ((run, task), (sequence, verdict)) in last.OrderBy(l => l.Value.Sequence))
        {
            var needsHuman = !verdict.Valid || verdict.Decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved;
            var completed = items.Any(i => i.Sequence > sequence && i.Payload is TaskStatusChangedPayload { To: WorkTaskStatus.Completed } done
                && done.RunId == run && done.Task == task);
            if (needsHuman && !completed)
            {
                var validity = verdict.Valid ? string.Empty : ", no válido";
                var text = !verdict.Valid ? TextReviewInvalid : verdict.Decision == VerdictDecision.EscalateToHuman ? TextReviewEscalated : TextReviewUnresolved;
                problems.Add(new PendingProblem(ReviewNeedsHumanCode, run, $"La revisión {task} de la ejecución {run} necesita a una persona: el veredicto {verdict.VerdictId} es {verdict.Decision}{validity}.", UserText.Of(text, task.ToString(), run, verdict.VerdictId)));
            }
        }
    }

    private static void Debates(List<RunItem> items, List<PendingProblem> problems)
    {
        foreach (var debate in items.Where(i => i.Payload is DebateOpenedPayload).Select(i => i.Payload!.RunId).Distinct(StringComparer.Ordinal))
        {
            var closed = items.Select(i => i.Payload).OfType<DebateClosedPayload>().LastOrDefault(p => p.RunId == debate);
            if (closed is null)
            {
                problems.Add(new PendingProblem(DebateUnfinishedCode, debate, $"El debate {debate} no ha terminado: está en curso o interrumpido.", UserText.Of(TextDebateUnfinished, debate)));
            }
            else if (closed.Outcome is DebateOutcome.Unresolved or DebateOutcome.EscalatedToUser)
            {
                problems.Add(new PendingProblem(DebateUnresolvedCode, debate, $"El debate {debate} terminó sin resolver (regla {closed.Rule}): necesita tu decisión.", UserText.Of(TextDebateUnresolved, debate)));
            }
        }
    }

    private static void Challenges(List<RunItem> items, List<PendingProblem> problems)
    {
        foreach (var (item, challenge) in items.Where(i => i.Payload is VerdictChallengedPayload).Select(i => (i, (VerdictChallengedPayload)i.Payload!)))
        {
            var closed = items.Any(i => i.Payload is DebateClosedPayload done && done.RunId == challenge.RevisionDebate);
            if (!closed)
            {
                problems.Add(new PendingProblem(ChallengeOpenCode, challenge.RunId, $"La impugnación del veredicto {challenge.Verdict} sigue abierta: su debate de revisión {challenge.RevisionDebate} no ha terminado.", UserText.Of(TextChallengeOpen, challenge.Verdict.ToString(), challenge.RevisionDebate.ToString())));
            }
        }
    }

    private static void Replans(List<RunItem> items, List<PendingProblem> problems)
    {
        foreach (var (item, request) in items.Where(i => i.Payload is ReplanRequestedPayload).Select(i => (i, (ReplanRequestedPayload)i.Payload!)))
        {
            // As the delivery reads it: a person's dismissal asked after the request, or a follow-up run started after it.
            var run = request.RunId;
            var attended = PersonApprovals.Granted(items, ReplanDismissalAction, unit => string.Equals(unit, run, StringComparison.Ordinal)).Any(yes => yes.Requested > item.Sequence)
                || items.Any(i => i.Sequence > item.Sequence && i.Payload is RunStartedPayload follow && string.Equals(follow.Follows, run, StringComparison.Ordinal));
            if (!attended)
            {
                problems.Add(new PendingProblem(ReplanPendingCode, run, $"La ejecución {run} tiene una petición de replanificar sin atender ({string.Join(", ", request.Findings)}).", UserText.Of(TextReplanPending, run, string.Join(", ", request.Findings))));
            }
        }
    }

    /// <summary>Entries of the memory still in force: not superseded by a later one, nor rejected or retracted.</summary>
    private static void Memory(List<RunItem> items, List<PendingProblem> problems)
    {
        var recorded = items.Select(i => i.Payload).OfType<MemoryRecordedPayload>().ToList();
        var superseded = recorded.Where(r => r.Supersedes is not null).Select(r => r.Supersedes!).ToHashSet(StringComparer.Ordinal);
        var reviews = items.Select(i => i.Payload).OfType<MemoryReviewedPayload>()
            .GroupBy(r => r.Entry, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Decision, StringComparer.Ordinal);
        foreach (var entry in recorded.Where(r => !superseded.Contains(r.Entry)))
        {
            var reviewed = reviews.TryGetValue(entry.Entry, out var decision);
            if (reviewed && decision is MemoryReview.Rejected or MemoryReview.Retracted)
            {
                continue;
            }

            if (entry.Origin == MemoryOrigin.Agent && !reviewed)
            {
                problems.Add(new PendingProblem(MemoryUnreviewedCode, entry.Entry, $"La entrada de memoria {entry.Entry} ({entry.Kind}) cita texto de un agente y nadie la ha revisado.", UserText.Of(TextMemoryUnreviewed, entry.Entry)));
            }
            else if (entry.Kind == MemoryKind.PendingTask)
            {
                problems.Add(new PendingProblem(MemoryPendingTaskCode, entry.Entry, $"La memoria tiene una tarea pendiente: la entrada {entry.Entry}.", UserText.Of(TextMemoryPendingTask, entry.Entry)));
            }
        }
    }
}
