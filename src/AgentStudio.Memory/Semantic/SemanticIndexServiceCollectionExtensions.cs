using AgentStudio.Application.Semantic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Memory.Semantic;

public static class SemanticIndexServiceCollectionExtensions
{
    /// <summary>
    /// Registers the semantic index of the projects (<see cref="ISemanticIndex"/>, docs/17 D-01 to D-06): one
    /// <see cref="SemanticIndex"/>, so one refresh of a project runs at a time. It needs the memory
    /// (<c>AddAgentStudioProjectMemory</c>), the store of the index and the local model (<see cref="ISemanticIndexStore"/> and
    /// <see cref="IEmbedderSource"/>, Infrastructure), which this method does not register. Once it is registered, the
    /// context packer ranks its tiers by relevance with it. The system clock is registered unless a
    /// <see cref="TimeProvider"/> already is, and anything registered before for the index is kept.
    /// </summary>
    public static IServiceCollection AddAgentStudioSemanticIndex(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISemanticIndex, SemanticIndex>();
        return services;
    }
}
