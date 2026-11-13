using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Hosting.GitHub;

namespace AgentStudio.Tests.Infrastructure.Hosting.GitHub;

/// <summary>
/// The structural half of F10-2, F10-5, F10-11 and F10-12 (docs/21 §9): the whole battery of what the adapter can ask is run (every member of the contract, with hostile text and hostile variables of the process),
/// and every <see cref="ProcessSpec"/> that a <c>gh</c> received is walked. The list of calls is closed: each one has to be one of the entries of the catalog, and every entry of the catalog has to appear, so an
/// entry that is added later cannot escape the walk. Nothing here launches <c>gh</c>.
/// </summary>
public sealed class HostingBatteryTests
{
    private static readonly RemoteRepository Repo = GhRig.Repo;
    private static readonly PullRequestRef Pr = GhRig.Pr;

    // The closed list of calls, as the design fixes it (docs/21 D-10): the method and the form of the path (and, for the two that share one, what their body says).
    private static readonly (GitHubEndpoint Endpoint, string Method, Regex Path, Regex? Body)[] Catalog =
    [
        (GitHubEndpoint.Viewer, "GET", new(@"^/user$"), null),
        (GitHubEndpoint.Repository, "GET", new(@"^/repos/octo/hello$"), null),
        (GitHubEndpoint.BranchRules, "GET", new(@"^/repos/octo/hello/rules/branches/[A-Za-z0-9._/-]+$"), null),
        (GitHubEndpoint.Branch, "GET", new(@"^/repos/octo/hello/branches/[A-Za-z0-9._/-]+$"), null),
        (GitHubEndpoint.FindPullRequests, "GET", new(@"^/repos/octo/hello/pulls\?head=octo%3A[A-Za-z0-9._%-]+&state=all&per_page=5$"), null),
        (GitHubEndpoint.OpenPullRequest, "POST", new(@"^/repos/octo/hello/pulls$"), new(@"^\{""title"":.*,""head"":.*,""base"":.*,""body"":.*,""draft"":(true|false)\}$", RegexOptions.Singleline)),
        (GitHubEndpoint.UpdatePullRequest, "PATCH", new(@"^/repos/octo/hello/pulls/7$"), new(@"^\{""title"":.*,""body"":.*\}$", RegexOptions.Singleline)),
        (GitHubEndpoint.ClosePullRequest, "PATCH", new(@"^/repos/octo/hello/pulls/7$"), new(@"^\{""state"":""closed""\}$")),
        (GitHubEndpoint.PullRequest, "GET", new(@"^/repos/octo/hello/pulls/7$"), null),
        (GitHubEndpoint.PullRequestCommits, "GET", new(@"^/repos/octo/hello/pulls/7/commits\?per_page=100&page=[1-3]$"), null),
        (GitHubEndpoint.PullRequestTimeline, "GET", new(@"^/repos/octo/hello/issues/7/timeline\?per_page=100&page=[1-3]$"), null),
        (GitHubEndpoint.Reviews, "GET", new(@"^/repos/octo/hello/pulls/7/reviews\?per_page=100$"), null),
        (GitHubEndpoint.RequestReviewers, "POST", new(@"^/repos/octo/hello/pulls/7/requested_reviewers$"), new(@"^\{""reviewers"":\[(""[A-Za-z0-9-]+"",?)+\]\}$")),
        (GitHubEndpoint.CheckRuns, "GET", new(@"^/repos/octo/hello/commits/[0-9a-f]{40}/check-runs\?per_page=100$"), null),
        (GitHubEndpoint.CombinedStatus, "GET", new(@"^/repos/octo/hello/commits/[0-9a-f]{40}/status\?per_page=100$"), null),
        (GitHubEndpoint.Commit, "GET", new(@"^/repos/octo/hello/commits/[0-9a-f]{40}$"), null),
        (GitHubEndpoint.CommitList, "GET", new(@"^/repos/octo/hello/commits\?sha=(main&since=[0-9A-Za-z%.-]+&per_page=50|[0-9a-f]{40}&per_page=\d+)$"), null),
        (GitHubEndpoint.PostCommitStatus, "POST", new(@"^/repos/octo/hello/statuses/[0-9a-f]{40}$"), new(@"^\{""state"":""(success|failure|pending)"",""context"":""agent-studio/verdict"",""description"":"".{1,120}""\}$")),
        (GitHubEndpoint.PostComment, "POST", new(@"^/repos/octo/hello/issues/7/comments$"), new(@"^\{""body"":"".*""\}$", RegexOptions.Singleline)),
        (GitHubEndpoint.EditComment, "PATCH", new(@"^/repos/octo/hello/issues/comments/[0-9]+$"), new(@"^\{""body"":"".*""\}$", RegexOptions.Singleline)),
        (GitHubEndpoint.Comments, "GET", new(@"^/repos/octo/hello/issues/7/comments\?per_page=100&page=[1-3]$"), null),
        (GitHubEndpoint.Merge, "PUT", new(@"^/repos/octo/hello/pulls/7/merge$"), new(@"^\{""merge_method"":""(merge|squash|rebase)"",""sha"":""[0-9a-f]{40}""\}$")),
        (GitHubEndpoint.DeleteBranch, "DELETE", new(@"^/repos/octo/hello/git/refs/heads/agentstudio/run-[A-Za-z0-9_-]+$"), null),
        (GitHubEndpoint.Deployments, "GET", new(@"^/repos/octo/hello/deployments\?sha=[0-9a-f]{40}&per_page=20$"), null),
        (GitHubEndpoint.DeploymentStatuses, "GET", new(@"^/repos/octo/hello/deployments/[0-9]+/statuses\?per_page=10$"), null),
    ];

    private static readonly string[] Planted = ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GH_HOST", "GH_REPO", "GH_CONFIG_DIR", "GH_DEBUG", "GIT_ASKPASS", "GCM_TRACE", "GIT_SSH_COMMAND", "GIT_TRACE"];

    private static readonly string[] Hostile =
    [
        "-x", "--admin", "--upload-pack=x", "@fichero", "..", "%2e%2e", "?", "#", "a b", "\"comillas\"", "a&b", "a|b", "{owner}/{repo}/{branch}", "'; rm -rf /; '", "`code`", "line1\nline2", "\\", "\u2028x",
    ];

    private static GhRig Rig() => new(ambient: Planted.ToDictionary(n => n, n => "sentinel-" + n, StringComparer.OrdinalIgnoreCase));

    private static (string Id, string Json) Entry(string sha, string message = "m") =>
        (sha, "{\"sha\":\"" + sha + "\",\"commit\":{\"author\":{\"name\":\"Octo\",\"date\":\"2026-11-13T09:00:00Z\"},\"message\":\"" + message + "\"}}");

    private static void RouteEverything(ScriptedGh script, Func<bool> merged)
    {
        script.Route("GET", "/user", "handmade-user-200.http");
        script.Route("GET", "/repos/octo/hello", "handmade-repo-200.http");
        script.Route("GET", "/repos/octo/hello/rules/branches/main", "handmade-rules-200.http");
        script.Route("GET", "/repos/octo/hello/branches/main", "handmade-branch-200.http");
        script.Route("GET", "/repos/octo/hello/branches/agentstudio/run-run1", "handmade-branch-200.http");
        script.Route("GET", "/repos/octo/hello/pulls?head=", "handmade-pulls-list-200.http");
        script.Route("POST", "/repos/octo/hello/pulls", "handmade-open-pr-201.http");
        script.Route("PATCH", "/repos/octo/hello/pulls/7", Gh.Ok("{}"));
        script.Route("GET", "/repos/octo/hello/pulls/7", _ => Gh.Fixture(merged() ? "handmade-pull-merged-200.http" : "handmade-pull-open-200.http"));
        script.Route("GET", "/repos/octo/hello/pulls/7/commits", Gh.Ok("[" + Entry(Gh.Sha2, "primero").Json + "," + Entry(Gh.Sha1, "segundo").Json + "]"));
        script.Route("GET", "/repos/octo/hello/issues/7/timeline", "handmade-timeline-200.http");
        script.Route("GET", "/repos/octo/hello/pulls/7/reviews", "handmade-reviews-200.http");
        script.Route("POST", "/repos/octo/hello/pulls/7/requested_reviewers", Gh.Http(201, "{}"));
        script.Route("GET", "/repos/octo/hello/commits/" + Gh.Sha1 + "/check-runs", "handmade-check-runs-200.http");
        script.Route("GET", "/repos/octo/hello/commits/" + Gh.Sha1 + "/status", "handmade-combined-status-200.http");
        script.Route("GET", "/repos/octo/hello/commits/" + Gh.Merge, Gh.Ok("{\"sha\":\"" + Gh.Merge + "\",\"parents\":[{\"sha\":\"" + Gh.Sha3 + "\"}],\"commit\":{\"author\":{\"name\":\"Octo\",\"date\":\"2026-11-13T09:00:00Z\"},\"message\":\"segundo\"}}"));
        script.Route("GET", "/repos/octo/hello/commits/" + Gh.Sha3, Gh.Ok("{\"sha\":\"" + Gh.Sha3 + "\",\"parents\":[{\"sha\":\"" + Gh.Sha4 + "\"}],\"commit\":{\"author\":{\"name\":\"Octo\",\"date\":\"2026-11-13T09:00:00Z\"},\"message\":\"primero\"}}"));
        script.Route("GET", "/repos/octo/hello/commits?sha=" + Gh.Merge + "&", Gh.Ok("[" + Entry(Gh.Merge).Json + "," + Entry(Gh.Sha3).Json + "]"));
        script.Route("GET", "/repos/octo/hello/commits?sha=main&", Gh.Ok("[" + Entry(Gh.Sha4, "This reverts commit " + Gh.Merge + ".").Json + "]"));
        script.Route("POST", "/repos/octo/hello/statuses/", "handmade-statuses-201.http");
        script.Route("POST", "/repos/octo/hello/issues/7/comments", "handmade-comment-201.http");
        script.Route("PATCH", "/repos/octo/hello/issues/comments/", Gh.Ok("{\"id\":4242}"));
        script.Route("GET", "/repos/octo/hello/issues/7/comments", Gh.Ok("[]"));
        script.Route("PUT", "/repos/octo/hello/pulls/7/merge", "handmade-merge-200.http");
        script.Route("DELETE", "/repos/octo/hello/git/refs/heads/", Gh.Http(204));
        script.Route("GET", "/repos/octo/hello/deployments?sha=" + Gh.Merge + "&", "handmade-deployments-200.http");
        script.Route("GET", "/repos/octo/hello/deployments/501/statuses", "handmade-deployment-statuses-200.http");
    }

    /// <summary>Every member of the contract, with hostile text, with and without conditions, for each of the three merge modes and for the poll of a merged pull request.</summary>
    private static async Task RunBatteryAsync(GhRig rig)
    {
        var merged = false;
        RouteEverything(rig.Script, () => merged);
        var provider = rig.Provider;
        var ct = CancellationToken.None;
        await provider.ProbeAsync(ct);
        await provider.ReadAccountAsync(Repo, ct);
        await provider.ReadRepositoryAsync(Repo, "W/\"r1\"", ct);
        await provider.ReadBranchRulesAsync(Repo, GhRig.Main, ct);
        await provider.FindPullRequestAsync(Repo, GhRig.RunBranch, ct);
        foreach (var text in Hostile)
        {
            await provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, GhRig.Main, new PullRequestText(text, text + "\n" + text), Draft: text.Length % 2 == 0), ct);
            await provider.UpdatePullRequestAsync(Pr, new PullRequestText(text, text), ct);
        }

        await provider.RequestReviewersAsync(Pr, [RemoteLogin.TryParse("alice")!, RemoteLogin.TryParse("bob")!], ct);
        var open = await provider.ObserveAsync(Pr, null, ct);
        await provider.ObserveAsync(Pr, open.Value!.Cursor, ct);
        merged = true;
        var done = await provider.ObserveAsync(Pr, open.Value.Cursor, ct);
        await provider.ObserveAsync(Pr, done.Value!.Cursor, ct);
        merged = false;
        foreach (var standing in Enum.GetValues<VerdictStanding>())
        {
            var notice = new VerdictNotice(new RunId("run1"), Gh.Sha1, standing, [new RequirementOutcome("R-1", true)], "claude-code", "model", true);
            await provider.PublishVerdictAsync(Pr, notice, "texto del sistema", null, ct);
            await provider.PublishVerdictAsync(Pr, notice, "texto del sistema", 4242, ct);
        }

        foreach (var method in Enum.GetValues<MergeMethod>())
        {
            await provider.MergeAsync(Pr, method, Gh.Sha1, ct);
        }

        await provider.CloseAsync(Pr, ct);
        await provider.DeleteRunBranchAsync(Repo, GhRig.RunBranch, Gh.Sha1, ct);
    }

    private static string Classify(GhCallSeen call)
    {
        var matches = Catalog.Where(e => e.Method == call.Method && e.Path.IsMatch(call.Path) && (e.Body is null || (call.Body is not null && e.Body.IsMatch(call.Body)))).Select(e => e.Endpoint).ToList();
        Assert.True(matches.Count == 1, $"{call} is not exactly one entry of the catalog ({matches.Count})");
        return matches[0].ToString();
    }

    [Fact]
    public void The_closed_list_names_every_entry_of_the_catalog_once()
    {
        Assert.Equal(Enum.GetValues<GitHubEndpoint>().Order(), Catalog.Select(e => e.Endpoint).Order());
    }

    [Fact]
    public async Task Every_call_of_the_battery_is_one_entry_of_the_closed_list_and_every_entry_was_called()
    {
        using var rig = Rig();

        await RunBatteryAsync(rig);

        var seen = rig.Script.Calls.Select(Classify).ToHashSet();
        Assert.Equal(Enum.GetValues<GitHubEndpoint>().Select(e => e.ToString()).Order(), seen.Order());
        Assert.Empty(rig.Script.Unmatched);
    }

    [Fact]
    public async Task Every_command_has_the_one_form_the_host_the_flags_and_the_headers_and_nothing_else()
    {
        using var rig = Rig();

        await RunBatteryAsync(rig);

        Assert.True(rig.Script.Specs.Count > 60);
        var allowedFlags = new HashSet<string> { "--hostname", "-X", "-i", "-H", "--input", "-" };
        foreach (var spec in rig.Script.Specs)
        {
            var args = spec.Arguments;
            Assert.Equal(Path.Combine(rig.Tools, "gh.exe"), spec.FileName);
            Assert.Equal("api", args[0]);
            Assert.Equal(["--hostname", "github.com"], args.Skip(1).Take(2));
            Assert.Contains(args[4], new[] { "GET", "POST", "PATCH", "PUT", "DELETE" });
            Assert.Equal("-X", args[3]);
            Assert.Equal("-i", args[5]);
            var headers = args.Select((a, i) => (a, i)).Where(p => p.a == "-H").Select(p => args[p.i + 1]).ToList();
            Assert.Equal("Accept: application/vnd.github+json", headers[0]);
            Assert.Equal("X-GitHub-Api-Version: 2022-11-28", headers[1]);
            Assert.True(headers.Count == 2 || (headers.Count == 3 && headers[2].StartsWith("If-None-Match: ", StringComparison.Ordinal)));
            Assert.All(args.Where(a => a.StartsWith('-')), a => Assert.Contains(a, allowedFlags));
            Assert.DoesNotContain(args, a => a.Contains('{', StringComparison.Ordinal) || a.Contains('}', StringComparison.Ordinal));
            Assert.Matches(@"^/(user|repos/octo/hello[^ ]*)$", args[^1]);
            Assert.DoesNotContain(args, a => a is "auth" or "token" or "--show-token" or "pr" or "review" or "--approve" or "-f" or "-F" or "--field" or "--raw-field" or "--jq" or "--template" or "--paginate" or "--cache" or "--verbose");
        }
    }

    [Fact]
    public async Task The_neutral_folder_and_the_time_and_size_limits_are_in_every_launch()
    {
        using var rig = Rig();

        await RunBatteryAsync(rig);

        Assert.All(rig.Script.Specs, spec =>
        {
            Assert.Equal(rig.Folders.WorkingFolder, spec.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(20), spec.Timeout);
            Assert.Equal(1024 * 1024, spec.MaxOutputCharacters);
            Assert.Equal(ProcessIsolation.None, spec.Isolation);
            Assert.False(spec.KeepStandardInputOpen);
        });
    }

    [Fact]
    public async Task The_data_of_a_write_travel_through_standard_input_as_json_and_a_read_sends_none()
    {
        using var rig = Rig();

        await RunBatteryAsync(rig);

        foreach (var call in rig.Script.Calls)
        {
            var writes = call.Method is "POST" or "PATCH" or "PUT";
            Assert.Equal(writes, call.Body is not null);
            Assert.Equal(writes, call.Spec.Arguments.Contains("--input"));
            if (call.Body is not null)
            {
                using var json = JsonDocument.Parse(call.Body);
                Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
                Assert.Equal(["--input", "-"], call.Spec.Arguments.Skip(call.Spec.Arguments.Count - 3).Take(2));
            }
        }
    }

    [Fact]
    public async Task No_variable_that_gives_gh_a_token_or_sends_it_elsewhere_reaches_any_launch_and_no_value_is_one()
    {
        using var rig = Rig();

        await RunBatteryAsync(rig);

        Assert.All(rig.Script.Specs, spec =>
        {
            var environment = spec.Environment!;
            foreach (var name in Planted)
            {
                Assert.True(environment.TryGetValue(name, out var value) && value is null, name);
            }

            Assert.DoesNotContain(environment.Values, v => v is not null && v.Contains("sentinel-", StringComparison.Ordinal));
            Assert.Equal("1", environment["GH_PROMPT_DISABLED"]);
            Assert.Equal("false", environment["GH_TELEMETRY"]);
        });
    }

    [Fact]
    public async Task A_review_is_never_sent_not_by_the_command_not_by_the_path_not_by_the_body()
    {
        using var rig = Rig();

        await RunBatteryAsync(rig);

        foreach (var call in rig.Script.Calls)
        {
            Assert.DoesNotContain(call.Spec.Arguments, a => a.Contains("approve", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(call.Spec.Arguments, a => a is "pr" or "review");
            if (call.Method != "GET")
            {
                Assert.False(call.Path.Contains("/reviews", StringComparison.Ordinal), call.ToString());
            }

            Assert.DoesNotContain("\"event\"", call.Body ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("APPROVE", call.Body ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.Contains(rig.Script.Calls, c => c.Method == "GET" && c.Path.Contains("/reviews", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_destructive_is_launched_outside_what_the_contract_declares()
    {
        using var rig = Rig();

        await RunBatteryAsync(rig);

        var destructive = rig.Script.Calls.Where(c => c.Method == "DELETE" || (c.Method == "PATCH" && c.Body == "{\"state\":\"closed\"}")).ToList();
        Assert.All(destructive.Where(c => c.Method == "DELETE"), c => Assert.StartsWith("/repos/octo/hello/git/refs/heads/agentstudio/run-", c.Path, StringComparison.Ordinal));
        Assert.Equal(2, destructive.Count);
        // A merge always carries the head that was expected, and nothing that would change who merges or how the message reads.
        foreach (var merge in rig.Script.Calls.Where(c => c.Method == "PUT"))
        {
            using var body = JsonDocument.Parse(merge.Body!);
            Assert.Equal(["merge_method", "sha"], body.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(40, body.RootElement.GetProperty("sha").GetString()!.Length);
        }
    }

    [Fact]
    public async Task Hostile_text_changes_no_argument_with_respect_to_innocuous_text()
    {
        using var rig = Rig();
        RouteEverything(rig.Script, () => false);
        await rig.Provider.ProbeAsync(CancellationToken.None);

        async Task<List<List<string>>> ArgumentsFor(string text)
        {
            rig.Script.Specs.Clear();
            await rig.Provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, GhRig.Main, new PullRequestText(text, text), false), CancellationToken.None);
            await rig.Provider.UpdatePullRequestAsync(Pr, new PullRequestText(text, text), CancellationToken.None);
            await rig.Provider.PublishVerdictAsync(Pr, new VerdictNotice(new RunId("run1"), Gh.Sha1, VerdictStanding.Approved, [], "p", "m", true), text, 4242, CancellationToken.None);
            return [.. rig.Script.Specs.Select(s => s.Arguments.ToList())];
        }

        var baseline = await ArgumentsFor("innocuous");
        foreach (var text in Hostile.Where(t => !t.Contains('\n', StringComparison.Ordinal) && !t.Contains('\u2028', StringComparison.Ordinal)))
        {
            Assert.Equal(baseline, await ArgumentsFor(text));
        }
    }
}
