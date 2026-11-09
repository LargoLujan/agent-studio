using AgentStudio.Application.Models;
using AgentStudio.Application.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Observability.Models;

/// <summary>
/// What the verified log of a project teaches of its models (docs/17 D-23), through <see cref="IProjectLogReader"/>: it reads
/// only, writes no row and launches nothing. A log that is not trusted teaches nothing (<see cref="LearnedModels.Empty"/>);
/// what the reader throws (a project that cannot be read) reaches the caller. Reading and projecting run on the thread pool,
/// never on the caller's thread: a reader that completes synchronously would otherwise block the window that asked.
/// </summary>
public sealed class ProjectModelLearning(IProjectLogReader logs, IModelCatalogSource catalog) : IModelLearning
{
    /// <inheritdoc />
    public Task<LearnedModels> LearnAsync(string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Task.Run(
            async () =>
            {
                var snapshot = await logs.ReadAsync(projectRoot, ct).ConfigureAwait(false);
                return LearnedModelsProjection.FromLog(snapshot, catalog.Current.Catalog);
            },
            ct);
    }
}

/// <summary>Registration of what the log teaches of the models (docs/17 D-23, WP2), for the composition root to call.</summary>
public static class ModelLearningServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IModelLearning"/>. It needs <see cref="IProjectLogReader"/> (Infrastructure) and
    /// <see cref="IModelCatalogSource"/> (Infrastructure, <c>AddAgentStudioModelCatalog</c>). Registering it twice registers it once.
    /// </summary>
    public static IServiceCollection AddAgentStudioModelLearning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IModelLearning, ProjectModelLearning>();
        return services;
    }
}
