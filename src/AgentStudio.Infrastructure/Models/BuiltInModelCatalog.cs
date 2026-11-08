using AgentStudio.Application.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Models;

/// <summary>
/// The catalog of models this version of the app ships (<see cref="ModelCatalogData"/>), read once and checked
/// (<see cref="ModelCatalogReader"/>). It never goes to the network: the list of models of Codex through its app-server
/// (<c>model/list</c>) is put off until it is measured (docs/17 D-23).
/// </summary>
public sealed class BuiltInModelCatalog : IModelCatalogSource
{
    private static readonly Lazy<ModelCatalogDocument> Document = new(() => ModelCatalogReader.Read(ModelCatalogData.Json), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The catalog of this version, read the first time it is asked for.</summary>
    public static ModelCatalogDocument Shipped => Document.Value;

    /// <inheritdoc />
    public ModelCatalogDocument Current => Shipped;
}

/// <summary>Registration of the catalog of models (docs/17 D-23, WP2), for the composition root to call.</summary>
public static class ModelCatalogServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IModelCatalogSource"/> as the catalog the app ships. Registering it twice registers it once.</summary>
    public static IServiceCollection AddAgentStudioModelCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IModelCatalogSource, BuiltInModelCatalog>();
        return services;
    }
}
