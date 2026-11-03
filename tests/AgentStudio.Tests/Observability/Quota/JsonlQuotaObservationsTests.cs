using System.IO;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Quota;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Quota;

/// <summary>
/// docs/14 D-14: every quota observation of any run goes to the user's record,
/// <c>%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl</c>, append-only and rotated by size. The panel reads back the latest
/// observation of each window of each provider, and the utilization is always the provider's own figure.
/// </summary>
public sealed class JsonlQuotaObservationsTests : IDisposable
{
    private const string RateLimitEvent = "claude rate_limit_event";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "agentstudio-quota-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempFolders.Delete(_folder);

    private string FilePath => Path.Combine(_folder, "usage", "quota.jsonl");

    private JsonlQuotaObservations Record(long maxBytes = 1024 * 1024, int retained = 3) =>
        new(new QuotaLogOptions { FilePath = FilePath, MaxFileBytes = maxBytes, RetainedFiles = retained });

    private static QuotaObservation Claude(string window, double utilization, DateTimeOffset at, TimeSpan? length = null) =>
        new(ProviderId.ClaudeCode, window, MetricValue<double>.Exact(utilization, RateLimitEvent), length, at.AddHours(5), at, RateLimitEvent);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task What_is_appended_comes_back_as_the_latest_of_each_window()
    {
        var record = Record();

        await record.AppendAsync([Claude("five_hour", 0.20, T0), Claude("seven_day", 0.40, T0)], Ct);
        await record.AppendAsync([Claude("five_hour", 0.30, T0.AddMinutes(10))], Ct);

        var latest = await record.LatestAsync(Ct);

        Assert.Equal([("five_hour", 0.30), ("seven_day", 0.40)], latest.Select(o => (o.Window, o.Utilization.Value!.Value)));
    }

    [Fact]
    public async Task The_latest_is_the_one_observed_last_not_the_one_written_last()
    {
        var record = Record();

        await record.AppendAsync([Claude("five_hour", 0.30, T0.AddMinutes(10))], Ct);
        await record.AppendAsync([Claude("five_hour", 0.20, T0)], Ct);

        Assert.Equal(0.30, Assert.Single(await record.LatestAsync(Ct)).Utilization.Value);
    }

    [Fact]
    public async Task Each_provider_keeps_its_own_windows()
    {
        var record = Record();
        var codex = new QuotaObservation(ProviderId.CodexCli, "five_hour", MetricValue<double>.Exact(0.10, "codex account/rateLimits/read"), TimeSpan.FromMinutes(300), null, T0, "codex account/rateLimits/read");

        await record.AppendAsync([Claude("five_hour", 0.50, T0), codex], Ct);

        var latest = await record.LatestAsync(Ct);
        Assert.Equal([(ProviderId.ClaudeCode, 0.50), (ProviderId.CodexCli, 0.10)], latest.Select(o => (o.Provider, o.Utilization.Value!.Value)));
    }

    [Fact]
    public async Task Every_field_comes_back_as_it_was_written()
    {
        var record = Record();
        var observation = new QuotaObservation(ProviderId.CodexCli, "codex/primary", MetricValue<double>.Exact(0.875, "codex account/rateLimits/read usedPercent"), TimeSpan.FromDays(7), new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero), T0, "codex account/rateLimits/read")
        {
            Label = "Límite semanal «Plus»",
            Reached = "primary",
        };

        await record.AppendAsync([observation], Ct);

        Assert.Equal(observation, Assert.Single(await record.LatestAsync(Ct)));
    }

    [Fact]
    public async Task An_observation_without_utilization_never_hides_an_earlier_one_that_has_it()
    {
        var record = Record();
        var withoutFigure = new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Missing("claude rate_limit_event sin utilization"), null, null, T0.AddMinutes(5), RateLimitEvent);

        await record.AppendAsync([Claude("five_hour", 0.25, T0), withoutFigure, new QuotaObservation(ProviderId.ClaudeCode, "seven_day", MetricValue<double>.Missing("sin utilization"), null, null, T0, RateLimitEvent)], Ct);

        var latest = await record.LatestAsync(Ct);
        Assert.Equal(0.25, latest.Single(o => o.Window == "five_hour").Utilization.Value);
        Assert.Equal(MetricOrigin.Missing, latest.Single(o => o.Window == "seven_day").Utilization.Origin);
    }

    [Fact]
    public async Task The_record_rotates_by_size_and_the_latest_of_every_window_survives()
    {
        var record = Record(maxBytes: 1200);
        await record.AppendAsync([Claude("seven_day", 0.40, T0)], Ct);

        for (var i = 1; i <= 30; i++)
        {
            await record.AppendAsync([Claude("five_hour", i / 100.0, T0.AddMinutes(i))], Ct);
        }

        Assert.True(File.Exists(Path.Combine(_folder, "usage", "quota.1.jsonl")));
        Assert.True(new FileInfo(FilePath).Length <= 1200 + 400);
        var latest = await record.LatestAsync(Ct);
        Assert.Equal([("five_hour", 0.30), ("seven_day", 0.40)], latest.Select(o => (o.Window, o.Utilization.Value!.Value)));
    }

    [Fact]
    public async Task Rotated_files_beyond_the_retention_are_deleted()
    {
        var record = Record(maxBytes: 300, retained: 2);

        for (var i = 1; i <= 20; i++)
        {
            await record.AppendAsync([Claude("five_hour", i / 100.0, T0.AddMinutes(i))], Ct);
        }

        Assert.Equal(["quota.1.jsonl", "quota.2.jsonl", "quota.jsonl"], Directory.GetFiles(Path.Combine(_folder, "usage")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(0.20, Assert.Single(await record.LatestAsync(Ct)).Utilization.Value);
    }

    [Fact]
    public async Task A_torn_last_line_and_lines_that_are_not_observations_are_skipped()
    {
        var record = Record();
        await record.AppendAsync([Claude("five_hour", 0.20, T0)], Ct);
        await File.AppendAllTextAsync(FilePath, "no es json\n{\"provider\":\"claude-code\"}\n{\"provider\":\"claude-code\",\"window\":\"seven_day\",\"utilization\":{\"value\":2,\"origin\":\"Missing\",\"source\":\"x\"},\"observedAt\":\"2026-01-01T10:00:00+00:00\",\"source\":\"x\"}\n{\"provider\":\"claude-co", Ct);

        var latest = await record.LatestAsync(Ct);

        Assert.Equal(0.20, Assert.Single(latest).Utilization.Value);
    }

    [Fact]
    public async Task An_append_after_a_torn_line_starts_on_a_line_of_its_own()
    {
        var record = Record();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, "{\"provider\":\"claude-co", Ct);

        await record.AppendAsync([Claude("five_hour", 0.20, T0)], Ct);

        Assert.Equal(0.20, Assert.Single(await record.LatestAsync(Ct)).Utilization.Value);
    }

    [Fact]
    public async Task Without_a_record_there_is_no_observation()
    {
        Assert.Empty(await Record().LatestAsync(Ct));
    }

    [Fact]
    public async Task A_utilization_that_agent_studio_worked_out_is_refused_and_nothing_is_written()
    {
        var record = Record();
        var computed = new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.EstimatedBySystem(0.5, "guess"), null, null, T0, "guess");

        await Assert.ThrowsAsync<ArgumentException>(() => record.AppendAsync([Claude("seven_day", 0.1, T0), computed], Ct));

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void The_record_lives_in_the_profile_of_the_user_by_default()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "usage", "quota.jsonl");

        Assert.Equal(expected, new QuotaLogOptions().FilePath);
    }

    [Fact]
    public async Task Gemini_does_not_expose_its_quota_and_nothing_is_launched()
    {
        IProviderQuotaReader reader = NotExposedQuotaReader.Gemini;

        var result = await reader.ReadAsync(Ct);

        Assert.Equal(ProviderId.GeminiCli, reader.Provider);
        Assert.Equal(QuotaReadStatus.NotExposed, result.Status);
        Assert.Empty(result.Observations);
        Assert.Contains("Gemini", result.Reason, StringComparison.Ordinal);
    }
}
