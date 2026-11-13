using System.Text.Json;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Hosting.GitHub;

namespace AgentStudio.Tests.Infrastructure.Hosting.GitHub;

/// <summary>
/// F10-2 (docs/21 §9): the adapter against answers written by hand in the form of <c>gh api -i</c>, with a process runner that records every <see cref="ProcessSpec"/>. Each case checks the command that was
/// launched (path, method, body through standard input) and what was understood. The answers are not captures (<c>Fixtures/README.md</c>): they prove that the adapter asks what it should and reads the form
/// that we believe the answer has, not that GitHub answers so.
/// </summary>
public sealed class GitHubHostingProviderTests
{
    private static readonly RemoteRepository Repo = GhRig.Repo;
    private static readonly RemoteBranch Main = GhRig.Main;
    private static readonly PullRequestRef Pr = GhRig.Pr;

    // ---- The account and the repository ----

    [Fact]
    public async Task The_account_is_the_login_of_get_user_and_nothing_else_is_read()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/user", "handmade-user-200.http");

        var result = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.Equal("octocat", result.Value!.Value);
        var call = Assert.Single(rig.Script.Calls);
        Assert.Equal("GET /user", call.ToString());
    }

    [Fact]
    public async Task A_login_that_does_not_pass_the_rule_is_an_invalid_answer_and_not_a_name()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/user", Gh.Ok("{\"login\":\"evil login\\u202E\"}"));

        var result = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);

        Assert.Equal(HostingOutcome.InvalidAnswer, result.Outcome);
        Assert.Equal(GitHubRefusal.AccountInvalid, result.RefusalCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task The_repository_gives_the_default_branch_the_merge_modes_the_permission_and_the_etag()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello", "handmade-repo-200.http");

        var result = await rig.Provider.ReadRepositoryAsync(Repo, null, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.Equal("main", result.Value!.DefaultBranch.Name);
        Assert.Equal([MergeMethod.MergeCommit, MergeMethod.Squash], result.Value.AllowedMergeMethods);
        Assert.True(result.Value.CanPush);
        Assert.False(result.Value.Archived);
        Assert.Equal("W/\"r1\"", result.ETag);
        Assert.Null(rig.Script.Calls.Single().IfNoneMatch);
    }

    [Fact]
    public async Task A_condition_goes_as_if_none_match_and_a_304_is_not_modified_with_the_same_etag()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello", "handmade-304.http");

        var result = await rig.Provider.ReadRepositoryAsync(Repo, "W/\"r1\"", CancellationToken.None);

        Assert.Equal(HostingOutcome.NotModified, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal("W/\"r1\"", result.ETag);
        Assert.Equal("W/\"r1\"", rig.Script.Calls.Single().IfNoneMatch);
    }

    [Theory]
    [InlineData("\"a\"\nX-Evil: 1")]
    [InlineData("abc")]
    [InlineData("\"a\"\r\nX-Evil: 1")]
    public async Task A_hostile_etag_is_dropped_and_the_request_goes_without_condition(string etag)
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello", "handmade-repo-200.http");

        await rig.Provider.ReadRepositoryAsync(Repo, etag, CancellationToken.None);

        var spec = Assert.Single(rig.Script.Specs);
        Assert.DoesNotContain(spec.Arguments, a => a.StartsWith("If-None-Match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_whose_default_branch_the_app_does_not_admit_says_why_with_the_code_of_the_rule()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello", Gh.Ok("{\"default_branch\":\"feat@{1}\",\"permissions\":{\"push\":true}}"));

        var result = await rig.Provider.ReadRepositoryAsync(Repo, null, CancellationToken.None);

        Assert.Equal(HostingOutcome.InvalidAnswer, result.Outcome);
        Assert.Equal(RemoteBranch.RefusalCharacters, result.RefusalCode);
    }

    [Fact]
    public async Task The_rules_of_a_branch_are_read_with_the_flag_of_protection_and_a_rule_that_is_not_known_is_one_unknown()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/rules/branches/main", "handmade-rules-200.http");
        rig.Script.Route("GET", "/repos/octo/hello/branches/main", "handmade-branch-200.http");

        var result = await rig.Provider.ReadBranchRulesAsync(Repo, Main, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.True(result.Value!.Readable);
        Assert.Contains(new BranchRule(BranchRuleKind.RequiredReviews, 2), result.Value.Rules);
        Assert.Contains(new BranchRule(BranchRuleKind.RequiredChecks, 2), result.Value.Rules);
        Assert.Contains(new BranchRule(BranchRuleKind.SignedCommits, null), result.Value.Rules);
        Assert.Contains(new BranchRule(BranchRuleKind.Protected, null), result.Value.Rules);
        Assert.Equal(1, result.Value.Rules.Count(r => r.Kind == BranchRuleKind.Unknown));
        Assert.False(result.Value.Has(BranchRuleKind.LinearHistory));
        Assert.Equal(["GET /repos/octo/hello/rules/branches/main", "GET /repos/octo/hello/branches/main"], rig.Script.Calls.Select(c => c.ToString()));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Rules_the_platform_does_not_show_are_unreadable_and_never_no_rules(int status)
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/rules/branches/main", Gh.Http(status, "{\"message\":\"x\"}"));

        var result = await rig.Provider.ReadBranchRulesAsync(Repo, Main, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.False(result.Value!.Readable);
        Assert.Empty(result.Value.Rules);
    }

    // ---- Finding, opening and changing the pull request ----

    [Fact]
    public async Task The_open_pull_request_of_the_branch_is_found_and_a_closed_one_does_not_count()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls?head=", "handmade-pulls-list-200.http");

        var found = await rig.Provider.FindPullRequestAsync(Repo, GhRig.RunBranch, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, found.Outcome);
        Assert.Equal(Pr, found.Value);
        Assert.Equal("GET /repos/octo/hello/pulls?head=octo%3Aagentstudio%2Frun-run1&state=all&per_page=5", rig.Script.Calls.Single().ToString());
    }

    [Fact]
    public async Task With_no_open_pull_request_or_one_from_another_repository_the_answer_is_ok_with_nothing()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls?head=", Gh.Ok("[{\"number\":3,\"state\":\"closed\",\"head\":{\"ref\":\"agentstudio/run-run1\",\"repo\":{\"full_name\":\"octo/hello\"}}},{\"number\":4,\"state\":\"open\",\"head\":{\"ref\":\"agentstudio/run-run1\",\"repo\":{\"full_name\":\"fork/hello\"}}},{\"number\":5,\"state\":\"open\",\"head\":{\"ref\":\"other\",\"repo\":{\"full_name\":\"octo/hello\"}}}]"));

        var result = await rig.Provider.FindPullRequestAsync(Repo, GhRig.RunBranch, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_pull_request_is_opened_with_a_post_whose_data_travel_through_standard_input_as_json()
    {
        using var rig = new GhRig();
        rig.Script.Route("POST", "/repos/octo/hello/pulls", "handmade-open-pr-201.http");
        var draft = new PullRequestDraft(Repo, GhRig.RunBranch, Main, new PullRequestText("Agent Studio · hito 1", "Cuerpo\nde dos líneas con `código`."), Draft: true);

        var result = await rig.Provider.OpenPullRequestAsync(draft, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.Equal(Pr, result.Value);
        var call = Assert.Single(rig.Script.Calls);
        Assert.Equal("POST /repos/octo/hello/pulls", call.ToString());
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("Agent Studio · hito 1", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("agentstudio/run-run1", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.Equal("Cuerpo\nde dos líneas con `código`.", body.RootElement.GetProperty("body").GetString());
        Assert.True(body.RootElement.GetProperty("draft").GetBoolean());
        Assert.Equal(5, body.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain(call.Spec.Arguments, a => a.Contains("Agent Studio", StringComparison.Ordinal) || a.Contains("Cuerpo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_validation_failure_of_opening_is_a_refusal_with_the_code_of_the_status_and_never_its_text()
    {
        using var rig = new GhRig();
        rig.Script.Route("POST", "/repos/octo/hello/pulls", Gh.Http(422, "{\"message\":\"Validation Failed ghp_sentinel\",\"errors\":[{\"message\":\"A pull request already exists\"}]}"));

        var result = await rig.Provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, Main, new PullRequestText("t", "b"), false), CancellationToken.None);

        Assert.Equal(HostingOutcome.Refused, result.Outcome);
        Assert.Equal("http.422", result.RefusalCode);
        Assert.DoesNotContain("sentinel", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_title_and_the_body_are_updated_by_a_patch_and_the_pull_request_is_closed_by_another_with_only_its_state()
    {
        using var rig = new GhRig();
        rig.Script.Route("PATCH", "/repos/octo/hello/pulls/7", Gh.Ok("{}"));

        var updated = await rig.Provider.UpdatePullRequestAsync(Pr, new PullRequestText("Nuevo título", "Nuevo cuerpo"), CancellationToken.None);
        var closed = await rig.Provider.CloseAsync(Pr, CancellationToken.None);

        Assert.True(updated.Value);
        Assert.True(closed.Value);
        var calls = rig.Script.Calls;
        Assert.Equal(["PATCH /repos/octo/hello/pulls/7", "PATCH /repos/octo/hello/pulls/7"], calls.Select(c => c.ToString()));
        using var update = JsonDocument.Parse(calls[0].Body!);
        Assert.Equal(["title", "body"], update.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("{\"state\":\"closed\"}", calls[1].Body);
    }

    [Fact]
    public async Task Reviewers_are_asked_by_a_post_with_their_logins_in_json_and_a_repeated_or_empty_list_is_refused_before_anything_is_launched()
    {
        using var rig = new GhRig();
        rig.Script.Route("POST", "/repos/octo/hello/pulls/7/requested_reviewers", Gh.Http(201, "{}"));
        var alice = RemoteLogin.TryParse("alice")!;
        var bob = RemoteLogin.TryParse("bob")!;

        var empty = await rig.Provider.RequestReviewersAsync(Pr, [], CancellationToken.None);
        var repeated = await rig.Provider.RequestReviewersAsync(Pr, [alice, RemoteLogin.TryParse("ALICE")!], CancellationToken.None);
        var tooMany = await rig.Provider.RequestReviewersAsync(Pr, [.. Enumerable.Range(0, 16).Select(i => RemoteLogin.TryParse("user" + i)!)], CancellationToken.None);
        Assert.Empty(rig.Script.Specs);
        Assert.All(new[] { empty, repeated, tooMany }, r => Assert.Equal(GitHubRefusal.ReviewersInvalid, r.RefusalCode));

        var ok = await rig.Provider.RequestReviewersAsync(Pr, [alice, bob], CancellationToken.None);

        Assert.True(ok.Value);
        var call = Assert.Single(rig.Script.Calls);
        Assert.Equal("POST /repos/octo/hello/pulls/7/requested_reviewers", call.ToString());
        Assert.Equal("{\"reviewers\":[\"alice\",\"bob\"]}", call.Body);
    }

    // ---- Text that goes to GitHub ----

    [Theory]
    [InlineData("", "b")]
    [InlineData("   ", "b")]
    [InlineData("two\nlines", "b")]
    [InlineData("tab\there", "b")]
    [InlineData("rtl\u202Eover", "b")]
    [InlineData("zero\u200Bwidth", "b")]
    [InlineData("t", "bell\u0007")]
    [InlineData("t", "rtl\u202Eover")]
    [InlineData("t", "nul\0byte")]
    public async Task A_title_or_a_body_with_control_or_format_characters_is_refused_before_anything_is_launched(string title, string body)
    {
        using var rig = new GhRig();

        var open = await rig.Provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, Main, new PullRequestText(title, body), false), CancellationToken.None);
        var update = await rig.Provider.UpdatePullRequestAsync(Pr, new PullRequestText(title, body), CancellationToken.None);

        Assert.Equal(HostingOutcome.Refused, open.Outcome);
        Assert.Equal(HostingOutcome.Refused, update.Outcome);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task The_limits_of_the_title_and_the_body_are_200_and_20000_characters()
    {
        using var rig = new GhRig();
        rig.Script.Route("POST", "/repos/octo/hello/pulls", "handmade-open-pr-201.http");
        PullRequestDraft Draft(string title, string body) => new(Repo, GhRig.RunBranch, Main, new PullRequestText(title, body), false);

        Assert.Equal(GitHubRefusal.TitleInvalid, (await rig.Provider.OpenPullRequestAsync(Draft(new string('t', 201), "b"), CancellationToken.None)).RefusalCode);
        Assert.Equal(GitHubRefusal.BodyInvalid, (await rig.Provider.OpenPullRequestAsync(Draft("t", new string('b', 20_001)), CancellationToken.None)).RefusalCode);
        Assert.Empty(rig.Script.Specs);
        Assert.Equal(HostingOutcome.Ok, (await rig.Provider.OpenPullRequestAsync(Draft(new string('t', 200), new string('b', 20_000)), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Hostile_text_travels_only_in_the_json_and_the_arguments_are_the_same_as_with_innocuous_text()
    {
        using var rig = new GhRig();
        rig.Script.Route("POST", "/repos/octo/hello/pulls", "handmade-open-pr-201.http");
        rig.Script.Route("PATCH", "/repos/octo/hello/pulls/7", Gh.Ok("{}"));
        var hostile = new[] { "-x", "--admin", "--upload-pack=x", "@fichero", "..", "%2e%2e", "?", "#", "a b", "\"comillas\"", "a&b", "a|b", "{owner}/{repo}", "'; rm -rf /; '", "`code`" };

        await rig.Provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, Main, new PullRequestText("innocuous", "innocuous"), false), CancellationToken.None);
        await rig.Provider.UpdatePullRequestAsync(Pr, new PullRequestText("innocuous", "innocuous"), CancellationToken.None);
        var baseline = rig.Script.Specs.Select(s => s.Arguments.ToList()).ToList();
        rig.Script.Specs.Clear();

        foreach (var text in hostile)
        {
            await rig.Provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, Main, new PullRequestText(text, text + "\n" + text), false), CancellationToken.None);
            await rig.Provider.UpdatePullRequestAsync(Pr, new PullRequestText(text, text), CancellationToken.None);
        }

        var seen = rig.Script.Specs.Select(s => s.Arguments.ToList()).ToList();
        Assert.Equal(hostile.Length * 2, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.Equal(baseline[i % 2], seen[i]);
        }

        Assert.All(rig.Script.Specs, spec => Assert.NotNull(JsonDocument.Parse(spec.StandardInput!)));
    }

    // ---- Merge, close and delete ----

    [Fact]
    public async Task A_merge_asks_for_the_method_and_the_head_that_was_judged_and_nothing_else()
    {
        using var rig = new GhRig();
        rig.Script.Route("PUT", "/repos/octo/hello/pulls/7/merge", "handmade-merge-200.http");

        var result = await rig.Provider.MergeAsync(Pr, MergeMethod.Squash, Gh.Sha1, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.Equal(new MergeOutcome(true, Gh.Merge, null), result.Value);
        var call = Assert.Single(rig.Script.Calls);
        Assert.Equal("PUT /repos/octo/hello/pulls/7/merge", call.ToString());
        Assert.Equal("{\"merge_method\":\"squash\",\"sha\":\"" + Gh.Sha1 + "\"}", call.Body);
    }

    [Theory]
    [InlineData(MergeMethod.MergeCommit, "merge")]
    [InlineData(MergeMethod.Squash, "squash")]
    [InlineData(MergeMethod.Rebase, "rebase")]
    public async Task Each_merge_method_is_its_word_of_the_api(MergeMethod method, string word)
    {
        using var rig = new GhRig();
        rig.Script.Route("PUT", "/repos/octo/hello/pulls/7/merge", "handmade-merge-200.http");

        await rig.Provider.MergeAsync(Pr, method, Gh.Sha1, CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Script.Calls.Single().Body!);
        Assert.Equal(word, body.RootElement.GetProperty("merge_method").GetString());
        Assert.Equal(["merge_method", "sha"], body.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(405, "merge.blocked")]
    [InlineData(409, "merge.head-moved")]
    public async Task A_refused_merge_says_in_what_state_the_pull_request_is_from_the_closed_vocabulary_and_never_the_message_of_the_remote(int status, string code)
    {
        using var rig = new GhRig();
        rig.Script.Route("PUT", "/repos/octo/hello/pulls/7/merge", Gh.Http(status, "{\"message\":\"Pull Request is not mergeable ghp_sentinel\"}"));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"state\":\"open\",\"merged\":false,\"mergeable_state\":\"blocked\",\"updated_at\":\"2026-11-13T10:00:00Z\",\"head\":{\"sha\":\"" + Gh.Sha1 + "\"},\"base\":{\"ref\":\"main\"}}"));

        var result = await rig.Provider.MergeAsync(Pr, MergeMethod.Squash, Gh.Sha1, CancellationToken.None);

        Assert.Equal(HostingOutcome.Refused, result.Outcome);
        Assert.Equal(code, result.RefusalCode);
        Assert.Equal(new MergeOutcome(false, null, "blocked"), result.Value);
        Assert.DoesNotContain("sentinel", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_422_is_not_a_block_it_is_a_failed_validation_with_its_own_code_and_no_state_is_read()
    {
        using var rig = new GhRig();
        rig.Script.Route("PUT", "/repos/octo/hello/pulls/7/merge", "handmade-merge-422.http");

        var result = await rig.Provider.MergeAsync(Pr, MergeMethod.Squash, Gh.Sha1, CancellationToken.None);

        Assert.Equal(HostingOutcome.Refused, result.Outcome);
        Assert.Equal("merge.rejected", result.RefusalCode);
        Assert.Equal(new MergeOutcome(false, null, null), result.Value);
        Assert.Single(rig.Script.Specs);
    }

    [Fact]
    public async Task The_documented_types_of_rules_that_protect_a_branch_are_protection_and_only_what_is_not_known_is_unknown()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/rules/branches/main", Gh.Ok("[{\"type\":\"deletion\"},{\"type\":\"non_fast_forward\"},{\"type\":\"creation\"},{\"type\":\"update\"}]"));
        rig.Script.Route("GET", "/repos/octo/hello/branches/main", Gh.Ok("{\"name\":\"main\",\"protected\":false}"));

        var result = await rig.Provider.ReadBranchRulesAsync(Repo, Main, CancellationToken.None);

        Assert.Equal([new BranchRule(BranchRuleKind.Protected, null)], result.Value!.Rules);
        Assert.True(result.Value.Readable);
    }

    [Fact]
    public async Task Merge_settings_that_are_missing_are_no_mode_for_an_account_that_cannot_write_and_an_answer_not_understood_for_one_that_can()
    {
        using var readOnly = new GhRig();
        readOnly.Script.Route("GET", "/repos/octo/hello", Gh.Ok("{\"default_branch\":\"main\",\"permissions\":{\"push\":false}}"));
        using var writer = new GhRig();
        writer.Script.Route("GET", "/repos/octo/hello", Gh.Ok("{\"default_branch\":\"main\",\"allow_squash_merge\":true,\"permissions\":{\"push\":true}}"));
        using var noPermissions = new GhRig();
        noPermissions.Script.Route("GET", "/repos/octo/hello", Gh.Ok("{\"default_branch\":\"main\"}"));

        var cannot = await readOnly.Provider.ReadRepositoryAsync(Repo, null, CancellationToken.None);
        var can = await writer.Provider.ReadRepositoryAsync(Repo, null, CancellationToken.None);
        var unknown = await noPermissions.Provider.ReadRepositoryAsync(Repo, null, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, cannot.Outcome);
        Assert.False(cannot.Value!.CanPush);
        Assert.Empty(cannot.Value.AllowedMergeMethods);
        Assert.Equal(HostingOutcome.InvalidAnswer, can.Outcome);
        Assert.Equal("repository.merge-settings-missing", can.RefusalCode);
        Assert.Equal(HostingOutcome.Ok, unknown.Outcome);
        Assert.False(unknown.Value!.CanPush);
    }

    [Fact]
    public async Task A_422_when_deleting_a_branch_is_a_reference_that_does_not_exist()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/branches/agentstudio/run-run1", "handmade-branch-200.http");
        rig.Script.Route("DELETE", "/repos/octo/hello/git/refs/heads/agentstudio/run-run1", Gh.Http(422, "{\"message\":\"Reference does not exist\"}"));

        var result = await rig.Provider.DeleteRunBranchAsync(Repo, GhRig.RunBranch, Gh.Sha1, CancellationToken.None);

        Assert.Equal(HostingOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task The_secondary_limit_of_a_403_without_headers_is_a_rate_limited_outcome_for_the_provider_and_for_the_writes_after_it()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/user", Gh.Http(403, "{\"message\":\"You have exceeded a secondary rate limit.\"}"));
        var start = rig.Time.GetUtcNow();

        var read = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);
        var write = await rig.Provider.CloseAsync(Pr, CancellationToken.None);

        Assert.Equal(HostingOutcome.RateLimited, read.Outcome);
        Assert.Equal(start.AddMinutes(1), read.RetryAt);
        Assert.Equal(HostingOutcome.RateLimited, write.Outcome);
        Assert.Single(rig.Script.Specs);
    }

    [Fact]
    public void A_wait_that_a_local_file_gives_back_is_cut_at_two_hours_from_now_and_a_past_hour_or_a_null_is_no_wait()
    {
        using var rig = new GhRig();
        var now = rig.Time.GetUtcNow();

        rig.Provider.RestoreRateLimit(now.AddDays(400));
        Assert.Equal(now.AddHours(2), rig.Provider.RateLimitedUntil);
        rig.Provider.RestoreRateLimit(now.AddMinutes(20));
        Assert.Equal(now.AddMinutes(20), rig.Provider.RateLimitedUntil);
        rig.Provider.RestoreRateLimit(now.AddSeconds(-1));
        Assert.Null(rig.Provider.RateLimitedUntil);
        rig.Provider.RestoreRateLimit(DateTimeOffset.MinValue);
        Assert.Null(rig.Provider.RateLimitedUntil);
        rig.Provider.RestoreRateLimit(now.AddMinutes(5));
        rig.Provider.RestoreRateLimit(null);
        Assert.Null(rig.Provider.RateLimitedUntil);
    }

    [Fact]
    public async Task A_state_outside_the_vocabulary_is_unknown()
    {
        using var rig = new GhRig();
        rig.Script.Route("PUT", "/repos/octo/hello/pulls/7/merge", "handmade-merge-405.http");
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"state\":\"open\",\"merged\":false,\"mergeable_state\":\"<script>alert(1)</script>\",\"updated_at\":\"2026-11-13T10:00:00Z\",\"head\":{\"sha\":\"" + Gh.Sha1 + "\"},\"base\":{\"ref\":\"main\"}}"));

        var result = await rig.Provider.MergeAsync(Pr, MergeMethod.Squash, Gh.Sha1, CancellationToken.None);

        Assert.Equal("unknown", result.Value!.BlockedState);
    }

    [Fact]
    public async Task A_merge_with_a_method_the_adapter_does_not_declare_or_a_head_that_is_not_a_commit_launches_nothing()
    {
        using var rig = new GhRig();

        var badHead = await rig.Provider.MergeAsync(Pr, MergeMethod.Squash, "main", CancellationToken.None);
        var badMethod = await rig.Provider.MergeAsync(Pr, (MergeMethod)99, Gh.Sha1, CancellationToken.None);

        Assert.Equal(GitHubRefusal.CommitInvalid, badHead.RefusalCode);
        Assert.Equal(HostingOutcome.Unsupported, badMethod.Outcome);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task Only_the_branch_of_a_run_is_deleted_and_only_if_its_head_is_the_expected_commit()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/branches/agentstudio/run-run1", "handmade-branch-200.http");
        rig.Script.Route("DELETE", "/repos/octo/hello/git/refs/heads/agentstudio/run-run1", Gh.Http(204));

        var notARun = await rig.Provider.DeleteRunBranchAsync(Repo, Main, Gh.Sha1, CancellationToken.None);
        Assert.Equal(GitHubRefusal.NotRunBranch, notARun.RefusalCode);
        Assert.Empty(rig.Script.Specs);

        var moved = await rig.Provider.DeleteRunBranchAsync(Repo, GhRig.RunBranch, Gh.Sha2, CancellationToken.None);
        Assert.Equal(GitHubRefusal.BranchHeadDiffers, moved.RefusalCode);
        Assert.Equal(["GET /repos/octo/hello/branches/agentstudio/run-run1"], rig.Script.Calls.Select(c => c.ToString()));

        var deleted = await rig.Provider.DeleteRunBranchAsync(Repo, GhRig.RunBranch, Gh.Sha1, CancellationToken.None);
        Assert.True(deleted.Value);
        Assert.Equal("DELETE /repos/octo/hello/git/refs/heads/agentstudio/run-run1", rig.Script.Calls[^1].ToString());
        Assert.Null(rig.Script.Calls[^1].Body);
    }

    // ---- The outcomes of the contract ----

    [Fact]
    public async Task The_outcome_comes_from_the_status_line_or_from_a_gh_without_session_or_connection_and_never_from_text()
    {
        var cases = new (ProcessResult Answer, HostingOutcome Outcome, string? Code)[]
        {
            (Gh.Http(401, "{\"message\":\"Bad credentials\"}"), HostingOutcome.SignInNeeded, null),
            (Gh.Http(404, "{}"), HostingOutcome.NotFound, null),
            (Gh.Http(403, "{}", "\r\n", ("X-Ratelimit-Remaining", "100")), HostingOutcome.Forbidden, null),
            (Gh.Http(301, "{}"), HostingOutcome.Refused, GitHubRefusal.Moved),
            (Gh.Http(502, "{}"), HostingOutcome.Unreachable, GitHubRefusal.ServerError),
            (Gh.Http(418, "{}"), HostingOutcome.Refused, "http.418"),
            (new ProcessResult(4, string.Empty, "gh auth login", TimeSpan.Zero, false), HostingOutcome.SignInNeeded, null),
            (new ProcessResult(1, string.Empty, "dial tcp: lookup api.github.com ghp_sentinel", TimeSpan.Zero, false), HostingOutcome.Unreachable, null),
            (new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromSeconds(20), true), HostingOutcome.Unreachable, null),
            (new ProcessResult(0, "not an answer", string.Empty, TimeSpan.Zero, false), HostingOutcome.InvalidAnswer, GitHubRefusal.InvalidAnswer),
            (Gh.Http(200, "not json"), HostingOutcome.InvalidAnswer, GitHubRefusal.AccountInvalid),
        };

        foreach (var (answer, outcome, code) in cases)
        {
            using var rig = new GhRig();
            rig.Script.Route("GET", "/user", answer);

            var result = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);

            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(code, result.RefusalCode);
            Assert.Null(result.Value);
            Assert.DoesNotContain("sentinel", result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_limit_is_a_rate_limited_outcome_with_the_hour_and_the_second_call_launches_nothing()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/user", "handmade-rate-limit-403.http");

        var first = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);
        var second = await rig.Provider.ReadRepositoryAsync(Repo, null, CancellationToken.None);
        var write = await rig.Provider.CloseAsync(Pr, CancellationToken.None);

        Assert.Equal(HostingOutcome.RateLimited, first.Outcome);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794574800), first.RetryAt);
        Assert.Equal(HostingOutcome.RateLimited, second.Outcome);
        Assert.Equal(HostingOutcome.RateLimited, write.Outcome);
        Assert.Equal(first.RetryAt, write.RetryAt);
        Assert.Single(rig.Script.Specs);
        Assert.Equal(first.RetryAt, rig.Provider.RateLimitedUntil);
    }

    [Fact]
    public async Task A_stored_wait_is_loaded_by_the_provider_and_stops_the_first_call()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/user", "handmade-user-200.http");
        rig.Provider.RestoreRateLimit(rig.Time.GetUtcNow().AddMinutes(30));

        var result = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);

        Assert.Equal(HostingOutcome.RateLimited, result.Outcome);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task The_standard_error_never_reaches_a_message_or_a_result()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/user", new ProcessResult(1, string.Empty, "gh: HTTP 500 {\"message\":\"ghp_sentinel\"}", TimeSpan.Zero, false));
        rig.Script.Route("POST", "/repos/octo/hello/pulls", new ProcessResult(1, "HTTP/2.0 500 Internal Server Error\n\n{\"message\":\"ghp_sentinel\"}", "ghp_sentinel", TimeSpan.Zero, false));

        var account = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);
        var open = await rig.Provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, Main, new PullRequestText("t", "b"), false), CancellationToken.None);

        Assert.DoesNotContain("sentinel", account.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel", open.ToString(), StringComparison.Ordinal);
        Assert.Equal(GitHubRefusal.ServerError, open.RefusalCode);
    }
}