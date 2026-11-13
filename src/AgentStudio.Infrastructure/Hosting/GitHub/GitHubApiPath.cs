using System.Globalization;
using System.Text;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// The only builder of the query of a path (docs/21 D-10): each name is a constant of the adapter and each value goes through <see cref="Uri.EscapeDataString"/>, so a branch with <c>&amp;</c>,
/// <c>#</c>, <c>%</c>, <c>+</c>, <c>{</c> or <c>}</c> cannot add a parameter, cut the address or reach <c>gh</c> as a brace.
/// </summary>
internal sealed class GitHubApiQuery
{
    private readonly StringBuilder _text = new();

    internal GitHubApiQuery Add(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _text.Append(_text.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        return this;
    }

    internal GitHubApiQuery Add(string name, int value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

    public override string ToString() => _text.ToString();
}

/// <summary>
/// The only builder of paths for <c>gh api</c> (docs/21 D-10): always <c>/user</c> or <c>/repos/{owner}/{name}/…</c> with every segment escaped one by one, and the whole path checked against
/// the form the runner accepts. A branch is split at its slashes and each piece is escaped apart, so the slash stays literal. What does not come from a validated type (a commit that is not a full id,
/// a page outside 1 to 3, a count outside 1 to 100) throws: the provider refuses it before it gets here.
/// </summary>
internal static class GitHubApiPath
{
    /// <summary>The most pages of a list: three of a hundred (docs/21 D-10).</summary>
    internal const int MaxPages = 3;

    internal const int PageSize = 100;

    private static GitHubTarget Make(GitHubEndpoint endpoint, string path) => new(endpoint, path);

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static string Repo(RemoteRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return "/repos/" + Segment(repository.Owner) + "/" + Segment(repository.Name);
    }

    private static string BranchPath(RemoteBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return string.Join('/', branch.Name.Split('/').Select(Segment));
    }

    private static string Number(int value) =>
        value >= 1 ? value.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(value), "A number is greater than 0.");

    private static string Page(int page) =>
        page is >= 1 and <= MaxPages ? page.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(page), "A page is 1 to 3.");

    /// <summary>A full commit id: 40 or 64 lowercase hexadecimal characters (the rule of the domain is internal to it).</summary>
    internal static bool IsCommitId(string? value) =>
        value is { Length: 40 or 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string Commit(string sha) =>
        IsCommitId(sha) ? sha : throw new ArgumentException("A commit is a full id of 40 or 64 hexadecimal characters.", nameof(sha));

    private static string PullRequestPath(PullRequestRef pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return Repo(pullRequest.Repository) + "/pulls/" + Number(pullRequest.Number);
    }

    private static string IssuePath(PullRequestRef pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        return Repo(pullRequest.Repository) + "/issues/" + Number(pullRequest.Number);
    }

    internal static GitHubTarget Viewer() => Make(GitHubEndpoint.Viewer, "/user");

    internal static GitHubTarget Repository(RemoteRepository repository) => Make(GitHubEndpoint.Repository, Repo(repository));

    internal static GitHubTarget Branch(RemoteRepository repository, RemoteBranch branch) =>
        Make(GitHubEndpoint.Branch, Repo(repository) + "/branches/" + BranchPath(branch));

    internal static GitHubTarget BranchRules(RemoteRepository repository, RemoteBranch branch) =>
        Make(GitHubEndpoint.BranchRules, Repo(repository) + "/rules/branches/" + BranchPath(branch));

    internal static GitHubTarget FindPullRequests(RemoteRepository repository, RemoteBranch head)
    {
        var query = new GitHubApiQuery().Add("head", repository.Owner + ":" + head.Name).Add("state", "all").Add("per_page", 5);
        return Make(GitHubEndpoint.FindPullRequests, Repo(repository) + "/pulls" + query);
    }

    internal static GitHubTarget OpenPullRequest(RemoteRepository repository) => Make(GitHubEndpoint.OpenPullRequest, Repo(repository) + "/pulls");

    /// <summary>The pull request itself: <see cref="GitHubEndpoint.PullRequest"/> to read it, <see cref="GitHubEndpoint.UpdatePullRequest"/> or <see cref="GitHubEndpoint.ClosePullRequest"/> to change it.</summary>
    internal static GitHubTarget PullRequest(GitHubEndpoint endpoint, PullRequestRef pullRequest)
    {
        if (endpoint is not (GitHubEndpoint.PullRequest or GitHubEndpoint.UpdatePullRequest or GitHubEndpoint.ClosePullRequest))
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "That entry is not the pull request itself.");
        }

        return Make(endpoint, PullRequestPath(pullRequest));
    }

    internal static GitHubTarget PullRequestCommits(PullRequestRef pullRequest, int page) =>
        Make(GitHubEndpoint.PullRequestCommits, PullRequestPath(pullRequest) + "/commits" + new GitHubApiQuery().Add("per_page", PageSize).Add("page", Page(page)));

    internal static GitHubTarget PullRequestTimeline(PullRequestRef pullRequest, int page) =>
        Make(GitHubEndpoint.PullRequestTimeline, IssuePath(pullRequest) + "/timeline" + new GitHubApiQuery().Add("per_page", PageSize).Add("page", Page(page)));

    internal static GitHubTarget Reviews(PullRequestRef pullRequest) =>
        Make(GitHubEndpoint.Reviews, PullRequestPath(pullRequest) + "/reviews" + new GitHubApiQuery().Add("per_page", PageSize));

    internal static GitHubTarget RequestReviewers(PullRequestRef pullRequest) =>
        Make(GitHubEndpoint.RequestReviewers, PullRequestPath(pullRequest) + "/requested_reviewers");

    internal static GitHubTarget Merge(PullRequestRef pullRequest) => Make(GitHubEndpoint.Merge, PullRequestPath(pullRequest) + "/merge");

    internal static GitHubTarget CheckRuns(RemoteRepository repository, string sha) =>
        Make(GitHubEndpoint.CheckRuns, Repo(repository) + "/commits/" + Commit(sha) + "/check-runs" + new GitHubApiQuery().Add("per_page", PageSize));

    internal static GitHubTarget CombinedStatus(RemoteRepository repository, string sha) =>
        Make(GitHubEndpoint.CombinedStatus, Repo(repository) + "/commits/" + Commit(sha) + "/status" + new GitHubApiQuery().Add("per_page", PageSize));

    internal static GitHubTarget Commit(RemoteRepository repository, string sha) =>
        Make(GitHubEndpoint.Commit, Repo(repository) + "/commits/" + Commit(sha));

    /// <summary>The commits of <paramref name="branch"/> since <paramref name="since"/> (UTC, to the second): where a revert of a merge shows up.</summary>
    internal static GitHubTarget CommitsSince(RemoteRepository repository, RemoteBranch branch, DateTimeOffset since)
    {
        var query = new GitHubApiQuery()
            .Add("sha", branch.Name)
            .Add("since", since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Add("per_page", 50);
        return Make(GitHubEndpoint.CommitList, Repo(repository) + "/commits" + query);
    }

    /// <summary>The <paramref name="count"/> commits that end at <paramref name="sha"/>, newest first: what a rebase left in the base branch.</summary>
    internal static GitHubTarget CommitsUpTo(RemoteRepository repository, string sha, int count)
    {
        if (count is < 1 or > PageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "A count is 1 to 100.");
        }

        var query = new GitHubApiQuery().Add("sha", Commit(sha)).Add("per_page", count);
        return Make(GitHubEndpoint.CommitList, Repo(repository) + "/commits" + query);
    }

    internal static GitHubTarget PostCommitStatus(RemoteRepository repository, string sha) =>
        Make(GitHubEndpoint.PostCommitStatus, Repo(repository) + "/statuses/" + Commit(sha));

    internal static GitHubTarget PostComment(PullRequestRef pullRequest) => Make(GitHubEndpoint.PostComment, IssuePath(pullRequest) + "/comments");

    internal static GitHubTarget Comments(PullRequestRef pullRequest, int page) =>
        Make(GitHubEndpoint.Comments, IssuePath(pullRequest) + "/comments" + new GitHubApiQuery().Add("per_page", PageSize).Add("page", Page(page)));

    internal static GitHubTarget EditComment(RemoteRepository repository, long commentId) =>
        Make(GitHubEndpoint.EditComment, Repo(repository) + "/issues/comments/" + (commentId >= 1 ? commentId.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(commentId), "An id is greater than 0.")));

    internal static GitHubTarget DeleteBranch(RemoteRepository repository, RemoteBranch branch) =>
        Make(GitHubEndpoint.DeleteBranch, Repo(repository) + "/git/refs/heads/" + BranchPath(branch));

    internal static GitHubTarget Deployments(RemoteRepository repository, string sha) =>
        Make(GitHubEndpoint.Deployments, Repo(repository) + "/deployments" + new GitHubApiQuery().Add("sha", Commit(sha)).Add("per_page", 20));

    internal static GitHubTarget DeploymentStatuses(RemoteRepository repository, long deploymentId) =>
        Make(GitHubEndpoint.DeploymentStatuses, Repo(repository) + "/deployments/" + (deploymentId >= 1 ? deploymentId.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(deploymentId), "An id is greater than 0.")) + "/statuses" + new GitHubApiQuery().Add("per_page", 10));
}
