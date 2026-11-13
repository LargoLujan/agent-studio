using System.Text.Json;
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
    private readonly CancellationTokenSource _stop = new();
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

    private async Task<GhReply> SendCoreAsync(GhCall call, CancellationToken caller)
    {
        // After Dispose nothing more is launched: what was still queued ends as cancelled, and a gh in the air is cancelled too.
        _stop.Token.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _stop.Token);
        var ct = linked.Token;
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
        if (http.Status == 429 || (http.Status == 403 && (http.RateLimitRemaining == 0 || http.RetryAfter is not null || SaysRateLimit(http.Body))))
        {
            var until = Wait(http);
            return new GhReply(GhReplyKind.RateLimited, RetryAt: until);
        }

        _strikes = 0;
        return new GhReply(GhReplyKind.Response, http);
    }

    /// <summary>
    /// The secondary limit of GitHub arrives as a 403 (or a 429) that may carry neither <c>retry-after</c> nor a limit at zero; the body says it in its message or in the address of its documentation. The body is read
    /// only to classify the answer: nothing of it is kept or shown.
    /// </summary>
    private static bool SaysRateLimit(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 16_384)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var name in new[] { "message", "documentation_url" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text
                    && (text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || text.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) || text.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// The hour to wait until. <c>retry-after</c> always counts; <c>x-ratelimit-reset</c> only when <c>x-ratelimit-remaining</c> is 0 (otherwise it is the reset of a limit that was not hit). From the second blow in a
    /// row on, the wait is at least the step of the ladder, so a clock of this computer that is ahead (a reset that already looks past) cannot make it hammer: with no usable header, or a past one, a minute and then 2, 5, 15 and 60.
    /// </summary>
    private DateTimeOffset Wait(GhHttpResponse http)
    {
        var now = _time.GetUtcNow();
        var step = now + Ladder[Math.Min(_strikes, Ladder.Length - 1)];
        var asked = http.RetryAfter ?? (http.RateLimitRemaining == 0 ? http.RateLimitReset : null);
        var until = asked is { } hour ? (_strikes == 0 ? Max(hour, now.AddSeconds(1)) : Max(hour, step)) : step;
        _strikes++;
        until = Min(until, now + MaxWait);
        Interlocked.Exchange(ref _limitedUntilTicks, until.UtcTicks);
        return until;
    }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    public void Dispose()
    {
        _stop.Cancel();
        _queue.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _queue.DisposeAsync().ConfigureAwait(false);
    }
}
