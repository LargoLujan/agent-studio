using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Domain.Hosting;

public enum PullRequestState
{
    Open,
    Draft,
    Merged,
    Closed,
}

public enum RemoteMergeMode
{
    MergeCommit,
    Squash,
    Rebase,
    SquashOrRebase,
    Unknown,
}

/// <summary>The decision of a reviewer; it covers the numeric votes of Azure DevOps.</summary>
public enum ReviewVerdict
{
    Approved,
    ApprovedWithSuggestions,
    ChangesRequested,
    Waiting,
    Rejected,
    Commented,
    Dismissed,
}

public enum ChecksRollup
{
    None,
    Pending,
    Passing,
    Failing,
}

public enum DeploymentState
{
    Pending,
    Succeeded,
    Failed,
    Inactive,
    Unknown,
}

public sealed record ChecksSummary(ChecksRollup Rollup, int Passing, int Failing, int Pending, bool Truncated);

public sealed record ReviewsSummary(int Approved, int ChangesRequested, int Commented, IReadOnlyList<RemoteLogin> Approvers);

public sealed record RemoteDeployment(long Id, string Commit, DeploymentState State);

/// <summary>
/// What was seen of one pull request (docs/21 D-23): identifiers, numbers and enums only. No text of the remote. Whoever builds one from what
/// the platform said (an adapter) or from what the log holds (<c>RemoteSnapshotPayload.ToSnapshot</c>) calls <see cref="EnsureValid"/>.
/// </summary>
public sealed record RemoteSnapshot(
    PullRequestRef PullRequest,
    PullRequestState State,
    string HeadCommit,
    RemoteBranch BaseBranch,
    bool HeadBranchExists,
    string? MergeCommit,
    RemoteMergeMode? MergeMode,
    RemoteLogin? MergedBy,
    IReadOnlyList<string> OriginalCommits,
    IReadOnlyList<string> ResultCommits,
    bool CommitsTruncated,
    ChecksSummary Checks,
    ReviewsSummary Reviews,
    int ForcePushes,
    bool TimelineTruncated,
    IReadOnlyList<string> RevertCommits,
    RemoteDeployment? Deployment,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The most commits a list of a snapshot holds (docs/21 D-28: three pages of 100, and 250 is the cap of the platform).</summary>
    public const int MaxCommits = 300;

    private const int MaxApprovers = 100;

    /// <summary>
    /// The SHA-256 of the canonical form, in lowercase hexadecimal; <see cref="UpdatedAt"/> is not part of it (docs/21 D-23). Two snapshots with the
    /// same fingerprint are one: nothing is recorded. The canonical form is one <c>key=value</c> line per field in a fixed order, the sets (the
    /// approvers and the reverts) sorted, and the lists of commits in the order they have, which counts in a rebase.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder("remote-snapshot/1\n");
            void Add(string key, object? value) =>
                text.Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
            void AddList(string key, IEnumerable<string> values) => Add(key, string.Join(",", values));

            Add("pr", PullRequest.TraceId);
            Add("state", State);
            Add("head", HeadCommit);
            Add("base", BaseBranch.Name);
            Add("headBranchExists", HeadBranchExists);
            Add("mergeCommit", MergeCommit);
            Add("mergeMode", MergeMode);
            Add("mergedBy", MergedBy?.Value);
            AddList("originals", OriginalCommits);
            AddList("results", ResultCommits);
            Add("commitsTruncated", CommitsTruncated);
            Add("checks", $"{Checks.Rollup}/{Checks.Passing}/{Checks.Failing}/{Checks.Pending}/{Checks.Truncated}");
            Add("reviews", $"{Reviews.Approved}/{Reviews.ChangesRequested}/{Reviews.Commented}");
            AddList("approvers", Reviews.Approvers.Select(a => a.Value).Order(StringComparer.Ordinal));
            Add("forcePushes", ForcePushes);
            Add("timelineTruncated", TimelineTruncated);
            AddList("reverts", RevertCommits.Order(StringComparer.Ordinal));
            Add("deployment", Deployment is null ? null : $"{Deployment.Id}/{Deployment.Commit}/{Deployment.State}");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

    /// <summary>
    /// Checks what a snapshot must hold, because it may come from the platform or from the log, which is read as data: every commit is a full id, the
    /// lists are bounded, the counts are not negative, and a state is consistent with its merge data.
    /// </summary>
    /// <exception cref="ArgumentException">Something is not what a snapshot holds.</exception>
    public void EnsureValid()
    {
        if (!HostingIdentifiers.IsCommitId(HeadCommit))
        {
            throw new ArgumentException("The head of a snapshot is a full commit id.", nameof(HeadCommit));
        }

        if (MergeCommit is not null && !HostingIdentifiers.IsCommitId(MergeCommit))
        {
            throw new ArgumentException("The merge commit of a snapshot is a full commit id.", nameof(MergeCommit));
        }

        foreach (var (name, list) in new[] { (nameof(OriginalCommits), OriginalCommits), (nameof(ResultCommits), ResultCommits), (nameof(RevertCommits), RevertCommits) })
        {
            if (list is null || list.Count > MaxCommits || list.Any(c => !HostingIdentifiers.IsCommitId(c)))
            {
                throw new ArgumentException($"{name} holds up to {MaxCommits} full commit ids.", name);
            }
        }

        if (Deployment is { } deployment && (deployment.Id < 0 || !HostingIdentifiers.IsCommitId(deployment.Commit)))
        {
            throw new ArgumentException("A deployment of a snapshot has an id and a full commit id.", nameof(Deployment));
        }

        if (Checks is null || Reviews is null || Reviews.Approvers is null || Reviews.Approvers.Count > MaxApprovers
            || new[] { Checks.Passing, Checks.Failing, Checks.Pending, Reviews.Approved, Reviews.ChangesRequested, Reviews.Commented, ForcePushes }.Any(n => n < 0))
        {
            throw new ArgumentException("The counts of a snapshot are not negative and its approvers are bounded.");
        }

        if (!Enum.IsDefined(State) || (MergeMode is { } mode && !Enum.IsDefined(mode)))
        {
            throw new ArgumentException("The state or the merge mode of a snapshot is not one of the known ones.");
        }

        if (State != PullRequestState.Merged && (MergeCommit is not null || MergeMode is not null || MergedBy is not null))
        {
            throw new ArgumentException("Only a merged pull request has a merge commit, a merge mode and who merged it.");
        }
    }
}

/// <summary>What changed between two snapshots of a pull request. Appended, never reordered: the log names them.</summary>
public enum RemoteChangeKind
{
    Opened,
    HeadMoved,
    ForcePushed,
    Merged,
    Closed,
    Reopened,
    HeadBranchDeleted,
    ChecksChanged,
    ReviewsChanged,
    RevertSuspected,
    DeploymentChanged,
}

/// <param name="Discarded">The observation is older than what is recorded, or goes backwards in what only grows: it is thrown away and nothing is recorded.</param>
/// <param name="Changes">What changed since the last recorded snapshot; empty when the fingerprint is the same or the observation was discarded.</param>
public sealed record ReconcileResult(bool Discarded, IReadOnlyList<RemoteChangeKind> Changes);

/// <summary>
/// Pure (docs/21 D-23): the changes between the last recorded snapshot and a new one. The same fingerprint is no change; an older <c>UpdatedAt</c>
/// is discarded, and so is a snapshot that goes backwards in what only grows (merged is terminal; the force pushes, the reverts and the commits only
/// grow). The rule of the checks and the reviews that must repeat in two rounds belongs to the mailbox (WP3), which holds the pending confirmation.
/// </summary>
public static class RemoteReconciler
{
    public static ReconcileResult Reconcile(RemoteSnapshot? lastRecorded, RemoteSnapshot observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (lastRecorded is null)
        {
            var first = new List<RemoteChangeKind> { RemoteChangeKind.Opened };
            if (observed.State == PullRequestState.Merged)
            {
                first.Add(RemoteChangeKind.Merged);
            }
            else if (observed.State == PullRequestState.Closed)
            {
                first.Add(RemoteChangeKind.Closed);
            }

            if (!observed.HeadBranchExists)
            {
                first.Add(RemoteChangeKind.HeadBranchDeleted);
            }

            if (observed.Deployment is not null)
            {
                first.Add(RemoteChangeKind.DeploymentChanged);
            }

            return new ReconcileResult(false, first);
        }

        if (string.Equals(lastRecorded.Fingerprint, observed.Fingerprint, StringComparison.Ordinal))
        {
            return new ReconcileResult(false, []);
        }

        if (GoesBackwards(lastRecorded, observed))
        {
            return new ReconcileResult(true, []);
        }

        var changes = new List<RemoteChangeKind>();
        var wasOpen = lastRecorded.State is PullRequestState.Open or PullRequestState.Draft;
        if (observed.State == PullRequestState.Merged && lastRecorded.State != PullRequestState.Merged)
        {
            changes.Add(RemoteChangeKind.Merged);
        }
        else if (observed.State == PullRequestState.Closed && wasOpen)
        {
            changes.Add(RemoteChangeKind.Closed);
        }
        else if (lastRecorded.State == PullRequestState.Closed && observed.State is PullRequestState.Open or PullRequestState.Draft)
        {
            changes.Add(RemoteChangeKind.Reopened);
        }

        if (!string.Equals(lastRecorded.HeadCommit, observed.HeadCommit, StringComparison.Ordinal))
        {
            changes.Add(RemoteChangeKind.HeadMoved);
        }

        if (observed.ForcePushes > lastRecorded.ForcePushes)
        {
            changes.Add(RemoteChangeKind.ForcePushed);
        }

        if (lastRecorded.HeadBranchExists && !observed.HeadBranchExists)
        {
            changes.Add(RemoteChangeKind.HeadBranchDeleted);
        }

        if (lastRecorded.Checks != observed.Checks)
        {
            changes.Add(RemoteChangeKind.ChecksChanged);
        }

        if (!SameReviews(lastRecorded.Reviews, observed.Reviews))
        {
            changes.Add(RemoteChangeKind.ReviewsChanged);
        }

        if (observed.RevertCommits.Count > lastRecorded.RevertCommits.Count)
        {
            changes.Add(RemoteChangeKind.RevertSuspected);
        }

        if (lastRecorded.Deployment != observed.Deployment)
        {
            changes.Add(RemoteChangeKind.DeploymentChanged);
        }

        return new ReconcileResult(false, changes);
    }

    /// <summary>
    /// The guard of each resource (docs/21 D-23): a lagging replica of the platform may answer with data older than what is recorded. The pull request is guarded by its
    /// <c>updated_at</c> and merged is terminal; the commits and the timeline only grow (a shorter list for the same head is an old answer); the deployments only advance in
    /// their id; the commits of the base branch since the merge (the reverts) only grow. The checks and the reviews carry no reliable mark: the mailbox asks them to repeat in two rounds.
    /// </summary>
    private static bool GoesBackwards(RemoteSnapshot last, RemoteSnapshot observed) =>
        observed.UpdatedAt < last.UpdatedAt
        || (last.State == PullRequestState.Merged && observed.State != PullRequestState.Merged)
        || observed.ForcePushes < last.ForcePushes
        || observed.RevertCommits.Count < last.RevertCommits.Count
        || (observed.Deployment is { } later && last.Deployment is { } earlier && later.Id < earlier.Id)
        || (string.Equals(last.HeadCommit, observed.HeadCommit, StringComparison.Ordinal)
            && (observed.OriginalCommits.Count < last.OriginalCommits.Count || observed.ResultCommits.Count < last.ResultCommits.Count));

    private static bool SameReviews(ReviewsSummary a, ReviewsSummary b) =>
        a.Approved == b.Approved && a.ChangesRequested == b.ChangesRequested && a.Commented == b.Commented
        && a.Approvers.Select(x => x.Value).Order(StringComparer.Ordinal).SequenceEqual(b.Approvers.Select(x => x.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
