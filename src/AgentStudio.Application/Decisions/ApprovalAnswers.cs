using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;

namespace AgentStudio.Application.Decisions;

/// <summary>
/// The answer to an approval as the log records it (docs/16 D-13, F8-11; ADR-0037), the same wherever it is written: the
/// approval gate, the approval of a plan on the canvas and the acceptance of a history. With a method other than the dialog
/// alone it is version 2 and says how the person was confirmed; otherwise it is version 1, byte for byte as before Phase 8:
/// a version 1 answer is one the dialog gave.
/// </summary>
public static class ApprovalAnswers
{
    /// <summary>The payload of <c>ApprovalGranted</c> or <c>ApprovalDenied</c> for an answer given with <paramref name="verification"/>.</summary>
    public static ApprovalAnsweredPayload Payload(
        string action,
        bool approved,
        string decidedBy,
        string? comment,
        TaskId? task,
        ApprovalVerification verification,
        string runId)
    {
        var method = verification == ApprovalVerification.Dialog ? (ApprovalVerification?)null : verification;
        return new ApprovalAnsweredPayload(action, approved, decidedBy, comment, task, method)
        {
            RunId = runId,
            V = method is null ? 1 : ApprovalAnsweredPayload.VerificationVersion,
        };
    }
}
