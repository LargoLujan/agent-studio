using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Desktop.Composition;

internal static class RepositoryUiServices
{
    /// <summary>
    /// Registers «Repositorio y cambios» (docs/16 F9-9, D-44), one document for the session that follows the open project. It registers no
    /// port of its own: the history of git (the hardened layer), the annotator, the timeline of the log and the remotes are taken when the
    /// composition root registered them, and without any of them the screen says what is missing. The shell connects
    /// <see cref="RepositoryViewModel.ChainRequested"/> to the screen of the chain; until then no version offers its button.
    /// </summary>
    internal static IServiceCollection AddRepositoryUiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton(provider => new RepositoryViewModel(
            provider.GetService<IProjectWorkspace>(),
            provider.GetService<IRepositoryHistoryReader>(),
            provider.GetService<IRepositoryAnnotator>(),
            provider.GetService<IRepositoryActivityReader>(),
            provider.GetService<IRepositoryRemoteReader>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
    }
}
