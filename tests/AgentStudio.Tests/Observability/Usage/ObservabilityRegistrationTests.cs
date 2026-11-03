using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability;
using AgentStudio.Observability.Quota;
using AgentStudio.Observability.Usage;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// The composition of the observability module (docs/14 section 9, integration): one line registers the usage report,
/// its export, the user's record of quota observations and the quota of Gemini, «no expuesto». The logs and the journal
/// are read through the ports of WP3, which other modules register.
/// </summary>
public sealed class ObservabilityRegistrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "agentstudio-observability-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempFolders.Delete(_folder);

    private ServiceProvider Services(int registrations = 1)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectLogReader, NoLogs>();
        services.AddSingleton<IProjectStorageReader, NoStorage>();
        for (var i = 0; i < registrations; i++)
        {
            services.AddAgentStudioObservability(new QuotaLogOptions { FilePath = Path.Combine(_folder, "quota.jsonl") });
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task One_line_registers_the_report_its_export_and_the_quota_record()
    {
        using var provider = Services();

        var report = provider.GetRequiredService<IUsageReport>();
        Assert.IsType<UsageProjector>(report);
        Assert.IsType<UsageExporter>(provider.GetRequiredService<IUsageExporter>());
        var quota = Assert.IsType<JsonlQuotaObservations>(provider.GetRequiredService<IQuotaObservations>());
        Assert.Same(quota, provider.GetRequiredService<IQuotaObservations>());

        var empty = await report.QueryAsync([], new UsageQuery(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), TimeZoneInfo.Utc, []), TestContext.Current.CancellationToken);
        Assert.Empty(empty.Attempts);
    }

    [Fact]
    public async Task The_quota_record_writes_where_it_is_told()
    {
        using var provider = Services();

        await provider.GetRequiredService<IQuotaObservations>().AppendAsync(
            [new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.5, "claude rate_limit_event"), null, null, DateTimeOffset.UnixEpoch, "claude rate_limit_event")],
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_folder, "quota.jsonl")));
    }

    [Fact]
    public void The_quota_of_gemini_is_not_exposed_and_registering_twice_adds_it_once()
    {
        using var provider = Services(registrations: 2);

        var reader = Assert.Single(provider.GetServices<IProviderQuotaReader>());
        Assert.Equal(ProviderId.GeminiCli, reader.Provider);
        Assert.Single(provider.GetServices<IUsageReport>());
    }

    [Fact]
    public void A_clock_registered_before_is_kept()
    {
        var services = new ServiceCollection();
        var clock = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(clock);

        services.AddAgentStudioObservability();

        using var provider = services.BuildServiceProvider();
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }

    private sealed class NoLogs : IProjectLogReader
    {
        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct) => throw new IOException("no logs here");
    }

    private sealed class NoStorage : IProjectStorageReader
    {
        public Stream OpenForRead(string projectRoot, string relativePath) => throw new FileNotFoundException("no storage here", relativePath);
    }
}
