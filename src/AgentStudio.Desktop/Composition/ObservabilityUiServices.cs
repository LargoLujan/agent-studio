using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// <para>
    /// Tanda 2 (docs/18 P3, P36 and P37): the quota follows the ends of the agent runs (<see cref="IAgentRunEnds"/>), so the
    /// quota of Codex is read after a run of Codex with the opt-in; the context windows each project learned
    /// (<see cref="ContextWindowCatalogs"/>, over <see cref="IProjectLogReader"/>) reach the monitors; and the bars of the
    /// status bar (<see cref="StatusStripViewModel"/>) follow the quota and the monitors, on the UI thread.
    /// </para>
    /// <para>
    /// Phase 12 (docs/17 D-02 to D-06; ADR-0047): «Memoria» searches through the semantic index, with the trust of the journal
    /// and the installer of the model (<see cref="MemorySearchBackend"/>, on the UI thread), and the
    /// <see cref="SemanticIndexUpkeep"/> refreshes the index in the background after each derivation and once the model was
    /// downloaded, and reads again the state on the screen when a refresh ends.
    /// </para>
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
                Time(provider),
                provider.GetService<IAgentRunEnds>(),
                UiContext()))
            .AddSingleton(provider => new ContextWindowCatalogs(provider.GetService<IProjectLogReader>()))
            .AddSingleton(provider => new StatusStripViewModel(provider.GetRequiredService<QuotaViewModel>(), Time(provider), UiContext()))
            .AddSingleton(provider => new UsageDashboardViewModel(
                provider.GetService<IUsageReport>(),
                provider.GetService<IUsageExporter>(),
                provider.GetRequiredService<ISaveFilePicker>(),
                provider.GetRequiredService<IAppSettingsStore>(),
                Time(provider),
                provider.GetRequiredService<QuotaViewModel>(),
                provider.GetRequiredService<ObservabilitySettingsViewModel>()))
            .AddSingleton(provider => new LanguageSettingsViewModel(provider.GetRequiredService<IAppSettingsStore>()))
            .AddSingleton(provider => new MemorySettingsViewModel(provider.GetRequiredService<IAppSettingsStore>()))
            .AddSingleton(provider => new SettingsDocumentViewModel(
                provider.GetRequiredService<IAppSettingsStore>(),
                provider.GetRequiredService<ObservabilitySettingsViewModel>(),
                provider.GetService<UnattendedSettingsViewModel>(),
                provider.GetService<LockSettingsViewModel>(),
                Time(provider),
                provider.GetRequiredService<MemorySettingsViewModel>(),
                provider.GetRequiredService<LanguageSettingsViewModel>()))
            .AddSingleton(provider => new DailySummaryViewModel(provider.GetService<IDailySummaryService>(), Time(provider)))
            .AddSingleton(provider => new SemanticIndexUpkeep(
                provider.GetService<ISemanticIndex>(),
                provider.GetService<IEmbedderSource>(),
                provider.GetService<ILogger<SemanticIndexUpkeep>>()))
            .AddSingleton(provider =>
            {
                var memory = new MemoryViewModel(
                    provider.GetService<IProjectWorkspace>(),
                    provider.GetService<IProjectMemory>(),
                    provider.GetService<IMemoryExporter>(),
                    provider.GetService<IRunActivity>(),
                    provider.GetRequiredService<DailySummaryViewModel>(),
                    Time(provider),
                    new MemorySearchBackend(
                        provider.GetService<ISemanticIndex>(),
                        provider.GetService<IJournalTrust>(),
                        provider.GetService<IEmbeddingModelInstaller>(),
                        UiContext()));
                provider.GetRequiredService<SemanticIndexUpkeep>().Follow(memory);
                return memory;
            })
            .AddSingleton(provider => new ProjectMemoryUpkeep(
                provider.GetService<IProjectWorkspace>(),
                provider.GetService<IMemoryDerivation>(),
                provider.GetService<IMemoryExporter>(),
                provider.GetRequiredService<SemanticIndexUpkeep>()));
    }

    private static TimeProvider Time(IServiceProvider provider) => provider.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// The UI thread's context, whatever thread resolves the screen: the quota read after a run of Codex, the bars of the
    /// status bar and the day «Bitácora de hoy» read again (ShellServices) are applied on the dispatcher. Null without an
    /// application (a composition in a test).
    /// </summary>
    internal static System.Windows.Threading.DispatcherSynchronizationContext? UiContext() =>
        System.Windows.Application.Current?.Dispatcher is { } dispatcher ? new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher) : null;
}
