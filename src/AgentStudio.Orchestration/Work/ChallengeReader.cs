using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>Where the challenge of a verdict stands, and the decision of its revision when the revision settled one.</summary>
public sealed record ChallengeReading(ChallengeStatus Status, VerdictDecision? Revised);

/// <summary>
/// Reads where the challenge of a verdict stands (docs/15 D-38 to D-40) from the verified log, for the board. The Orchestration module cannot
/// reference the Debate module (ADR-0002), so this repeats the rules of <c>ChallengeLog.StatusOf</c> and a test pins that, for the same log,
/// both give the same <see cref="ChallengeStatus"/>. The one thing more it asks: the verdicts of the revision must be in the store, valid.
/// </summary>
public static class ChallengeReader
{
    /// <summary>The action of a person's acceptance of a revision that turned towards an approval: the value of <c>DeliveryService.RevisionAcceptanceAction</c>.</summary>
    public const string RevisionAcceptanceAction = "verdict.revision.accept";

    /// <param name="log">The verified log.</param>
    /// <param name="challenged">The verdict as the store holds it.</param>
    /// <param name="stored">The verdicts of the store by id, to check those of the revision.</param>
    public static ChallengeReading Read(IReadOnlyList<DecisionEntry> log, StoredVerdict challenged, IReadOnlyDictionary<string, StoredVerdict> stored)
    {
        ArgumentNullException.ThrowIfNull(log);
        return Read(WorkLog.Parse(log), challenged, stored);
    }

    /// <summary>The same over the log already deserialized (<see cref="WorkLog.Parse"/>), so a caller that reads many verdicts deserializes it once.</summary>
    public static ChallengeReading Read(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, StoredVerdict challenged, IReadOnlyDictionary<string, StoredVerdict> stored)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(challenged);
        ArgumentNullException.ThrowIfNull(stored);

        string? debate = null;
        foreach (var (_, payload) in items)
        {
            switch (payload)
            {
                case VerdictChallengedPayload c when string.Equals(c.Verdict, challenged.Id, StringComparison.Ordinal):
                    debate = c.RevisionDebate;
                    break;
                case DebateOpenedPayload o when string.Equals(o.Challenges, challenged.Id, StringComparison.Ordinal):
                    debate = o.RunId;
                    break;
            }
        }

        if (debate is null)
        {
            return new ChallengeReading(ChallengeStatus.None, null);
        }

        var opened = items.Select(i => i.Payload).OfType<DebateOpenedPayload>().FirstOrDefault(o => string.Equals(o.RunId, debate, StringComparison.Ordinal));
        var closing = items.LastOrDefault(i => i.Payload is DebateClosedPayload && string.Equals(i.Payload.RunId, debate, StringComparison.Ordinal));
        if (opened is null || !string.Equals(opened.Challenges, challenged.Id, StringComparison.Ordinal) || closing.Payload is not DebateClosedPayload closed)
        {
            return new ChallengeReading(ChallengeStatus.Pending, null);
        }

        if (string.Equals(closed.Rule, JudgingPanel.User, StringComparison.Ordinal))
        {
            return new ChallengeReading(ChallengeStatus.DecidedByUser, null);
        }

        if (closed.Outcome != DebateOutcome.Resolved || DecisionOf(items.Select(i => i.Payload), closed, stored) is not { } revision)
        {
            return new ChallengeReading(ChallengeStatus.Pending, null);
        }

        var announced = items.Select(i => i.Payload).OfType<VerdictIssuedPayload>().LastOrDefault(v => string.Equals(v.VerdictId, challenged.Id, StringComparison.Ordinal));
        var approved = challenged.Valid && challenged.Verdict.Decision == VerdictDecision.Approved
            && announced is { Valid: true, Decision: VerdictDecision.Approved };
        var approves = revision == VerdictDecision.Approved;
        var status = (approved, approves) switch
        {
            (true, true) => ChallengeStatus.Confirmed,
            (true, false) => ChallengeStatus.Revoked,
            (false, true) => Accepted(items, challenged.RunId, debate, closing.Entry.Sequence) ? ChallengeStatus.Accepted : ChallengeStatus.AwaitingAcceptance,
            (false, false) => ChallengeStatus.Confirmed,
        };
        return new ChallengeReading(status, revision);
    }

    /// <summary>The decision of a revision that closed by its judge or its agreeing panel: each verdict it names announced valid and stored valid, all with the same decision.</summary>
    private static VerdictDecision? DecisionOf(IEnumerable<DecisionPayload?> payloads, DebateClosedPayload closed, IReadOnlyDictionary<string, StoredVerdict> stored)
    {
        if (closed.Verdicts.Count == 0)
        {
            return null;
        }

        var issuedAll = payloads.OfType<VerdictIssuedPayload>().ToList();
        var decisions = new HashSet<VerdictDecision>();
        foreach (var id in closed.Verdicts)
        {
            var issued = issuedAll.LastOrDefault(v => string.Equals(v.VerdictId, id, StringComparison.Ordinal));
            if (issued is not { Valid: true } || !stored.TryGetValue(id, out var row) || !row.Valid)
            {
                return null;
            }

            decisions.Add(issued.Decision);
        }

        return decisions.Count == 1 ? decisions.Single() : null;
    }

    /// <summary>A person's yes to the acceptance whose request comes after the close, in the unit of the verdict or in the revision debate.</summary>
    private static bool Accepted(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, string? unit, string debate, long closedAt)
    {
        var requests = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (entry, payload) in items)
        {
            if (payload is not { RunId: { } u } || !(string.Equals(u, debate, StringComparison.Ordinal) || (unit is not null && string.Equals(u, unit, StringComparison.Ordinal))))
            {
                continue;
            }

            switch (payload)
            {
                case ApprovalRequestedPayload asked when entry.Kind == DecisionKind.ApprovalRequested && string.Equals(asked.Action, RevisionAcceptanceAction, StringComparison.Ordinal):
                    requests[u] = entry.Sequence;
                    break;
                case ApprovalAnsweredPayload answer when string.Equals(answer.Action, RevisionAcceptanceAction, StringComparison.Ordinal):
                    if (requests.Remove(u, out var requested) && entry.Kind == DecisionKind.ApprovalGranted && answer.Approved && entry.Actor.Kind == ActorKind.User && requested > closedAt)
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }
}
