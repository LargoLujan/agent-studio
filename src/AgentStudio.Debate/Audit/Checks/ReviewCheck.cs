using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Mandatory reviews not omitted (D-33, <c>orchestrator.review-omitted</c>, high):
/// <list type="bullet">
/// <item>a <c>ReviewSkipped</c> in the run is a review skipped;</item>
/// <item>a review task completes only after its <c>ReviewRequested</c> and its <c>VerdictIssued</c> (or, when the review
/// escalated, the approval of the person it went to), as <c>IndependentReview</c> and the engine record them;</item>
/// <item>when the run finished as succeeded, every task that wrote code for an agent whose review policy asks for an
/// independent review has a completed review task after it in the approved plan (the rule of
/// <c>OrchestrationRun.Unreviewed</c>). The policies come from the trusted header only.</item>
/// </list>
/// </summary>
internal static class ReviewCheck
{
    public static void Run(AuditedRun run, AuditFindings findings)
    {
        var requested = new HashSet<TaskId>();
        var judged = new HashSet<TaskId>();
        var statuses = new TaskStatuses();
        long? succeededAt = null;
        foreach (var item in run.Items)
        {
            switch (item.Payload)
            {
                case ReviewRequestedPayload request:
                    requested.Add(request.Task);
                    break;
                case VerdictIssuedPayload { Task: { } task }:
                    judged.Add(task);
                    break;
                case ApprovalAnsweredPayload { Approved: true, Task: { } task } answer
                    when item.Kind == DecisionKind.ApprovalGranted && item.Entry.Actor.Kind == ActorKind.User
                        && string.Equals(answer.Action, OrchestratorAudit.ReviewEscalationAction, StringComparison.Ordinal):
                    judged.Add(task);
                    break;
                case TaskStatusChangedPayload { To: WorkTaskStatus.Completed } done when run.KindOf(done.Task) == WorkTaskKind.Review:
                    Completed(item, done.Task, requested, judged, findings);
                    break;
                case RunFinishedPayload { Phase: RunPhase.Succeeded } when succeededAt is null:
                    succeededAt = item.Sequence;
                    Policy(run, item.Sequence, statuses, findings);
                    break;
            }

            if (item.Kind == DecisionKind.ReviewSkipped)
            {
                findings.Add(OrchestratorAudit.ReviewOmittedCode, Severity.High, AuditedRun.FreeFormTasks(item.Entry), [item.Sequence], FormattableString.Invariant(
                    $"Entry {item.Sequence} is a ReviewSkipped: a review of the run was skipped ({item.Entry.Summary})."));
            }

            statuses.Apply(item);
        }
    }

    private static void Completed(RunItem item, TaskId review, HashSet<TaskId> requested, HashSet<TaskId> judged, AuditFindings findings)
    {
        var missing = new List<string>();
        if (!requested.Contains(review))
        {
            missing.Add("a ReviewRequested");
        }

        if (!judged.Contains(review))
        {
            missing.Add("a VerdictIssued (nor the approval of the person it escalated to)");
        }

        if (missing.Count > 0)
        {
            findings.Add(OrchestratorAudit.ReviewOmittedCode, Severity.High, [review], [item.Sequence], FormattableString.Invariant(
                $"Review task '{review}' completed (entry {item.Sequence}) without {string.Join(" and without ", missing)} for it: no judge reviewed it."));
        }
    }

    private static void Policy(AuditedRun run, long finished, TaskStatuses statuses, AuditFindings findings)
    {
        if (run.Plan is null || run.Header is null)
        {
            return;
        }

        var reviews = run.Specs.Values.Where(s => s.Kind == WorkTaskKind.Review).ToList();
        var writers = run.Specs.Values
            .Where(s => s is { Kind: WorkTaskKind.AgentWork, Workspace: WorkspaceMode.Write, Agent: { } agent }
                && statuses.Of(s.Id) == WorkTaskStatus.Completed
                && run.Definitions.TryGetValue(agent, out var definition) && definition.Review.RequiresIndependentReview)
            .OrderBy(s => s.Id.Value, StringComparer.Ordinal);
        foreach (var writer in writers)
        {
            var after = reviews.Where(r => run.IsAncestor(writer.Id, r.Id)).ToList();
            if (after.Any(r => statuses.Of(r.Id) == WorkTaskStatus.Completed))
            {
                continue;
            }

            var detail = after.Count == 0
                ? "the approved plan has no review task after it"
                : $"{string.Join(", ", after.Select(r => $"'{r.Id}' is {statuses.Of(r.Id)}"))}";
            findings.Add(OrchestratorAudit.ReviewOmittedCode, Severity.High, [writer.Id], [finished], FormattableString.Invariant(
                $"Task '{writer.Id}' wrote code for '{writer.Agent}', whose review policy asks for an independent review, and the run finished as succeeded (entry {finished}) with no completed review after it ({detail})."));
        }
    }
}
