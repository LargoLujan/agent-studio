using AgentStudio.Domain.Judging;

namespace AgentStudio.Domain.Work.Milestones;

/// <summary>
/// The columns of the board of a milestone (docs/16 D-19). A card is never dragged: its column is derived from the verified
/// log by <see cref="BoardRules"/>. The names are written to exports and tests, so new columns are appended, never inserted.
/// </summary>
public enum BoardColumn
{
    /// <summary>No task of a run linked to the milestone covers the requirement.</summary>
    Backlog,

    /// <summary>A plan that was approved and has not begun covers it.</summary>
    Planned,

    /// <summary>A task that covers it is running, waiting for an approval or waiting for quota.</summary>
    InProgress,

    /// <summary>Its run ended without a valid verdict, the verdict is challenged, or the verdict leaves the decision to a person.</summary>
    InReview,

    /// <summary>The last valid verdict does not give it as satisfied, or the run failed.</summary>
    Returned,

    /// <summary>An approved, valid verdict gives it as satisfied.</summary>
    Done,

    /// <summary>Done, and its run was delivered to the person's branch (with Phase 10, its pull request was merged).</summary>
    Integrated,
}

/// <summary>
/// What the last valid verdict that judged a requirement says (D-19): its decision, and what it assessed for the requirement
/// (null when the verdict does not assess it). A verdict that does not validate (<c>VerdictValidator</c>) is no verdict here:
/// the projection leaves it out and the card goes on as if there were none.
/// </summary>
public sealed record BoardVerdictFacts(VerdictDecision Decision, AssessmentStatus? Assessment);

/// <summary>What the verified log says about one requirement of a milestone, which is all the board needs to place its card.</summary>
/// <param name="CoveringTasks">The statuses of the tasks, in the plans of the runs linked to the milestone (and their follow-ups), that cover it (<c>TaskSpec.Covers</c>).</param>
/// <param name="RunEnded">The run of those tasks reached a final phase.</param>
/// <param name="RunFailed">That run failed or was cancelled.</param>
/// <param name="Verdict">The last valid verdict that judged the requirement, or null when there is none.</param>
/// <param name="VerdictChallenged">That verdict was challenged by a person (<c>VerdictChallenged</c>).</param>
/// <param name="Delivered">The run was delivered to the person's branch (<c>IntegrationPerformed</c> with <c>IsDelivery</c>).</param>
public sealed record RequirementBoardFacts(
    IReadOnlyList<WorkTaskStatus> CoveringTasks,
    bool RunEnded,
    bool RunFailed,
    BoardVerdictFacts? Verdict,
    bool VerdictChallenged,
    bool Delivered);

/// <summary>What the verified log says about one task of a plan, for its card.</summary>
/// <param name="Status">The status of the task, rebuilt by replaying the run.</param>
/// <param name="RunVerdict">The decision of the last valid verdict of its run that nobody challenged, or null when there is none (yet).</param>
public sealed record TaskBoardFacts(WorkTaskStatus Status, VerdictDecision? RunVerdict);

/// <summary>
/// The rule of the board (docs/16 D-19, F9-2), pure: where the card of a requirement or of a task goes. One invariant matters
/// above the rest: <b>no card is <see cref="BoardColumn.Done"/> or <see cref="BoardColumn.Integrated"/> without an approved,
/// valid verdict</b>. A verdict that escalates to a person or is unresolved leaves the requirement in review, because the
/// person decides.
/// </summary>
public static class BoardRules
{
    /// <summary>The column of a requirement.</summary>
    public static BoardColumn ColumnOf(RequirementBoardFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.CoveringTasks);

        if (facts.CoveringTasks.Count == 0)
        {
            return BoardColumn.Backlog;
        }

        if (facts.Verdict is { } verdict)
        {
            // A challenge sets the verdict aside until the revision debate says otherwise.
            if (facts.VerdictChallenged)
            {
                return BoardColumn.InReview;
            }

            return verdict.Decision switch
            {
                VerdictDecision.Approved => verdict.Assessment switch
                {
                    AssessmentStatus.Satisfied => facts.Delivered ? BoardColumn.Integrated : BoardColumn.Done,
                    AssessmentStatus.NotSatisfied => BoardColumn.Returned,

                    // Approved, but the verdict does not say this requirement is satisfied: it is not done, and nobody returned it.
                    _ => BoardColumn.InReview,
                },
                VerdictDecision.ChangesRequested or VerdictDecision.Rejected => BoardColumn.Returned,
                _ => BoardColumn.InReview,
            };
        }

        if (facts.RunFailed)
        {
            return BoardColumn.Returned;
        }

        if (facts.RunEnded)
        {
            return BoardColumn.InReview;
        }

        if (facts.CoveringTasks.Any(IsActive))
        {
            return BoardColumn.InProgress;
        }

        // Nothing is running: still planned when no covering task has begun, and under way when some finished and others wait their turn.
        return facts.CoveringTasks.All(s => s == WorkTaskStatus.Pending) ? BoardColumn.Planned : BoardColumn.InProgress;
    }

    /// <summary>The column of a task of a plan, or null for a task that is not on the board (a skipped one: its branch was not taken).</summary>
    public static BoardColumn? ColumnOf(TaskBoardFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Status switch
        {
            WorkTaskStatus.Pending => BoardColumn.Planned,
            WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval or WorkTaskStatus.Blocked => BoardColumn.InProgress,
            WorkTaskStatus.Completed => facts.RunVerdict switch
            {
                VerdictDecision.Approved => BoardColumn.Done,
                VerdictDecision.ChangesRequested or VerdictDecision.Rejected => BoardColumn.Returned,

                // No verdict yet, or one that leaves the decision to a person: its run awaits the review.
                _ => BoardColumn.InReview,
            },
            WorkTaskStatus.Failed or WorkTaskStatus.Cancelled => BoardColumn.Returned,
            _ => null,
        };
    }

    private static bool IsActive(WorkTaskStatus status) =>
        status is WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval or WorkTaskStatus.Blocked;
}
