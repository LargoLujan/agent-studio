using System.Net;
using System.Net.Http;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills.GitHub;
using static AgentStudio.Tests.Infrastructure.Skills.GitHub.GitHubTestData;

namespace AgentStudio.Tests.Infrastructure.Skills.GitHub;

/// <summary>F6-13: the only builder of addresses and the guard that checks every request (docs/20 D-14). No test sends anything.</summary>
public sealed class GitHubAddressAndGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitHubSkillOrigin Origin(string folder = "skills/tdd") =>
        GitHubSkillOrigin.Create("octocat", "Hello-World", "main", Pinned, folder);

    [Fact]
    public void The_addresses_are_exactly_these_and_nothing_else()
    {
        Assert.Equal(Repo, GitHubApiAddress.Repository(Origin()).AbsoluteUri);
        Assert.Equal(PinnedCommits, GitHubApiAddress.Commits(Origin(), Pinned).AbsoluteUri);
        Assert.Equal(BranchCommits, GitHubApiAddress.Commits(Origin(), "main").AbsoluteUri);
        Assert.Equal(Repo + "/commits?sha=main&per_page=1", GitHubApiAddress.Commits(Origin(string.Empty), "main").AbsoluteUri);
    }

    [Fact]
    public void Only_the_commit_or_the_branch_of_the_origin_can_be_asked_for()
    {
        Assert.Throws<ArgumentException>(() => GitHubApiAddress.Commits(Origin(), "other"));
        Assert.Throws<ArgumentException>(() => GitHubApiAddress.Commits(Origin(), "main?x=1"));
    }

    [Theory]
    [InlineData("skills/../x")]
    [InlineData("skills?x=1")]
    [InlineData("skills#x")]
    [InlineData("skills%2Ftdd")]
    [InlineData("skills%2e%2e/x")]
    [InlineData("skills\\tdd")]
    public void An_origin_with_a_manipulated_folder_cannot_be_created(string folder)
    {
        Assert.False(GitHubSkillOrigin.TryCreate("octocat", "Hello-World", "main", Pinned, folder, out _, out var problem));
        Assert.Equal(GitHubOriginProblem.Folder, problem);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/octocat/Hello-World", true)]
    [InlineData("https://API.GITHUB.COM/repos/octocat/Hello-World/commits?sha=main", true)]
    [InlineData("https://api.github.com/repositories/1296269/commits", true)]
    [InlineData("http://api.github.com/repos/octocat/Hello-World", false)]
    [InlineData("https://evil.example/repos/octocat/Hello-World", false)]
    [InlineData("https://api.github.com.evil.example/repos/octocat/Hello-World", false)]
    [InlineData("https://api.github.com:8443/repos/octocat/Hello-World", false)]
    [InlineData("https://octocat:secret@api.github.com/repos/octocat/Hello-World", false)]
    [InlineData("https://api.github.com/user", false)]
    [InlineData("https://api.github.com/repos/octocat/../user", false)]
    [InlineData("https://api.github.com/repos/octocat/%2e%2e/user", false)]
    [InlineData("https://api.github.com/repos/octocat/Hello-World#frag", false)]
    [InlineData("ftp://api.github.com/repos/octocat/Hello-World", false)]
    public void The_allowed_address_is_https_api_github_com_under_repos(string address, bool allowed) =>
        Assert.Equal(allowed, GitHubApiAddress.IsAllowed(new Uri(address)));

    [Fact]
    public void A_relative_address_and_null_are_not_allowed()
    {
        Assert.False(GitHubApiAddress.IsAllowed(null));
        Assert.False(GitHubApiAddress.IsAllowed(new Uri("/repos/a/b", UriKind.Relative)));
    }

    [Theory]
    [InlineData("https://evil.example/repos/a/b")]
    [InlineData("http://api.github.com/repos/a/b")]
    [InlineData("https://api.github.com/user")]
    [InlineData("https://octocat:secret@api.github.com/repos/a/b")]
    public async Task The_guard_fails_closed_and_the_inner_handler_never_sees_the_request(string address)
    {
        var inner = new FakeGitHubTransport(_ => FakeGitHubTransport.Answer("{}"));
        using var invoker = new HttpMessageInvoker(new GitHubHostGuardHandler(inner));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, address), Ct));

        Assert.Equal(SkillHealthFailure.RedirectRefused, ex.Failure);
        Assert.DoesNotContain("evil", ex.Message, StringComparison.Ordinal);
        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task The_guard_refuses_a_request_that_is_not_a_plain_get()
    {
        var inner = new FakeGitHubTransport(_ => FakeGitHubTransport.Answer("{}"));
        using var invoker = new HttpMessageInvoker(new GitHubHostGuardHandler(inner));

        await Assert.ThrowsAsync<GitHubApiException>(() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, Repo), Ct));
        await Assert.ThrowsAsync<GitHubApiException>(() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, Repo) { Content = new StringContent("x") }, Ct));

        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task The_guard_lets_an_allowed_request_through()
    {
        var inner = new FakeGitHubTransport(_ => FakeGitHubTransport.Answer("{}"));
        using var invoker = new HttpMessageInvoker(new GitHubHostGuardHandler(inner));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, Repo), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(inner.Sent);
    }

    [Fact]
    public async Task The_client_refuses_an_address_that_is_not_allowed_before_queueing_it()
    {
        var inner = new FakeGitHubTransport(_ => FakeGitHubTransport.Answer("{}"));
        await using var client = new GitHubApiClient(inner);

        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetAsync(new Uri("https://evil.example/repos/a/b"), null, null, Ct));

        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task The_client_can_be_disposed_synchronously_by_the_container_even_with_a_request_made()
    {
        var inner = new FakeGitHubTransport(_ => FakeGitHubTransport.Answer("{}"));
        var client = new GitHubApiClient(inner);
        await client.GetAsync(new Uri(Repo), null, null, Ct);

        client.Dispose();

        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetAsync(new Uri(Repo), null, null, Ct));
    }

    [Fact]
    public void The_production_transport_follows_no_redirection_keeps_no_cookies_and_sends_no_credentials()
    {
        using var transport = GitHubApiClient.CreateTransport();

        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        Assert.Null(transport.Credentials);
        Assert.Null(transport.DefaultProxyCredentials);
        Assert.False(transport.PreAuthenticate);
    }
}
