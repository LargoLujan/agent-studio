using AgentStudio.Application.Security;
using AgentStudio.Domain.Security;

namespace AgentStudio.Security.Approvals;

/// <summary>
/// The level of risk the gate records and shows for a request: the one the caller declared, raised when the words of
/// the action say it is worse, never lowered. An action that reaches the user's branch or history (deliver, merge,
/// push, force, reset, rebase) is <see cref="RiskLevel.Critical"/>; one that throws work away (delete, remove, discard,
/// drop, prune, clean, overwrite) is at least <see cref="RiskLevel.Destructive"/>.
/// </summary>
/// <remarks>
/// Only whole words of the action id count (<c>worktree-remove</c>, <c>DeleteRef</c>, <c>discard_changes</c>), in
/// English or Spanish, so <c>undeleted-report</c> is not a deletion. The classification is a floor for display and the
/// record: every request is asked to a person whatever its level. Whether a request asks for the person's presence goes by
/// its action and never by its words (<see cref="PresenceOf"/>, docs/17 D-37).
/// </remarks>
public static class ApprovalRiskClassifier
{
    private static readonly HashSet<string> CriticalWords = new(StringComparer.Ordinal)
    {
        "deliver", "delivery", "merge", "push", "force", "reset", "rebase",
        "entrega", "entregar", "fusionar",
    };

    private static readonly HashSet<string> DestructiveWords = new(StringComparer.Ordinal)
    {
        "delete", "remove", "discard", "drop", "prune", "clean", "cleanup", "purge", "wipe", "overwrite",
        "borrar", "eliminar", "descartar", "limpiar", "quitar", "sobrescribir",
    };

    /// <summary>
    /// The actions of the approvals that the list of the fixed barrier names (docs/17 D-37, <see cref="PresencePolicy"/>), by
    /// their exact identifier and never by its words: each one is the constant of the code that asks it, in its module
    /// (<c>DeliveryService.ApprovalAction</c>, <c>UnanchoredHistory.AcceptAction</c>, <c>DebateCoordinator.OpenAction</c>…).
    /// The modules do not reference each other (ADR-0002), so the identifiers are written here again, as other modules do
    /// with <c>verdict.revision.accept</c>; a test checks each one against its constant, so a rename fails the tests and never
    /// takes an action off the list.
    /// </summary>
    private static readonly Dictionary<string, PresenceAction> PresenceActions = new(StringComparer.Ordinal)
    {
        ["deliver-integration"] = PresenceAction.DeliverToBranch,
        ["decision-log.accept-origin"] = PresenceAction.AcceptHistoryAsOrigin,
        ["debate.open"] = PresenceAction.OpenOrResumeDebate,
        ["debate.resume"] = PresenceAction.OpenOrResumeDebate,
        ["debate.human-resolution"] = PresenceAction.OpenOrResumeDebate,
        ["audit.run"] = PresenceAction.AuditProject,
        ["verdict.revision.accept"] = PresenceAction.AcceptReview,
        ["discard-run-work"] = PresenceAction.DiscardAgentWork,
        ["skills.recomendacion-asistida"] = PresenceAction.AuxiliaryQuotaConsent,
    };

    /// <summary>The identifiers of the actions <see cref="PresenceActionOf"/> knows, with the action of the barrier of each.</summary>
    public static IReadOnlyDictionary<string, PresenceAction> KnownPresenceActions => PresenceActions;

    /// <summary>
    /// The action of the fixed barrier that <paramref name="request"/> is (docs/17 D-37), by the exact identifier of its
    /// action; null for an action the list does not name (the approval nodes of a plan, whose action the plan writes, or the
    /// dismissal of a replan).
    /// </summary>
    public static PresenceAction? PresenceActionOf(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Action is { } action && PresenceActions.TryGetValue(action, out var known) ? known : null;
    }

    /// <summary>
    /// Whether <paramref name="request"/> asks for the person's presence (docs/17 D-37 and D-38, decision 6 (a)): what
    /// <see cref="PresencePolicy"/> says of its action when the list names it. An action the list does not name goes by its
    /// level, as every other approval: <see cref="RiskLevel.Critical"/> and <see cref="RiskLevel.Destructive"/> are
    /// <see cref="PresenceRequirement.Configurable"/> (on by default), <see cref="RiskLevel.Low"/> is
    /// <see cref="PresenceRequirement.Never"/>. Its words never make it <see cref="PresenceRequirement.Always"/> nor take it
    /// off the list: only the code that acts asks the actions of the list, with their constant.
    /// </summary>
    public static PresenceRequirement PresenceOf(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (PresenceActionOf(request) is { } action)
        {
            return PresencePolicy.RequirementOf(action);
        }

        return Classify(request) == RiskLevel.Low ? PresenceRequirement.Never : PresenceRequirement.Configurable;
    }

    /// <summary>
    /// Whether <paramref name="verification"/> confirms the person for an action of the list
    /// (<see cref="PresenceRequirement.Always"/>): only Windows Hello and the verified lock of Windows do (docs/17 D-40 (a)).
    /// The dialog alone does not, nor does it with the warning that Hello is not there.
    /// </summary>
    public static bool ConfirmsPresence(ApprovalVerification verification) =>
        verification is ApprovalVerification.WindowsHello or ApprovalVerification.WindowsLock;

    public static RiskLevel Classify(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var words = Words(request.Action ?? string.Empty);
        var classified = words.Any(CriticalWords.Contains) ? RiskLevel.Critical
            : words.Any(DestructiveWords.Contains) ? RiskLevel.Destructive
            : RiskLevel.Low;
        return (RiskLevel)Math.Max((int)request.Risk, (int)classified);
    }

    /// <summary>Lowercase words of an identifier: split at anything that is not a letter and before an inner capital.</summary>
    private static List<string> Words(string action)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < action.Length; i++)
        {
            var c = action[i];
            if (!char.IsLetter(c) || (char.IsUpper(c) && current.Length > 0 && char.IsLower(action[i - 1])))
            {
                Flush();
            }

            if (char.IsLetter(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
