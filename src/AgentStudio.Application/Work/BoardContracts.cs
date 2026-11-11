using AgentStudio.Application.Projects;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Application.Work;

// Phase 9 contracts of the board (docs/16 D-19, F9-2). The board is a view: its columns are derived from the verified log by
// BoardRules (Domain), and no card is dragged. WP1 implements the projector; WP5 draws it.

/// <summary>What a card of the board is.</summary>
public enum BoardCardKind
{
    /// <summary>A requirement of the milestone (its columns: backlog, planned, in progress, in review, returned, done, integrated).</summary>
    Requirement,

    /// <summary>A task of the plan of a run linked to the milestone (without «backlog» and «integrated»).</summary>
    Task,
}

/// <summary>Stable codes of why a card is where it is, which the desktop says with its resources (ADR-0054). They are for the cases a column alone does not explain.</summary>
public static class BoardCardReasons
{
    /// <summary>A task waits for the approval of a person (in progress).</summary>
    public const string AwaitingApproval = "board.awaiting-approval";

    /// <summary>A task waits for the quota of its provider to reset (in progress).</summary>
    public const string WaitingForQuota = "board.waiting-for-quota";

    /// <summary>A task waits for the answer to a question of its agent (in progress).</summary>
    public const string AwaitingAnswer = "board.awaiting-answer";

    /// <summary>A task is blocked for another reason, such as a restart that interrupted it (in progress).</summary>
    public const string Blocked = "board.blocked";

    /// <summary>The verdict was challenged by a person and its revision has not ended (in review).</summary>
    public const string Challenged = "board.challenged";

    /// <summary>The verdict escalates the decision to a person (in review).</summary>
    public const string EscalatedToPerson = "board.escalated-to-person";

    /// <summary>The verdict could not settle it with the evidence there is (in review).</summary>
    public const string Unresolved = "board.unresolved";

    /// <summary>The run failed or was cancelled (returned).</summary>
    public const string RunFailed = "board.run-failed";
}

/// <summary>
/// One card of the board. <paramref name="Id"/> is the id of the requirement or the task, <paramref name="Run"/> the run it comes
/// from when it is a task or the requirement is covered, and <paramref name="Reason"/> one of <see cref="BoardCardReasons"/> or null.
/// </summary>
public sealed record BoardCard(BoardCardKind Kind, string Id, string Title, BoardColumn Column, string? Run, string? Reason);

/// <summary>
/// The board of a milestone, as it is when the verified log has <paramref name="LogEntries"/> entries and
/// <paramref name="LogHeadHash"/> as its head, so what the board describes is always known. The requirement cards are those of
/// the frozen set the milestone works against now.
/// </summary>
public sealed record MilestoneBoard(MilestoneId Milestone, long LogEntries, string LogHeadHash, IReadOnlyList<BoardCard> Requirements, IReadOnlyList<BoardCard> Tasks);

/// <summary>
/// Projects the board from the verified log (docs/16 D-19). It reads only; a log that does not verify fails closed, as the
/// memory does. A milestone with problems in its replay shows its problems and no board.
/// </summary>
public interface IBoardProjector
{
    Task<MilestoneBoard> ProjectAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken);

    /// <summary>The items of the backlog in force as cards of the «backlog» column, in the order of the backlog, for the board of the project.</summary>
    Task<IReadOnlyList<BoardCard>> BacklogAsync(ProjectContext project, CancellationToken cancellationToken);
}
