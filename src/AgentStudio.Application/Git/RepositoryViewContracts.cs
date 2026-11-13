using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Application.Git;

// Phase 9 contracts of «Repositorio y cambios» (docs/16 D-44 and D-45, F9-9): the view of what the agents and the person did,
// annotated from the log. It is not a git client. The history is read through the hardened git layer (ADR-0012): no filters, no
// textconv, no network. The texts that come from git (a subject, an author name) are data of whoever wrote the commit: the screen
// shows them as such. Phase 10 appends the remote branches and the activity of the publications.

/// <summary>What a branch is. Phase 10 appends the remote ones.</summary>
public enum BranchKind
{
    /// <summary>A branch of the person.</summary>
    Local,

    /// <summary>A branch of the system for a run (<c>agentstudio/…</c>).</summary>
    Run,

    /// <summary>The branch of a run on the remote (Phase 10, docs/21 §5.1): <c>agentstudio/run-&lt;id&gt;</c> as the platform has it.</summary>
    RemoteRun,
}

/// <summary>
/// The facts git gives about the repository. <paramref name="Accepted"/> is false when the hardened layer does not accept it (its
/// configuration, ADR-0012): the facts are then null and <paramref name="Warnings"/> holds the codes of the reasons.
/// </summary>
public sealed record RepositorySummary(string Root, bool Accepted, string? HeadBranch, string? HeadCommit, bool HasUncommittedChanges, IReadOnlyList<string> Warnings);

/// <summary>A branch with the commit it points at.</summary>
public sealed record BranchSummary(string Name, string Commit, bool IsCurrent, BranchKind Kind);

/// <summary>
/// A commit of the graph: its full id and those of its parents, its time and what git says of its subject and author (data of its writer).
/// <paramref name="Message"/> is the whole message when the reader gives it (the trailers of the system are in its last paragraph, and the
/// suggestions of the trace read them there); null when it gives only the subject.
/// </summary>
public sealed record CommitNode(string Id, IReadOnlyList<string> Parents, DateTimeOffset At, string Subject, string AuthorName, IReadOnlyList<string> Refs, string? Message = null);

/// <summary>What a read of the graph is bounded by: the number of commits (200 by default), a period and a branch.</summary>
public sealed record CommitGraphQuery(int MaxCommits = 200, DateTimeOffset? Since = null, DateTimeOffset? Until = null, string? Branch = null);

/// <summary>The history of the repository, by the hardened git layer (WP4). It reads only.</summary>
public interface IRepositoryHistoryReader
{
    Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken);

    Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken);

    /// <summary>The graph, newest first, bounded by the query.</summary>
    Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken);
}

/// <summary>What the log knows of a commit: the run, task and attempt that produced it with the agent and model (a commit of the system), and its links and suggestions.</summary>
/// <remarks>Phase 10 (docs/21 D-28) adds <paramref name="Successor"/> at the end: what became of the commit on the remote (a squash, a rebase…), from the observations of the log; null when nothing merged names it.</remarks>
public sealed record CommitAnnotation(string? Run, string? Task, int? Attempt, AgentId? Agent, string? Model, IReadOnlyList<TraceLink> Links, IReadOnlyList<TraceSuggestion> Suggestions, CommitSuccessor? Successor = null);

/// <summary>A commit with its annotation, or none when the log knows nothing of it.</summary>
public sealed record AnnotatedCommit(CommitNode Commit, CommitAnnotation? Annotation);

/// <summary>Annotates commits from the verified log and the trace (D-44). It reads only.</summary>
public interface IRepositoryAnnotator
{
    Task<IReadOnlyList<AnnotatedCommit>> AnnotateAsync(ProjectContext project, IReadOnlyList<CommitNode> commits, CancellationToken cancellationToken);
}

/// <summary>What happened, in the timeline of the log. Phase 10 appends the publications and the observations of the remote.</summary>
public enum RepositoryActivityKind
{
    Integration,
    Delivery,
    WorktreeRemoved,

    /// <summary>Something the app did on the remote (Phase 10): a publication, a merge… from <c>HostingOperationPerformed</c>.</summary>
    RemoteOperation,

    /// <summary>What the app saw on the remote (Phase 10): a merge, a close, a deployment… from <c>RemoteStateObserved</c>.</summary>
    RemoteObservation,
}

/// <summary>An event of the timeline with its real result: <paramref name="ErrorCode"/> is the code of what failed, null when it worked; <paramref name="Sequence"/> the entry of the log.</summary>
/// <remarks>
/// Phase 10 (docs/21 §7.3) appends what a remote entry says of itself, so the screen can name it: <paramref name="Operation"/> and <paramref name="Outcome"/> of an operation the app did, <paramref name="Changes"/> of an observation
/// (including <see cref="RemoteChangeKind.BaseChanged"/>) and <paramref name="PullRequest"/> the number it is about. They are null for what is not a remote entry.
/// </remarks>
public sealed record RepositoryActivity(
    DateTimeOffset At,
    RepositoryActivityKind Kind,
    string? Run,
    string? Task,
    string? Commit,
    bool Succeeded,
    string? ErrorCode,
    long Sequence,
    HostingOperationKind? Operation = null,
    Hosting.HostingOutcome? Outcome = null,
    IReadOnlyList<RemoteChangeKind>? Changes = null,
    int? PullRequest = null);

/// <summary>The filters of the timeline, all dimensions the log or the links already have. Null lets everything through.</summary>
public sealed record RepositoryActivityFilter(
    string? Run = null,
    string? Task = null,
    MilestoneId? Milestone = null,
    AgentId? Agent = null,
    ProviderId? Provider = null,
    string? Model = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>The timeline of the log of the project (the «Actividad» tab). It reads only.</summary>
public interface IRepositoryActivityReader
{
    Task<IReadOnlyList<RepositoryActivity>> ListAsync(ProjectContext project, RepositoryActivityFilter? filter, CancellationToken cancellationToken);
}
