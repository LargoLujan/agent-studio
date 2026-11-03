using AgentStudio.Observability.Logging;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Observability;

/// <summary>The provider inside the container and the host, the way the app composes it.</summary>
public sealed class FileLoggingRegistrationTests : IDisposable
{
    private readonly TempLogDirectory _logs = new();

    // 10:00 UTC is 12:00 local (+02:00), so the local day is 2026-10-15.
    private readonly LoggingClock _clock = new(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));

    public void Dispose() => _logs.Dispose();

    private ServiceProvider Services(Action<ILoggingBuilder>? extra = null, int registrations = 1, Action<FileLoggerOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddAgentStudioSecurity();
        services.AddLogging(logging =>
        {
            for (var i = 0; i < registrations; i++)
            {
                logging.AddAgentStudioFileLogging(options =>
                {
                    options.LogDirectory = _logs.Path;
                    configure?.Invoke(options);
                });
            }

            extra?.Invoke(logging);
        });
        return services.BuildServiceProvider();
    }

    private HostApplicationBuilder HostBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddAgentStudioSecurity();
        return builder;
    }

    private string?[] Messages() => _logs.Records().Select(r => r.GetProperty("message").GetString()).ToArray();

    [Fact]
    public void The_provider_is_part_of_the_standard_logging_pipeline()
    {
        using (var provider = Services())
        {
            provider.GetRequiredService<ILogger<FileLoggingRegistrationTests>>().LogInformation("through the container {N}", 1);
        }

        var record = _logs.Records().Single();
        Assert.Equal(typeof(FileLoggingRegistrationTests).FullName, record.GetProperty("category").GetString());
        Assert.Equal("through the container 1", record.GetProperty("message").GetString());
        Assert.Equal(["agentstudio-20261015.log"], _logs.FileNames());
    }

    [Fact]
    public void Registering_twice_registers_one_provider()
    {
        using (var provider = Services(registrations: 2))
        {
            Assert.Single(provider.GetServices<ILoggerProvider>().OfType<FileLoggerProvider>());
            provider.GetRequiredService<ILogger<FileLoggingRegistrationTests>>().LogInformation("once");
        }

        Assert.Equal(["once"], Messages());
    }

    [Fact]
    public void The_container_flushes_the_provider_when_it_is_disposed()
    {
        using (var provider = Services())
        {
            var logger = provider.GetRequiredService<ILogger<FileLoggingRegistrationTests>>();
            for (var i = 0; i < 500; i++)
            {
                logger.LogInformation("entry {N}", i);
            }
        }

        Assert.Equal(500, _logs.Records().Count);
    }

    [Fact]
    public async Task A_host_flushes_the_provider_when_it_shuts_down()
    {
        var builder = HostBuilder();
        builder.Logging.AddAgentStudioFileLogging(options => options.LogDirectory = _logs.Path);
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<FileLoggingRegistrationTests>>();
        for (var i = 0; i < 300; i++)
        {
            logger.LogInformation("entry {N}", i);
        }

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(300, Messages().Count(m => m!.StartsWith("entry ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Without_a_filter_the_framework_default_keeps_debug_entries_out()
    {
        using (var provider = Services(configure: options => options.MinLevel = LogLevel.Debug))
        {
            provider.GetRequiredService<ILogger<FileLoggingRegistrationTests>>().LogDebug("debug");
        }

        Assert.Empty(_logs.AllLines());
    }

    [Fact]
    public void A_filter_for_the_provider_lets_a_lower_configured_level_through()
    {
        using (var provider = Services(logging => logging.AddFilter<FileLoggerProvider>(null, LogLevel.Debug), configure: options => options.MinLevel = LogLevel.Debug))
        {
            var logger = provider.GetRequiredService<ILogger<FileLoggingRegistrationTests>>();
            logger.LogTrace("trace");
            logger.LogDebug("debug");
        }

        Assert.Equal(["debug"], Messages());
    }

    [Fact]
    public async Task The_standard_file_alias_configuration_applies_to_the_provider()
    {
        var builder = HostBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:LogLevel:Default"] = "Warning" });
        builder.Logging.AddAgentStudioFileLogging(options => options.LogDirectory = _logs.Path);
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<FileLoggingRegistrationTests>>();
        logger.LogInformation("hidden by the File:LogLevel setting");
        logger.LogWarning("shown");
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(["shown"], Messages());
    }

    [Fact]
    public async Task A_provider_without_a_floor_of_its_own_lets_the_standard_file_configuration_lower_the_level()
    {
        // What the app does when Logging:File:MinLevel is not set: MinLevel = Trace, no filter, the framework decides.
        var builder = HostBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:LogLevel:Default"] = "Debug" });
        builder.Logging.AddAgentStudioFileLogging(options =>
        {
            options.LogDirectory = _logs.Path;
            options.MinLevel = LogLevel.Trace;
        });
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<FileLoggingRegistrationTests>>();
        logger.LogTrace("trace");
        logger.LogDebug("debug");
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(["debug"], Messages().Where(m => m is "trace" or "debug"));
    }

    [Fact]
    public async Task A_provider_without_a_floor_of_its_own_is_Information_by_default_through_the_framework()
    {
        var builder = HostBuilder();
        builder.Logging.AddAgentStudioFileLogging(options =>
        {
            options.LogDirectory = _logs.Path;
            options.MinLevel = LogLevel.Trace;
        });
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<FileLoggingRegistrationTests>>();
        logger.LogDebug("debug");
        logger.LogInformation("information");
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(["information"], Messages().Where(m => m is "debug" or "information"));
    }

    [Fact]
    public async Task An_explicit_level_with_its_filter_wins_over_the_standard_file_configuration()
    {
        // What the app does when Logging:File:MinLevel is set: MinLevel plus a filter for the provider.
        var builder = HostBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:LogLevel:Default"] = "Debug" });
        builder.Logging.AddAgentStudioFileLogging(options =>
        {
            options.LogDirectory = _logs.Path;
            options.MinLevel = LogLevel.Warning;
        });
        builder.Logging.AddFilter<FileLoggerProvider>(null, LogLevel.Warning);
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services.GetRequiredService<ILogger<FileLoggingRegistrationTests>>();
        logger.LogInformation("information");
        logger.LogWarning("warning");
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(["warning"], Messages().Where(m => m is "information" or "warning"));
    }

    [Fact]
    public void Without_a_redactor_the_logger_cannot_be_built_instead_of_logging_unredacted()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddAgentStudioFileLogging(options => options.LogDirectory = _logs.Path));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ILoggerFactory>());
    }
}
