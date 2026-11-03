using AgentStudio.Application.Security;

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
/// record: every request is asked to a person whatever its level.
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
