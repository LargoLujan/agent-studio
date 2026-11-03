using System.IO;
using System.Text.Json;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli.Codex.AppServer;

namespace AgentStudio.Tests.Providers.Codex;

/// <summary>
/// docs/14 D-15 and F4-4: the answer of <c>account/rateLimits/read</c> is read by a whitelist. Of each bucket
/// (<c>rateLimitsByLimitId</c>, else <c>rateLimits</c>) only the windows, the limit's id and name, the limit reached and
/// the plan are taken, plus <c>ordinaryUsageAllowed</c>; the account id, the upsell, the credit balance and the reset
/// credits never leave the parser.
/// </summary>
public sealed class CodexRateLimitsParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The synthetic answer of 0.160.0 (see <c>Synthetic/README.md</c>), found by walking up from the output folder.</summary>
    private static string[] SyntheticLines()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Providers", "Codex", "Synthetic", CodexTestSupport.FixtureVersion, "05-rate-limits.jsonl");
            if (File.Exists(candidate))
            {
                return File.ReadAllLines(candidate);
            }
        }

        throw new FileNotFoundException("The synthetic answer of account/rateLimits/read is missing.");
    }

    private static QuotaReadResult Read(IEnumerable<string> lines) =>
        lines.Select(line => CodexRateLimitsParser.FromLine(line, ObservedAt)).FirstOrDefault(r => r is not null)
        ?? throw new InvalidOperationException("No line answered the quota read.");

    private static QuotaReadResult Answer(string result) => Read([$$"""{"id":2,"result":{{result}}}"""]);

    private static QuotaReadResult Error(string message) => Read([JsonSerializer.Serialize(new { id = 2, error = new { code = -32600, message } })]);

    [Fact]
    public void Every_window_of_every_bucket_is_an_observation_with_the_utilization_codex_gave()
    {
        var result = Read(SyntheticLines());

        Assert.Equal(QuotaReadStatus.Read, result.Status);
        Assert.Equal(
            [
                ("codex/primary", 0.25, TimeSpan.FromHours(5), 1792066800L, (string?)null, (string?)null),
                ("codex/secondary", 0.61, TimeSpan.FromDays(7), 1792350000L, null, null),
                ("codex_other/primary", 0.42, TimeSpan.FromHours(1), 1792060000L, "codex_other", "rate_limit_reached"),
            ],
            result.Observations.Select(o => (o.Window, o.Utilization.Value!.Value, o.Length!.Value, o.ResetsAt!.Value.ToUnixTimeSeconds(), o.Label, o.Reached)));
        Assert.All(result.Observations, o => Assert.Equal((ProviderId.CodexCli, ObservedAt, MetricOrigin.ReportedByProvider), (o.Provider, o.ObservedAt, o.Utilization.Origin)));
        Assert.All(result.Observations, o => Assert.Contains("account/rateLimits/read", o.Source, StringComparison.Ordinal));
        Assert.All(result.Observations, o => Assert.Contains("usedPercent", o.Utilization.Source, StringComparison.Ordinal));
        Assert.Equal(("plus", true), (result.PlanType, result.OrdinaryUsageAllowed));
        Assert.Null(result.Reason);
    }

    [Fact]
    public void The_account_id_the_upsell_the_balance_and_the_reset_credits_never_leave_the_parser()
    {
        var result = Read(SyntheticLines());

        var everything = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("acct-SINTETICO", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("BALANCE-SINTETICO", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("UPSELL-SINTETICO", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("availableCount", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-5.5-codex", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_buckets_by_limit_the_single_bucket_view_is_read()
    {
        var result = Answer("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":7,"windowDurationMins":300,"resetsAt":1792066800},"secondary":null,"planType":"pro"}}""");

        var only = Assert.Single(result.Observations);
        Assert.Equal(("codex/primary", 0.07), (only.Window, only.Utilization.Value!.Value));
        Assert.Equal("pro", result.PlanType);
        Assert.Null(result.OrdinaryUsageAllowed);
    }

    [Fact]
    public void A_bucket_without_an_id_names_its_windows_by_slot_alone()
    {
        var result = Answer("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":15,"resetsAt":null},"secondary":{"usedPercent":100}}}""");

        Assert.Equal(["primary", "secondary"], result.Observations.Select(o => o.Window));
        Assert.Equal([0.0, 1.0], result.Observations.Select(o => o.Utilization.Value!.Value));
        Assert.Null(result.Observations[0].ResetsAt);
        Assert.Null(result.Observations[1].Length);
    }

    [Fact]
    public void Values_out_of_range_are_dropped_and_never_guessed()
    {
        var result = Answer("""{"rateLimits":{"limitId":"codex x","limitName":"a\u0007b","primary":{"usedPercent":-3,"windowDurationMins":300},"secondary":{"usedPercent":12,"windowDurationMins":0,"resetsAt":-5},"planType":"plan con espacios","rateLimitReachedType":"x y"}}""");

        // A negative utilization is no figure: that window is left out; the other keeps what is valid.
        var only = Assert.Single(result.Observations);
        Assert.Equal(("secondary", 0.12), (only.Window, only.Utilization.Value!.Value));
        Assert.Null(only.Length);
        Assert.Null(only.ResetsAt);
        Assert.Null(only.Label);
        Assert.Null(only.Reached);
        Assert.Null(result.PlanType);
    }

    [Fact]
    public void With_an_api_key_the_quota_is_not_exposed()
    {
        var result = Error("chatgpt authentication required to read rate limits");

        Assert.Equal(QuotaReadStatus.NotExposed, result.Status);
        Assert.Equal("no expuesto: Codex usa API key", result.Reason);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public void Without_a_session_the_quota_is_missing_and_says_how_to_log_in()
    {
        var result = Error("codex account authentication required to read rate limits");

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.Contains("codex login", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_error_is_missing_with_a_short_reason_without_control_characters()
    {
        var result = Error("failed to fetch codex rate limits:\n\u0001 " + new string('x', 500));

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.StartsWith("sin dato: ", result.Reason, StringComparison.Ordinal);
        Assert.True(result.Reason!.Length <= 220, result.Reason);
        Assert.DoesNotContain(result.Reason, char.IsControl);
    }

    [Fact]
    public void A_refused_handshake_is_missing()
    {
        var result = Read(["""{"id":1,"error":{"code":-32600,"message":"Invalid request"}}"""]);

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.Contains("initialize", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_without_rate_limits_is_missing()
    {
        var result = Answer("""{"ordinaryUsageAllowed":true}""");

        Assert.Equal(QuotaReadStatus.Missing, result.Status);
        Assert.Empty(result.Observations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("[1,2]")]
    [InlineData("""{"id":1,"result":{"userAgent":"x"}}""")]
    [InlineData("""{"method":"account/rateLimits/updated","params":{}}""")]
    [InlineData("""{"id":"2","result":{"rateLimits":{}}}""")]
    [InlineData("""{"id":3,"result":{"rateLimits":{}}}""")]
    public void Lines_that_do_not_answer_the_read_are_ignored(string line)
    {
        Assert.Null(CodexRateLimitsParser.FromLine(line, ObservedAt));
    }

    [Fact]
    public void The_recorded_capture_of_the_tested_version_parses_once_it_exists()
    {
        var capture = Path.Combine(AppContext.BaseDirectory, "Providers", "Fixtures", "codex-cli", CodexTestSupport.FixtureVersion, "05-rate-limits.jsonl");
        Assert.SkipUnless(File.Exists(capture), "There is no recorded capture yet: CodexQuotaLiveTests records it, with the user's OK.");

        var result = Read(File.ReadAllLines(capture));

        Assert.True(result.Status is QuotaReadStatus.Read or QuotaReadStatus.NotExposed, result.Reason);
        Assert.DoesNotContain("accountId", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
}
