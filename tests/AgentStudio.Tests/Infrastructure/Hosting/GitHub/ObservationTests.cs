using System.Globalization;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Hosting.GitHub;

namespace AgentStudio.Tests.Infrastructure.Hosting.GitHub;

/// <summary>
/// docs/21 D-23, D-24 and D-27: one round of observation of a pull request. Each resource is asked with its own <c>ETag</c> and a 304 of one says nothing of the others; what answered 304 is composed from the last
/// snapshot; when everything answered 304 the answer is not-modified with no snapshot; the way a merge was made is deduced from real commits; a failure returns no cursor, so nothing advances. The server of the tests
/// answers 304 exactly when the request carries an <c>If-None-Match</c> (it models the platform: a 304 only with a condition); the answers are written by hand (<c>Fixtures/README.md</c>).
/// </summary>
public sealed class ObservationTests
{
    private static readonly PullRequestRef Pr = GhRig.Pr;
    private const string Reviews = "/repos/octo/hello/pulls/7/reviews";
    private const string Timeline = "/repos/octo/hello/issues/7/timeline";
    private const string Commits = "/repos/octo/hello/pulls/7/commits";

    private static string CheckRuns(string sha) => "/repos/octo/hello/commits/" + sha + "/check-runs";

    private static string Status(string sha) => "/repos/octo/hello/commits/" + sha + "/status";

    private static string CommitJson(string sha, string name = "Octo", string date = "2026-11-13T09:00:00Z", string message = "mensaje") =>
        "{\"sha\":\"" + sha + "\",\"commit\":{\"author\":{\"name\":\"" + name + "\",\"date\":\"" + date + "\"},\"message\":\"" + message + "\"}}";

    private static string SingleCommitJson(string sha, string[] parents, string name = "Octo", string date = "2026-11-13T09:00:00Z", string message = "mensaje") =>
        "{\"sha\":\"" + sha + "\",\"parents\":[" + string.Join(",", parents.Select(p => "{\"sha\":\"" + p + "\"}")) + "],\"commit\":{\"author\":{\"name\":\"" + name + "\",\"date\":\"" + date + "\"},\"message\":\"" + message + "\"}}";

    /// <summary>A resource of the platform: with a condition it answers 304, without it the fixture; the fixture may change between rounds.</summary>
    private static void Conditional(ScriptedGh script, string path, Func<ProcessResult> answer) =>
        script.Route("GET", path, call => call.IfNoneMatch is not null ? Gh.NotModified() : answer());

    private static void RouteOpenPullRequest(ScriptedGh script)
    {
        Conditional(script, "/repos/octo/hello/pulls/7", () => Gh.Fixture("handmade-pull-open-200.http"));
        Conditional(script, Reviews, () => Gh.Fixture("handmade-reviews-200.http"));
        Conditional(script, CheckRuns(Gh.Sha1), () => Gh.Fixture("handmade-check-runs-200.http"));
        Conditional(script, Status(Gh.Sha1), () => Gh.Fixture("handmade-combined-status-200.http"));
        script.Route("GET", Commits, Gh.Ok(Gh.Json(CommitJson(Gh.Sha2, message: "primero"), CommitJson(Gh.Sha1, message: "segundo"))));
        script.Route("GET", Timeline, "handmade-timeline-200.http");
    }

    private static async Task<PullRequestObservation> FirstRoundAsync(GhRig rig)
    {
        RouteOpenPullRequest(rig.Script);
        var first = await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None);
        Assert.Equal(HostingOutcome.Ok, first.Outcome);
        return first.Value!;
    }

    [Fact]
    public async Task The_first_round_composes_the_snapshot_from_the_pull_request_the_reviews_the_checks_the_commits_and_the_timeline()
    {
        using var rig = new GhRig();

        var observation = await FirstRoundAsync(rig);

        var snapshot = observation.Snapshot;
        Assert.Equal(Pr, snapshot.PullRequest);
        Assert.Equal(PullRequestState.Open, snapshot.State);
        Assert.Equal(Gh.Sha1, snapshot.HeadCommit);
        Assert.Equal("main", snapshot.BaseBranch.Name);
        Assert.True(snapshot.HeadBranchExists);
        Assert.Null(snapshot.MergeCommit);
        Assert.Null(snapshot.MergeMode);
        Assert.Empty(snapshot.ResultCommits);
        Assert.Equal([Gh.Sha2, Gh.Sha1], snapshot.OriginalCommits);
        Assert.False(snapshot.CommitsTruncated);
        Assert.Equal(2, snapshot.ForcePushes);
        Assert.False(snapshot.TimelineTruncated);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero), snapshot.UpdatedAt);

        // Two runs pass or wait, one fails; the status of another system passes; the status of the verdict of Agent Studio is not a check of the project.
        Assert.Equal(new ChecksSummary(ChecksRollup.Failing, 2, 1, 1, false), snapshot.Checks);
        Assert.Equal(1, snapshot.Reviews.Approved);
        Assert.Equal(1, snapshot.Reviews.ChangesRequested);
        Assert.Equal(1, snapshot.Reviews.Commented);
        Assert.Equal(["bob"], snapshot.Reviews.Approvers.Select(a => a.Value));
        Assert.Equal("Agent Studio · hito 1", observation.Display.Title);
        Assert.Equal(["test"], observation.Display.FailingCheckNames);
    }

    [Fact]
    public async Task The_merge_commit_of_an_open_pull_request_is_a_test_commit_and_is_never_read()
    {
        using var rig = new GhRig();

        var observation = await FirstRoundAsync(rig);

        Assert.Null(observation.Snapshot.MergeCommit);
        Assert.Null(observation.Snapshot.MergedBy);
    }

    [Fact]
    public async Task Each_resource_keeps_its_own_etag_and_the_commits_and_the_timeline_keep_none()
    {
        using var rig = new GhRig();

        var observation = await FirstRoundAsync(rig);

        var etags = observation.Cursor.ETags;
        Assert.Equal(
            new Dictionary<ObservedResource, string>
            {
                [ObservedResource.PullRequest] = "W/\"p1\"",
                [ObservedResource.Reviews] = "W/\"v1\"",
                [ObservedResource.CheckRuns] = "W/\"c1\"",
                [ObservedResource.CombinedStatus] = "W/\"s1\"",
            }.OrderBy(p => p.Key),
            etags.OrderBy(p => p.Key));
        Assert.Same(observation.Snapshot, observation.Cursor.LastSnapshot);
    }

    [Fact]
    public async Task The_first_round_asks_six_resources_and_none_with_a_condition()
    {
        using var rig = new GhRig();

        await FirstRoundAsync(rig);

        Assert.Equal(
            [
                "GET /repos/octo/hello/pulls/7",
                "GET " + Reviews + "?per_page=50&page=1",
                "GET " + CheckRuns(Gh.Sha1) + "?per_page=100",
                "GET " + Status(Gh.Sha1) + "?per_page=100",
                "GET " + Commits + "?per_page=50&page=1",
                "GET " + Timeline + "?per_page=30&page=1",
            ],
            rig.Script.Calls.Select(c => c.ToString()));
        Assert.All(rig.Script.Calls, c => Assert.Null(c.IfNoneMatch));
        Assert.Empty(rig.Script.Unmatched);
    }

    [Fact]
    public async Task When_every_resource_answers_304_the_answer_is_not_modified_with_no_snapshot_and_four_requests_only()
    {
        using var rig = new GhRig();
        var first = await FirstRoundAsync(rig);
        rig.Script.Specs.Clear();

        var second = await rig.Provider.ObserveAsync(Pr, first.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.NotModified, second.Outcome);
        Assert.Null(second.Value);
        var calls = rig.Script.Calls;
        Assert.Equal(4, calls.Count);
        Assert.Equal("W/\"p1\"", calls.Single(c => c.Path == "/repos/octo/hello/pulls/7").IfNoneMatch);
        Assert.Equal("W/\"v1\"", calls.Single(c => c.Path.StartsWith(Reviews, StringComparison.Ordinal)).IfNoneMatch);
        Assert.Equal("W/\"c1\"", calls.Single(c => c.Path.StartsWith(CheckRuns(Gh.Sha1), StringComparison.Ordinal)).IfNoneMatch);
        Assert.Equal("W/\"s1\"", calls.Single(c => c.Path.StartsWith(Status(Gh.Sha1), StringComparison.Ordinal)).IfNoneMatch);
        Assert.DoesNotContain(calls, c => c.Path.StartsWith(Commits, StringComparison.Ordinal) || c.Path.StartsWith(Timeline, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_304_of_the_pull_request_says_nothing_of_its_checks_and_the_one_that_did_not_change_is_asked_again_whole()
    {
        using var rig = new GhRig();
        var first = await FirstRoundAsync(rig);
        rig.Script.Specs.Clear();

        // The checks changed (another ETag and every run passes); the statuses did not.
        rig.Script.Route("GET", CheckRuns(Gh.Sha1), _ => Gh.Ok("{\"total_count\":1,\"check_runs\":[{\"name\":\"build\",\"status\":\"completed\",\"conclusion\":\"success\"}]}", "W/\"c2\""));
        var second = await rig.Provider.ObserveAsync(Pr, first.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, second.Outcome);
        Assert.Equal(new ChecksSummary(ChecksRollup.Passing, 2, 0, 0, false), second.Value!.Snapshot.Checks);
        Assert.Equal(first.Snapshot.HeadCommit, second.Value.Snapshot.HeadCommit);
        Assert.Equal(first.Snapshot.OriginalCommits, second.Value.Snapshot.OriginalCommits);
        Assert.Equal(first.Snapshot.Reviews, second.Value.Snapshot.Reviews);
        Assert.Equal("W/\"c2\"", second.Value.Cursor.ETags[ObservedResource.CheckRuns]);
        var statusCalls = rig.Script.Calls.Where(c => c.Path.StartsWith(Status(Gh.Sha1), StringComparison.Ordinal)).ToList();
        Assert.Equal(2, statusCalls.Count);
        Assert.NotNull(statusCalls[0].IfNoneMatch);
        Assert.Null(statusCalls[1].IfNoneMatch);
        Assert.NotEqual(first.Snapshot.Fingerprint, second.Value.Snapshot.Fingerprint);
    }

    [Fact]
    public async Task A_head_that_moved_asks_the_checks_without_condition_and_reads_the_commits_and_the_timeline_again()
    {
        using var rig = new GhRig();
        var first = await FirstRoundAsync(rig);
        rig.Script.Specs.Clear();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"number\":7,\"state\":\"open\",\"draft\":true,\"merged\":false,\"title\":\"t\",\"mergeable_state\":\"clean\",\"updated_at\":\"2026-11-13T10:30:00Z\",\"head\":{\"ref\":\"agentstudio/run-run1\",\"sha\":\"" + Gh.Sha3 + "\"},\"base\":{\"ref\":\"main\"}}", "W/\"p9\""));
        Conditional(rig.Script.Route("GET", CheckRuns(Gh.Sha3), Gh.Ok("{\"total_count\":0,\"check_runs\":[]}")), Status(Gh.Sha3), () => Gh.Ok("{\"state\":\"pending\",\"total_count\":0,\"statuses\":[]}"));
        rig.Script.Route("GET", Commits, Gh.Ok(Gh.Json(CommitJson(Gh.Sha2), CommitJson(Gh.Sha3))));

        var second = await rig.Provider.ObserveAsync(Pr, first.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, second.Outcome);
        var snapshot = second.Value!.Snapshot;
        Assert.Equal(PullRequestState.Draft, snapshot.State);
        Assert.Equal(Gh.Sha3, snapshot.HeadCommit);
        Assert.Equal([Gh.Sha2, Gh.Sha3], snapshot.OriginalCommits);
        Assert.Equal(new ChecksSummary(ChecksRollup.None, 0, 0, 0, false), snapshot.Checks);
        var calls = rig.Script.Calls;
        Assert.All(calls.Where(c => c.Path.Contains("/commits/" + Gh.Sha3, StringComparison.Ordinal)), c => Assert.Null(c.IfNoneMatch));
        Assert.Contains(calls, c => c.Path.StartsWith(Commits, StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Path.StartsWith(Timeline, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cursor_with_etags_but_no_snapshot_has_nothing_to_compose_a_304_from_and_asks_everything_without_condition()
    {
        using var rig = new GhRig();
        RouteOpenPullRequest(rig.Script);
        var cursor = new ObservationCursor(
            new Dictionary<ObservedResource, string> { [ObservedResource.PullRequest] = "W/\"p1\"", [ObservedResource.Reviews] = "W/\"v1\"", [ObservedResource.CheckRuns] = "W/\"c1\"", [ObservedResource.CombinedStatus] = "W/\"s1\"" },
            null);

        var result = await rig.Provider.ObserveAsync(Pr, cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.All(rig.Script.Calls, c => Assert.Null(c.IfNoneMatch));
    }

    [Fact]
    public async Task A_cursor_of_another_pull_request_is_not_used()
    {
        using var rig = new GhRig();
        var first = await FirstRoundAsync(rig);
        rig.Script.Specs.Clear();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/8", Gh.Http(404, "{}"));

        var result = await rig.Provider.ObserveAsync(new PullRequestRef(GhRig.Repo, 8), first.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.NotFound, result.Outcome);
        Assert.Equal("/repos/octo/hello/pulls/8", rig.Script.Calls[0].Path);
        Assert.Null(rig.Script.Calls[0].IfNoneMatch);
    }

    [Fact]
    public async Task A_round_that_fails_midway_returns_no_cursor_and_the_cursor_it_was_given_is_not_changed()
    {
        using var rig = new GhRig();
        var first = await FirstRoundAsync(rig);
        var before = first.Cursor.ETags.OrderBy(p => p.Key).ToList();
        rig.Script.Route("GET", Reviews, Gh.Http(502, "{}"));

        // The pull request answers 200 with a new ETag and then the reviews fail: the ETag of the pull request must not move.
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"number\":7,\"state\":\"open\",\"draft\":false,\"merged\":false,\"mergeable_state\":\"clean\",\"updated_at\":\"2026-11-13T10:30:00Z\",\"head\":{\"sha\":\"" + Gh.Sha1 + "\"},\"base\":{\"ref\":\"main\"}}", "W/\"p9\""));
        var second = await rig.Provider.ObserveAsync(Pr, first.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.Unreachable, second.Outcome);
        Assert.Null(second.Value);
        Assert.Equal(before, first.Cursor.ETags.OrderBy(p => p.Key).ToList());
    }

    [Fact]
    public async Task A_pull_request_that_is_not_there_is_not_found_and_one_with_a_hostile_answer_is_invalid()
    {
        using var missing = new GhRig();
        missing.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Http(404, "{}"));
        using var hostile = new GhRig();
        hostile.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"state\":\"open\",\"head\":{\"sha\":\"not-a-commit\"},\"base\":{\"ref\":\"main\"},\"updated_at\":\"2026-11-13T10:30:00Z\"}"));
        using var typed = new GhRig();
        typed.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"state\":7,\"head\":[],\"base\":\"main\"}"));

        Assert.Equal(HostingOutcome.NotFound, (await missing.Provider.ObserveAsync(Pr, null, CancellationToken.None)).Outcome);
        Assert.Equal(HostingOutcome.InvalidAnswer, (await hostile.Provider.ObserveAsync(Pr, null, CancellationToken.None)).Outcome);
        Assert.Equal(HostingOutcome.InvalidAnswer, (await typed.Provider.ObserveAsync(Pr, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_json_deeper_than_16_levels_is_an_invalid_answer()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok(string.Concat(Enumerable.Repeat("[", 40)) + string.Concat(Enumerable.Repeat("]", 40))));

        Assert.Equal(HostingOutcome.InvalidAnswer, (await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None)).Outcome);
    }

    // ---- Commits: up to three pages, and what still comes full is truncated ----

    private static string Page(int count, int offset = 0) =>
        Gh.Json([.. Enumerable.Range(offset, count).Select(i => CommitJson((i + 1).ToString("x", CultureInfo.InvariantCulture).PadLeft(40, '0')))]);

    [Fact]
    public async Task Six_full_pages_of_commits_are_truncated_and_a_short_page_ends_the_list()
    {
        using var full = new GhRig();
        RouteOpenPullRequest(full.Script);
        full.Script.Route("GET", Commits, call => Gh.Ok(Page(50, (PageNumber(call) - 1) * 50)));

        var truncated = await full.Provider.ObserveAsync(Pr, null, CancellationToken.None);

        Assert.Equal(300, truncated.Value!.Snapshot.OriginalCommits.Count);
        Assert.True(truncated.Value.Snapshot.CommitsTruncated);
        Assert.Equal(6, full.Script.Calls.Count(c => c.Path.StartsWith(Commits, StringComparison.Ordinal)));

        using var short1 = new GhRig();
        RouteOpenPullRequest(short1.Script);
        short1.Script.Route("GET", Commits, call => Gh.Ok(PageNumber(call) == 2 ? Page(30, 50) : Page(50)));

        var ended = await short1.Provider.ObserveAsync(Pr, null, CancellationToken.None);

        Assert.Equal(80, ended.Value!.Snapshot.OriginalCommits.Count);
        Assert.False(ended.Value.Snapshot.CommitsTruncated);
        Assert.Equal(2, short1.Script.Calls.Count(c => c.Path.StartsWith(Commits, StringComparison.Ordinal)));
    }

    private static int PageNumber(GhCallSeen call) => int.Parse(call.Path[(call.Path.LastIndexOf("page=", StringComparison.Ordinal) + 5)..], CultureInfo.InvariantCulture);

    private static ProcessResult TooBig() => new(-1, "HTTP/2.0 200 OK\n\n[", string.Empty, TimeSpan.Zero, false, OutputLimitExceeded: true);

    [Fact]
    public async Task A_page_of_the_timeline_that_does_not_fit_in_a_megabyte_does_not_fail_the_observation_it_goes_on_truncated_with_the_last_known_force_pushes()
    {
        using var rig = new GhRig();
        var first = await FirstRoundAsync(rig);
        Assert.Equal(2, first.Snapshot.ForcePushes);
        rig.Script.Specs.Clear();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"number\":7,\"state\":\"open\",\"draft\":false,\"merged\":false,\"mergeable_state\":\"clean\",\"updated_at\":\"2026-11-13T10:30:00Z\",\"head\":{\"sha\":\"" + Gh.Sha3 + "\"},\"base\":{\"ref\":\"main\"}}", "W/\"p9\""));
        rig.Script.Route("GET", CheckRuns(Gh.Sha3), Gh.Ok("{\"total_count\":0,\"check_runs\":[]}"));
        rig.Script.Route("GET", Status(Gh.Sha3), Gh.Ok("{\"total_count\":0,\"statuses\":[]}"));
        rig.Script.Route("GET", Commits, Gh.Ok(Gh.Json(CommitJson(Gh.Sha3))));
        rig.Script.Route("GET", Timeline, _ => TooBig());

        var second = await rig.Provider.ObserveAsync(Pr, first.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, second.Outcome);
        Assert.True(second.Value!.Snapshot.TimelineTruncated);
        Assert.Equal(2, second.Value.Snapshot.ForcePushes);
        Assert.True(second.Value.Snapshot.HeadBranchExists);
        Assert.Equal(Gh.Sha3, second.Value.Snapshot.HeadCommit);
        Assert.Single(rig.Script.Calls, c => c.Path.StartsWith(Timeline, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_of_commits_that_does_not_fit_ends_the_list_there_truncated_and_the_first_observation_has_no_known_force_pushes_to_keep()
    {
        using var rig = new GhRig();
        RouteOpenPullRequest(rig.Script);
        rig.Script.Route("GET", Commits, call => PageNumber(call) == 1 ? Gh.Ok(Page(50)) : TooBig());
        rig.Script.Route("GET", Timeline, _ => TooBig());

        var result = await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.Equal(50, result.Value!.Snapshot.OriginalCommits.Count);
        Assert.True(result.Value.Snapshot.CommitsTruncated);
        Assert.True(result.Value.Snapshot.TimelineTruncated);
        Assert.Equal(0, result.Value.Snapshot.ForcePushes);
    }
    [Fact]
    public async Task Ten_full_pages_of_the_timeline_are_truncated_and_the_events_of_all_the_pages_count()
    {
        using var rig = new GhRig();
        RouteOpenPullRequest(rig.Script);
        var page = Gh.Json([.. Enumerable.Range(0, 30).Select(i => i == 0 ? "{\"event\":\"head_ref_force_pushed\"}" : "{\"event\":\"labeled\"}")]);
        rig.Script.Route("GET", Timeline, _ => Gh.Ok(page));

        var result = await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None);

        Assert.True(result.Value!.Snapshot.TimelineTruncated);
        Assert.Equal(10, result.Value.Snapshot.ForcePushes);
        Assert.Equal(10, rig.Script.Calls.Count(c => c.Path.StartsWith(Timeline, StringComparison.Ordinal)));
    }

    // ---- The reviews: every page, or none ----

    private static string ReviewJson(string login, string state) => "{\"user\":{\"login\":\"" + login + "\"},\"state\":\"" + state + "\"}";

    private static string ReviewsPage(int count, string state = "COMMENTED", string prefix = "user") =>
        Gh.Json([.. Enumerable.Range(0, count).Select(i => ReviewJson(prefix + i, state))]);

    [Fact]
    public async Task The_reviews_of_every_page_are_counted_and_the_etag_of_a_full_first_page_is_not_kept()
    {
        using var rig = new GhRig();
        RouteOpenPullRequest(rig.Script);
        rig.Script.Route("GET", Reviews, call => Gh.Ok(PageNumber(call) == 1 ? ReviewsPage(50, "APPROVED", "a") : Gh.Json(ReviewJson("late", "CHANGES_REQUESTED"), ReviewJson("a0", "COMMENTED")), "W/\"v7\""));

        var first = await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None);

        var reviews = first.Value!.Snapshot.Reviews;
        Assert.Equal(50, reviews.Approved);
        Assert.Equal(1, reviews.ChangesRequested);
        Assert.Equal(2, rig.Script.Calls.Count(c => c.Path.StartsWith(Reviews, StringComparison.Ordinal)));
        Assert.False(first.Value.Cursor.ETags.ContainsKey(ObservedResource.Reviews));

        rig.Script.Specs.Clear();
        await rig.Provider.ObserveAsync(Pr, first.Value.Cursor, CancellationToken.None);
        Assert.All(rig.Script.Calls.Where(c => c.Path.StartsWith(Reviews, StringComparison.Ordinal)), c => Assert.Null(c.IfNoneMatch));
    }

    [Fact]
    public async Task More_reviews_than_the_cap_are_never_a_half_count_the_answer_is_invalid_with_its_own_code_and_no_cursor()
    {
        using var rig = new GhRig();
        RouteOpenPullRequest(rig.Script);
        rig.Script.Route("GET", Reviews, _ => Gh.Ok(ReviewsPage(50, "APPROVED", "a")));

        var result = await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None);

        Assert.Equal(HostingOutcome.InvalidAnswer, result.Outcome);
        Assert.Equal("reviews.too-many", result.RefusalCode);
        Assert.Null(result.Value);
        Assert.Equal(10, rig.Script.Calls.Count(c => c.Path.StartsWith(Reviews, StringComparison.Ordinal)));
    }

    [Fact]
    public void An_account_that_does_not_pass_the_rule_of_a_login_is_the_same_account_in_every_review()
    {
        var summary = GitHubReviewReader.Summarize(
        [
            new GitHubReviewReader.ReviewEntry("x[bot]", ReviewVerdict.Approved), new GitHubReviewReader.ReviewEntry("x[bot]", ReviewVerdict.Approved), new GitHubReviewReader.ReviewEntry("alice", ReviewVerdict.Approved),
        ]);
        var dismissed = GitHubReviewReader.Summarize(
        [
            new GitHubReviewReader.ReviewEntry("x[bot]", ReviewVerdict.Approved), new GitHubReviewReader.ReviewEntry("X[bot]", ReviewVerdict.Dismissed),
        ]);

        Assert.Equal(2, summary.Approved);
        Assert.Equal(["alice"], summary.Approvers.Select(a => a.Value));
        Assert.Equal(0, dismissed.Approved);
        Assert.Empty(dismissed.Approvers);
    }
    // ---- The way a merge was made, deduced from real commits (D-27) ----

    private static void RouteMerged(ScriptedGh script, string commitsJson, string mergeCommitJson, string? parentJson = null, string? upToJson = null)
    {
        script.Route("GET", "/repos/octo/hello/pulls/7", "handmade-pull-merged-200.http");
        script.Route("GET", Commits, Gh.Ok(commitsJson));
        script.Route("GET", Timeline, Gh.Ok("[{\"event\":\"head_ref_force_pushed\"},{\"event\":\"head_ref_force_pushed\"},{\"event\":\"head_ref_deleted\"}]"));
        script.Route("GET", "/repos/octo/hello/commits/" + Gh.Merge, Gh.Ok(mergeCommitJson));
        if (parentJson is not null)
        {
            script.Route("GET", "/repos/octo/hello/commits/" + Gh.Sha3, Gh.Ok(parentJson));
        }

        if (upToJson is not null)
        {
            script.Route("GET", "/repos/octo/hello/commits?sha=" + Gh.Merge + "&per_page=2", Gh.Ok(upToJson));
        }
    }

    private static async Task<RemoteSnapshot> MergedAsync(GhRig rig)
    {
        var open = await FirstRoundAsync(rig);
        rig.Script.Specs.Clear();
        var merged = await rig.Provider.ObserveAsync(Pr, open.Cursor, CancellationToken.None);
        Assert.Equal(HostingOutcome.Ok, merged.Outcome);
        return merged.Value!.Snapshot;
    }

    [Fact]
    public async Task A_merge_commit_with_two_parents_is_a_merge_commit()
    {
        using var rig = new GhRig();
        var open = await FirstRoundAsync(rig);
        RouteMerged(rig.Script, Gh.Json(CommitJson(Gh.Sha2), CommitJson(Gh.Sha1)), SingleCommitJson(Gh.Merge, [Gh.Sha4, Gh.Sha1]));
        rig.Script.Specs.Clear();

        var merged = await rig.Provider.ObserveAsync(Pr, open.Cursor, CancellationToken.None);

        var snapshot = merged.Value!.Snapshot;
        Assert.Equal(PullRequestState.Merged, snapshot.State);
        Assert.Equal(Gh.Merge, snapshot.MergeCommit);
        Assert.Equal(RemoteMergeMode.MergeCommit, snapshot.MergeMode);
        Assert.Equal([Gh.Merge], snapshot.ResultCommits);
        Assert.Equal("octocat", snapshot.MergedBy!.Value);
        Assert.False(snapshot.HeadBranchExists);
        Assert.Equal([Gh.Sha2, Gh.Sha1], snapshot.OriginalCommits);
        Assert.Equal(ReconcileKind(open.Snapshot, snapshot), [RemoteChangeKind.Merged, RemoteChangeKind.HeadBranchDeleted]);
    }

    private static IReadOnlyList<RemoteChangeKind> ReconcileKind(RemoteSnapshot last, RemoteSnapshot observed) => RemoteReconciler.Reconcile(last, observed).Changes;

    [Fact]
    public async Task A_merge_commit_with_one_parent_that_differs_from_the_next_to_last_commit_is_a_squash()
    {
        using var rig = new GhRig();
        var open = await FirstRoundAsync(rig);
        RouteMerged(
            rig.Script,
            Gh.Json(CommitJson(Gh.Sha2, message: "primero"), CommitJson(Gh.Sha1, message: "segundo")),
            SingleCommitJson(Gh.Merge, [Gh.Sha3], message: "Agent Studio (#7)"),
            SingleCommitJson(Gh.Sha3, [Gh.Sha4], name: "Otra persona", message: "un commit de la rama base"));
        rig.Script.Specs.Clear();

        var snapshot = (await rig.Provider.ObserveAsync(Pr, open.Cursor, CancellationToken.None)).Value!.Snapshot;

        Assert.Equal(RemoteMergeMode.Squash, snapshot.MergeMode);
        Assert.Equal([Gh.Merge], snapshot.ResultCommits);
    }

    [Fact]
    public async Task A_merge_commit_whose_parent_has_the_author_the_date_and_the_message_of_the_next_to_last_commit_is_a_rebase_and_leaves_as_many_commits_as_the_pull_request_had()
    {
        using var rig = new GhRig();
        var open = await FirstRoundAsync(rig);
        RouteMerged(
            rig.Script,
            Gh.Json(CommitJson(Gh.Sha2, message: "primero"), CommitJson(Gh.Sha1, message: "segundo")),
            SingleCommitJson(Gh.Merge, [Gh.Sha3], message: "segundo"),
            SingleCommitJson(Gh.Sha3, [Gh.Sha4], message: "primero"),
            Gh.Json(CommitJson(Gh.Merge), CommitJson(Gh.Sha3)));
        rig.Script.Specs.Clear();

        var snapshot = (await rig.Provider.ObserveAsync(Pr, open.Cursor, CancellationToken.None)).Value!.Snapshot;

        Assert.Equal(RemoteMergeMode.Rebase, snapshot.MergeMode);
        Assert.Equal([Gh.Sha3, Gh.Merge], snapshot.ResultCommits);
        Assert.Empty(rig.Script.Unmatched);
    }

    [Fact]
    public async Task With_a_single_commit_a_one_parent_merge_is_squash_or_rebase_and_with_none_it_is_unknown()
    {
        using var one = new GhRig();
        var open = await FirstRoundAsync(one);
        RouteMerged(one.Script, Gh.Json(CommitJson(Gh.Sha1)), SingleCommitJson(Gh.Merge, [Gh.Sha3]));
        one.Script.Specs.Clear();

        var single = (await one.Provider.ObserveAsync(Pr, open.Cursor, CancellationToken.None)).Value!.Snapshot;

        Assert.Equal(RemoteMergeMode.SquashOrRebase, single.MergeMode);
        Assert.Equal([Gh.Merge], single.ResultCommits);

        using var none = new GhRig();
        var openToo = await FirstRoundAsync(none);
        RouteMerged(none.Script, Gh.Json(CommitJson(Gh.Sha1)), SingleCommitJson(Gh.Merge, []));
        none.Script.Specs.Clear();

        var root = (await none.Provider.ObserveAsync(Pr, openToo.Cursor, CancellationToken.None)).Value!.Snapshot;

        Assert.Equal(RemoteMergeMode.Unknown, root.MergeMode);
    }

    [Fact]
    public async Task A_merged_pull_request_with_no_merge_commit_in_the_answer_is_invalid()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"state\":\"closed\",\"merged\":true,\"updated_at\":\"2026-11-13T11:00:00Z\",\"head\":{\"sha\":\"" + Gh.Sha1 + "\"},\"base\":{\"ref\":\"main\"}}"));

        Assert.Equal(HostingOutcome.InvalidAnswer, (await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_pull_request_closed_without_merging_reads_its_commits_and_its_timeline_once_and_asks_only_the_pull_request_after()
    {
        using var rig = new GhRig();
        var open = await FirstRoundAsync(rig);
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"number\":7,\"state\":\"closed\",\"merged\":false,\"mergeable_state\":\"unknown\",\"updated_at\":\"2026-11-13T11:00:00Z\",\"head\":{\"sha\":\"" + Gh.Sha1 + "\"},\"base\":{\"ref\":\"main\"}}", "W/\"p3\""));
        rig.Script.Specs.Clear();

        var closed = await rig.Provider.ObserveAsync(Pr, open.Cursor, CancellationToken.None);

        Assert.Equal(PullRequestState.Closed, closed.Value!.Snapshot.State);
        Assert.Null(closed.Value.Snapshot.MergeCommit);
        Assert.Contains(rig.Script.Calls, c => c.Path.StartsWith(Commits, StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Script.Calls, c => c.Path.StartsWith(CheckRuns(Gh.Sha1), StringComparison.Ordinal) || c.Path.StartsWith(Reviews, StringComparison.Ordinal));

        Conditional(rig.Script, "/repos/octo/hello/pulls/7", () => Gh.Ok("{}", "W/\"p3\""));
        rig.Script.Specs.Clear();
        var again = await rig.Provider.ObserveAsync(Pr, closed.Value.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.NotModified, again.Outcome);
        Assert.Single(rig.Script.Specs);
    }

    // ---- A pull request merged earlier: the base branch and the deployments ----

    private static string RevertMessage(string sha) => "Revert \\\"x\\\"\\n\\nThis reverts commit " + sha + ".";

    private static void RouteMergedPoll(ScriptedGh script, Func<string> baseCommits, Func<ProcessResult>? deployments = null)
    {
        Conditional(script, "/repos/octo/hello/commits?sha=main&", () => Gh.Ok(baseCommits(), "W/\"b1\""));
        Conditional(script, "/repos/octo/hello/deployments?sha=" + Gh.Merge + "&", deployments ?? (() => Gh.Fixture("handmade-deployments-200.http")));
        script.Route("GET", "/repos/octo/hello/deployments/501/statuses", "handmade-deployment-statuses-200.http");
    }
    private static async Task<PullRequestObservation> MergedCursorAsync(GhRig rig)
    {
        var open = await FirstRoundAsync(rig);
        RouteMerged(rig.Script, Gh.Json(CommitJson(Gh.Sha2), CommitJson(Gh.Sha1)), SingleCommitJson(Gh.Merge, [Gh.Sha4, Gh.Sha1]));
        var merged = await rig.Provider.ObserveAsync(Pr, open.Cursor, CancellationToken.None);
        Assert.Equal(HostingOutcome.Ok, merged.Outcome);
        return merged.Value!;
    }

    [Fact]
    public async Task A_merged_pull_request_asks_only_the_base_branch_since_the_merge_and_the_deployments_and_finds_a_revert_by_the_line_of_git()
    {
        using var rig = new GhRig();
        var merged = await MergedCursorAsync(rig);
        RouteMergedPoll(rig.Script, () => Gh.Json(CommitJson(Gh.Sha3, message: "otra cosa"), CommitJson(Gh.Sha4, message: RevertMessage(Gh.Merge)), CommitJson(Gh.Sha2, message: RevertMessage(Gh.Sha3))));
        rig.Script.Specs.Clear();

        var round = await rig.Provider.ObserveAsync(Pr, merged.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, round.Outcome);
        var snapshot = round.Value!.Snapshot;
        Assert.Equal([Gh.Sha4], snapshot.RevertCommits);
        Assert.Equal(new RemoteDeployment(501, Gh.Merge, DeploymentState.Succeeded), snapshot.Deployment);
        Assert.Equal("production", round.Value.Display.DeploymentEnvironment);
        Assert.Equal(merged.Snapshot.MergeCommit, snapshot.MergeCommit);
        Assert.Equal(merged.Snapshot.UpdatedAt, snapshot.UpdatedAt);
        Assert.Equal(
            [
                "GET /repos/octo/hello/commits?sha=main&since=2026-11-13T10%3A00%3A00Z&per_page=50&page=1",
                "GET /repos/octo/hello/deployments?sha=" + Gh.Merge + "&per_page=20",
                "GET /repos/octo/hello/deployments/501/statuses?per_page=10",
            ],
            rig.Script.Calls.Select(c => c.ToString()));
        Assert.Equal(
            [RemoteChangeKind.RevertSuspected, RemoteChangeKind.DeploymentChanged],
            RemoteReconciler.Reconcile(merged.Snapshot, snapshot).Changes);
    }

    [Fact]
    public async Task With_both_resources_unchanged_a_merged_poll_is_two_requests_with_a_condition_and_not_modified()
    {
        using var rig = new GhRig();
        var merged = await MergedCursorAsync(rig);
        RouteMergedPoll(rig.Script, () => Gh.Json(CommitJson(Gh.Sha4, message: RevertMessage(Gh.Merge))));
        var first = await rig.Provider.ObserveAsync(Pr, merged.Cursor, CancellationToken.None);
        rig.Script.Specs.Clear();

        var second = await rig.Provider.ObserveAsync(Pr, first.Value!.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.NotModified, second.Outcome);
        Assert.Equal(2, rig.Script.Specs.Count);
        Assert.All(rig.Script.Calls, c => Assert.NotNull(c.IfNoneMatch));
        Assert.DoesNotContain(rig.Script.Calls, c => c.Path == "/repos/octo/hello/pulls/7");
    }

    [Fact]
    public async Task The_reverts_and_the_deployment_only_grow_a_read_that_shows_less_is_an_old_replica()
    {
        using var rig = new GhRig();
        var merged = await MergedCursorAsync(rig);
        var commits = Gh.Json(CommitJson(Gh.Sha4, message: RevertMessage(Gh.Merge)));
        var deployments = true;
        RouteMergedPoll(rig.Script, () => commits, () => deployments ? Gh.Fixture("handmade-deployments-200.http") : Gh.Ok("[]", "W/\"d2\""));
        var first = await rig.Provider.ObserveAsync(Pr, merged.Cursor, CancellationToken.None);

        // Another read of the base branch that shows another revert and not the first, and a list of deployments that is empty.
        commits = Gh.Json(CommitJson(Gh.Sha3, message: RevertMessage(Gh.Merge)));
        deployments = false;
        var cursor = first.Value!.Cursor with { ETags = new Dictionary<ObservedResource, string>() };
        var second = await rig.Provider.ObserveAsync(Pr, cursor, CancellationToken.None);

        var snapshot = second.Value!.Snapshot;
        Assert.Equal([Gh.Sha4, Gh.Sha3], snapshot.RevertCommits);
        Assert.Equal(new RemoteDeployment(501, Gh.Merge, DeploymentState.Succeeded), snapshot.Deployment);
    }

    [Fact]
    public async Task The_base_branch_is_read_in_pages_and_a_revert_on_a_later_page_is_found_and_the_etag_of_a_full_first_page_is_not_kept()
    {
        using var rig = new GhRig();
        var merged = await MergedCursorAsync(rig);
        RouteMergedPoll(rig.Script, () => string.Empty);
        rig.Script.Route("GET", "/repos/octo/hello/commits?sha=main&", call => PageNumber(call) == 1 ? Gh.Ok(Page(50), "W/\"b1\"") : Gh.Ok(Gh.Json(CommitJson(Gh.Sha4, message: RevertMessage(Gh.Merge)))));
        rig.Script.Specs.Clear();

        var round = await rig.Provider.ObserveAsync(Pr, merged.Cursor, CancellationToken.None);

        Assert.Equal([Gh.Sha4], round.Value!.Snapshot.RevertCommits);
        Assert.Equal(2, rig.Script.Calls.Count(c => c.Path.StartsWith("/repos/octo/hello/commits?sha=main", StringComparison.Ordinal)));
        Assert.False(round.Value.Cursor.ETags.ContainsKey(ObservedResource.BaseCommits));
    }

    [Fact]
    public async Task A_revert_that_leaves_the_window_of_the_read_stays_in_the_snapshot_so_the_reconciler_never_sees_the_list_shrink()
    {
        using var rig = new GhRig();
        var merged = await MergedCursorAsync(rig);
        var commits = Gh.Json(CommitJson(Gh.Sha4, message: RevertMessage(Gh.Merge)));
        RouteMergedPoll(rig.Script, () => commits);
        var first = await rig.Provider.ObserveAsync(Pr, merged.Cursor, CancellationToken.None);

        commits = Gh.Json(CommitJson(Gh.Sha3, message: "otra cosa"));
        var cursor = first.Value!.Cursor with { ETags = new Dictionary<ObservedResource, string>() };
        var second = await rig.Provider.ObserveAsync(Pr, cursor, CancellationToken.None);

        Assert.Equal([Gh.Sha4], second.Value!.Snapshot.RevertCommits);
        Assert.False(RemoteReconciler.Reconcile(first.Value.Snapshot, second.Value.Snapshot).Discarded);
    }

    [Fact]
    public async Task A_deployment_list_that_is_not_a_list_is_invalid_and_a_message_that_only_mentions_a_revert_is_not_one()
    {
        using var rig = new GhRig();
        var merged = await MergedCursorAsync(rig);
        RouteMergedPoll(rig.Script, () => Gh.Json(CommitJson(Gh.Sha4, message: "this reverts commit " + Gh.Merge), CommitJson(Gh.Sha3, message: "This reverts commit " + Gh.Merge + " and more")));
        rig.Script.Route("GET", "/repos/octo/hello/deployments?sha=" + Gh.Merge + "&", _ => Gh.Ok("{\"not\":\"a list\"}"));

        var result = await rig.Provider.ObserveAsync(Pr, merged.Cursor, CancellationToken.None);

        Assert.Equal(HostingOutcome.InvalidAnswer, result.Outcome);
    }

    [Fact]
    public async Task What_the_remote_wrote_in_a_title_or_a_check_name_is_cleaned_and_cut_for_the_screen()
    {
        using var rig = new GhRig();
        RouteOpenPullRequest(rig.Script);
        var longTitle = new string('t', 500);
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{\"number\":7,\"state\":\"open\",\"draft\":false,\"merged\":false,\"title\":\"a\\u202Eb\\nc " + longTitle + "\",\"updated_at\":\"2026-11-13T10:00:00Z\",\"head\":{\"sha\":\"" + Gh.Sha1 + "\"},\"base\":{\"ref\":\"main\"}}"));
        rig.Script.Route("GET", CheckRuns(Gh.Sha1), Gh.Ok("{\"total_count\":1,\"check_runs\":[{\"name\":\"x\\u202Ey\\u0007z\\u001b[31m\",\"status\":\"completed\",\"conclusion\":\"failure\"}]}"));

        var result = await rig.Provider.ObserveAsync(Pr, null, CancellationToken.None);

        var title = result.Value!.Display.Title!;
        Assert.Equal(200, title.Length);
        Assert.StartsWith("ab c ", title, StringComparison.Ordinal);
        Assert.Equal(["xy z [31m"], result.Value.Display.FailingCheckNames);
    }
}
