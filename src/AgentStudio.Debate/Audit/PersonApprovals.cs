using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// A person's yes to an approval in a unit of the log, read as the delivery reads it (<c>DeliveryService</c>, docs/15 D-35
/// and D-40): an <c>ApprovalGranted</c> of a person (actor <see cref="ActorKind.User"/>) that answers the last
/// <c>ApprovalRequested</c> of the same action in the same unit, which the answer spends (the gate records the request first,
/// then the answer). <see cref="Yes.Requested"/> is the sequence of that request: an approval asked after something happened
/// is one whose request comes after it.
/// </summary>
internal static class PersonApprovals
{
    public sealed record Yes(string Unit, long Requested, long Answered, string DecidedBy);

    /// <summary>Every person's yes to <paramref name="action"/> in the units <paramref name="inUnit"/> accepts, in log order.</summary>
    public static List<Yes> Granted(IEnumerable<RunItem> items, string action, Func<string, bool> inUnit)
    {
        var requests = new Dictionary<string, long>(StringComparer.Ordinal);
        var granted = new List<Yes>();
        foreach (var item in items)
        {
            if (item.Payload is not { RunId: { } unit } payload || !inUnit(unit))
            {
                continue;
            }

            switch (payload)
            {
                case ApprovalRequestedPayload asked when item.Entry.Kind == DecisionKind.ApprovalRequested && string.Equals(asked.Action, action, StringComparison.Ordinal):
                    requests[unit] = item.Sequence;
                    break;
                case ApprovalAnsweredPayload answer when string.Equals(answer.Action, action, StringComparison.Ordinal):
                    if (requests.Remove(unit, out var requested)
                        && item.Entry.Kind == DecisionKind.ApprovalGranted
                        && answer.Approved
                        && item.Entry.Actor.Kind == ActorKind.User)
                    {
                        granted.Add(new Yes(unit, requested, item.Sequence, answer.DecidedBy));
                    }

                    break;
            }
        }

        return granted;
    }
}
