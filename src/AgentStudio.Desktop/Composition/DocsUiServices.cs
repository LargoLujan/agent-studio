using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Graph;
using AgentStudio.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The composition of the diagrams and the discrepancies (docs/20 D-23 to D-30, ADR-0045): the cache of the diagrams and the checks of Memory, the line of Auto Doctor that counts
/// the discrepancies, and the two sections «Diagramas» and «Discrepancias» of the document Auto Graph (they are <see cref="GraphSection"/>s, which
/// <c>GraphUiServices</c> hands to the document after «Grafo»). What is registered earlier is kept, so tests put doubles first.
/// </summary>
internal static class DocsUiServices
{
    /// <param name="services">The service collection.</param>
    /// <param name="diagramsFolder">Where the cache of the diagrams lives: <c>%LOCALAPPDATA%\AgentStudio\diagrams</c>.</param>
    internal static IServiceCollection AddDocsUiServices(this IServiceCollection services, string diagramsFolder)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagramsFolder);
        services.AddAgentStudioDiagrams(diagramsFolder);
        services.TryAddSingleton<IDocsClipboard, SystemDocsClipboard>();
        services.TryAddSingleton(provider => new DocsBackend(
            Optional<IDiagramCatalog>(provider),
            Optional<IDiagramTextReader>(provider),
            Optional<ICommitAnalysisService>(provider),
            provider.GetService<IRepositoryHistoryReader>(),
            provider.GetService<IDocsClipboard>()));
        services.AddSingleton<GraphSection>(provider => new DocsDiagramsSection(provider.GetRequiredService<DocsBackend>()));
        services.AddSingleton<GraphSection>(provider => new DocsDiscrepanciesSection(provider.GetRequiredService<DocsBackend>()));
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
