using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Graph;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Services;
using AgentStudio.Memory.Consistency;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Desktop.Docs;

/// <summary>
/// «Diagramas» and «Discrepancias» in the composition the app builds at startup (docs/20 section 9.7): the registrations are factories that a check of the container does not
/// validate and the sections are built with services that may be missing (they then say they are not available), so this is the proof that the real composition gives them
/// everything: the document holds «Grafo» and the two sections in order, both are available, and the checks, the service that runs them and the line of Auto Doctor are there.
/// </summary>
public sealed class DocsCompositionTests
{
    private static ServiceProvider Build(TempDirectory temp)
    {
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void The_document_of_auto_graph_holds_the_three_sections_in_order_and_the_two_of_this_package_are_available()
    {
        using var temp = new TempDirectory();
        using var provider = Build(temp);

        var document = provider.GetRequiredService<GraphDocumentViewModel>();

        Assert.Collection(
            document.Sections,
            first => Assert.IsType<GraphMapSection>(first),
            second => Assert.True(Assert.IsType<DocsDiagramsSection>(second).IsAvailable),
            third => Assert.True(Assert.IsType<DocsDiscrepanciesSection>(third).IsAvailable));
    }

    [Fact]
    public void The_catalog_the_checks_the_service_and_the_line_of_auto_doctor_resolve_and_the_catalog_lives_under_the_local_folder_of_the_app()
    {
        using var temp = new TempDirectory();
        using var provider = Build(temp);

        Assert.NotNull(provider.GetRequiredService<IDiagramCatalog>());
        Assert.Same(provider.GetRequiredService<IDiagramCatalog>(), provider.GetRequiredService<IDiagramTextReader>());
        Assert.NotNull(provider.GetRequiredService<ICommitAnalysisService>());
        Assert.IsType<ConsistencyService>(provider.GetRequiredService<IConsistencyService>());
        Assert.Equal(
            ConsistencyCodes.All.Order(StringComparer.Ordinal).ToArray(),
            provider.GetServices<IConsistencyCheck>().Select(c => c.Code).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(provider.GetServices<IDoctorCheckSource>(), s => s is DiscrepanciesCheckSource);
        Assert.Equal(4, provider.GetServices<IDiagramGenerator>().Count());

        // Nothing was written to the machine of the test while the container was built: the cache is created when the first diagram is.
        Assert.False(System.IO.Directory.Exists(temp.Combine("local", "diagrams")));
    }
}
