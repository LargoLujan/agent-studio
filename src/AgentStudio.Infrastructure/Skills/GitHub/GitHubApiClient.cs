using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using AgentStudio.Application.Skills;

namespace AgentStudio.Infrastructure.Skills.GitHub;

/// <summary>Why a request to GitHub failed. The message is fixed text: it carries no address, no header, no body and never the token.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(SkillHealthFailure failure, DateTimeOffset? resetsAt = null)
        : base("The request to GitHub failed: " + failure + ".")
    {
        Failure = failure;
        ResetsAt = resetsAt;
    }

    public SkillHealthFailure Failure { get; }

    /// <summary>When the limit is reset, for <see cref="SkillHealthFailure.RateLimited"/>.</summary>
    public DateTimeOffset? ResetsAt { get; }
}

/// <summary>An answer of GitHub as the checker needs it: the status, the <c>ETag</c>, the body (at most <see cref="GitHubApiClient.MaxResponseBytes"/>) and the limit.</summary>
/// <param name="Redirected">The answer came after a redirection: its <c>ETag</c> belongs to another address and is not kept.</param>
public sealed record GitHubResponse(int Status, string? ETag, byte[] Body, int? RateLimitRemaining, DateTimeOffset? RateLimitResetsAt, bool Redirected);

/// <summary>
/// The one HTTP client of the app for GitHub (docs/20 D-13 to D-15, F6-13). Every request:
/// <list type="bullet">
/// <item>goes through <see cref="GitHubHostGuardHandler"/>, which fails closed on any address but <c>https://api.github.com/repos/…</c>;</item>
/// <item>runs one at a time, in order, from a queue with a single consumer (GitHub asks for requests in series);</item>
/// <item>never follows a redirection by itself (<c>AllowAutoRedirect = false</c>): <see cref="MaxRedirects"/> are followed by hand, only to <c>https</c>
/// and to the same host, and the <c>Authorization</c> header and the <c>ETag</c> go in the first request only, never in a followed one;</item>
/// <item>has no cookies, a fixed <c>User-Agent</c> and nothing of the project or of the person in it;</item>
/// <item>has <see cref="RequestTimeout"/> for the whole exchange, the body included, and reads at most <see cref="MaxResponseBytes"/> of it;</item>
/// <item>respects <c>429</c>, <c>403</c> with <c>x-ratelimit-remaining: 0</c> and <c>retry-after</c>: it stops with <see cref="SkillHealthFailure.RateLimited"/>.</item>
/// </list>
/// An error is a <see cref="GitHubApiException"/> without the exception that caused it, whose message could carry what was sent.
/// </summary>
public sealed class GitHubApiClient : IAsyncDisposable, IDisposable
{
    public const int MaxResponseBytes = 1024 * 1024;

    public const int MaxRedirects = 3;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Fixed. GitHub requires one; it says the app and nothing else.</summary>
    public const string UserAgent = "AgentStudio-SkillsDoctor";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _startGate = new();
    private Task? _consumer;

    /// <param name="transport">What sends the bytes. Only a test passes one; the guard and the redirections of this class stay in front of it.</param>
    public GitHubApiClient(HttpMessageHandler? transport = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _http = new HttpClient(new GitHubHostGuardHandler(transport ?? CreateTransport()), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    /// <summary>The transport of production: no redirections, no cookies, no credentials of the system, a small header limit.</summary>
    public static SocketsHttpHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PreAuthenticate = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        MaxResponseHeadersLength = 32,
        MaxConnectionsPerServer = 1,
        ConnectTimeout = RequestTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>Asks <paramref name="uri"/> (built by <see cref="GitHubApiAddress"/>). <paramref name="etag"/> and <paramref name="token"/> go in the first request only.</summary>
    /// <exception cref="GitHubApiException">The request could not be made or the answer is not usable.</exception>
    public Task<GitHubResponse> GetAsync(Uri uri, string? etag, GitHubToken? token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!GitHubApiAddress.IsAllowed(uri))
        {
            throw new GitHubApiException(SkillHealthFailure.RedirectRefused);
        }

        var pending = new Pending(uri, etag, token, ct, new TaskCompletionSource<GitHubResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
        EnsureConsumer();
        if (!_queue.Writer.TryWrite(pending))
        {
            throw new GitHubApiException(SkillHealthFailure.NoNetwork);
        }

        return pending.Result.Task;
    }

    /// <summary>For a container that is disposed synchronously (the app's): stops the queue, waits a moment for the consumer and releases the client.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        Task? consumer;
        lock (_startGate)
        {
            consumer = _consumer;
        }

        var finished = true;
        if (consumer is not null)
        {
            try
            {
                finished = consumer.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The consumer ended with the cancellation.
            }
        }

        _http.Dispose();
        if (finished)
        {
            _stop.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? consumer;
        lock (_startGate)
        {
            consumer = _consumer;
        }

        if (consumer is not null)
        {
            try
            {
                await consumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped on purpose.
            }
        }

        _http.Dispose();
        _stop.Dispose();
    }

    private void EnsureConsumer()
    {
        lock (_startGate)
        {
            _consumer ??= Task.Run(ConsumeAsync);
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (pending.Ct.IsCancellationRequested)
                {
                    pending.Result.TrySetCanceled(pending.Ct);
                    continue;
                }

                try
                {
                    pending.Result.TrySetResult(await SendWithRedirectsAsync(pending).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (pending.Ct.IsCancellationRequested)
                {
                    pending.Result.TrySetCanceled(pending.Ct);
                }
                catch (GitHubApiException ex)
                {
                    pending.Result.TrySetException(ex);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or NotSupportedException)
                {
                    // The exception is dropped on purpose: its message is not ours and could carry what was sent.
                    pending.Result.TrySetException(new GitHubApiException(SkillHealthFailure.NoNetwork));
                }
                catch (Exception)
                {
                    // Anything else (an argument, an address that does not parse): the same, and above all the consumer stays alive. If it died, this request and
                    // every later one would wait forever.
                    pending.Result.TrySetException(new GitHubApiException(SkillHealthFailure.UnreadableResponse));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        finally
        {
            // Whoever is still queued is told, instead of waiting for an answer that will not come.
            while (_queue.Reader.TryRead(out var left))
            {
                left.Result.TrySetException(new GitHubApiException(SkillHealthFailure.NoNetwork));
            }
        }
    }

    private async Task<GitHubResponse> SendWithRedirectsAsync(Pending pending)
    {
        var current = pending.Uri;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (hop == 0)
            {
                if (pending.ETag is not null)
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", pending.ETag);
                }

                if (pending.Token is not null)
                {
                    // The only place the token is read: the first request, whose address passed the guard of this class and passes it again below.
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pending.Token.Reveal());
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(pending.Ct, _stop.Token);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 307 or 308)
                {
                    current = NextHop(current, response.Headers.Location, hop);
                    continue;
                }

                var limit = ReadLimit(response);
                switch (status)
                {
                    case 429:
                        throw new GitHubApiException(SkillHealthFailure.RateLimited, ResetOf(response, limit));
                    case 403 when limit.Remaining == 0 || response.Headers.RetryAfter is not null:
                        throw new GitHubApiException(SkillHealthFailure.RateLimited, ResetOf(response, limit));
                    case 401 or 403:
                        throw new GitHubApiException(SkillHealthFailure.Forbidden);
                    case 200 or 304 or 404:
                        break;
                    default:
                        throw new GitHubApiException(SkillHealthFailure.UnreadableResponse);
                }

                var body = status == 200 ? await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false) : [];
                return new GitHubResponse(status, ReadETag(response), body, limit.Remaining, limit.ResetsAt, hop > 0);
            }
            catch (OperationCanceledException) when (!pending.Ct.IsCancellationRequested && !_stop.IsCancellationRequested)
            {
                // The ten seconds ran out: no answer, whatever the reason.
                throw new GitHubApiException(SkillHealthFailure.NoNetwork);
            }
        }
    }

    /// <summary>A redirection is followed only to https, to the same host and under <c>/repos/</c> or <c>/repositories/</c>, at most <see cref="MaxRedirects"/> times.</summary>
    private static Uri NextHop(Uri current, Uri? location, int hop)
    {
        if (hop >= MaxRedirects || location is null)
        {
            throw new GitHubApiException(SkillHealthFailure.RedirectRefused);
        }

        var target = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (!GitHubApiAddress.IsAllowed(target) || !string.Equals(target.Host, current.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubApiException(SkillHealthFailure.RedirectRefused);
        }

        return target;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new GitHubApiException(SkillHealthFailure.UnreadableResponse);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new GitHubApiException(SkillHealthFailure.UnreadableResponse);
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string? ReadETag(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString() is { Length: > 0 and <= GitHubResponseCache.MaxETagLength } etag && GitHubResponseCache.IsValidETag(etag) ? etag : null;

    private static (int? Remaining, DateTimeOffset? ResetsAt) ReadLimit(HttpResponseMessage response)
    {
        int? remaining = null;
        DateTimeOffset? resetsAt = null;
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var left)
            && int.TryParse(left.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            remaining = number;
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 0 and < 253402300799)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return (remaining, resetsAt);
    }

    private DateTimeOffset ResetOf(HttpResponseMessage response, (int? Remaining, DateTimeOffset? ResetsAt) limit)
    {
        var now = _time.GetUtcNow();
        if (response.Headers.RetryAfter is { } retry)
        {
            var at = retry.Date ?? (retry.Delta is { } delta ? now + delta : (DateTimeOffset?)null);
            if (at is { } known)
            {
                return known;
            }
        }

        return limit.ResetsAt ?? now.AddMinutes(1);
    }

    private sealed record Pending(Uri Uri, string? ETag, GitHubToken? Token, CancellationToken Ct, TaskCompletionSource<GitHubResponse> Result);
}
