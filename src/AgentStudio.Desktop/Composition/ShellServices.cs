using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Controls.Editor;
using AgentStudio.Desktop.Controls.Terminal;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Skills;
using AgentStudio.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

internal static class ShellServices
{
    /// <summary>
    /// Registers the IDE shell: settings, layouts, ViewModels, the main window, the editor, the terminal, the screens
    /// (orchestrator, skills, usage and memory) and the unattended work of Phase 8 (the lock, the tray and its background).
    /// A settings store registered earlier (the composition root's) is kept.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="paths">Per-user data folder; defaults to %APPDATA%\AgentStudio.</param>
    internal static IServiceCollection AddShellServices(this IServiceCollection services, AppDataPaths? paths = null)
    {
        services.AddSingleton(paths ?? AppDataPaths.ForCurrentUser());
        services.TryAddSingleton<IAppSettingsStore>(provider => new JsonAppSettingsStore(provider.GetRequiredService<AppDataPaths>()));

        // V44 (docs/17 D-48): the language the agents write in for the person is the one of the interface; the layers below ask for it.
        services.TryAddSingleton<AgentStudio.Application.Prompts.IResponseLanguage, AgentStudio.Desktop.Localization.UiResponseLanguage>();
        return services
            .AddSingleton<ILayoutStore, FileLayoutStore>()
            .AddSingleton<IFolderPicker, WindowsFolderPicker>()
            .AddSingleton<IUnsavedChangesPrompt, WpfUnsavedChangesPrompt>()
            .AddSingleton<ProviderDiagnosticsViewModel>()
            .AddSingleton<ExplorerViewModel>()
            .AddSingleton<IJournalWatcher, JournalFolderWatcher>()

            // V32: «Bitácora de hoy» follows the journal folder and the day by itself, and shows what it reads on the UI thread.
            .AddSingleton(provider => new JournalViewModel(
                provider.GetRequiredService<TimeProvider>(),
                provider.GetService<IProjectStorageReader>(),
                provider.GetService<IJournalWatcher>(),
                ObservabilityUiServices.UiContext()))
            .AddSingleton<QuickOpenViewModel>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<IReviewNavigation>(provider => provider.GetRequiredService<MainViewModel>())
            .AddSingleton<MainWindow>()
            .AddEditorServices()
            .AddTerminalServices()
            .AddOrchestrationUiServices()
            .AddAgentStudioSkillsScreen()
            .AddObservabilityUiServices()
            .AddDebateUiServices()
            .AddUnattendedWorkServices();
    }
}
