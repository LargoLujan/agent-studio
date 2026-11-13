using AgentStudio.Application.Consistency;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Models;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Graph;
using AgentStudio.Infrastructure.Graph;
using AgentStudio.Orchestration.Graph;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The composition of Auto Graph (docs/20 D-18 to D-22, ADR-0062): the copy of a commit over the isolated work area and the git layer, the host of the analysis
/// process (which ships next to the app), the analysis by commit with its cache, the relations an AI proposed, the index of tasks, the section «Grafo» and the
/// document of the shell. What is registered earlier is kept, so tests put doubles first. The analysis is composed only when its parts are; without them the
/// section says it is not available, and «Proponer relaciones» only when the auxiliary runner and the register of OKs are.
/// </summary>
internal static class GraphUiServices
{
    /// <param name="services">The service collection.</param>
    /// <param name="graphFolder">Where the analyses and the relations of the AI are kept: <c>%LOCALAPPDATA%\AgentStudio\graph</c>.</param>
    internal static IServiceCollection AddGraphUiServices(this IServiceCollection services, string graphFolder)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphFolder);
        services.TryAddSingleton<ICommitAnalysisCache>(_ => new CommitAnalysisFileCache(graphFolder));
        services.TryAddSingleton<IGraphInferenceStore>(_ => new GraphInferenceFileStore(graphFolder));
        services.TryAddSingleton<IGraphWorkerLauncher>(_ => new LowIntegrityGraphWorkerLauncher());
        services.TryAddSingleton<ICodeGraphBuilder>(provider => new GraphWorkerHost(provider.GetRequiredService<IGraphWorkerLauncher>()));
        services.TryAddSingleton<IRepositorySnapshots>(provider => new RepositorySnapshots(
            provider.GetRequiredService<IIsolatedWorkAreaFactory>(),
            provider.GetRequiredService<IWorktreeManager>()));

        // The checks of the discrepancies (WP-D) are optional: without them the analysis says they were not checked, and it is calculated again when they exist.
        services.TryAddSingleton<ICommitAnalysisService>(provider => new CommitAnalysisService(
            provider.GetRequiredService<IRepositorySnapshots>(),
            provider.GetRequiredService<ICodeGraphBuilder>(),
            provider.GetRequiredService<ICommitAnalysisCache>(),
            provider.GetService<IConsistencyService>()));
        services.TryAddSingleton<IGraphTaskIndex>(provider => new GraphTaskIndex(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<IPlanStore>()));
        services.TryAddSingleton<IGraphRelationProposer>(provider => new GraphInferenceService(
            provider.GetRequiredService<IAuxiliaryAgentRunner>(),
            provider.GetRequiredService<IQuotaConsentLedger>(),
            provider.GetRequiredService<IGraphInferenceStore>(),
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<IModelCatalogSource>()));
        services.TryAddSingleton(provider => new GraphBackend(
            Optional<ICommitAnalysisService>(provider),
            provider.GetService<IRepositoryHistoryReader>(),
            provider.GetService<IGraphInferenceStore>(),
            Optional<IGraphTaskIndex>(provider),
            Optional<IGraphRelationProposer>(provider),
            provider.GetService<IUsageEstimator>()));
        services.TryAddSingleton(provider => new GraphMapSection(provider.GetRequiredService<GraphBackend>(), provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton(provider => new GraphDocumentViewModel([provider.GetRequiredService<GraphMapSection>()], provider.GetService<IProjectWorkspace>()));
        return services;
    }

    /// <summary>A service built from other services that the composition may not have: null when one of them is missing, never an error at startup.</summary>
    private static T? Optional<T>(IServiceProvider provider)
        where T : class
    {
        try
        {
            return provider.GetService<T>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
