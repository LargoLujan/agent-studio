using System.Text.Json;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Hosting.GitHub;

namespace AgentStudio.Tests.Infrastructure.Hosting.GitHub;

/// <summary>
/// F10-5 (docs/21 D-21): the verdict reaches the pull request as the status of the judged commit and as a tagged comment, never as a review. The status has the fixed context and one of three
/// states with a fixed description; the comment has its mark in the first line, is edited in place by its id (or found by its mark and its author) and carries no text of the judge that the caller did not give.
/// The structural proof that no call of a review is ever launched is in <see cref="HostingBatteryTests"/>.
/// </summary>
public sealed class VerdictPublicationTests
{
    private const string Mark = "<!-- agent-studio:verdict run=run1 -->";

    private static readonly PullRequestRef Pr = GhRig.Pr;

    private static VerdictNotice Notice(VerdictStanding standing, string commit = Gh.Sha1) =>
        new(new RunId("run1"), commit, standing, [new RequirementOutcome("R-1", true), new RequirementOutcome("R-2", standing == VerdictStanding.Approved)], "claude-code", "model-x", HeadIsJudgedCommit: true);

    private static GhRig WithRoutes()
    {
        var rig = new GhRig();
        rig.Script.Route("POST", "/repos/octo/hello/statuses/", "handmade-statuses-201.http");
        rig.Script.Route("POST", "/repos/octo/hello/issues/7/comments", "handmade-comment-201.http");
        rig.Script.Route("PATCH", "/repos/octo/hello/issues/comments/", Gh.Ok("{\"id\":4242}"));
        rig.Script.Route("GET", "/repos/octo/hello/issues/comments/", "handmade-comment-200.http");
        rig.Script.Route("GET", "/user", "handmade-user-200.http");
        rig.Script.Route("GET", "/repos/octo/hello/issues/7/comments", Gh.Ok("[]"));
        return rig;
    }

    [Theory]
    [InlineData(VerdictStanding.Approved, "success", "Descripción compuesta por quien llama: aprobado")]
    [InlineData(VerdictStanding.Rejected, "failure", "Descripción compuesta por quien llama: no aprobado")]
    [InlineData(VerdictStanding.InReview, "pending", "Descripción compuesta por quien llama: en revisión")]
    public async Task The_status_of_the_judged_commit_has_the_fixed_context_the_state_of_the_standing_and_the_description_the_caller_composed(VerdictStanding standing, string state, string description)
    {
        using var rig = WithRoutes();

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(standing), description, "Comentario del sistema", null, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.True(result.Value!.StatusPublished);
        Assert.Equal(4242L, result.Value.CommentId);
        var status = rig.Script.Calls.Single(c => c.Path.Contains("/statuses/", StringComparison.Ordinal));
        Assert.Equal("POST /repos/octo/hello/statuses/" + Gh.Sha1, status.ToString());
        using var body = JsonDocument.Parse(status.Body!);
        Assert.Equal(["state", "context", "description"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(state, body.RootElement.GetProperty("state").GetString());
        Assert.Equal("agent-studio/verdict", body.RootElement.GetProperty("context").GetString());
        Assert.Equal(description, body.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task The_status_goes_on_the_judged_commit_and_not_on_the_head_of_the_pull_request()
    {
        using var rig = WithRoutes();

        await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved, Gh.Sha3), "Estado del veredicto", "c", null, CancellationToken.None);

        Assert.Contains(rig.Script.Calls, c => c.Path == "/repos/octo/hello/statuses/" + Gh.Sha3);
    }

    [Fact]
    public async Task Without_an_id_the_comment_is_looked_up_by_its_mark_and_its_author_and_edited_in_place()
    {
        using var rig = WithRoutes();
        rig.Script.Route("GET", "/repos/octo/hello/issues/7/comments", Gh.Ok(Gh.Json(
            "{\"id\":1,\"body\":\"" + Mark + "\\nde otra persona\",\"user\":{\"login\":\"mallory\"}}",
            "{\"id\":2,\"body\":\"texto\\n" + Mark + "\",\"user\":{\"login\":\"octocat\"}}",
            "{\"id\":3,\"body\":\"" + Mark + "\\nel nuestro\",\"user\":{\"login\":\"OctoCat\"}}")));

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Cuerpo", null, CancellationToken.None);

        Assert.Equal(3L, result.Value!.CommentId);
        Assert.Contains(rig.Script.Calls, c => c.ToString() == "PATCH /repos/octo/hello/issues/comments/3");
        Assert.DoesNotContain(rig.Script.Calls, c => c.Method == "POST" && c.Path.EndsWith("/comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_comment_of_ours_one_is_written_and_its_id_comes_back()
    {
        using var rig = WithRoutes();

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Rejected), "Estado del veredicto", "Cuerpo", null, CancellationToken.None);

        Assert.Equal(4242L, result.Value!.CommentId);
        var post = rig.Script.Calls.Single(c => c.Method == "POST" && c.Path.EndsWith("/comments", StringComparison.Ordinal));
        Assert.Equal("POST /repos/octo/hello/issues/7/comments", post.ToString());
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(Mark + "\nCuerpo", body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task With_the_id_stored_the_comment_is_edited_without_a_lookup_and_the_mark_is_kept_when_the_caller_already_put_it()
    {
        using var rig = WithRoutes();

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", Mark + "\nYa lleva su marca", 777, CancellationToken.None);

        Assert.Equal(777L, result.Value!.CommentId);
        // The id is not trusted: the account and the comment are read first, and the comments of the pull request are not listed.
        Assert.Equal(
            ["GET /user", "GET /repos/octo/hello/issues/comments/777", "PATCH /repos/octo/hello/issues/comments/777"],
            rig.Script.Calls.Where(c => c.Path != "/repos/octo/hello/statuses/" + Gh.Sha1).Select(c => c.ToString()));
        var edit = rig.Script.Calls.Single(c => c.Method == "PATCH");
        Assert.Equal("PATCH /repos/octo/hello/issues/comments/777", edit.ToString());
        using var body = JsonDocument.Parse(edit.Body!);
        Assert.Equal(Mark + "\nYa lleva su marca", body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_comment_that_somebody_deleted_is_written_again_once()
    {
        using var rig = WithRoutes();
        rig.Script.Route("GET", "/repos/octo/hello/issues/comments/777", Gh.Http(404, "{}"));

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Cuerpo", 777, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.Equal(4242L, result.Value!.CommentId);
        Assert.Equal(1, rig.Script.Calls.Count(c => c.Method == "POST" && c.Path.EndsWith("/comments", StringComparison.Ordinal)));
        Assert.DoesNotContain(rig.Script.Calls, c => c.Method == "PATCH");
    }

    [Theory]
    [InlineData("{\"id\":777,\"body\":\"<!-- agent-studio:verdict run=run1 -->\\nx\",\"user\":{\"login\":\"mallory\"},\"issue_url\":\"https://api.github.com/repos/octo/hello/issues/7\"}")]
    [InlineData("{\"id\":777,\"body\":\"<!-- agent-studio:verdict run=other -->\\nx\",\"user\":{\"login\":\"octocat\"},\"issue_url\":\"https://api.github.com/repos/octo/hello/issues/7\"}")]
    [InlineData("{\"id\":777,\"body\":\"texto cualquiera\",\"user\":{\"login\":\"octocat\"},\"issue_url\":\"https://api.github.com/repos/octo/hello/issues/7\"}")]
    [InlineData("{\"id\":777,\"body\":\"<!-- agent-studio:verdict run=run1 -->\\nx\",\"user\":{\"login\":\"octocat\"},\"issue_url\":\"https://api.github.com/repos/octo/hello/issues/8\"}")]
    [InlineData("{\"id\":777,\"body\":\"<!-- agent-studio:verdict run=run1 -->\\nx\",\"user\":{\"login\":\"octocat\"},\"issue_url\":\"https://api.github.com/repos/evil/hello/issues/7\"}")]
    [InlineData("{\"id\":777,\"body\":\"<!-- agent-studio:verdict run=run1 -->\\nx\",\"user\":{\"login\":\"octocat\"}}")]
    [InlineData("{\"id\":777,\"body\":\"<!-- agent-studio:verdict run=run1 -->\\nx\",\"user\":{\"login\":\"octocat\"},\"issue_url\":\"https://evil.example/repos/octo/hello/issues/7\"}")]
    [InlineData("{\"id\":777,\"body\":\"<!-- agent-studio:verdict run=run1 -->\\nx\",\"user\":{\"login\":\"octocat\"},\"issue_url\":\"http://api.github.com/repos/octo/hello/issues/7\"}")]
    public async Task An_id_that_is_not_the_comment_of_this_verdict_is_never_edited_the_comment_is_looked_up_by_its_mark_instead(string comment)
    {
        using var rig = WithRoutes();
        rig.Script.Route("GET", "/repos/octo/hello/issues/comments/777", Gh.Ok(comment));
        rig.Script.Route("GET", "/repos/octo/hello/issues/7/comments", Gh.Ok(Gh.Json("{\"id\":3,\"body\":\"" + Mark + "\\nel nuestro\",\"user\":{\"login\":\"octocat\"}}")));

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Cuerpo", 777, CancellationToken.None);

        Assert.Equal(3L, result.Value!.CommentId);
        Assert.DoesNotContain(rig.Script.Calls, c => c.Path == "/repos/octo/hello/issues/comments/777" && c.Method == "PATCH");
        Assert.Contains(rig.Script.Calls, c => c.ToString() == "PATCH /repos/octo/hello/issues/comments/3");
    }

    [Fact]
    public async Task The_description_of_the_status_loses_its_control_characters_is_cut_at_120_and_an_empty_one_publishes_nothing()
    {
        using var rig = WithRoutes();

        await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.InReview), "uno\u001b[31m\ndos " + new string('x', 200), "Cuerpo", null, CancellationToken.None);

        var status = rig.Script.Calls.Single(c => c.Path.Contains("/statuses/", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(status.Body!);
        var description = body.RootElement.GetProperty("description").GetString()!;
        Assert.Equal(120, description.Length);
        Assert.StartsWith("uno [31m dos x", description, StringComparison.Ordinal);
        Assert.DoesNotContain(description, c => char.IsControl(c));

        rig.Script.Specs.Clear();
        foreach (var empty in new[] { string.Empty, "   ", "\u001b\u0007\n" })
        {
            var refused = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.InReview), empty, "Cuerpo", null, CancellationToken.None);
            Assert.Equal("text.description-invalid", refused.RefusalCode);
        }

        Assert.Empty(rig.Script.Specs);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Without_permission_for_the_status_the_comment_still_says_it(int status)
    {
        using var rig = WithRoutes();
        rig.Script.Route("POST", "/repos/octo/hello/statuses/", Gh.Http(status, "{\"message\":\"Resource not accessible by integration\"}"));

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Cuerpo", null, CancellationToken.None);

        Assert.Equal(HostingOutcome.Ok, result.Outcome);
        Assert.False(result.Value!.StatusPublished);
        Assert.Equal(4242L, result.Value.CommentId);
    }

    [Fact]
    public async Task A_page_of_comments_that_does_not_fit_leaves_the_status_published_and_writes_no_comment_that_could_be_a_duplicate()
    {
        using var rig = WithRoutes();
        rig.Script.Route("GET", "/repos/octo/hello/issues/7/comments", new ProcessResult(-1, "HTTP/2.0 200 OK\n\n[", string.Empty, TimeSpan.Zero, false, OutputLimitExceeded: true));

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.InReview), "Estado", "Cuerpo", null, CancellationToken.None);

        Assert.Equal(HostingOutcome.InvalidAnswer, result.Outcome);
        Assert.Equal("comment.lookup-unknown", result.RefusalCode);
        Assert.True(result.Value!.StatusPublished);
        Assert.Null(result.Value.CommentId);
        Assert.DoesNotContain(rig.Script.Calls, c => c.Method == "POST" && c.Path.EndsWith("/comments", StringComparison.Ordinal));
        Assert.Contains(rig.Script.Calls, c => c.Path.Contains("/statuses/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_that_is_gone_or_a_limit_stops_the_whole_publication_and_tells_what_was_already_published()
    {
        using var rig = WithRoutes();
        rig.Script.Route("POST", "/repos/octo/hello/issues/7/comments", Gh.Http(401, "{}"));
        rig.Script.Route("GET", "/repos/octo/hello/issues/7/comments", Gh.Ok("[]"));

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Cuerpo", null, CancellationToken.None);

        Assert.Equal(HostingOutcome.SignInNeeded, result.Outcome);
        Assert.True(result.Value!.StatusPublished);
        Assert.Null(result.Value.CommentId);
    }

    [Fact]
    public async Task A_version_of_gh_below_the_tested_one_declares_nothing_and_publishes_nothing()
    {
        using var rig = new GhRig("2.90.0");

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Cuerpo", null, CancellationToken.None);

        Assert.Equal(HostingOutcome.Unsupported, result.Outcome);
        Assert.Empty(rig.Script.Specs);
    }
    [Theory]
    [InlineData("not-a-commit")]
    [InlineData("")]
    [InlineData("1111")]
    public async Task A_judged_commit_that_is_not_a_full_id_publishes_nothing(string commit)
    {
        using var rig = WithRoutes();

        var result = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved, commit), "Estado del veredicto", "Cuerpo", null, CancellationToken.None);

        Assert.Equal(HostingOutcome.Refused, result.Outcome);
        Assert.Equal(GitHubRefusal.CommitInvalid, result.RefusalCode);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task A_comment_with_control_characters_or_past_the_limit_publishes_nothing_not_even_the_status()
    {
        using var rig = WithRoutes();

        var control = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Cuerpo\u202E", null, CancellationToken.None);
        var big = await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", new string('x', 20_001), null, CancellationToken.None);

        Assert.Equal(GitHubRefusal.BodyInvalid, control.RefusalCode);
        Assert.Equal(GitHubRefusal.BodyInvalid, big.RefusalCode);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task The_text_of_the_judge_is_not_added_by_the_adapter_the_comment_is_the_mark_and_what_the_caller_gave()
    {
        using var rig = WithRoutes();

        await rig.Provider.PublishVerdictAsync(Pr, Notice(VerdictStanding.Approved), "Estado del veredicto", "Solo esto", null, CancellationToken.None);

        var post = rig.Script.Calls.Single(c => c.Method == "POST" && c.Path.EndsWith("/comments", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(post.Body!);
        var text = body.RootElement.GetProperty("body").GetString()!;
        Assert.Equal(Mark + "\nSolo esto", text);
        Assert.DoesNotContain("claude-code", text, StringComparison.Ordinal);
        Assert.DoesNotContain("model-x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("R-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_the_verdict_never_launches_a_call_of_a_review()
    {
        using var rig = WithRoutes();

        foreach (var standing in Enum.GetValues<VerdictStanding>())
        {
            await rig.Provider.PublishVerdictAsync(Pr, Notice(standing), "Estado del veredicto", "Cuerpo", null, CancellationToken.None);
            await rig.Provider.PublishVerdictAsync(Pr, Notice(standing), "Estado del veredicto", "Cuerpo", 4242, CancellationToken.None);
        }

        Assert.NotEmpty(rig.Script.Specs);
        Assert.All(rig.Script.Calls, c => Assert.DoesNotContain("review", c.Path, StringComparison.OrdinalIgnoreCase));
        Assert.All(rig.Script.Specs, s => Assert.DoesNotContain(s.Arguments, a => a.Contains("approve", StringComparison.OrdinalIgnoreCase) || a is "pr"));
        Assert.All(rig.Script.Specs, s => Assert.DoesNotContain("\"event\"", s.StandardInput ?? string.Empty, StringComparison.Ordinal));
    }
}
