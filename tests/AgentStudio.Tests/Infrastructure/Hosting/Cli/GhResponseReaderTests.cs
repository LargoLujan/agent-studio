using System.Text;
using AgentStudio.Infrastructure.Hosting.Cli;

namespace AgentStudio.Tests.Infrastructure.Hosting.Cli;

/// <summary>
/// docs/21 D-12: <c>gh api -i</c> prints the status line, the headers, a blank line and the body, and the reader decides by that structure, never by a phrase and never by the exit code. The answers are written
/// by hand (<c>GitHub/Fixtures/README.md</c>).
/// </summary>
public sealed class GhResponseReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static string Text(string eol, string body = "{\"login\":\"octocat\"}") =>
        string.Join(eol, "HTTP/2.0 200 OK", "Content-Type: application/json; charset=utf-8", "Etag: W/\"abc\"", "X-Ratelimit-Remaining: 4999", "X-Ratelimit-Reset: 1794571200", "X-Poll-Interval: 60", string.Empty, body);

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void Both_line_ends_give_the_same_answer(string eol)
    {
        var answer = GhResponseReader.Read(Text(eol), Now)!;

        Assert.Equal(200, answer.Status);
        Assert.Equal("W/\"abc\"", answer.ETag);
        Assert.Equal("{\"login\":\"octocat\"}", answer.Body);
        Assert.Equal(4999, answer.RateLimitRemaining);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794571200), answer.RateLimitReset);
        Assert.Equal(60, answer.PollIntervalSeconds);
    }

    [Fact]
    public void A_304_has_a_status_and_an_etag_and_no_body_even_without_the_blank_line()
    {
        var answer = GhResponseReader.Read("HTTP/2.0 304 Not Modified\r\nEtag: \"x\"\r\n", Now)!;

        Assert.Equal(304, answer.Status);
        Assert.Equal("\"x\"", answer.ETag);
        Assert.Equal(string.Empty, answer.Body);
    }

    [Fact]
    public void The_http_versions_that_gh_can_print_are_read()
    {
        foreach (var line in new[] { "HTTP/1.0 200 OK", "HTTP/1.1 200 OK", "HTTP/2 200", "HTTP/2.0 200 OK", "HTTP/3 200 OK" })
        {
            Assert.Equal(200, GhResponseReader.Read(line + "\n\n", Now)!.Status);
        }
    }

    [Fact]
    public void Header_names_ignore_case_the_first_of_each_wins_and_only_five_are_kept()
    {
        var answer = GhResponseReader.Read("HTTP/2.0 403 Forbidden\nETAG: \"first\"\netag: \"second\"\nRETRY-AFTER: 30\nSet-Cookie: s=1\nLink: <https://evil.example>; rel=\"next\"\n\n{}", Now)!;

        Assert.Equal("\"first\"", answer.ETag);
        Assert.Equal(Now.AddSeconds(30), answer.RetryAfter);
    }

    [Fact]
    public void A_retry_after_may_be_a_date()
    {
        var answer = GhResponseReader.Read("HTTP/2.0 429 Too Many Requests\nRetry-After: Fri, 13 Nov 2026 12:05:00 GMT\n\n", Now)!;

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 12, 5, 0, TimeSpan.Zero), answer.RetryAfter);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("\"a b\"")]
    [InlineData("\"a\"; X: 1")]
    public void An_etag_that_has_not_the_exact_form_is_dropped_and_the_answer_stands(string etag)
    {
        var answer = GhResponseReader.Read("HTTP/2.0 200 OK\nEtag: " + etag + "\n\n{}", Now)!;

        Assert.Null(answer.ETag);
        Assert.Equal(200, answer.Status);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("99999999999999999999")]
    [InlineData("abc")]
    [InlineData("")]
    public void A_number_header_that_is_not_a_plain_number_is_not_read(string value)
    {
        var answer = GhResponseReader.Read("HTTP/2.0 403 Forbidden\nX-Ratelimit-Remaining: " + value + "\nX-Ratelimit-Reset: " + value + "\nRetry-After: " + value + "\n\n", Now)!;

        Assert.Null(answer.RateLimitRemaining);
        Assert.Null(answer.RateLimitReset);
        Assert.Null(answer.RetryAfter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gh: HTTP 304")]
    [InlineData("{\"login\":\"octocat\"}")]
    [InlineData("HTTP/9.9 200 OK\n\n")]
    [InlineData("HTTP/2.0 20 OK\n\n")]
    [InlineData("HTTP/2.0 600 X\n\n")]
    [InlineData("http/2.0 200 OK\n\n")]
    [InlineData(" HTTP/2.0 200 OK\n\n")]
    [InlineData("HTTP/2.0 100 Continue\n\n")]
    [InlineData("HTTP/2.0 200 OK\nthis is not a header\n\nbody")]
    [InlineData("HTTP/2.0 200 OK\n: no-name\n\nbody")]
    [InlineData("HTTP/2.0 200 OK\nBad Name: x\n\nbody")]
    public void What_is_not_a_status_line_and_headers_is_not_an_answer(string? output) => Assert.Null(GhResponseReader.Read(output, Now));

    [Fact]
    public void More_than_200_header_lines_or_a_line_of_8193_characters_is_not_an_answer()
    {
        var many = new StringBuilder("HTTP/2.0 200 OK\n");
        for (var i = 0; i < GhResponseReader.MaxHeaderLines + 1; i++)
        {
            many.Append("X-Filler: 1\n");
        }

        Assert.Null(GhResponseReader.Read(many + "\nbody", Now));
        Assert.Null(GhResponseReader.Read("HTTP/2.0 200 OK\nX-Long: " + new string('a', GhResponseReader.MaxHeaderLineLength) + "\n\nbody", Now));
    }

    [Fact]
    public void The_standard_error_text_of_gh_is_not_a_body_the_status_line_is_the_one_of_the_output()
    {
        // What gh says on its standard error when it gets a 304 never reaches this reader: it reads the standard output only. A line that looks like a status inside the body does not count.
        var answer = GhResponseReader.Read("HTTP/2.0 200 OK\n\nHTTP/2.0 500 Error\n", Now)!;

        Assert.Equal(200, answer.Status);
        Assert.Equal("HTTP/2.0 500 Error\n", answer.Body);
    }
}
