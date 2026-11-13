using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Hosting;

// The contracts of a hosting platform (Phase 10, docs/21 §5.2, ADR-0065 and ADR-0066). The adapter is Infrastructure (WP2); the
// service that uses it, behind the gate of the policies, is Orchestration/Publication (WP3). RemoteRepositoryFacts, BranchRule and
// BranchRules live in Domain (Domain/Hosting): HostingCapabilities.NarrowedBy uses them and Domain depends on nothing (ADR-0002).

/// <summary>How a call to a platform ended. Appended, never reordered: the log names them.</summary>
public enum HostingOutcome
{
    Ok,
    NotModified,
    SignInNeeded,
    RateLimited,
    Unreachable,
    Forbidden,
    NotFound,
    Refused,
    Unsupported,
    InvalidAnswer,
}

/// <summary>
/// Every answer of a provider. <paramref name="RetryAt"/> comes from <c>retry-after</c> or <c>x-ratelimit-reset</c>;
/// <paramref name="RefusalCode"/> is a stable code, never text of the remote.
/// </summary>
public sealed record HostingResult<T>(HostingOutcome Outcome, T? Value = default, string? ETag = null, DateTimeOffset? RetryAt = null, string? RefusalCode = null);

/// <summary>What <see cref="IHostingProvider.ProbeAsync"/> found: whether the CLI is installed, its version and what the adapter can do with it.</summary>
public sealed record HostingAvailability(bool Installed, Version? CliVersion, HostingCapabilities Capabilities);

/// <summary>The title and the body of a pull request. They travel through standard input, never as an argument of a program.</summary>
public sealed record PullRequestText(string Title, string Body);

/// <summary>What is needed to open a pull request: the repository, the head and base branches, the text, and whether it opens as a draft.</summary>
public sealed record PullRequestDraft(RemoteRepository Repository, RemoteBranch Head, RemoteBranch Base, PullRequestText Text, bool Draft);

/// <summary>The resources of a pull request that are read apart, each with its own ETag (docs/21 D-24).</summary>
public enum ObservedResource
{
    PullRequest,
    Commits,
    Timeline,
    Reviews,
    CheckRuns,
    CombinedStatus,
    WorkflowRuns,
    MergeCommit,
    BaseCommits,
    Deployments,
}

/// <summary>One ETag per resource (docs/21 D-24): a 304 of the pull request says nothing of its checks, of the base branch or of the deployments.</summary>
public sealed record ObservationCursor(IReadOnlyDictionary<ObservedResource, string> ETags, RemoteSnapshot? LastSnapshot);

/// <summary>
/// Texts of the remote for the screen only (docs/21 D-12): bounded, cleaned, kept in the local cache, never written to the log.
/// </summary>
public sealed record RemoteDisplayTexts(string? Title, IReadOnlyList<string> FailingCheckNames, string? DeploymentEnvironment)
{
    /// <summary>Nothing to show.</summary>
    public static RemoteDisplayTexts None { get; } = new(null, [], null);
}

/// <summary>
/// The snapshot composed from the resources that changed and the last known of those that answered 304. A result with <see cref="HostingOutcome.NotModified"/>
/// means every resource answered 304.
/// </summary>
public sealed record PullRequestObservation(RemoteSnapshot Snapshot, ObservationCursor Cursor, RemoteDisplayTexts Display);

/// <summary>Where a verdict stands, as it is published (docs/21 D-21).</summary>
public enum VerdictStanding
{
    Approved,
    Rejected,
    InReview,
}

/// <summary>One requirement of the verdict, by its id, and whether it was satisfied.</summary>
public sealed record RequirementOutcome(string RequirementId, bool Satisfied);

/// <summary>What is published of a verdict (docs/21 D-21): a closed vocabulary and ids. No text of the judge.</summary>
public sealed record VerdictNotice(
    RunId Run,
    string JudgedCommit,
    VerdictStanding Standing,
    IReadOnlyList<RequirementOutcome> Requirements,
    string JudgeProvider,
    string JudgeModel,
    bool HeadIsJudgedCommit);

/// <summary>What was published of a verdict: whether the status of the commit was, and the id of the tagged comment.</summary>
public sealed record PublishedVerdict(bool StatusPublished, long? CommentId);

/// <summary>The result of a merge: whether it merged, the commit it left, and the state the platform gave when it was blocked.</summary>
public sealed record MergeOutcome(bool Merged, string? MergeCommit, string? BlockedState);

/// <summary>
/// A hosting platform through its official CLI (docs/21 D-09 and D-10). No member submits a review, approves, or merges as an administrator:
/// the interface has no way to ask for any of them (D-21).
/// </summary>
public interface IHostingProvider
{
    HostingProviderKind Kind { get; }

    /// <summary>The version of the CLI and what it can do. Runs <c>--version</c> only: no network, no account.</summary>
    Task<HostingAvailability> ProbeAsync(CancellationToken ct);

    Task<HostingResult<RemoteLogin>> ReadAccountAsync(RemoteRepository repository, CancellationToken ct);

    Task<HostingResult<RemoteRepositoryFacts>> ReadRepositoryAsync(RemoteRepository repository, string? etag, CancellationToken ct);

    Task<HostingResult<BranchRules>> ReadBranchRulesAsync(RemoteRepository repository, RemoteBranch branch, CancellationToken ct);

    Task<HostingResult<PullRequestRef?>> FindPullRequestAsync(RemoteRepository repository, RemoteBranch head, CancellationToken ct);

    Task<HostingResult<PullRequestRef>> OpenPullRequestAsync(PullRequestDraft draft, CancellationToken ct);

    Task<HostingResult<bool>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestText text, CancellationToken ct);

    Task<HostingResult<bool>> RequestReviewersAsync(PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct);

    Task<HostingResult<PullRequestObservation>> ObserveAsync(PullRequestRef pullRequest, ObservationCursor? cursor, CancellationToken ct);

    /// <summary>
    /// The status of the judged commit and the tagged comment (D-21). <paramref name="statusDescription"/> and <paramref name="commentBody"/> are texts of the
    /// system, composed by the caller from its templates (D-19): the adapter only carries them, and keeps no sentence of its own.
    /// <paramref name="commentId"/> is the comment to edit; null looks it up by its mark and its author.
    /// </summary>
    Task<HostingResult<PublishedVerdict>> PublishVerdictAsync(PullRequestRef pullRequest, VerdictNotice notice, string statusDescription, string commentBody, long? commentId, CancellationToken ct);

    /// <summary>Merges only when the head of the pull request is still <paramref name="expectedHead"/> (D-20).</summary>
    Task<HostingResult<MergeOutcome>> MergeAsync(PullRequestRef pullRequest, MergeMethod method, string expectedHead, CancellationToken ct);

    Task<HostingResult<bool>> CloseAsync(PullRequestRef pullRequest, CancellationToken ct);

    Task<HostingResult<bool>> DeleteRunBranchAsync(RemoteRepository repository, RemoteBranch runBranch, string expectedCommit, CancellationToken ct);

    /// <summary>
    /// Until when the platform asked to wait (docs/21 D-24), already capped; null when nothing is waited for. While it is in the future the provider launches
    /// nothing, reads and writes alike, and answers <see cref="HostingOutcome.RateLimited"/>. The mailbox stores it (<see cref="RemoteSyncState.RateLimitedUntil"/>).
    /// </summary>
    DateTimeOffset? RateLimitedUntil { get; }

    /// <summary>
    /// Gives the provider back the wait a previous session stored, so that restarting the app does not forget it (D-24). The value comes from a local file,
    /// which is read as not trusted: the provider applies its own cap to it, and a time in the past or null is no wait.
    /// </summary>
    void RestoreRateLimit(DateTimeOffset? until);
}

/// <summary>The provider of a repository: by the kind its identifier carries. An unknown host has no provider.</summary>
public interface IHostingProviders
{
    IHostingProvider? For(RemoteRepository repository);

    /// <summary>The repository the remote <paramref name="remoteName"/> of the project names, or null when there is none or it is not recognised. Reads the configuration of the repository; opens no connection.</summary>
    Task<RemoteRepository?> IdentifyAsync(ProjectContext project, string remoteName, CancellationToken ct);
}
