using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Dependencies respected (D-33, <c>orchestrator.dependency-order</c>, high): no task of the approved plan is dispatched (its
/// transition out of pending to running, to awaiting approval or to blocked, its instructions, or the launch of its agent,
/// which the engine records before the task is running) before it is ready by <see cref="Readiness"/>, as the log says at
/// that point. Each task is reported once, at its first dispatch out of order. The replayer refuses the same transitions;
/// <see cref="ReplayCheck"/> leaves those to this finding, so one start out of order is one finding.
/// </summary>
internal static class DependencyOrderCheck
{
    /// <returns>The tasks it found dispatched out of order.</returns>
    public static IReadOnlySet<TaskId> Run(AuditedRun run, AuditFindings findings)
    {
        var reported = new HashSet<TaskId>();
        if (run.Plan is null)
        {
            return reported;
        }

        var statuses = new TaskStatuses();
        foreach (var item in run.Items)
        {
            if (AuditedRun.IsStart(item, out var task, out var signal) && run.Specs.TryGetValue(task, out var spec) && !reported.Contains(task)
                && Readiness.Unmet(spec, Readiness.Effective(run, statuses), statuses) is { Count: > 0 } unmet)
            {
                reported.Add(task);
                var join = spec.Join == JoinPolicy.AnyCompleted ? " (any of them)" : string.Empty;
                findings.Add(OrchestratorAudit.DependencyOrderCode, Severity.High, [task, .. unmet.Select(u => u.Prerequisite)], [item.Sequence], FormattableString.Invariant(
                    $"Entry {item.Sequence} ({signal}) dispatches task '{task}' while {string.Join(", ", unmet.Select(u => u.Why))}: its prerequisites in the approved plan{join} were not satisfied."));
            }

            statuses.Apply(item);
        }

        return reported;
    }
}
