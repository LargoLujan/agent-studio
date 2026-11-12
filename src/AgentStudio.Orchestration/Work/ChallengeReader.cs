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
/// <para>
/// Two things differ on purpose, to read the log as <c>DeliveryService.Revisions</c> does (a table test, <c>RevisionAgreementTests</c>, runs both over the same log): a challenge whose
/// revision a person declined to open counts for nothing (<c>StatusOf</c> says <see cref="ChallengeStatus.Pending"/>), and the challenge of a verdict that is not the one the run
/// ended with is read as if that verdict were an approval, so its revision never awaits an acceptance (<c>StatusOf</c> says <see cref="ChallengeStatus.AwaitingAcceptance"/>).
/// </para>
/// </summary>
public static class ChallengeReader
{
    /// <summary>The action of a person's acceptance of a revision that turned towards an approval: the value of <c>DeliveryService.RevisionAcceptanceAction</c>.</summary>
    public const string RevisionAcceptanceAction = "verdict.revision.accept";

    /// <summary>The action of the question that asks a person to open a revision debate: the value of <c>DeliveryService.DebateOpenAction</c>.</summary>
    public const string DebateOpenAction = "debate.open";

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

        var challenges = Challenges(items, challenged.Id);
        if (challenges.Count == 0)
        {
            return new ChallengeReading(ChallengeStatus.None, null);
        }

        // The verdict the run ended with is the only one whose revision a person has to accept when it turns towards an approval; a challenge of any other verdict of the run
        // (an attempt its judge corrected) is about the same work and, like the delivery (DeliveryService.Revisions.Judge), is read as if that verdict were an approval.
        var closing = ClosingVerdict(items, challenged.RunId);
        var asApproval = closing is not null && !string.Equals(closing, challenged.Id, StringComparison.Ordinal);

        // A challenge whose revision a person declined to open never started and counts for nothing (DeliveryService.Revisions), but as the latest one it is still the latest
        // word: the ones before it that never got a judgment give way to it, and a judged one, or one still open, stands beside it.
        var live = challenges.Where(c => !c.Declined).ToList();
        if (live.Count == 0)
        {
            return new ChallengeReading(ChallengeStatus.None, null);
        }

        var reading = ReadDebate(items, challenged, stored, live[^1].Debate, asApproval);
        if (challenges[^1].Declined && !(reading.Status is ChallengeStatus.Confirmed or ChallengeStatus.Revoked or ChallengeStatus.Accepted or ChallengeStatus.AwaitingAcceptance
            || (reading.Status == ChallengeStatus.Pending && IsOpen(items, live[^1].Debate))))
        {
            return new ChallengeReading(ChallengeStatus.None, null);
        }

        return reading;
    }

    /// <summary>The sequence of the log entry that first challenged the verdict with a revision that was not declined, or null when none did: what the verdicts of a revision must come after.</summary>
    internal static long? ChallengedAt(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, string verdict) =>
        Challenges(items, verdict).Where(c => !c.Declined).Select(c => (long?)c.Sequence).FirstOrDefault();

    /// <summary>The challenges of the verdict in the order of the log: the revision debate each named (or opened against it) and whether a person declined to open it.</summary>
    private static List<(string Debate, long Sequence, bool Declined)> Challenges(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, string verdict)
    {
        var found = new List<(string Debate, long Sequence, bool Declined)>();
        foreach (var (entry, payload) in items)
        {
            switch (payload)
            {
                case VerdictChallengedPayload c when string.Equals(c.Verdict, verdict, StringComparison.Ordinal):
                    found.Add((c.RevisionDebate, entry.Sequence, Declined(items, c.RevisionDebate)));
                    break;
                case DebateOpenedPayload o when string.Equals(o.Challenges, verdict, StringComparison.Ordinal):
                    found.Add((o.RunId, entry.Sequence, false));
                    break;
            }
        }

        return found;
    }

    /// <summary>Whether a person's no to opening the debate is in the log and the debate never opened (the same rule as <c>DeliveryService.Revisions</c>).</summary>
    private static bool Declined(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, string debate) =>
        !items.Any(i => i.Payload is DebateOpenedPayload && string.Equals(i.Payload.RunId, debate, StringComparison.Ordinal))
        && items.Any(i => i.Entry.Kind == DecisionKind.ApprovalDenied && i.Entry.Actor.Kind == ActorKind.User
            && i.Payload is ApprovalAnsweredPayload { Approved: false } answer && string.Equals(answer.RunId, debate, StringComparison.Ordinal)
            && string.Equals(answer.Action, DebateOpenAction, StringComparison.Ordinal));

    private static bool IsOpen(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, string debate) =>
        items.Any(i => i.Payload is DebateOpenedPayload && string.Equals(i.Payload.RunId, debate, StringComparison.Ordinal))
        && !items.Any(i => i.Payload is DebateClosedPayload && string.Equals(i.Payload.RunId, debate, StringComparison.Ordinal));

    /// <summary>The verdict the run ended with (<c>RunFinished.VerdictId</c>, the last one), or null when the run has not finished or says none.</summary>
    private static string? ClosingVerdict(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, string? run) =>
        run is null ? null : items.Select(i => i.Payload).OfType<RunFinishedPayload>().LastOrDefault(f => string.Equals(f.RunId, run, StringComparison.Ordinal))?.VerdictId;

    private static ChallengeReading ReadDebate(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, StoredVerdict challenged, IReadOnlyDictionary<string, StoredVerdict> stored, string debate, bool asApproval)
    {
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
        var approved = asApproval || (challenged.Valid && challenged.Verdict.Decision == VerdictDecision.Approved
            && announced is { Valid: true, Decision: VerdictDecision.Approved });
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
