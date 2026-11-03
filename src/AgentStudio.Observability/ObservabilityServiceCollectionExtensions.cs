using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Observability.Quota;
using AgentStudio.Observability.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the observability of Phase 4 (docs/14 WP2): the usage report as a projection of the decision logs
    /// (<see cref="IUsageReport"/>, <see cref="UsageProjector"/>), its CSV and JSON export (<see cref="IUsageExporter"/>),
    /// the user's record of quota observations (<see cref="IQuotaObservations"/>, at <paramref name="quota"/> or by default
    /// <c>%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl</c>) and the quota of Gemini, «no expuesto»
    /// (<see cref="IProviderQuotaReader"/>). Registering it twice registers each once.
    /// </summary>
    /// <remarks>
    /// It needs what other modules register: <c>IProjectLogReader</c> and <c>IProjectStorageReader</c> (Infrastructure),
    /// through which every log and every day of the journal is read. It uses the container's <see cref="TimeProvider"/>,
    /// and registers <see cref="TimeProvider.System"/> only when there is none. The context health tracker is not a
    /// service: the screens that follow an attempt make one for it (the monitor of a run and the planning, docs/18 P36),
    /// with the thresholds of the settings and the <c>ModelWindowCatalog</c> of the project over the base table of windows.
    /// Tanda 2 (P37): it also registers the ends of the agent runs of the process (<see cref="AgentRunEnds"/>), which the
    /// provider pipeline notes with the quota observation, so the quota of Codex can be read after a run of Codex.
    /// </remarks>
    public static IServiceCollection AddAgentStudioObservability(this IServiceCollection services, QuotaLogOptions? quota = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(quota ?? new QuotaLogOptions());
        services.TryAddSingleton<IQuotaObservations>(sp => new JsonlQuotaObservations(sp.GetRequiredService<QuotaLogOptions>()));
        services.TryAddSingleton<AgentRunEnds>();
        services.TryAddSingleton<IAgentRunEnds>(sp => sp.GetRequiredService<AgentRunEnds>());
        services.TryAddSingleton<UsageProjector>();
        services.TryAddSingleton<IUsageReport>(sp => sp.GetRequiredService<UsageProjector>());
        services.TryAddSingleton<IUsageExporter>(sp => new UsageExporter(sp.GetRequiredService<TimeProvider>()));
        if (!services.Any(d => d.ServiceType == typeof(IProviderQuotaReader) && ReferenceEquals(d.ImplementationInstance, NotExposedQuotaReader.Gemini)))
        {
            services.AddSingleton<IProviderQuotaReader>(NotExposedQuotaReader.Gemini);
        }

        return services;
    }
}
