using System.IO;
using System.Net;
using System.Net.Http;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills.GitHub;
using static AgentStudio.Tests.Infrastructure.Skills.GitHub.GitHubTestData;
using FakeSecrets = AgentStudio.Tests.Security.FakeSecrets;

namespace AgentStudio.Tests.Infrastructure.Skills.GitHub;

/// <summary>
/// F6-6 and F6-13: the batch against the API of GitHub, with a transport that only answers (hand-written fixtures, no network): the exact addresses, what each
/// answer means, the opt-in, a manipulated origin, the redirections, the limit and the token.
/// </summary>
public sealed class GitHubSkillHealthCheckerTests : IAsyncDisposable
{
    private readonly string _folder = NewFolder();
    private readonly List<GitHubApiClient> _clients = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        DeleteFolder(_folder);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SkillHealthCheck Check(SkillHealthBatchEntry entry, string code) => Assert.Single(entry.Report.Checks, c => c.Code == code);

    private (GitHubSkillHealthChecker Checker, FakeGitHubTransport Transport, StubDoctorSecrets Secrets) Rig(
        Func<SentRequest, HttpResponseMessage> route,
        bool allowed = true,
        SkillsDoctorTokenUse? use = null)
    {
        var transport = new FakeGitHubTransport(route);
        var client = new GitHubApiClient(transport);
        _clients.Add(client);
        var secrets = new StubDoctorSecrets(use ?? SkillsDoctorTokenUse.None);
        return (new GitHubSkillHealthChecker(new StubPolicy(allowed), secrets, client, new GitHubResponseCache(_folder)), transport, secrets);
    }

    [Fact]
    public async Task A_pinned_skill_whose_branch_has_not_moved_is_healthy_with_exactly_three_requests()
    {
        var (checker, transport, _) = Rig(Healthy);

        var result = await checker.CheckAsync([Skill()], Ct);

        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.Failure);
        Assert.Equal(SkillHealthStatus.Healthy, Check(entry, SkillHealthCodes.Archived).Status);
        Assert.Equal(SkillHealthStatus.Healthy, Check(entry, SkillHealthCodes.Upstream).Status);
        Assert.Equal([Repo, PinnedCommits, BranchCommits], transport.Sent.Select(s => s.Url).ToArray());
        Assert.All(transport.Sent, s =>
        {
            Assert.Null(s.Authorization);
            Assert.Equal("AgentStudio-SkillsDoctor", s.UserAgent);
        });
        Assert.False(result.UsedToken);
    }

    [Fact]
    public async Task A_folder_that_changed_upstream_is_an_update_to_review()
    {
        var (checker, _, _) = Rig(sent => sent.Url == BranchCommits ? FakeGitHubTransport.Fixture("commits-branch-newer.json") : Healthy(sent));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        var upstream = Check(entry, SkillHealthCodes.Upstream);
        Assert.Equal(SkillHealthStatus.Warning, upstream.Status);
        Assert.Contains("Actualización por revisar", upstream.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_empty_answers_are_a_folder_that_is_not_there_never_a_healthy_upstream()
    {
        var (checker, _, _) = Rig(sent => sent.Url == Repo ? Healthy(sent) : FakeGitHubTransport.Fixture("commits-empty.json"));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        var upstream = Check(entry, SkillHealthCodes.Upstream);
        Assert.Equal(SkillHealthStatus.Warning, upstream.Status);
        Assert.Contains("No se encontró la carpeta", upstream.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exception_of_any_type_fails_that_request_but_the_queue_keeps_answering_and_the_text_does_not_leak()
    {
        const string sentinel = "sentinel-in-an-unlisted-exception";
        var calls = 0;
        var (checker, transport, _) = Rig(sent => Interlocked.Increment(ref calls) == 1 ? throw new ArgumentException(sentinel) : Healthy(sent));

        var result = await checker.CheckAsync([Skill("a"), Skill("b")], Ct);

        Assert.Equal(SkillHealthFailure.UnreadableResponse, result.Entries[0].Failure);
        Assert.Null(result.Entries[1].Failure);
        Assert.Equal(4, transport.Sent.Count);
        Assert.DoesNotContain(sentinel, string.Join('\n', result.Entries.SelectMany(e => e.Report.Checks).Select(c => c.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_archived_repository_is_critical()
    {
        var (checker, _, _) = Rig(sent => sent.Url == Repo ? FakeGitHubTransport.Fixture("repo-archived.json") : Healthy(sent));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthStatus.Critical, Check(entry, SkillHealthCodes.Archived).Status);
    }

    [Fact]
    public async Task A_pinned_commit_that_no_longer_exists_is_a_warning()
    {
        var (checker, _, _) = Rig(sent => sent.Url == PinnedCommits ? FakeGitHubTransport.Answer("{}", HttpStatusCode.NotFound) : Healthy(sent));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthStatus.Warning, Check(entry, SkillHealthCodes.Upstream).Status);
    }

    [Fact]
    public async Task A_deleted_or_private_repository_is_a_warning_and_asks_nothing_more()
    {
        var (checker, transport, _) = Rig(_ => FakeGitHubTransport.Answer("{}", HttpStatusCode.NotFound));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.NotFound, entry.Failure);
        Assert.Equal(SkillHealthStatus.Warning, Check(entry, SkillHealthCodes.Archived).Status);
        Assert.Equal(SkillHealthStatus.Unknown, Check(entry, SkillHealthCodes.Upstream).Status);
        Assert.Equal([Repo], transport.Sent.Select(s => s.Url).ToArray());
    }

    [Fact]
    public async Task The_second_batch_asks_with_if_none_match_and_a_304_is_read_from_the_cache()
    {
        var (checker, transport, _) = Rig(sent => sent.IfNoneMatch is not null ? FakeGitHubTransport.Answer(string.Empty, HttpStatusCode.NotModified) : Healthy(sent));

        await checker.CheckAsync([Skill()], Ct);
        var second = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(6, transport.Sent.Count);
        Assert.All(transport.Sent.Take(3), s => Assert.Null(s.IfNoneMatch));
        Assert.Equal(["\"repo-1\"", "\"pinned-1\"", "\"branch-1\""], transport.Sent.Skip(3).Select(s => s.IfNoneMatch).ToArray());
        Assert.Null(second.Failure);
        Assert.Equal(SkillHealthStatus.Healthy, Check(second, SkillHealthCodes.Upstream).Status);
        Assert.Contains("pinned-1", File.ReadAllText(Path.Combine(_folder, "cache.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cache_keeps_the_etag_and_the_two_values_never_the_body()
    {
        var (checker, _, _) = Rig(Healthy);

        await checker.CheckAsync([Skill()], Ct);

        var cache = File.ReadAllText(Path.Combine(_folder, "cache.json"));
        Assert.DoesNotContain("evil.example", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("Texto", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("Fix all the bugs", cache, StringComparison.Ordinal);
        Assert.Contains(Pinned, cache, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hostile_cache_file_is_ignored()
    {
        Directory.CreateDirectory(_folder);
        var key = GitHubResponseCache.KeyOf(new Uri(Repo), authenticated: false);
        File.WriteAllText(Path.Combine(_folder, "cache.json"), "{\"entries\":{\"" + key + "\":{\"etag\":\"a\\r\\nX-Evil: 1\",\"archived\":false},\"n|/x\":{\"etag\":\"ok\",\"sha\":\"../../user\"}}}");
        var (checker, transport, _) = Rig(Healthy);

        await checker.CheckAsync([Skill()], Ct);

        Assert.All(transport.Sent, s => Assert.Null(s.IfNoneMatch));
    }

    [Fact]
    public async Task With_the_setting_off_not_one_request_is_built_and_the_secrets_are_not_touched()
    {
        var (checker, transport, secrets) = Rig(Healthy, allowed: false);

        var result = await checker.CheckAsync([Skill(), Skill("other")], Ct);

        Assert.Empty(transport.Sent);
        Assert.Equal(0, secrets.Calls);
        Assert.All(result.Entries, e => Assert.Equal(SkillHealthFailure.NetworkNotAllowed, e.Failure));
        Assert.All(result.Entries, e => Assert.All(e.Report.Checks, c => Assert.Equal(SkillHealthStatus.Unknown, c.Status)));
        Assert.False(File.Exists(Path.Combine(_folder, "cache.json")));
    }

    [Theory]
    [InlineData("octocat/../user", "main", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World?x=1", "main", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello%2FWorld", "main", Pinned, "skills/tdd")]
    [InlineData("octocat@evil.example/Hello-World", "main", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World/extra", "main", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World", "main/../../user", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World", "main?sha=x", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World", "main%2Fx", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World", "ma@{in", Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World", null, Pinned, "skills/tdd")]
    [InlineData("octocat/Hello-World", "main", null, "skills/tdd")]
    [InlineData("octocat/Hello-World", "main", "6DCB09B5B57875F334F61AEBED695E2E4193DB5E", "skills/tdd")]
    [InlineData("octocat/Hello-World", "main", "main", "skills/tdd")]
    [InlineData("octocat/Hello-World", "main", Pinned, "skills/../x")]
    [InlineData("octocat/Hello-World", "main", Pinned, "skills/%2e%2e/x")]
    [InlineData("octocat/Hello-World", "main", Pinned, "skills?x=1")]
    [InlineData("octocat/Hello-World", "main", Pinned, "skills#x")]
    [InlineData("octocat/Hello-World", "main", Pinned, "skills@x")]
    public async Task A_manipulated_origin_is_invalid_and_no_request_is_made(string repository, string? branch, string? commit, string path)
    {
        var (checker, transport, secrets) = Rig(Healthy);

        var result = await checker.CheckAsync([Skill(repository: repository, branch: branch, commit: commit, path: path)], Ct);

        Assert.Empty(transport.Sent);
        Assert.Equal(0, secrets.Calls);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(SkillHealthFailure.InvalidOrigin, entry.Failure);
        Assert.Contains("origen no válido", Check(entry, SkillHealthCodes.Upstream).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_invalid_skill_does_not_stop_the_valid_one()
    {
        var (checker, transport, _) = Rig(Healthy);

        var result = await checker.CheckAsync([Skill("bad", repository: "octocat/../user"), Skill()], Ct);

        Assert.Equal(SkillHealthFailure.InvalidOrigin, result.Entries[0].Failure);
        Assert.Null(result.Entries[1].Failure);
        Assert.Equal(3, transport.Sent.Count);
    }

    [Fact]
    public async Task A_redirection_to_another_host_is_refused_and_nothing_is_sent_there()
    {
        var (checker, transport, _) = Rig(_ => FakeGitHubTransport.Redirect(HttpStatusCode.MovedPermanently, "https://evil.example/repos/octocat/Hello-World"));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.RedirectRefused, entry.Failure);
        Assert.Single(transport.Sent);
        Assert.DoesNotContain(transport.Sent, s => s.Url.Contains("evil", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://api.github.com/repos/octocat/Hello-World")]
    [InlineData("https://api.github.com/user")]
    [InlineData("https://api.github.com:8443/repos/octocat/Hello-World")]
    [InlineData("https://octocat:x@api.github.com/repos/octocat/Hello-World")]
    public async Task A_redirection_to_http_or_outside_the_repos_is_refused(string location)
    {
        var (checker, transport, _) = Rig(_ => FakeGitHubTransport.Redirect(HttpStatusCode.Found, location));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.RedirectRefused, entry.Failure);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task A_redirection_to_the_same_host_is_followed_and_the_token_does_not_travel_after_it()
    {
        var token = new GitHubToken(FakeSecrets.GitHubFineGrainedToken());
        var (checker, transport, _) = Rig(
            sent => sent.Url == Repo
                ? FakeGitHubTransport.Redirect(HttpStatusCode.MovedPermanently, "https://api.github.com/repositories/1296269")
                : sent.Url == "https://api.github.com/repositories/1296269" ? FakeGitHubTransport.Fixture("repo-active.json", "\"moved\"") : Healthy(sent),
            use: new SkillsDoctorTokenUse(SkillsDoctorTokenState.Granted, token));

        var result = await checker.CheckAsync([Skill()], Ct);

        Assert.Null(Assert.Single(result.Entries).Failure);
        var first = transport.Sent[0];
        var followed = transport.Sent[1];
        Assert.Equal(Repo, first.Url);
        Assert.NotNull(first.Authorization);
        Assert.Equal("https://api.github.com/repositories/1296269", followed.Url);
        Assert.Null(followed.Authorization);
        Assert.Null(followed.IfNoneMatch);

        // What came after a redirection is not kept: its ETag belongs to another address.
        Assert.DoesNotContain("moved", File.ReadAllText(Path.Combine(_folder, "cache.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_three_redirections_are_refused()
    {
        var hops = 0;
        var (checker, transport, _) = Rig(_ => FakeGitHubTransport.Redirect(HttpStatusCode.TemporaryRedirect, "https://api.github.com/repos/octocat/Hello-World/" + Interlocked.Increment(ref hops)));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.RedirectRefused, entry.Failure);
        Assert.Equal(GitHubApiClient.MaxRedirects + 1, transport.Sent.Count);
    }

    [Fact]
    public async Task A_response_larger_than_one_megabyte_is_unreadable_by_its_declared_length()
    {
        var (checker, _, _) = Rig(_ => FakeGitHubTransport.Answer(new string(' ', GitHubApiClient.MaxResponseBytes + 1)));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.UnreadableResponse, entry.Failure);
    }

    [Fact]
    public async Task A_response_without_a_length_is_read_only_up_to_the_limit()
    {
        var served = new CountingStream(GitHubApiClient.MaxResponseBytes * 8);
        var (checker, _, _) = Rig(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(served) };
            response.Content.Headers.ContentLength = null;
            return response;
        });

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.UnreadableResponse, entry.Failure);
        Assert.True(served.Consumed <= GitHubApiClient.MaxResponseBytes + (64 * 1024), "The body was read far past the limit.");
    }

    [Theory]
    [InlineData("repo-bad-shape.json")]
    [InlineData("commits-pinned.json")]
    public async Task A_repository_answer_that_is_not_the_expected_shape_is_unreadable(string fixture)
    {
        var (checker, _, _) = Rig(_ => FakeGitHubTransport.Fixture(fixture));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.UnreadableResponse, entry.Failure);
    }

    [Theory]
    [InlineData("commits-bad-sha.json")]
    [InlineData("repo-active.json")]
    public async Task A_commits_answer_with_a_bad_sha_or_the_wrong_shape_is_unreadable(string fixture)
    {
        var (checker, _, _) = Rig(sent => sent.Url == Repo ? FakeGitHubTransport.Fixture("repo-active.json") : FakeGitHubTransport.Fixture(fixture));

        var entry = Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries);

        Assert.Equal(SkillHealthFailure.UnreadableResponse, entry.Failure);
    }

    [Fact]
    public async Task A_deeply_nested_or_broken_json_is_unreadable_not_a_crash()
    {
        var (checker, _, _) = Rig(_ => FakeGitHubTransport.Answer(new string('[', 5000) + new string(']', 5000)));
        var (checker2, _, _) = Rig(_ => FakeGitHubTransport.Answer("{not json"));

        Assert.Equal(SkillHealthFailure.UnreadableResponse, Assert.Single((await checker.CheckAsync([Skill()], Ct)).Entries).Failure);
        Assert.Equal(SkillHealthFailure.UnreadableResponse, Assert.Single((await checker2.CheckAsync([Skill()], Ct)).Entries).Failure);
    }

    [Fact]
    public async Task The_text_of_the_response_never_reaches_a_message_and_no_address_of_it_is_followed()
    {
        var (checker, transport, _) = Rig(Healthy);

        var result = await checker.CheckAsync([Skill()], Ct);

        Assert.DoesNotContain(transport.Sent, s => s.Url.Contains("evil.example", StringComparison.Ordinal));
        Assert.All(result.Entries.SelectMany(e => e.Report.Checks), c =>
        {
            Assert.DoesNotContain("evil.example", c.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("oculto", c.Message, StringComparison.Ordinal);
            Assert.False(UntrustedTextHas(c.Message));
        });
    }

    private static bool UntrustedTextHas(string text) => AgentStudio.Domain.Common.UntrustedText.HasHidden(text);

    [Fact]
    public async Task A_429_stops_the_batch_and_the_skills_that_are_left_ask_nothing()
    {
        var (checker, transport, _) = Rig(_ => FakeGitHubTransport.Answer("{}", (HttpStatusCode)429, ("Retry-After", "120")));

        var result = await checker.CheckAsync([Skill("a"), Skill("b"), Skill("c")], Ct);

        Assert.Single(transport.Sent);
        Assert.All(result.Entries, e => Assert.Equal(SkillHealthFailure.RateLimited, e.Failure));
        Assert.Contains("límite de GitHub, se reinicia a las", Check(result.Entries[2], SkillHealthCodes.Upstream).Message, StringComparison.Ordinal);
        Assert.NotNull(result.RateLimitResetsAt);
    }

    [Fact]
    public async Task A_403_with_no_requests_left_is_the_limit_and_a_plain_403_is_only_forbidden()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (limited, limitedTransport, _) = Rig(_ => FakeGitHubTransport.Answer("{}", HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset)));
        var (plain, plainTransport, _) = Rig(_ => FakeGitHubTransport.Answer("{}", HttpStatusCode.Forbidden));

        var atLimit = await limited.CheckAsync([Skill("a"), Skill("b")], Ct);
        var forbidden = await plain.CheckAsync([Skill("a"), Skill("b")], Ct);

        Assert.Single(limitedTransport.Sent);
        Assert.All(atLimit.Entries, e => Assert.Equal(SkillHealthFailure.RateLimited, e.Failure));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(long.Parse(reset, System.Globalization.CultureInfo.InvariantCulture)), atLimit.RateLimitResetsAt);

        // A repository GitHub does not let read does not stop the others.
        Assert.All(forbidden.Entries, e => Assert.Equal(SkillHealthFailure.Forbidden, e.Failure));
        Assert.Equal(2, plainTransport.Sent.Count);
    }

    [Fact]
    public async Task When_the_last_answer_says_no_requests_are_left_none_is_sent()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (checker, transport, _) = Rig(sent =>
        {
            var response = Healthy(sent);
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset);
            return response;
        });

        var result = await checker.CheckAsync([Skill()], Ct);

        Assert.Single(transport.Sent);
        Assert.Equal(SkillHealthFailure.RateLimited, Assert.Single(result.Entries).Failure);
    }

    [Fact]
    public async Task No_connection_stops_the_batch_without_trying_every_skill()
    {
        var (checker, transport, _) = Rig(_ => throw new HttpRequestException("boom"));

        var result = await checker.CheckAsync([Skill("a"), Skill("b")], Ct);

        Assert.Single(transport.Sent);
        Assert.All(result.Entries, e => Assert.Equal(SkillHealthFailure.NoNetwork, e.Failure));
    }

    [Fact]
    public async Task Without_a_token_the_batch_goes_without_authorization()
    {
        var (checker, transport, secrets) = Rig(Healthy, use: SkillsDoctorTokenUse.None);

        var result = await checker.CheckAsync([Skill()], Ct);

        Assert.Equal(1, secrets.Calls);
        Assert.False(result.UsedToken);
        Assert.Equal(SkillsDoctorTokenState.NoneStored, result.TokenState);
        Assert.All(transport.Sent, s => Assert.Null(s.Authorization));
    }

    [Fact]
    public async Task With_a_token_every_request_to_the_api_carries_it_and_the_secrets_are_asked_once_per_batch()
    {
        var token = new GitHubToken(FakeSecrets.GitHubFineGrainedToken());
        var (checker, transport, secrets) = Rig(Healthy, use: new SkillsDoctorTokenUse(SkillsDoctorTokenState.Granted, token));

        var result = await checker.CheckAsync([Skill("a"), Skill("b")], Ct);

        Assert.Equal(1, secrets.Calls);
        Assert.True(result.UsedToken);
        Assert.Equal(SkillsDoctorTokenState.Granted, result.TokenState);
        Assert.Equal(6, transport.Sent.Count);
        Assert.All(transport.Sent, s => Assert.Equal("Bearer " + token.Reveal(), s.Authorization));
    }

    [Fact]
    public async Task With_the_presence_denied_the_batch_goes_without_a_token_and_says_so()
    {
        var (checker, transport, _) = Rig(Healthy, use: SkillsDoctorTokenUse.Denied);

        var result = await checker.CheckAsync([Skill()], Ct);

        Assert.False(result.UsedToken);
        Assert.Equal(SkillsDoctorTokenState.PresenceDenied, result.TokenState);
        Assert.All(transport.Sent, s => Assert.Null(s.Authorization));
        Assert.Equal(3, transport.Sent.Count);
    }

    [Fact]
    public async Task A_batch_with_nothing_valid_to_ask_does_not_ask_for_the_token()
    {
        var (checker, _, secrets) = Rig(Healthy);

        await checker.CheckAsync([Skill(repository: "octocat/../user")], Ct);

        Assert.Equal(0, secrets.Calls);
    }

    [Fact]
    public async Task The_requests_go_one_at_a_time_in_order()
    {
        var inFlight = 0;
        var peak = 0;
        var (checker, transport, _) = Rig(sent =>
        {
            var now = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, now);
            Thread.Sleep(5);
            Interlocked.Decrement(ref inFlight);
            return Healthy(sent);
        });

        await checker.CheckAsync([Skill("a"), Skill("b")], Ct);

        Assert.Equal(1, peak);
        Assert.Equal(6, transport.Sent.Count);
    }

    private sealed class CountingStream(long length) : Stream
    {
        public long Consumed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => Consumed; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Fill(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fill(buffer.Span));

        private int Fill(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - Consumed);
            buffer[..count].Fill((byte)' ');
            Consumed += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
