using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Hosting;

// The mailbox of a project and the poll (Phase 10, docs/21 D-23 to D-26, ADR-0067): one consumer, in order. The implementation is
// Orchestration/Publication (WP3); the projection that the screen reads is Orchestration/Work (WP4); the local state is Infrastructure/Hosting/State.

/// <summary>Where a piece of remote work stands. Appended, never reordered.</summary>
public enum RemoteWorkState
{
    Queued,
    WaitingForProject,
    Running,
    PendingSync,
    Done,
    Failed,
    Cancelled,
    Expired,
}

/// <summary>Why a piece of work is waiting to be synchronised (docs/21 D-26).</summary>
public enum PendingReason
{
    Offline,
    SignInNeeded,
    CredentialsNeeded,
    RateLimited,
    ProjectBusy,
}

/// <summary>The receipt of a request: its id, what it is (null for a plain observation) and the pull request it is about.</summary>
public sealed record RemoteWorkTicket(string Id, HostingOperationKind? Operation, PullRequestRef? PullRequest);

public sealed record RemoteWorkStatus(RemoteWorkTicket Ticket, RemoteWorkState State, PendingReason? Reason, DateTimeOffset? RetryAt, UserText? Message);

/// <summary>When it last synchronised well, how the last call ended (and when: a session seen as closed is said with its date), and until when the platform asked to wait.</summary>
public sealed record RemoteSyncHealth(DateTimeOffset? LastGoodSync, HostingOutcome? LastOutcome, DateTimeOffset? RateLimitedUntil, DateTimeOffset? LastOutcomeAt = null);

/// <summary>
/// The mailbox of a project and the poll (docs/21 D-23 to D-26): one consumer, in order. Reads by the CLI go on with the project busy; git and the log wait
/// for it.
/// </summary>
public interface IRemoteSync
{
    /// <summary>The click of the person: allowed even with the periodic poll off.</summary>
    Task SyncNowAsync(ProjectContext project, CancellationToken ct);

    Task FollowAsync(ProjectContext project, PullRequestRef pullRequest, RunId? run, CancellationToken ct);

    Task UnfollowAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct);

    Task CancelAsync(string projectId, string ticketId, CancellationToken ct);

    IReadOnlyList<RemoteWorkStatus> Pending(string projectId);

    /// <summary>Last good sync, session as last seen, rate limit until.</summary>
    RemoteSyncHealth Health(string projectId);

    /// <summary>Raised with the id of the project when something of it changed.</summary>
    event EventHandler<string>? Changed;
}

/// <summary>
/// The permission of the network (docs/21 D-02). <c>AppSettings</c> lives in the desktop, so the mailbox asks a port; without one in the composition, the
/// network is never allowed.
/// </summary>
public interface IRemoteSyncPolicy
{
    bool PeriodicSyncAllowed(string projectId);

    TimeSpan Interval { get; }
}

/// <summary>What the screen shows of a followed pull request: the last recorded snapshot from the verified log, with the display texts of the cache.</summary>
public sealed record PullRequestView(
    PullRequestRef PullRequest,
    RunId? Run,
    RemoteSnapshot? Snapshot,
    string? JudgedCommit,
    bool HeadIsJudgedCommit,
    VerdictStanding? Verdict,
    RemoteDisplayTexts Display,
    DateTimeOffset? ObservedAt,
    IReadOnlyList<RemoteWorkStatus> Pending);

/// <summary>The projection of the pull requests of a project from the verified log (WP4); it reads only.</summary>
public interface IPullRequestProjector
{
    Task<IReadOnlyList<PullRequestView>> ListAsync(ProjectContext project, CancellationToken ct);
}

/// <summary>
/// A pull request that is followed, as the local state keeps it (docs/21 §6): read as not trusted. <paramref name="PendingConfirmation"/> is a change of
/// checks or reviews seen once (D-23).
/// </summary>
public sealed record FollowedPullRequest(PullRequestRef PullRequest, string? Run, ObservationCursor Cursor, RemoteDisplayTexts Display, RemoteSnapshot? PendingConfirmation);

/// <summary>Work parked or waiting to sync; it expires after 24 hours (docs/21 D-26).</summary>
public sealed record PendingRemoteWork(RemoteWorkTicket Ticket, HostingOperation? Operation, RemoteSnapshot? Observation, string Stage, PendingReason Reason, DateTimeOffset Since, DateTimeOffset? RetryAt);

/// <summary>
/// The local state that can be deleted (docs/21 §6). It is read as not trusted: bounded, validated, and nothing in it authorises anything.
/// </summary>
public sealed record RemoteSyncState(
    RemoteRepository? Repository,
    IReadOnlyList<FollowedPullRequest> Followed,
    IReadOnlyList<PendingRemoteWork> Pending,
    DateTimeOffset? RateLimitedUntil,
    DateTimeOffset? LastGoodSync,
    HostingOutcome? LastOutcome,
    DateTimeOffset? LastOutcomeAt = null)
{
    /// <summary>A project that was never synchronised (or whose file was deleted): nothing followed, nothing pending.</summary>
    public static RemoteSyncState Empty { get; } = new(null, [], [], null, null, null);
}

public interface IRemoteSyncStore
{
    Task<RemoteSyncState> LoadAsync(string projectId, CancellationToken ct);

    Task SaveAsync(string projectId, RemoteSyncState state, CancellationToken ct);
}
