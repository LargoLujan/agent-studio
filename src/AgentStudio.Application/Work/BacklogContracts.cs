using AgentStudio.Application.Projects;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Backlog;

namespace AgentStudio.Application.Work;

// Phase 9 contracts of the backlog (docs/16 D-18, F9-2): the requirements in draft the person notes for later. Rows are
// append-only in SQLite (migration 0006, backlog_items), and each version is announced in the decision log by
// BacklogItemRecorded with the canonical SHA-256 of its content before its row is stored, as the memory is (ADR-0022): a
// row that was never announced, or changed since, is not read, and an announcement without a row is completed by writing
// again. The order, the priority and the retirement are the person's decisions, recorded in the log.

/// <summary>
/// The rows of <c>backlog_items</c>: every version of every item, written once. There is deliberately no update or delete
/// operation.
/// </summary>
public interface IBacklogStore
{
    /// <summary>Inserts a version. The same row twice writes nothing; another row with the same item and version is refused.</summary>
    Task AppendAsync(string projectId, BacklogItem item, CancellationToken cancellationToken);

    /// <summary>Every version of every item, in the order they were written.</summary>
    Task<IReadOnlyList<BacklogItem>> ListAsync(string projectId, CancellationToken cancellationToken);

    Task<BacklogItem?> FindAsync(string projectId, BacklogItemId id, int version, CancellationToken cancellationToken);
}

/// <summary>What the person notes for later (docs/16 D-18): a statement, its priority as a requirement and, if they want, its criteria.</summary>
public sealed record BacklogDraft(string Statement, RequirementPriority Priority, IReadOnlyList<AcceptanceCriterion> Criteria);

/// <summary>An item of the backlog in force: its last version, and how many versions it has had.</summary>
public sealed record BacklogEntry(BacklogItem Item, int Versions);

/// <summary>
/// The backlog of a project (docs/16 D-18): add, change (a new version with all its content), retire and reorder. It reads only
/// rows the log announced with the same hash. Writing waits until no run, debate or audit has the project, like the memory, and
/// fails closed on a log that does not verify.
/// </summary>
public interface IBacklogService
{
    /// <summary>Announces and stores version 1 of a new item.</summary>
    Task<BacklogItem> AddAsync(ProjectContext project, BacklogDraft draft, string addedBy, CancellationToken cancellationToken);

    /// <summary>Announces and stores the next version of an item, with all of its content.</summary>
    Task<BacklogItem> ReviseAsync(ProjectContext project, BacklogItemId item, BacklogDraft draft, string revisedBy, CancellationToken cancellationToken);

    /// <summary>Records <c>BacklogItemRetired</c>. No row is deleted.</summary>
    Task RetireAsync(ProjectContext project, BacklogItemId item, string retiredBy, string? reason, CancellationToken cancellationToken);

    /// <summary>Records <c>BacklogOrderChanged</c>: the items listed come first, in this order.</summary>
    Task ReorderAsync(ProjectContext project, IReadOnlyList<BacklogItemId> items, string decidedBy, CancellationToken cancellationToken);

    /// <summary>The items in force, in the order of the backlog.</summary>
    Task<IReadOnlyList<BacklogEntry>> ListAsync(ProjectContext project, CancellationToken cancellationToken);

    /// <summary>Every version of an item, oldest first, retired or not.</summary>
    Task<IReadOnlyList<BacklogItem>> HistoryAsync(ProjectContext project, BacklogItemId item, CancellationToken cancellationToken);
}
