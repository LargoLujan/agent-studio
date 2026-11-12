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

    private static bool InScope(TaskId? issuedOn, bool issuedOnOrdinaryTask, TaskId task) => !issuedOnOrdinaryTask || issuedOn == task;

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
