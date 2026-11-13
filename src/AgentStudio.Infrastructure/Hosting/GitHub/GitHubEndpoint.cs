using AgentStudio.Infrastructure.Hosting.Cli;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// The closed catalog of what the adapter asks of GitHub (docs/21 D-10). Whoever calls names an entry with typed values: it cannot give a path or an argument. There is no entry that writes to
/// <c>/reviews</c> (a review is never sent: D-21), none that merges as an administrator and none that deletes anything but the branch of a run.
/// </summary>
internal enum GitHubEndpoint
{
    /// <summary><c>GET /user</c>: the account that publishes (its <c>login</c> only).</summary>
    Viewer,

    /// <summary><c>GET /repos/{o}/{r}</c>.</summary>
    Repository,

    /// <summary><c>GET /repos/{o}/{r}/rules/branches/{branch}</c>.</summary>
    BranchRules,

    /// <summary><c>GET /repos/{o}/{r}/branches/{branch}</c>: the protection flag, the head of a branch and whether it exists.</summary>
    Branch,

    /// <summary><c>GET /repos/{o}/{r}/pulls?head=…&amp;state=all&amp;per_page=5</c>.</summary>
    FindPullRequests,

    /// <summary><c>POST /repos/{o}/{r}/pulls</c>.</summary>
    OpenPullRequest,

    /// <summary><c>PATCH /repos/{o}/{r}/pulls/{n}</c>, only <c>title</c> and <c>body</c>.</summary>
    UpdatePullRequest,

    /// <summary><c>PATCH /repos/{o}/{r}/pulls/{n}</c>, only <c>state: closed</c>. Destructive.</summary>
    ClosePullRequest,

    /// <summary><c>GET /repos/{o}/{r}/pulls/{n}</c>.</summary>
    PullRequest,

    /// <summary><c>GET /repos/{o}/{r}/pulls/{n}/commits?per_page=50&amp;page={1..6}</c>.</summary>
    PullRequestCommits,

    /// <summary><c>GET /repos/{o}/{r}/issues/{n}/timeline?per_page=30&amp;page={1..10}</c>.</summary>
    PullRequestTimeline,

    /// <summary><c>GET /repos/{o}/{r}/pulls/{n}/reviews?per_page=50&amp;page={1..10}</c>. Only <c>GET</c>.</summary>
    Reviews,

    /// <summary><c>POST /repos/{o}/{r}/pulls/{n}/requested_reviewers</c>.</summary>
    RequestReviewers,

    /// <summary><c>GET /repos/{o}/{r}/commits/{sha}/check-runs?per_page=100</c>.</summary>
    CheckRuns,

    /// <summary><c>GET /repos/{o}/{r}/commits/{sha}/status?per_page=100</c>.</summary>
    CombinedStatus,

    /// <summary><c>GET /repos/{o}/{r}/commits/{sha}</c>: the parents and the author of a commit (the way a merge was made).</summary>
    Commit,

    /// <summary><c>GET /repos/{o}/{r}/commits?sha={branch}&amp;since={date}&amp;per_page=50&amp;page={1..6}</c> (a revert), or <c>?sha={commit}&amp;per_page={n}</c> (the commits a rebase left).</summary>
    CommitList,

    /// <summary><c>POST /repos/{o}/{r}/statuses/{sha}</c>: the status of the verdict.</summary>
    PostCommitStatus,

    /// <summary><c>POST /repos/{o}/{r}/issues/{n}/comments</c>.</summary>
    PostComment,

    /// <summary><c>GET /repos/{o}/{r}/issues/comments/{id}</c>: read before an edit, to check that it is the comment of the verdict.</summary>
    Comment,

    /// <summary><c>PATCH /repos/{o}/{r}/issues/comments/{id}</c>.</summary>
    EditComment,

    /// <summary><c>GET /repos/{o}/{r}/issues/{n}/comments?per_page=30&amp;page={1..10}</c>.</summary>
    Comments,

    /// <summary><c>PUT /repos/{o}/{r}/pulls/{n}/merge</c> (<c>merge_method</c> and <c>sha</c>).</summary>
    Merge,

    /// <summary><c>DELETE /repos/{o}/{r}/git/refs/heads/{branch}</c>. Destructive: only the branch of a run.</summary>
    DeleteBranch,

    /// <summary><c>GET /repos/{o}/{r}/deployments?sha={sha}&amp;per_page=20</c>.</summary>
    Deployments,

    /// <summary><c>GET /repos/{o}/{r}/deployments/{id}/statuses?per_page=10</c>.</summary>
    DeploymentStatuses,
}

internal static class GitHubEndpoints
{
    /// <summary>The method of every entry: the catalog fixes it, nobody passes it.</summary>
    internal static GhMethod MethodOf(GitHubEndpoint endpoint) => endpoint switch
    {
        GitHubEndpoint.OpenPullRequest or GitHubEndpoint.RequestReviewers or GitHubEndpoint.PostCommitStatus or GitHubEndpoint.PostComment => GhMethod.Post,
        GitHubEndpoint.UpdatePullRequest or GitHubEndpoint.ClosePullRequest or GitHubEndpoint.EditComment => GhMethod.Patch,
        GitHubEndpoint.Merge => GhMethod.Put,
        GitHubEndpoint.DeleteBranch => GhMethod.Delete,
        GitHubEndpoint.Viewer or GitHubEndpoint.Repository or GitHubEndpoint.BranchRules or GitHubEndpoint.Branch or GitHubEndpoint.FindPullRequests or GitHubEndpoint.PullRequest
            or GitHubEndpoint.PullRequestCommits or GitHubEndpoint.PullRequestTimeline or GitHubEndpoint.Reviews or GitHubEndpoint.CheckRuns or GitHubEndpoint.CombinedStatus
            or GitHubEndpoint.Commit or GitHubEndpoint.CommitList or GitHubEndpoint.Comment or GitHubEndpoint.Comments or GitHubEndpoint.Deployments or GitHubEndpoint.DeploymentStatuses => GhMethod.Get,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown entry of the catalog."),
    };
}

/// <summary>
/// A path ready for <c>gh api</c> and the entry of the catalog it belongs to. Only <see cref="GitHubApiPath"/> builds one, and it checks the whole path against the form the runner accepts
/// before it gives it out.
/// </summary>
internal sealed record GitHubTarget
{
    internal GitHubTarget(GitHubEndpoint endpoint, string path)
    {
        if (!GhArguments.IsAllowedPath(path))
        {
            throw new ArgumentException("The path is not one the adapter builds.", nameof(path));
        }

        Endpoint = endpoint;
        Path = path;
    }

    internal GitHubEndpoint Endpoint { get; }

    internal string Path { get; }

    /// <summary>The call: the method comes from the catalog; the <paramref name="eTag"/> is dropped when it has not the exact form of one.</summary>
    internal GhCall Call(string? body = null, string? eTag = null) =>
        new(GitHubEndpoints.MethodOf(Endpoint), Path, GhArguments.IsETag(eTag) ? eTag : null, body);
}
