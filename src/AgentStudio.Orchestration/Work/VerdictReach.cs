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
/// (<see cref="ChallengeReader"/>, <c>DeliveryService.Revisions</c>).</item>
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
    public static VerdictStanding Standing(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> log, StoredVerdict stored, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts)
    {
        var decision = stored.Verdict.Decision;
        var reading = ChallengeReader.Read(log, stored, storedVerdicts);
        return reading.Status switch
        {
            ChallengeStatus.None => new VerdictStanding(decision, false, false),

            // The revision confirmed the verdict (or, when none approved, settled the decision that stands) or withdrew an approval: its decision stands.
            ChallengeStatus.Confirmed or ChallengeStatus.Revoked => new VerdictStanding(reading.Revised ?? decision, false, (reading.Revised ?? decision) != decision),

            // A person accepted a revision that turned towards an approval.
            ChallengeStatus.Accepted => new VerdictStanding(VerdictDecision.Approved, false, decision != VerdictDecision.Approved),

            // Not closed, decided by the person («Decidir yo»: the entry does not say what they decided) or awaiting their acceptance: in review.
            _ => new VerdictStanding(decision, true, false),
        };
    }
}
