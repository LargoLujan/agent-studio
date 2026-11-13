using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Hosting.Cli;

namespace AgentStudio.Tests.Infrastructure.Hosting.Cli;

/// <summary>
/// docs/21 D-24 and D-25: one <c>gh</c> at a time through a queue of a single consumer, and the state of the limit in it. A 429, or a 403 with the limit at zero or with <c>retry-after</c>, stops everything that
/// would reach GitHub, writes included, until the hour the headers say (at most two hours from when they were received); with no headers, a minute and then 2, 5, 15 and 60. Nothing is waited for by the
/// clock: the clock of the tests is moved by hand.
/// </summary>
public sealed class GhQueueTests
{
    private static readonly GhCall Read = new(GhMethod.Get, "/repos/octo/hello/pulls/7");
    private static readonly GhCall Write = new(GhMethod.Post, "/repos/octo/hello/issues/7/comments", Body: "{\"body\":\"x\"}");

    private static async Task Spin(Func<bool> done)
    {
        for (var i = 0; i < 2_000_000 && !done(); i++)
        {
            await Task.Yield();
        }

        Assert.True(done());
    }

    [Fact]
    public async Task Calls_run_one_at_a_time_in_the_order_they_came_whatever_the_number_of_callers()
    {
        using var rig = new GhRig();
        var release = new TaskCompletionSource();
        rig.Script.Hold = release.Task;
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", _ => Gh.Ok("{}"));

        var calls = Enumerable.Range(0, 5).Select(_ => rig.Queue.SendAsync(Read, CancellationToken.None)).ToList();
        await Spin(() => rig.Script.Specs.Count == 1);

        // The first is in the air and held: no other one has started.
        Assert.Single(rig.Script.Specs);
        release.SetResult();
        var replies = await Task.WhenAll(calls);

        Assert.All(replies, r => Assert.Equal(GhReplyKind.Response, r.Kind));
        Assert.Equal(5, rig.Script.Specs.Count);
        Assert.Equal(1, rig.Script.MostAtOnce);
    }

    [Fact]
    public async Task A_429_with_retry_after_stops_every_call_writes_included_until_that_hour_and_then_goes_on()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", _ => rig.Script.Specs.Count == 1 ? Gh.Fixture("handmade-rate-limit-429.http") : Gh.Ok("{}"));
        rig.Script.Route("POST", "/repos/octo/hello/issues/7/comments", Gh.Http(201, "{\"id\":1}"));
        var start = rig.Time.GetUtcNow();

        var first = await rig.Queue.SendAsync(Read, CancellationToken.None);
        Assert.Equal(GhReplyKind.RateLimited, first.Kind);
        Assert.Equal(start.AddSeconds(60), first.RetryAt);
        Assert.Equal(start.AddSeconds(60), rig.Queue.RateLimitedUntil);

        var read = await rig.Queue.SendAsync(Read, CancellationToken.None);
        var write = await rig.Queue.SendAsync(Write, CancellationToken.None);
        Assert.Equal(GhReplyKind.RateLimited, read.Kind);
        Assert.Equal(GhReplyKind.RateLimited, write.Kind);
        Assert.Equal(start.AddSeconds(60), write.RetryAt);
        Assert.Single(rig.Script.Specs);

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(rig.Queue.RateLimitedUntil);
        Assert.Equal(GhReplyKind.Response, (await rig.Queue.SendAsync(Read, CancellationToken.None)).Kind);
        Assert.Equal(2, rig.Script.Specs.Count);
    }

    [Fact]
    public async Task A_403_with_the_limit_at_zero_waits_until_the_reset_header()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Fixture("handmade-rate-limit-403.http"));

        var reply = await rig.Queue.SendAsync(Read, CancellationToken.None);

        Assert.Equal(GhReplyKind.RateLimited, reply.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794574800), reply.RetryAt);
    }

    [Fact]
    public async Task A_403_that_is_not_about_the_limit_is_an_answer_and_stops_nothing()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Http(403, "{\"message\":\"Resource not accessible\"}", "\r\n", ("X-Ratelimit-Remaining", "4000")));

        var reply = await rig.Queue.SendAsync(Read, CancellationToken.None);

        Assert.Equal(GhReplyKind.Response, reply.Kind);
        Assert.Equal(403, reply.Http!.Status);
        Assert.Null(rig.Queue.RateLimitedUntil);
    }

    [Fact]
    public async Task With_no_headers_the_wait_is_a_minute_and_then_2_5_15_and_60_and_a_good_answer_starts_the_ladder_again()
    {
        using var rig = new GhRig();
        var limited = true;
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", _ => limited ? Gh.Http(429, "{}") : Gh.Ok("{}"));
        var steps = new[] { 1, 2, 5, 15, 60, 60 };

        foreach (var minutes in steps)
        {
            var now = rig.Time.GetUtcNow();
            var reply = await rig.Queue.SendAsync(Read, CancellationToken.None);
            Assert.Equal(GhReplyKind.RateLimited, reply.Kind);
            Assert.Equal(now.AddMinutes(minutes), reply.RetryAt);
            rig.Time.Advance(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(1));
        }

        limited = false;
        Assert.Equal(GhReplyKind.Response, (await rig.Queue.SendAsync(Read, CancellationToken.None)).Kind);
        limited = true;
        var again = rig.Time.GetUtcNow();
        Assert.Equal(again.AddMinutes(1), (await rig.Queue.SendAsync(Read, CancellationToken.None)).RetryAt);
    }

    [Fact]
    public async Task What_the_headers_ask_is_cut_at_two_hours_from_when_it_was_received()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Http(429, "{}", "\r\n", ("Retry-After", "864000")));
        var start = rig.Time.GetUtcNow();

        var reply = await rig.Queue.SendAsync(Read, CancellationToken.None);

        Assert.Equal(start.AddHours(2), reply.RetryAt);
        Assert.Equal(start.AddHours(2), rig.Queue.RateLimitedUntil);
    }

    [Fact]
    public void A_stored_wait_is_loaded_cut_at_two_hours_and_a_past_one_clears_it()
    {
        using var rig = new GhRig();
        var now = rig.Time.GetUtcNow();

        rig.Queue.RestoreRateLimit(now.AddMinutes(10));
        Assert.Equal(now.AddMinutes(10), rig.Queue.RateLimitedUntil);

        rig.Queue.RestoreRateLimit(now.AddDays(30));
        Assert.Equal(now.AddHours(2), rig.Queue.RateLimitedUntil);

        rig.Queue.RestoreRateLimit(now.AddMinutes(-1));
        Assert.Null(rig.Queue.RateLimitedUntil);

        rig.Queue.RestoreRateLimit(now.AddMinutes(5));
        rig.Queue.RestoreRateLimit(null);
        Assert.Null(rig.Queue.RateLimitedUntil);
    }

    [Fact]
    public async Task The_other_ends_are_their_own_replies_and_a_failed_call_does_not_stop_the_queue()
    {
        using var rig = new GhRig();
        rig.Script.Route("GET", "/repos/octo/hello/pulls/1", new ProcessResult(4, string.Empty, "gh auth login", TimeSpan.Zero, false));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/2", new ProcessResult(1, string.Empty, "offline", TimeSpan.Zero, false));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/3", new ProcessResult(0, "not an answer", string.Empty, TimeSpan.Zero, false));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/4", new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromSeconds(20), true));
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", Gh.Ok("{}"));

        Assert.Equal(GhReplyKind.SignInNeeded, (await rig.Queue.SendAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/1"), CancellationToken.None)).Kind);
        Assert.Equal(GhReplyKind.Unreachable, (await rig.Queue.SendAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/2"), CancellationToken.None)).Kind);
        Assert.Equal(GhReplyKind.InvalidAnswer, (await rig.Queue.SendAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/3"), CancellationToken.None)).Kind);
        Assert.Equal(GhReplyKind.Unreachable, (await rig.Queue.SendAsync(new GhCall(GhMethod.Get, "/repos/octo/hello/pulls/4"), CancellationToken.None)).Kind);
        Assert.Equal(GhReplyKind.Response, (await rig.Queue.SendAsync(Read, CancellationToken.None)).Kind);
        Assert.Null(rig.Queue.RateLimitedUntil);
    }

    [Fact]
    public async Task A_caller_that_cancelled_before_its_turn_does_not_start_gh()
    {
        using var rig = new GhRig();
        var release = new TaskCompletionSource();
        rig.Script.Hold = release.Task;
        rig.Script.Route("GET", "/repos/octo/hello/pulls/7", _ => Gh.Ok("{}"));
        using var cancel = new CancellationTokenSource();

        var first = rig.Queue.SendAsync(Read, CancellationToken.None);
        await Spin(() => rig.Script.Specs.Count == 1);
        var second = rig.Queue.SendAsync(Read, cancel.Token);
        await cancel.CancelAsync();
        release.SetResult();
        await first;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Single(rig.Script.Specs);
    }
}
