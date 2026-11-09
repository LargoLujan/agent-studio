using AgentStudio.Application.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Observability.Analysis;

public static class ConsumptionAnalysisServiceCollectionExtensions
{
    /// <summary>
    /// Registers the two analyses of consumption (docs/17 D-34 and D-35, ADR-0052): the deterministic one
    /// (<see cref="IConsumptionAnalyzer"/>, <see cref="ConsumptionAnalyzer"/>) and the one with an agent
    /// (<see cref="IAssistedConsumptionAnalysis"/>, <see cref="AssistedConsumptionAnalysisService"/>, one instance, so a consent
    /// is spent once in the process). Registering it twice registers each once.
    /// </summary>
    /// <remarks>
    /// It needs what other modules register: the decision recorder and the evidence store (the persistence), the roster of
    /// agents, and the auxiliary runner (<c>IAuxiliaryAgentRunner</c>, which the composition registers with the providers); and
    /// a <see cref="TimeProvider"/>, the system's when there is none. The catalog of models of <paramref name="options"/> is the
    /// one the composition loaded (WP2); without it <c>model.oversized</c> says nothing.
    /// </remarks>
    public static IServiceCollection AddAgentStudioConsumptionAnalysis(this IServiceCollection services, ConsumptionAnalysisOptions? options = null, AssistedConsumptionOptions? assisted = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new ConsumptionAnalysisOptions());
        services.TryAddSingleton(assisted ?? new AssistedConsumptionOptions());
        services.TryAddSingleton<IConsumptionAnalyzer, ConsumptionAnalyzer>();
        services.TryAddSingleton<IAssistedConsumptionAnalysis, AssistedConsumptionAnalysisService>();
        return services;
    }
}
