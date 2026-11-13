using AgentStudio.Infrastructure.Diagnostics;

namespace AgentStudio.Infrastructure.Hosting.Cli;

internal enum GhReplyKind
{
    /// <summary>GitHub answered; <see cref="GhReply.Http"/> is the answer.</summary>
    Response,

    /// <summary>GitHub asked to wait (429, or 403 with the limit at zero or with <c>retry-after</c>), or the wait of an earlier answer has not ended: nothing was launched. <see cref="GhReply.RetryAt"/> says until when.</summary>
    RateLimited,

    NotInstalled,

    SignInNeeded,

    /// <summary>The server was not reached (no status line, or 20 seconds without an answer).</summary>
    Unreachable,

    InvalidAnswer,
}

internal sealed record GhReply(GhReplyKind Kind, GhHttpResponse? Http = null, DateTimeOffset? RetryAt = null);

/// <summary>
/// The queue of every call to <c>gh</c> (docs/21 D-25): one at a time, in order, from a channel with a single consumer (GitHub asks for requests in series), and where the state of the limit
/// lives. A 429, or a 403 with <c>x-ratelimit-remaining: 0</c> or with <c>retry-after</c>, stops everything that would reach GitHub, writes included, until the hour that the headers say; with
/// no headers, a minute and then a progressive wait of 2, 5, 15 and 60 minutes; whatever the headers ask is cut at two hours from when it was received (D-24). While the wait lasts nothing
/// is launched and nobody insists. The state is a time that <see cref="RestoreRateLimit"/> can load and <see cref="RateLimitedUntil"/> can give back, so the one who stores it survives a restart.
/// </summary>
internal sealed class GhQueue : IAsyncDisposable, IDisposable
{
    /// <summary>The most that a wait can last (D-24).</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromHours(2);

    /// <summary>The waits when the answer says nothing: the first strike and the ones that follow.</summary>
    internal static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60),
    ];

    private readonly GhRunner _runner;
    private readonly TimeProvider _time;
    private readonly SerialQueue _queue = new();
    private long _limitedUntilTicks;
    private int _strikes;

    internal GhQueue(GhRunner runner, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Until when GitHub asked to wait, or null when it did not or the wait ended.</summary>
    internal DateTimeOffset? RateLimitedUntil
    {
        get
        {
            var ticks = Interlocked.Read(ref _limitedUntilTicks);
            return ticks > 0 && new DateTimeOffset(ticks, TimeSpan.Zero) is var until && until > _time.GetUtcNow() ? until : null;
        }
    }

    /// <summary>Loads the wait that was stored (it is cut at two hours from now, whatever the file says). A null or a past time clears it.</summary>
    internal void RestoreRateLimit(DateTimeOffset? until)
    {
        var now = _time.GetUtcNow();
        var ticks = until is { } value && value > now ? Min(value, now + MaxWait).UtcTicks : 0;
        Interlocked.Exchange(ref _limitedUntilTicks, ticks);
    }

    /// <summary>Queues the call and answers when it ended. A call whose wait is on does not start <c>gh</c>.</summary>
    internal Task<GhReply> SendAsync(GhCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _queue.RunAsync(token => SendCoreAsync(call, token), ct);
    }

    private async Task<GhReply> SendCoreAsync(GhCall call, CancellationToken ct)
    {
        if (RateLimitedUntil is { } waiting)
        {
            return new GhReply(GhReplyKind.RateLimited, RetryAt: waiting);
        }

        var run = await _runner.RunAsync(call, ct).ConfigureAwait(false);
        switch (run.Status)
        {
            case GhRunStatus.NotInstalled:
                return new GhReply(GhReplyKind.NotInstalled);
            case GhRunStatus.SignInNeeded:
                return new GhReply(GhReplyKind.SignInNeeded);
            case GhRunStatus.NoResponse or GhRunStatus.TimedOut:
                return new GhReply(GhReplyKind.Unreachable);
            case GhRunStatus.InvalidAnswer:
                return new GhReply(GhReplyKind.InvalidAnswer);
        }

        var http = run.Http!;
        if (http.Status == 429 || (http.Status == 403 && (http.RateLimitRemaining == 0 || http.RetryAfter is not null)))
        {
            var until = Wait(http);
            return new GhReply(GhReplyKind.RateLimited, RetryAt: until);
        }

        _strikes = 0;
        return new GhReply(GhReplyKind.Response, http);
    }

    private DateTimeOffset Wait(GhHttpResponse http)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset until;
        if ((http.RetryAfter ?? http.RateLimitReset) is { } asked)
        {
            until = Max(asked, now.AddSeconds(1));
        }
        else
        {
            until = now + Ladder[Math.Min(_strikes, Ladder.Length - 1)];
        }

        _strikes++;
        until = Min(until, now + MaxWait);
        Interlocked.Exchange(ref _limitedUntilTicks, until.UtcTicks);
        return until;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    public void Dispose() => _queue.Dispose();

    public ValueTask DisposeAsync() => _queue.DisposeAsync();
}
