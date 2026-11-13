using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Hosting.Cli;
using AgentStudio.Infrastructure.Hosting.GitHub;

namespace AgentStudio.Tests.Infrastructure.Hosting.GitHub;

/// <summary>
/// docs/21 D-10 and F10-12: the one builder of paths and the one builder of queries. A path is always <c>/user</c> or <c>/repos/{owner}/{name}/…</c>; each segment is escaped one by one so a branch keeps its
/// slash literal; each value of a query goes through <c>EscapeDataString</c>, so a value cannot add a parameter, cut the address or reach <c>gh</c> as a brace.
/// </summary>
public sealed class GitHubApiPathTests
{
    private static readonly RemoteRepository Repo = GhRig.Repo;
    private static readonly RemoteBranch Main = GhRig.Main;
    private static readonly RemoteBranch Run = GhRig.RunBranch;
    private static readonly PullRequestRef Pr = GhRig.Pr;

    private static List<(GitHubTarget Target, string Path, GhMethod Method)> Table() =>
    [
        (GitHubApiPath.Viewer(), "/user", GhMethod.Get),
        (GitHubApiPath.Repository(Repo), "/repos/octo/hello", GhMethod.Get),
        (GitHubApiPath.Branch(Repo, Run), "/repos/octo/hello/branches/agentstudio/run-run1", GhMethod.Get),
        (GitHubApiPath.BranchRules(Repo, Main), "/repos/octo/hello/rules/branches/main", GhMethod.Get),
        (GitHubApiPath.FindPullRequests(Repo, Run), "/repos/octo/hello/pulls?head=octo%3Aagentstudio%2Frun-run1&state=all&per_page=5", GhMethod.Get),
        (GitHubApiPath.OpenPullRequest(Repo), "/repos/octo/hello/pulls", GhMethod.Post),
        (GitHubApiPath.PullRequest(GitHubEndpoint.PullRequest, Pr), "/repos/octo/hello/pulls/7", GhMethod.Get),
        (GitHubApiPath.PullRequest(GitHubEndpoint.UpdatePullRequest, Pr), "/repos/octo/hello/pulls/7", GhMethod.Patch),
        (GitHubApiPath.PullRequest(GitHubEndpoint.ClosePullRequest, Pr), "/repos/octo/hello/pulls/7", GhMethod.Patch),
        (GitHubApiPath.PullRequestCommits(Pr, 2), "/repos/octo/hello/pulls/7/commits?per_page=50&page=2", GhMethod.Get),
        (GitHubApiPath.PullRequestTimeline(Pr, 3), "/repos/octo/hello/issues/7/timeline?per_page=30&page=3", GhMethod.Get),
        (GitHubApiPath.Reviews(Pr, 2), "/repos/octo/hello/pulls/7/reviews?per_page=50&page=2", GhMethod.Get),
        (GitHubApiPath.RequestReviewers(Pr), "/repos/octo/hello/pulls/7/requested_reviewers", GhMethod.Post),
        (GitHubApiPath.Merge(Pr), "/repos/octo/hello/pulls/7/merge", GhMethod.Put),
        (GitHubApiPath.CheckRuns(Repo, Gh.Sha1), "/repos/octo/hello/commits/" + Gh.Sha1 + "/check-runs?per_page=100", GhMethod.Get),
        (GitHubApiPath.CombinedStatus(Repo, Gh.Sha1), "/repos/octo/hello/commits/" + Gh.Sha1 + "/status?per_page=100", GhMethod.Get),
        (GitHubApiPath.Commit(Repo, Gh.Sha1), "/repos/octo/hello/commits/" + Gh.Sha1, GhMethod.Get),
        (GitHubApiPath.CommitsSince(Repo, Main, new DateTimeOffset(2026, 11, 13, 10, 5, 7, TimeSpan.FromHours(2))), "/repos/octo/hello/commits?sha=main&since=2026-11-13T08%3A05%3A07Z&per_page=50&page=1", GhMethod.Get),
        (GitHubApiPath.CommitsUpTo(Repo, Gh.Sha2, 3), "/repos/octo/hello/commits?sha=" + Gh.Sha2 + "&per_page=3", GhMethod.Get),
        (GitHubApiPath.PostCommitStatus(Repo, Gh.Sha1), "/repos/octo/hello/statuses/" + Gh.Sha1, GhMethod.Post),
        (GitHubApiPath.PostComment(Pr), "/repos/octo/hello/issues/7/comments", GhMethod.Post),
        (GitHubApiPath.Comments(Pr, 1), "/repos/octo/hello/issues/7/comments?per_page=30&page=1", GhMethod.Get),
        (GitHubApiPath.Comment(Repo, 4242), "/repos/octo/hello/issues/comments/4242", GhMethod.Get),
        (GitHubApiPath.EditComment(Repo, 4242), "/repos/octo/hello/issues/comments/4242", GhMethod.Patch),
        (GitHubApiPath.DeleteBranch(Repo, Run), "/repos/octo/hello/git/refs/heads/agentstudio/run-run1", GhMethod.Delete),
        (GitHubApiPath.Deployments(Repo, Gh.Sha1), "/repos/octo/hello/deployments?sha=" + Gh.Sha1 + "&per_page=20", GhMethod.Get),
        (GitHubApiPath.DeploymentStatuses(Repo, 501), "/repos/octo/hello/deployments/501/statuses?per_page=10", GhMethod.Get),
    ];

    [Fact]
    public void Each_entry_builds_the_path_of_the_design_with_the_method_of_the_catalog()
    {
        foreach (var (target, expected, method) in Table())
        {
            Assert.Equal(expected, target.Path);
            Assert.Equal(method, GitHubEndpoints.MethodOf(target.Endpoint));
            Assert.True(GhArguments.IsAllowedPath(target.Path));
            Assert.DoesNotContain(target.Path, c => c is '{' or '}');
            Assert.Equal(method, target.Call().Method);
        }
    }

    [Fact]
    public void The_builders_cover_every_entry_of_the_catalog()
    {
        var covered = Table().Select(t => t.Target.Endpoint).ToHashSet();

        Assert.Equal(Enum.GetValues<GitHubEndpoint>().ToHashSet(), covered);
    }

    [Fact]
    public void The_catalog_writes_only_where_the_design_says_and_never_to_the_reviews()
    {
        var writes = Enum.GetValues<GitHubEndpoint>().Where(e => GitHubEndpoints.MethodOf(e) != GhMethod.Get).Order().ToList();

        Assert.Equal(
            new[]
            {
                GitHubEndpoint.OpenPullRequest, GitHubEndpoint.UpdatePullRequest, GitHubEndpoint.ClosePullRequest, GitHubEndpoint.RequestReviewers, GitHubEndpoint.PostCommitStatus,
                GitHubEndpoint.PostComment, GitHubEndpoint.EditComment, GitHubEndpoint.Merge, GitHubEndpoint.DeleteBranch,
            }.Order(),
            writes);
        Assert.Equal(GhMethod.Get, GitHubEndpoints.MethodOf(GitHubEndpoint.Reviews));
    }

    [Fact]
    public void A_pull_request_entry_that_is_not_the_pull_request_itself_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.PullRequest(GitHubEndpoint.Merge, Pr));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.PullRequest(GitHubEndpoint.Reviews, Pr));
    }

    [Fact]
    public void A_value_of_a_query_is_escaped_so_it_cannot_add_a_parameter_cut_the_address_or_be_a_brace()
    {
        var query = new GitHubApiQuery().Add("head", "o:a&b=c#d%e+f{g}h i?j/k\\l\"m|n").Add("per_page", 5).ToString();

        Assert.Equal("?head=o%3Aa%26b%3Dc%23d%25e%2Bf%7Bg%7Dh%20i%3Fj%2Fk%5Cl%22m%7Cn&per_page=5", query);
        Assert.True(GhArguments.IsAllowedPath("/repos/octo/hello/pulls" + query));
        Assert.Equal(string.Empty, new GitHubApiQuery().ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1111111111111111111111111111111111111111/../x")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("111111111111111111111111111111111111111")]
    [InlineData("11111111111111111111111111111111111111111")]
    [InlineData("main")]
    public void A_commit_that_is_not_a_full_lowercase_id_is_never_a_segment(string commit)
    {
        Assert.False(GitHubApiPath.IsCommitId(commit));
        Assert.Throws<ArgumentException>(() => GitHubApiPath.Commit(Repo, commit));
        Assert.Throws<ArgumentException>(() => GitHubApiPath.CheckRuns(Repo, commit));
        Assert.Throws<ArgumentException>(() => GitHubApiPath.PostCommitStatus(Repo, commit));
        Assert.Throws<ArgumentException>(() => GitHubApiPath.Deployments(Repo, commit));
    }

    [Fact]
    public void A_commit_of_64_characters_is_a_full_id_as_well()
    {
        Assert.True(GitHubApiPath.IsCommitId(new string('a', 64)));
        Assert.True(GitHubApiPath.IsCommitId(new string('a', 40)));
    }

    [Fact]
    public void A_page_a_count_or_an_id_outside_their_range_is_never_built()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.PullRequestCommits(Pr, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.PullRequestCommits(Pr, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.PullRequestTimeline(Pr, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.PullRequestTimeline(Pr, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.Reviews(Pr, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.Reviews(Pr, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.Comments(Pr, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.CommitsSince(Repo, Main, DateTimeOffset.UnixEpoch, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.Comment(Repo, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.CommitsUpTo(Repo, Gh.Sha1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.CommitsUpTo(Repo, Gh.Sha1, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.EditComment(Repo, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHubApiPath.DeploymentStatuses(Repo, 0));
    }

    [Fact]
    public void A_target_cannot_hold_a_path_the_runner_would_refuse()
    {
        Assert.Throws<ArgumentException>(() => new GitHubTarget(GitHubEndpoint.Viewer, "/user/../orgs/x"));
        Assert.Throws<ArgumentException>(() => new GitHubTarget(GitHubEndpoint.Viewer, "user"));
    }

    [Fact]
    public void An_etag_that_has_not_the_exact_form_is_dropped_and_the_call_goes_without_condition()
    {
        var target = GitHubApiPath.PullRequest(GitHubEndpoint.PullRequest, Pr);

        Assert.Equal("W/\"a\"", target.Call(eTag: "W/\"a\"").IfNoneMatch);
        Assert.Null(target.Call(eTag: "\"a\"\nX-Evil: 1").IfNoneMatch);
        Assert.Null(target.Call(eTag: "abc").IfNoneMatch);
        Assert.Null(target.Call(eTag: null).IfNoneMatch);
    }
}
