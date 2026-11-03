using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Domain.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Desktop.Composition;

internal static class ObservabilityUiServices
{
    /// <summary>
    /// Registers the Phase 4 screens (docs/14 section 9, WP5 and «Integración»): «Consumo» (usage and quota), «Memoria» (the
    /// project memory and the daily summary) and «Ajustes» (the preferences of the app: «Contexto y cuota», and «Trabajo
    /// desatendido» and «Windows Hello y bloqueo» of Phase 8 when they were composed), one document each for the session,
    /// and the upkeep that derives the memory from the log after each run and when a project is opened. «Consumo» and
    /// «Ajustes» share the one <see cref="ObservabilitySettingsViewModel"/>: what «Ajustes» saves is what the report is judged
    /// with. They register no port of their own: each port is taken when the composition root registered it
    /// (<see cref="PlatformServices.AddPlatformServices"/>), and without it the screen says what is missing. The quota
    /// reader is the one of Codex among the registered readers; the others are «no expuesto» and are not read.
    /// </summary>
    internal static IServiceCollection AddObservabilityUiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddSingleton<ISaveFilePicker, WindowsSaveFilePicker>()
            .AddSingleton(provider => new ObservabilitySettingsViewModel(provider.GetRequiredService<IAppSettingsStore>()))
            .AddSingleton(provider => new QuotaViewModel(
                provider.GetService<IQuotaObservations>(),
                provider.GetServices<IProviderQuotaReader>().FirstOrDefault(reader => reader.Provider == ProviderId.CodexCli),
                Time(provider)))
            .AddSingleton(provider => new UsageDashboardViewModel(
                provider.GetService<IUsageReport>(),
                provider.GetService<IUsageExporter>(),
                provider.GetRequiredService<ISaveFilePicker>(),
                provider.GetRequiredService<IAppSettingsStore>(),
                Time(provider),
                provider.GetRequiredService<QuotaViewModel>(),
                provider.GetRequiredService<ObservabilitySettingsViewModel>()))
            .AddSingleton(provider => new SettingsDocumentViewModel(
                provider.GetRequiredService<IAppSettingsStore>(),
                provider.GetRequiredService<ObservabilitySettingsViewModel>(),
                provider.GetService<UnattendedSettingsViewModel>(),
                provider.GetService<LockSettingsViewModel>(),
                Time(provider)))
            .AddSingleton(provider => new DailySummaryViewModel(provider.GetService<IDailySummaryService>(), Time(provider)))
            .AddSingleton(provider => new MemoryViewModel(
                provider.GetService<IProjectWorkspace>(),
                provider.GetService<IProjectMemory>(),
                provider.GetService<IMemoryExporter>(),
                provider.GetService<IRunActivity>(),
                provider.GetRequiredService<DailySummaryViewModel>(),
                Time(provider)))
            .AddSingleton(provider => new ProjectMemoryUpkeep(
                provider.GetService<IProjectWorkspace>(),
                provider.GetService<IMemoryDerivation>(),
                provider.GetService<IMemoryExporter>()));
    }

    private static TimeProvider Time(IServiceProvider provider) => provider.GetService<TimeProvider>() ?? TimeProvider.System;
}
