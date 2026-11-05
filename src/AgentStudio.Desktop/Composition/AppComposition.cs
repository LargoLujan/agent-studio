using System.IO;
using AgentStudio.Desktop.Services;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>Where the app keeps its data.</summary>
/// <param name="Roaming">Settings, layouts and the global skill activations (%APPDATA%\AgentStudio).</param>
/// <param name="Local">Worktrees, dossiers and skill sessions (%LOCALAPPDATA%\AgentStudio).</param>
/// <param name="DefaultSkillsIndexPath">The skills index used while the settings name none, if that folder exists.</param>
public sealed record AppFolders(AppDataPaths Roaming, LocalDataPaths Local, string? DefaultSkillsIndexPath)
{
    /// <summary>
    /// The folder <c>%LOCALAPPDATA%</c> resolves to for the semantic index and its model (docs/17 D-03 and D-04): the index
    /// of a project lives in <c>AgentStudio\index\&lt;key&gt;\semantic.db</c> under it, and the model in
    /// <c>AgentStudio\models\…</c>. For the current user it is the one of <see cref="SemanticIndexOptions.ForCurrentUser"/>
    /// and <see cref="EmbeddingModelLocation.ForCurrentUser"/>; otherwise, by default, the folder that holds
    /// <see cref="LocalDataPaths.BaseDirectory"/>, so a test with folders of its own keeps the index and the model in them.
    /// </summary>
    public string SemanticRoot { get; init; } = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Local.BaseDirectory)) ?? Local.BaseDirectory;

    public static AppFolders ForCurrentUser() =>
        new(AppDataPaths.ForCurrentUser(), LocalDataPaths.ForCurrentUser(), AppSettings.DefaultSkillsIndexPath)
        {
            SemanticRoot = SemanticIndexOptions.ForCurrentUser().LocalAppData,
        };
}

/// <summary>The composition root of the app: what App.OnStartup registers, reachable by tests with their own folders.</summary>
public static class AppComposition
{
    /// <summary>
    /// Registers the whole app: the platform (processes, journal, providers, security), the Phase 3 engine with every
    /// port it runs on, and the shell with the orchestrator screen. The settings store is the one of
    /// <paramref name="folders"/>, and the skills index it names (read now) is the catalog of the engine. The system clock
    /// is used unless a <see cref="TimeProvider"/> is registered first. The host adds logging.
    /// </summary>
    public static IServiceCollection AddAgentStudioApp(this IServiceCollection services, AppFolders? folders = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var app = folders ?? AppFolders.ForCurrentUser();
        var settings = new JsonAppSettingsStore(app.Roaming, app.DefaultSkillsIndexPath);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAppSettingsStore>(settings);
        services.AddPlatformServices(app, settings.Load().SkillsIndexPath);
        services.AddShellServices(app.Roaming);
        return services;
    }
}
