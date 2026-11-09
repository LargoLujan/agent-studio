using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// The alert <c>run.not-resumable</c> of a run that needs its plan approved again (the writers it was approved with changed,
/// or its approval did not confirm the person). A run that has the consent to resume by itself is tried again at every start
/// of the app, and <c>ResumeScheduler</c> only remembers a refusal within one session: the alert is written once per refusal,
/// not once per try, so the log does not fill with the same alert (WP5, re-review of 2026-11-09).
/// </summary>
internal static class NotResumableAlert
{
    /// <summary>The code of the alert.</summary>
    public const string Code = "run.not-resumable";

    /// <summary>
    /// Whether the last alert <see cref="Code"/> of the run (<paramref name="run"/>, its entries in log order) already says
    /// <paramref name="problem"/>, and nothing that could change the answer came after it: no approval, no start and no resume.
    /// Then the refusal is the same one, and it is not written again.
    /// </summary>
    public static bool AlreadyRecorded(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> run, string problem)
    {
        ArgumentNullException.ThrowIfNull(run);
        var last = run.LastOrDefault(e => e.Payload is AuditAlertPayload { Code: Code });
        if (last.Payload is not AuditAlertPayload alert || !string.Equals(alert.Message, problem, StringComparison.Ordinal))
        {
            return false;
        }

        return !run.Any(e => e.Entry.Sequence > last.Entry.Sequence
            && e.Entry.Kind is DecisionKind.ApprovalGranted or DecisionKind.ApprovalDenied or DecisionKind.RunStarted or DecisionKind.RunResumed);
    }
}
