using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Work;

namespace AgentStudio.Memory.Derivation;

/// <summary>Whether the question of a debate is quoted, and why not.</summary>
internal enum QuestionState
{
    /// <summary>Read as the log anchored it, written by a person.</summary>
    Quoted,

    /// <summary>Read as anchored, and empty.</summary>
    Empty,

    /// <summary>The log has no <c>EvidenceRegistered</c> of it: nothing to check it against, so nothing is read.</summary>
    NotAnchored,

    /// <summary>Anchored, but not provided by a person: only the person's question is quoted (D-46).</summary>
    NotByAPerson,

    /// <summary>Missing from the store, or not the content the log anchored: a problem with an integrity alert.</summary>
    Tampered,
}

/// <summary>
/// The summary of a closed debate (docs/15 D-46), a system entry whose text (<paramref name="Content"/>) is the system's own
/// from the structured fields of the log, to which the question the person wrote is added once read as the log anchored
/// it: <paramref name="Question"/> is its <c>EvidenceRegistered</c> (null when the log has none) and
/// <paramref name="QuestionSha256"/> the hash <c>DebateOpened</c> anchored, which must be the same. Its source is the
/// <c>DebateClosed</c> entry, so a debate has one summary, and the question is never read again once it is recorded.
/// </summary>
internal sealed record DebateSummaryFact(
    string Topic,
    string Source,
    long Order,
    string Debate,
    string Content,
    EvidenceId QuestionEvidence,
    EvidenceRegisteredPayload? Question,
    string QuestionSha256)
    : DerivedFact(MemoryKind.DebateSummary, MemoryOrigin.System, Topic, Source, Order);

/// <summary>
/// A fact of the log the memory keeps, with the one source it cites (<see cref="MemorySources"/>) and its place in the log
/// (<paramref name="Order"/>, the sequence of the entry that settles it), so the entries are recorded in log order.
/// </summary>
internal abstract record DerivedFact(MemoryKind Kind, MemoryOrigin Origin, string Topic, string Source, long Order);

/// <summary>A fact in the system's own words (<see cref="DerivationTexts"/>).</summary>
internal sealed record SystemFact(MemoryKind Kind, string Topic, string Source, long Order, string Content)
    : DerivedFact(Kind, MemoryOrigin.System, Topic, Source, Order);

/// <summary>What text of an agent a piece of evidence holds.</summary>
internal enum QuotedText
{
    /// <summary>The <c>summary</c> of the plan the orchestrator answered with.</summary>
    PlanSummary,

    /// <summary>The <c>openIssues</c> of an executor's report.</summary>
    OpenIssues,
}

/// <summary>
/// Text an agent wrote, to be quoted from the evidence the log anchored (<paramref name="Anchor"/>) once it is read and its
/// SHA-256 checked. Its source is that evidence and that hash, known before reading it.
/// </summary>
internal sealed record AgentQuote(MemoryKind Kind, string Topic, long Order, string Run, EvidenceRegisteredPayload Anchor, QuotedText What, string? Task)
    : DerivedFact(Kind, MemoryOrigin.Agent, Topic, MemorySources.Evidence(Anchor.Evidence, Anchor.Sha256), Order);

/// <summary>
/// The table of docs/14 D-18, over the payloads of Application (the derivation cannot reference the engine, ADR-0002). One
/// fact per source, from the runs recorded here only (<see cref="DerivationLog.OwnRuns"/>):
/// <list type="table">
/// <item><term>Decision, system</term><description>Each answer to the approval of a plan (<see cref="MemoryDerivation.PlanApprovalAction"/>)
/// or of a delivery (<see cref="MemoryDerivation.DeliveryApprovalAction"/>): <c>ApprovalGranted</c> or <c>ApprovalDenied</c>.
/// Source: that entry.</description></item>
/// <item><term>Decision, agent</term><description>The summary of an approved plan, when the plan that ran
/// (<c>RunStarted.PlanHash</c>) is the one the orchestrator proposed (<c>PlanCreated</c> citing its answer). Source: that
/// answer and its hash.</description></item>
/// <item><term>PendingTask, system</term><description>The tasks of a finished run whose last transition before
/// <c>RunFinished</c> is neither Completed nor Skipped, with their state, attempts and structured block reason. Source: the
/// <c>RunFinished</c> entry.</description></item>
/// <item><term>PendingTask, agent</term><description>The <c>openIssues</c> of the last report of each task of a finished
/// run (<c>AgentRunFinished.ReportEvidenceId</c>). Source: that report and its hash.</description></item>
/// <item><term>KnownError, system</term><description>A test run the system captured that did not pass
/// (<c>EvidenceRegistered</c> version 2 with its counters; the names of the failing tests are not in the log), an
/// integration with conflicts, and every audit alert of a run. Source: that entry.</description></item>
/// <item><term>Change, system</term><description>An integration accepted on the run's branch (not on a candidate ref) and a
/// delivery, with the commit, the commits integrated and, for a delivery, the paths its approval named. Source: that
/// entry.</description></item>
/// <item><term>Verdict, system</term><description>Each <c>VerdictIssued</c>: decision, validity, violations and the
/// verdict it corrects, or the challenged verdict it revises (D-40). The judge's answer is never announced with
/// <c>EvidenceRegistered</c>, so its summary is not quoted. Source: that entry.</description></item>
/// </list>
/// Phase 5 (docs/15 D-46) adds, in every unit of the log (runs, debates <c>deb-…</c> and audits <c>aud-…</c>, each named
/// as what it is):
/// <list type="table">
/// <item><term>DebateSummary, system</term><description>Each closed debate, from its first <c>DebateClosed</c>: mode,
/// object, the verdict a revision challenges, outcome, rule, the proposal chosen and the verdicts with their decision, and
/// the question the person wrote, quoted from the evidence the log anchored. Never the text of the judge (the reason of the
/// close may be its summary) nor of a participant. Source: that entry.</description></item>
/// <item><term>Decision, system</term><description>Each challenge of a verdict (<c>VerdictChallenged</c>, its reasons named,
/// never quoted), and each answer to the acceptance of a revision (<see cref="MemoryDerivation.RevisionAcceptanceAction"/>)
/// or to the dismissal of a request to replan (<see cref="MemoryDerivation.ReplanDismissalAction"/>). Source: that
/// entry.</description></item>
/// <item><term>KnownError, system</term><description>Each request to replan a run (<c>ReplanRequested</c>); the findings of
/// the auditor are already its audit alerts. Source: that entry.</description></item>
/// </list>
/// The daily summary is not derived here (<c>DailySummaryService</c>).
/// </summary>
internal static class DerivationRules
{
    public static IReadOnlyList<DerivedFact> Facts(DerivationLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var facts = new List<DerivedFact>();
        foreach (var (run, items) in log.OwnRuns)
        {
            Decisions(log, run, items, facts);
            Finished(log, run, items, facts);
            KnownErrors(run, items, facts);
            Changes(run, items, facts);
            Verdicts(run, items, facts);
            Debates(log, run, items, facts);
            Challenges(run, items, facts);
        }

        // A stable sort: facts settled by the same entry keep the order they were found in. One fact per source: two runs
        // that ran the same proposed plan quote the same answer of the orchestrator, and the first one keeps it.
        return [.. facts.OrderBy(f => f.Order).DistinctBy(f => (f.Origin, f.Source))];
    }

    private static void Decisions(DerivationLog log, string run, IReadOnlyList<LogItem> items, List<DerivedFact> facts)
    {
        foreach (var item in items)
        {
            if (item.Entry.Kind is not (DecisionKind.ApprovalGranted or DecisionKind.ApprovalDenied) || item.Payload is not ApprovalAnsweredPayload answer)
            {
                continue;
            }

            // The kind of the entry says which; the payload repeats it.
            var granted = item.Entry.Kind == DecisionKind.ApprovalGranted;
            var source = MemorySources.LogEntry(run, item.Sequence);
            if (string.Equals(answer.Action, MemoryDerivation.PlanApprovalAction, StringComparison.Ordinal))
            {
                var plan = items.Select(i => i.Payload).OfType<RunStartedPayload>().FirstOrDefault()?.PlanHash;
                var tasks = items.Count(i => i.Payload is TaskCreatedPayload);
                facts.Add(new SystemFact(
                    MemoryKind.Decision,
                    DerivationTexts.PlanTopic(run),
                    source,
                    item.Sequence,
                    DerivationTexts.PlanDecision(item.Entry.Actor, granted, plan, tasks, run, item.Entry.RecordedAt)));
                if (granted && plan is not null && log.ProposedPlan(plan)?.RawPlanEvidence is { } raw && log.Anchor(raw) is { } anchor)
                {
                    facts.Add(new AgentQuote(MemoryKind.Decision, DerivationTexts.PlanSummaryTopic(run), item.Sequence, run, anchor, QuotedText.PlanSummary, null));
                }
            }
            else if (string.Equals(answer.Action, MemoryDerivation.DeliveryApprovalAction, StringComparison.Ordinal))
            {
                facts.Add(new SystemFact(
                    MemoryKind.Decision,
                    DerivationTexts.DeliveryTopic(run),
                    source,
                    item.Sequence,
                    DerivationTexts.DeliveryDecision(item.Entry.Actor, granted, DeliveryRequest(items, item.Sequence)?.AffectedPaths.Count, run, item.Entry.RecordedAt)));
            }
            else if (string.Equals(answer.Action, MemoryDerivation.RevisionAcceptanceAction, StringComparison.Ordinal))
            {
                facts.Add(new SystemFact(
                    MemoryKind.Decision,
                    DerivationTexts.RevisionAcceptanceTopic(run),
                    source,
                    item.Sequence,
                    DerivationTexts.RevisionAcceptance(item.Entry.Actor, granted, run, item.Entry.RecordedAt)));
            }
            else if (string.Equals(answer.Action, MemoryDerivation.ReplanDismissalAction, StringComparison.Ordinal))
            {
                facts.Add(new SystemFact(
                    MemoryKind.Decision,
                    DerivationTexts.ReplanTopic(run),
                    source,
                    item.Sequence,
                    DerivationTexts.ReplanDismissal(item.Entry.Actor, granted, run, item.Entry.RecordedAt)));
            }
        }
    }

    private static void Finished(DerivationLog log, string run, IReadOnlyList<LogItem> items, List<DerivedFact> facts)
    {
        if (items.LastOrDefault(i => i.Payload is RunFinishedPayload) is not { Payload: RunFinishedPayload end } finished)
        {
            return;
        }

        var tasks = new List<TaskCreatedPayload>();
        var seen = new HashSet<TaskId>();
        var last = new Dictionary<TaskId, TaskStatusChangedPayload>();
        var reports = new Dictionary<TaskId, (long Sequence, EvidenceId Report)>();
        foreach (var item in items.Where(i => i.Sequence < finished.Sequence))
        {
            switch (item.Payload)
            {
                case TaskCreatedPayload created when seen.Add(created.Task):
                    tasks.Add(created);
                    break;
                case TaskStatusChangedPayload moved:
                    last[moved.Task] = moved;
                    break;
                case AgentRunFinishedPayload { Task: { } task, ReportEvidenceId: { } report }:
                    reports[task] = (item.Sequence, report);
                    break;
            }
        }

        var unfinished = tasks
            .Select(t => (Task: t, Last: last.GetValueOrDefault(t.Task)))
            .Where(t => t.Last is not { To: WorkTaskStatus.Completed or WorkTaskStatus.Skipped })
            .ToList();
        if (unfinished.Count > 0)
        {
            facts.Add(new SystemFact(
                MemoryKind.PendingTask,
                DerivationTexts.PendingTopic(run),
                MemorySources.LogEntry(run, finished.Sequence),
                finished.Sequence,
                DerivationTexts.Pending(run, end.Phase, unfinished)));
        }

        foreach (var (task, (sequence, report)) in reports.OrderBy(r => r.Value.Sequence))
        {
            if (log.Anchor(report) is { } anchor)
            {
                facts.Add(new AgentQuote(MemoryKind.PendingTask, DerivationTexts.OpenIssuesTopic(task, run), sequence, run, anchor, QuotedText.OpenIssues, task.Value));
            }
        }
    }

    private static void KnownErrors(string run, IReadOnlyList<LogItem> items, List<DerivedFact> facts)
    {
        foreach (var item in items)
        {
            var source = MemorySources.LogEntry(run, item.Sequence);
            switch (item.Payload)
            {
                case EvidenceRegisteredPayload { Kind: EvidenceKind.TestRun, Origin: EvidenceOrigin.CapturedBySystem, TestRun: { } tests } test
                    when test.AnchorsMetadata() && !tests.ToSummary().Succeeded:
                    facts.Add(new SystemFact(MemoryKind.KnownError, DerivationTexts.FailedTestsTopic(test.Task, run), source, item.Sequence, DerivationTexts.FailedTests(test, tests, run)));
                    break;
                case IntegrationPerformedPayload { ConflictPaths.Count: > 0 } conflict:
                    facts.Add(new SystemFact(MemoryKind.KnownError, DerivationTexts.ConflictTopic(conflict.Task, run), source, item.Sequence, DerivationTexts.Conflict(conflict, run)));
                    break;
                case AuditAlertPayload alert:
                    facts.Add(new SystemFact(MemoryKind.KnownError, DerivationTexts.AlertTopic(alert.Code, run), source, item.Sequence, DerivationTexts.Alert(alert, run)));
                    break;
                case ReplanRequestedPayload replan:
                    facts.Add(new SystemFact(MemoryKind.KnownError, DerivationTexts.ReplanTopic(run), source, item.Sequence, DerivationTexts.Replan(item.Entry.Actor, replan, run, item.Entry.RecordedAt)));
                    break;
            }
        }
    }

    private static void Changes(string run, IReadOnlyList<LogItem> items, List<DerivedFact> facts)
    {
        foreach (var item in items)
        {
            if (item.Payload is not IntegrationPerformedPayload { ConflictPaths.Count: 0, ResultCommit: { } commit } merge)
            {
                continue;
            }

            var source = MemorySources.LogEntry(run, item.Sequence);
            if (merge.IsDelivery)
            {
                facts.Add(new SystemFact(
                    MemoryKind.Change,
                    DerivationTexts.DeliveryTopic(run),
                    source,
                    item.Sequence,
                    DerivationTexts.Delivery(merge, commit, DeliveryRequest(items, item.Sequence)?.AffectedPaths, run)));
            }
            else if (!merge.TargetRef.StartsWith(MemoryDerivation.CandidateRefPrefix, StringComparison.Ordinal))
            {
                facts.Add(new SystemFact(MemoryKind.Change, DerivationTexts.IntegrationTopic(merge.Task, run), source, item.Sequence, DerivationTexts.Integration(merge, commit, run)));
            }
        }
    }

    private static void Verdicts(string run, IReadOnlyList<LogItem> items, List<DerivedFact> facts)
    {
        foreach (var item in items)
        {
            if (item.Payload is VerdictIssuedPayload verdict)
            {
                // The verdict of a revision (D-40) supersedes the verdict a challenge of the unit named before it; any other
                // superseding verdict is the one correction of its judge.
                var revises = verdict.Supersedes is { } superseded
                    && items.Any(i => i.Sequence < item.Sequence && i.Payload is VerdictChallengedPayload challenge && string.Equals(challenge.Verdict, superseded, StringComparison.Ordinal))
                        ? superseded
                        : null;
                facts.Add(new SystemFact(
                    MemoryKind.Verdict,
                    DerivationTexts.VerdictTopic(verdict.Task, run, revises),
                    MemorySources.LogEntry(run, item.Sequence),
                    item.Sequence,
                    DerivationTexts.Verdict(verdict, item.Entry.Actor.Id, run, revises)));
            }
        }
    }

    /// <summary>The summary of a debate of its own unit, from its first close (D-46); a debate that has not closed has none yet.</summary>
    private static void Debates(DerivationLog log, string run, IReadOnlyList<LogItem> items, List<DerivedFact> facts)
    {
        if (items.FirstOrDefault(i => i.Payload is DebateClosedPayload) is not { Payload: DebateClosedPayload closed } closing
            || items.FirstOrDefault(i => i.Payload is DebateOpenedPayload) is not { Payload: DebateOpenedPayload opened } opening)
        {
            return;
        }

        var verdicts = closed.Verdicts.Select(id => (id, log.Verdict(id))).ToList();
        facts.Add(new DebateSummaryFact(
            DerivationTexts.DebateTopic(run, opened.Mode),
            MemorySources.LogEntry(run, closing.Sequence),
            closing.Sequence,
            run,
            DerivationTexts.DebateSummary(run, opened, closed, verdicts, opening.Entry.RecordedAt, closing.Entry.RecordedAt),
            opened.Question,
            log.Anchor(opened.Question),
            opened.QuestionSha256));
    }

    /// <summary>Each challenge of a verdict of the unit (D-38): a decision of the person who made it.</summary>
    private static void Challenges(string run, IReadOnlyList<LogItem> items, List<DerivedFact> facts)
    {
        foreach (var item in items)
        {
            if (item.Payload is VerdictChallengedPayload challenge)
            {
                facts.Add(new SystemFact(
                    MemoryKind.Decision,
                    DerivationTexts.ChallengeTopic(challenge.Verdict, run),
                    MemorySources.LogEntry(run, item.Sequence),
                    item.Sequence,
                    DerivationTexts.Challenge(item.Entry.Actor, challenge, run, item.Entry.RecordedAt)));
            }
        }
    }

    /// <summary>The last request to approve a delivery of the run before <paramref name="sequence"/>: it names the paths the delivery changes.</summary>
    private static ApprovalRequestedPayload? DeliveryRequest(IReadOnlyList<LogItem> items, long sequence) =>
        items.LastOrDefault(i => i.Sequence < sequence && i.Payload is ApprovalRequestedPayload request
            && string.Equals(request.Action, MemoryDerivation.DeliveryApprovalAction, StringComparison.Ordinal))?.Payload as ApprovalRequestedPayload;
}
