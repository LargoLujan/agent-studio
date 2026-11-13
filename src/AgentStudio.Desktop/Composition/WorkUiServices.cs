using AgentStudio.Application.Coordination;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The composition of «Trabajo» (docs/16 §4.6, ADR-0038 to ADR-0040): the budget of the project, kept on this computer and read by the
/// gate of the engine, the ports of the screen over whatever the rest of the composition registered, and the document of the shell.
/// </summary>
internal static class WorkUiServices
{
    /// <summary>
    /// Registers <see cref="LocalProjectBudgetStore"/> as the <see cref="IProjectBudgetStore"/> (the orchestrator takes it as the optional
    /// port of its gate, so a budget the person saves is what the engine checks before it launches an attempt), the
    /// <see cref="WorkBackend"/> and the <see cref="WorkDocumentViewModel"/>. What is registered earlier is kept, so tests put doubles first.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="budgetsFolder">Where the budget of each project lives; by default <see cref="LocalProjectBudgetStore.DefaultFolder"/>.</param>
    internal static IServiceCollection AddWorkUiServices(this IServiceCollection services, string? budgetsFolder = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IProjectBudgetStore>(_ => new LocalProjectBudgetStore(budgetsFolder ?? LocalProjectBudgetStore.DefaultFolder));
        services.TryAddSingleton(provider => new WorkBackend(
            provider.GetService<IProjectWorkspace>(),
            provider.GetService<IMilestoneService>(),
            provider.GetService<IMilestoneCloser>(),
            provider.GetService<IBacklogService>(),
            provider.GetService<IBoardProjector>(),
            provider.GetService<ITraceLinkService>(),
            provider.GetService<ITraceSuggester>(),
            provider.GetService<ITraceChainReader>(),
            provider.GetService<IParticipationProjector>(),
            provider.GetService<IProjectBudgetStore>(),
            provider.GetService<IAgentRoster>(),
            provider.GetService<IRunRegistry>(),
            provider.GetService<IDebateCoordinator>(),
            provider.GetService<IEvidenceStore>(),
            provider.GetService<IVerdictStore>(),

            // Phase 10 (docs/21 D-29): the pull requests the log follows, so a milestone says «desplegado» only on a report of the platform.
            provider.GetService<AgentStudio.Application.Hosting.IPullRequestProjector>()));
        services.TryAddSingleton(provider => new WorkDocumentViewModel(
            provider.GetRequiredService<WorkBackend>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
