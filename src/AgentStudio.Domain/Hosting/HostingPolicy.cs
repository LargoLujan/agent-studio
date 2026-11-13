namespace AgentStudio.Domain.Hosting;

/// <summary>The four classes of remote operation (docs/21 D-15). Appended, never reordered: the log names them.</summary>
public enum HostingOperationClass
{
    PublishRunBranch,
    PullRequestWrite,
    MergePullRequest,
    Destructive,
}

/// <summary>What the app can do on a remote, one by one. Appended, never reordered.</summary>
public enum HostingOperationKind
{
    PushRunBranch,
    OpenPullRequest,
    UpdatePullRequest,
    RequestReviewers,
    PublishVerdictStatus,
    PublishVerdictComment,
    MergePullRequest,
    ClosePullRequest,
    DeleteRemoteBranch,
}

/// <summary>How a pull request is merged. Appended, never reordered.</summary>
public enum MergeMethod
{
    MergeCommit,
    Squash,
    Rebase,
}

/// <summary>
/// The rule of docs/21 D-15, pure: which class an operation is, whether it may be covered by a standing grant, and whether a use
/// needs an approved verdict. The rule is given by the type: nothing in a setting or a file can change it.
/// </summary>
public static class HostingPolicy
{
    public static HostingOperationClass ClassOf(HostingOperationKind operation) => operation switch
    {
        HostingOperationKind.PushRunBranch => HostingOperationClass.PublishRunBranch,
        HostingOperationKind.OpenPullRequest
            or HostingOperationKind.UpdatePullRequest
            or HostingOperationKind.RequestReviewers
            or HostingOperationKind.PublishVerdictStatus
            or HostingOperationKind.PublishVerdictComment => HostingOperationClass.PullRequestWrite,
        HostingOperationKind.MergePullRequest => HostingOperationClass.MergePullRequest,
        HostingOperationKind.ClosePullRequest or HostingOperationKind.DeleteRemoteBranch => HostingOperationClass.Destructive,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown remote operation."),
    };

    /// <summary>
    /// By operation: false for everything of <see cref="HostingOperationClass.MergePullRequest"/> and <see cref="HostingOperationClass.Destructive"/>,
    /// and for <see cref="HostingOperationKind.RequestReviewers"/> (it notifies a person by name).
    /// </summary>
    public static bool CanBePersistent(HostingOperationKind operation) => operation switch
    {
        HostingOperationKind.PushRunBranch
            or HostingOperationKind.OpenPullRequest
            or HostingOperationKind.UpdatePullRequest
            or HostingOperationKind.PublishVerdictStatus
            or HostingOperationKind.PublishVerdictComment => true,
        HostingOperationKind.RequestReviewers
            or HostingOperationKind.MergePullRequest
            or HostingOperationKind.ClosePullRequest
            or HostingOperationKind.DeleteRemoteBranch => false,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown remote operation."),
    };

    /// <summary>
    /// Whether a standing grant of <paramref name="operationClass"/> can exist: when at least one of its operations may be persistent. A grant of
    /// any other class cannot be built (<see cref="HostingPolicyGrant"/>) or recorded.
    /// </summary>
    public static bool ClassAdmitsGrant(HostingOperationClass operationClass) =>
        Enum.GetValues<HostingOperationKind>().Any(o => ClassOf(o) == operationClass && CanBePersistent(o)) && Enum.IsDefined(operationClass);

    /// <summary>
    /// By operation, not by class: false for <see cref="HostingOperationKind.PublishVerdictStatus"/> and <see cref="HostingOperationKind.PublishVerdictComment"/>
    /// (they must be able to say «not approved» and «in review») and for <see cref="HostingOperationKind.ClosePullRequest"/> and
    /// <see cref="HostingOperationKind.DeleteRemoteBranch"/> (what is closed is just what was not approved); true for everything else.
    /// </summary>
    public static bool RequiresApprovedVerdict(HostingOperationKind operation) => operation switch
    {
        HostingOperationKind.PushRunBranch
            or HostingOperationKind.OpenPullRequest
            or HostingOperationKind.UpdatePullRequest
            or HostingOperationKind.RequestReviewers
            or HostingOperationKind.MergePullRequest => true,
        HostingOperationKind.PublishVerdictStatus
            or HostingOperationKind.PublishVerdictComment
            or HostingOperationKind.ClosePullRequest
            or HostingOperationKind.DeleteRemoteBranch => false,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown remote operation."),
    };
}

/// <summary>
/// A standing grant as the log holds it (docs/21 D-16). It matches only the same class and the same repository, and the base branch when it names
/// one; «publish the branch» has no base, so its grant never carries one. A grant of a class that admits none (merge, destructive) cannot be built.
/// </summary>
public sealed record HostingPolicyGrant
{
    public HostingPolicyGrant(string grantId, HostingOperationClass @class, RemoteRepository repository, RemoteBranch? baseBranch, string grantedBy, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantedBy);
        if (!HostingPolicy.ClassAdmitsGrant(@class))
        {
            throw new ArgumentException($"A standing grant of the class {@class} cannot exist: none of its operations may be persistent.", nameof(@class));
        }

        if (baseBranch is not null && @class != HostingOperationClass.PullRequestWrite)
        {
            throw new ArgumentException("Only a grant to write on the pull request may name a base branch.", nameof(baseBranch));
        }

        GrantId = grantId;
        Class = @class;
        Repository = repository;
        BaseBranch = baseBranch;
        GrantedBy = grantedBy;
        At = at;
    }

    public string GrantId { get; }

    public HostingOperationClass Class { get; }

    public RemoteRepository Repository { get; }

    /// <summary>Only <see cref="HostingOperationClass.PullRequestWrite"/> may name one; null covers any base.</summary>
    public RemoteBranch? BaseBranch { get; }

    public string GrantedBy { get; }

    public DateTimeOffset At { get; }

    /// <summary>
    /// Whether an operation of <paramref name="class"/> on <paramref name="repository"/> towards <paramref name="baseBranch"/> is covered: the same
    /// class and repository; and, when the grant names a base, only that one (an operation that names none is not covered by a grant that does).
    /// </summary>
    public bool Covers(HostingOperationClass @class, RemoteRepository repository, RemoteBranch? baseBranch)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return Class == @class && Repository == repository && (BaseBranch is null || BaseBranch == baseBranch);
    }
}
