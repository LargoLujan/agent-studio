using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// What a person's challenge of a verdict leaves of it: the decision that stands, whether the verdict is set aside (in review) and whether the revision settled a decision
/// other than the verdict's own (a withdrawn approval, an approval a person accepted).
/// </summary>
internal sealed record VerdictStanding(VerdictDecision Decision, bool Challenged, bool Revised);

/// <summary>
/// The one rule that says which tasks and requirements a verdict judged (docs/16 D-29 to D-32 and D-40, docs/04 §23.6, ADR-0039), shared by the board, the chain and the
/// participation so that they cannot tell different stories.
/// <list type="bullet">
/// <item><b>Scope.</b> A verdict issued on an ordinary task of the plan judged that task. One issued on the review task (the engine issues the verdict of a run there, and it
/// covers nothing), on none, or on a task the plan does not have (the judge task of a revision, D-40, which keeps the run of the verdict it challenges) judged the work of the run.</item>
/// <item><b>Requirements.</b> Within its scope a verdict judges a task only if it assessed a requirement the task covers, and a requirement only if it assessed it. A verdict
/// with no assessments by requirement judges every task and requirement of its scope.</item>
/// <item><b>In force.</b> Of the valid verdicts that judged a requirement the last one decides, and where several runs cover it, the last run that started (the board's rule).
/// A verdict a person challenged is set aside («en revisión») until its revision debate closes (<see cref="Standing"/>).</item>
/// <item><b>Revision verdicts.</b> A verdict that supersedes (through <c>Supersedes</c>) a challenged verdict, or one of its own chain, is part of the revision (D-40): it never
/// competes to be the one in force, and its decision reaches the work only through the standing of the challenged verdict (<see cref="Revises"/>, <see cref="Standing"/>), so
/// that a revision turned towards an approval stays in review until a person accepts it. A <c>Supersedes</c> with no challenge behind it (a judge correcting itself, or a
/// correction issued before the challenge was opened, by the order of the log) replaces the earlier verdict as usual.</item>
/// <item><b>Challenges.</b> A challenge whose revision a person declined to open counts for nothing. A person has to accept a revision that turns towards an approval only for
/// the verdict the run ended with; the challenge of another verdict of the run (an attempt its judge corrected) is read as if that verdict were an approval, as the delivery does
/// (<see cref="ChallengeReader"/>, <c>DeliveryService.Revisions</c>). The verdict of a revision can be challenged in its turn: <see cref="Standing"/> follows the whole chain
/// (<see cref="ChallengeRules.Follow"/>), so that a card is done only if every branch ends in an approval that counts.</item>
/// <item><b>Tasks.</b> A task is judged as a requirement is: the card of a completed task follows the last verdict that judged it, with its standing, and is in review while that
/// verdict is set aside or when none judged it. A task that covers no requirement (an integration, a review) is judged by every verdict of its scope
/// (<see cref="JudgesUncovered"/>).</item>
/// </list>
/// </summary>
internal static class VerdictReach
{
    /// <summary>
    /// Whether the verdict judged the task, which covers <paramref name="covers"/>. <paramref name="issuedOnOrdinaryTask"/>: it was issued on an ordinary (not review) task of the plan.
    /// </summary>
    public static bool Judges(Verdict verdict, TaskId? issuedOn, bool issuedOnOrdinaryTask, TaskId task, IReadOnlyList<RequirementId> covers)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(covers);
        return InScope(issuedOn, issuedOnOrdinaryTask, task) && (verdict.Assessments.Count == 0 || verdict.Assessments.Any(a => covers.Contains(a.Requirement)));
    }

    /// <summary>Whether the verdict judged the requirement, as covered by <paramref name="task"/>.</summary>
    public static bool Judges(Verdict verdict, TaskId? issuedOn, bool issuedOnOrdinaryTask, TaskId task, RequirementId requirement)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return InScope(issuedOn, issuedOnOrdinaryTask, task) && (verdict.Assessments.Count == 0 || verdict.Assessments.Any(a => a.Requirement == requirement));
    }

    /// <summary>
    /// Whether the verdict judged a task that covers no requirement (an integration, a review): it has none to be assessed on, so every verdict of its scope judged it, whatever
    /// requirements the verdict assessed.
    /// </summary>
    public static bool JudgesUncovered(TaskId? issuedOn, bool issuedOnOrdinaryTask, TaskId task) => InScope(issuedOn, issuedOnOrdinaryTask, task);

    private static bool InScope(TaskId? issuedOn, bool issuedOnOrdinaryTask, TaskId task) => !issuedOnOrdinaryTask || issuedOn == task;

    /// <summary>
    /// The challenged verdict that <paramref name="stored"/> belongs to the revision of (it supersedes it, or a verdict of the same chain of corrections), or null when it is
    /// not a verdict of a revision: no <c>Supersedes</c>, one with no challenge behind it (a judge correcting itself), or one issued before the challenge opened. The order is
    /// the one of the log (the sequence of its entries), as in the delivery (<c>DeliveryService.Revisions</c>): every verdict of the chain, up to the challenged one, must have
    /// been issued after its challenge. A verdict the log does not announce has no order to check.
    /// </summary>
    public static StoredVerdict? Revises(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> log, StoredVerdict stored, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(storedVerdicts);
        return Revises(log, stored, storedVerdicts, new HashSet<string>(StringComparer.Ordinal) { stored.Id });
    }

    /// <param name="seen">The verdicts already walked, shared by the whole walk, so that a cycle in <c>Supersedes</c> (a store that was tampered with) ends.</param>
    private static StoredVerdict? Revises(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> log, StoredVerdict stored, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts, HashSet<string> seen)
    {
        var current = stored;
        var path = new List<string> { stored.Id };
        while (current.Supersedes is { } previous && storedVerdicts.TryGetValue(previous, out var parent) && seen.Add(parent.Id))
        {
            if (ChallengeReader.Read(log, parent, storedVerdicts).Status != ChallengeStatus.None
                && ChallengeReader.ChallengedAt(log, parent.Id) is { } challengedAt
                && path.All(id => IssuedAfter(log, id, challengedAt)))
            {
                // A challenged verdict that is itself part of an earlier revision hands the question to the challenged verdict of that revision.
                return Revises(log, parent, storedVerdicts, seen) ?? parent;
            }

            current = parent;
            path.Add(parent.Id);
        }

        return null;
    }

    /// <summary>Whether the verdict was announced (the last time) after <paramref name="sequence"/>; true when the log does not announce it, since there is no order to hold against it.</summary>
    private static bool IssuedAfter(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> log, string verdict, long sequence)
    {
        var issued = log.LastOrDefault(i => i.Payload is VerdictIssuedPayload v && string.Equals(v.VerdictId, verdict, StringComparison.Ordinal));
        return issued.Payload is null || issued.Entry.Sequence > sequence;
    }

    /// <summary>What a person's challenge of a verdict leaves of it (docs/15 D-38 to D-40), from the verified log.</summary>
    /// <remarks>
    /// The whole chain of challenges counts (<see cref="ChallengeRules.Follow"/>, as the delivery walks it): the verdict of a revision can be challenged in its turn, and what stands is
    /// where the last revision of the chain left it. The attempts the verdict's judge corrected (<c>Supersedes</c>, with no challenge before the correction) are about the same work: one
    /// that is in review, or whose revision withdrew an approval, takes the card with it, as the delivery stops for it (<c>DeliveryService.Revisions.Judge</c>).
    /// </remarks>
    public static VerdictStanding Standing(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> log, StoredVerdict stored, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts)
    {
        var standing = StandingOfChain(log, stored, storedVerdicts);
        if (Revises(log, stored, storedVerdicts) is not null)
        {
            // A verdict of a revision stands through the verdict it revises, which carries its own earlier attempts.
            return standing;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { stored.Id };
        var current = stored;
        while (current.Supersedes is { } previous && storedVerdicts.TryGetValue(previous, out var earlier) && seen.Add(earlier.Id))
        {
            // As the delivery reads it, the challenge of an attempt is read as if that attempt were an approval: a revision that does not approve withdraws it, whatever the attempt said.
            var aside = ChallengeRules.Follow(log, earlier, storedVerdicts);
            if (aside.End == ChainEnd.InReview)
            {
                return new VerdictStanding(stored.Verdict.Decision, true, false);
            }

            if (aside.End == ChainEnd.NotApproved && !standing.Challenged && standing.Decision == VerdictDecision.Approved)
            {
                standing = new VerdictStanding(aside.Decision ?? VerdictDecision.Rejected, false, true);
            }

            current = earlier;
        }

        return standing;
    }

    private static VerdictStanding StandingOfChain(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> log, StoredVerdict stored, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts)
    {
        var decision = stored.Verdict.Decision;
        var chain = ChallengeRules.Follow(log, stored, storedVerdicts);
        return chain.End switch
        {
            ChainEnd.None => new VerdictStanding(decision, false, false),

            // The chain ended in an approval that counts (a confirmation, or an acceptance of a person), or in the decision a revision settled (a withdrawn approval, a rejection).
            ChainEnd.Approved => new VerdictStanding(VerdictDecision.Approved, false, decision != VerdictDecision.Approved),
            ChainEnd.NotApproved => new VerdictStanding(chain.Decision ?? decision, false, (chain.Decision ?? decision) != decision),

            // Not closed, aborted, decided by the person («Decidir yo»: the entry does not say what they decided), awaiting their acceptance or a branch that is: in review.
            _ => new VerdictStanding(decision, true, false),
        };
    }
}
