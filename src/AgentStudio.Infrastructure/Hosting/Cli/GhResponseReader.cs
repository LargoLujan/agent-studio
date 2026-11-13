using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Infrastructure.Hosting.Cli;

/// <summary>
/// What an answer of GitHub holds, as <c>gh api -i</c> printed it: the status, five headers by list and the body. <paramref name="RetryAfter"/> and <paramref name="RateLimitReset"/> are
/// absolute times; <paramref name="ETag"/> is null when the header is missing or has not the exact form of an <c>ETag</c>.
/// </summary>
internal sealed record GhHttpResponse(
    int Status,
    string? ETag,
    string Body,
    DateTimeOffset? RetryAfter = null,
    int? RateLimitRemaining = null,
    DateTimeOffset? RateLimitReset = null,
    int? PollIntervalSeconds = null);

/// <summary>
/// Reads the standard output of <c>gh api -i</c> by structure (docs/21 D-12): the status line, the headers up to a blank line and the body. What does not have that shape is not an answer
/// (null): the reader never guesses. The status is the one of the line, not the exit code of <c>gh</c>, which is not zero for a 304 or a 4xx. LF and CRLF both end a line. Of the headers only
/// <c>etag</c>, <c>retry-after</c>, <c>x-ratelimit-remaining</c>, <c>x-ratelimit-reset</c> and <c>x-poll-interval</c> are kept; the first of each wins.
/// </summary>
internal static partial class GhResponseReader
{
    internal const int MaxHeaderLines = 200;

    internal const int MaxHeaderLineLength = 8192;

    private const long MaxEpochSeconds = 253402300799;

    private const int MaxSeconds = 10 * 365 * 24 * 3600;

    [GeneratedRegex(@"^HTTP/(?:1\.0|1\.1|2(?:\.0)?|3(?:\.0)?) (?<code>[1-5][0-9]{2})(?: [^\r\n]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^(?<name>[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}):[ \t]*(?<value>[^\r\n]*?)[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLine();

    /// <summary>The answer, or null when <paramref name="output"/> is not a status line, headers, a blank line and a body.</summary>
    internal static GhHttpResponse? Read(string? output, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var position = 0;
        var first = NextLine(output, ref position);
        var status = first is null ? null : StatusLine().Match(first);
        if (status is not { Success: true })
        {
            return null;
        }

        var code = int.Parse(status.Groups["code"].Value, CultureInfo.InvariantCulture);
        if (code < 200)
        {
            // An interim answer (1xx) is not the answer: gh prints the final one.
            return null;
        }

        string? etag = null;
        string? retryAfter = null;
        string? remaining = null;
        string? reset = null;
        string? poll = null;
        var lines = 0;
        while (position < output.Length)
        {
            var line = NextLine(output, ref position)!;
            if (line.Length == 0)
            {
                break;
            }

            if (++lines > MaxHeaderLines || line.Length > MaxHeaderLineLength)
            {
                return null;
            }

            var header = HeaderLine().Match(line);
            if (!header.Success)
            {
                return null;
            }

            var value = header.Groups["value"].Value;
            switch (header.Groups["name"].Value.ToLowerInvariant())
            {
                case "etag":
                    etag ??= value;
                    break;
                case "retry-after":
                    retryAfter ??= value;
                    break;
                case "x-ratelimit-remaining":
                    remaining ??= value;
                    break;
                case "x-ratelimit-reset":
                    reset ??= value;
                    break;
                case "x-poll-interval":
                    poll ??= value;
                    break;
            }
        }

        var body = position >= output.Length ? string.Empty : output[position..];
        return new GhHttpResponse(
            code,
            GhArguments.IsETag(etag) ? etag : null,
            body,
            ReadRetryAfter(retryAfter, now),
            ReadNumber(remaining, int.MaxValue),
            ReadEpoch(reset),
            ReadNumber(poll, MaxSeconds));
    }

    private static string? NextLine(string text, ref int position)
    {
        if (position >= text.Length)
        {
            return null;
        }

        var end = text.IndexOf('\n', position);
        string line;
        if (end < 0)
        {
            line = text[position..];
            position = text.Length;
        }
        else
        {
            line = text[position..end];
            position = end + 1;
        }

        return line.EndsWith('\r') ? line[..^1] : line;
    }

    private static int? ReadNumber(string? value, int maximum) =>
        value is { Length: > 0 and <= 10 } && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= maximum
            ? (int)number
            : null;

    private static DateTimeOffset? ReadEpoch(string? value) =>
        value is { Length: > 0 and <= 12 } && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= MaxEpochSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static DateTimeOffset? ReadRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (ReadNumber(value, MaxSeconds) is { } seconds)
        {
            return now.AddSeconds(seconds);
        }

        return DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
}
