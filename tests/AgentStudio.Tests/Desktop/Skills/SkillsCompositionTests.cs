using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Skills;
using AgentStudio.Infrastructure;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Memory;
using AgentStudio.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>
/// The composition of the Skills screen that the integration adds with one line each: the screen over whatever ports are
/// registered, and the assisted recommendation (its folders, the path of the MCP server, the auxiliary runner and the
/// service). Nothing is run here: the services are only resolved.
/// </summary>
public sealed class SkillsCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_screen_alone_is_composed_and_says_what_is_missing()
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddAgentStudioSkillsScreen();
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(SkillsBackend.None, provider.GetRequiredService<SkillsBackend>());
        var document = provider.GetRequiredService<ISkillsDocumentFactory>().Create(null);
        await document.OpenAsync(Ct);

        Assert.Contains(document.Notices, n => n.Contains("catálogo", StringComparison.Ordinal));
        Assert.Contains(document.Notices, n => n.Contains("recomendación asistida", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_platform_every_piece_of_the_screen_and_of_the_assisted_recommendation_is_there()
    {
        using var temp = new TempDirectory();
        var recommendations = temp.Combine("skill-recommendations");
        const string Server = @"C:\Program Files\AgentStudio\AgentStudio.SkillsMcp.exe";
        var services = new ServiceCollection()
            .AddAgentStudioInfrastructure()
            .AddAgentStudioMemory()
            .AddAgentStudioPersistence(new DecisionLogAnchorOptions(temp.Combine("anchors")))
            .AddAgentStudioSkills(new SkillCatalogOptions(null), new SkillActivationStoreOptions(temp.Combine("skill-activations.jsonl")))
            .AddAgentStudioProviders()
            .AddSingleton<IApprovalPrompt>(new FakePrompt())
            .AddAgentStudioSkillRecommendation(recommendations, Server)
            .AddAgentStudioSkillsScreen();
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<SkillRecommendationService>(provider.GetRequiredService<ISkillRecommendationService>());
        Assert.IsType<AuxiliaryAgentRunner>(provider.GetRequiredService<IAuxiliaryAgentRunner>());
        Assert.Equal(recommendations, Assert.IsType<SkillRecommendationFolders>(provider.GetRequiredService<ISkillRecommendationFolders>()).Root);
        Assert.Equal(Server, provider.GetRequiredService<SkillRecommendationOptions>().ServerExecutable);

        var backend = provider.GetRequiredService<SkillsBackend>();
        Assert.NotNull(backend.Catalog);
        Assert.NotNull(backend.Activations);
        Assert.NotNull(backend.Roster);
        Assert.IsType<SkillHealthReader>(backend.Health);
        Assert.NotNull(backend.Recommendations);
        Assert.IsAssignableFrom<IProjectWorkspace>(backend.Workspace);
        Assert.IsType<FakePrompt>(backend.Prompt);

        // Without an index the catalog is empty, and the screen says so with the catalog's own warning.
        var report = await backend.Catalog!(Ct);
        Assert.Empty(report.Skills);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public void An_auxiliary_runner_the_composition_root_already_registered_is_kept()
    {
        var services = new ServiceCollection().AddSingleton<IAuxiliaryAgentRunner, NoRunner>();

        services.AddAgentStudioSkillRecommendation(@"C:\local\skill-recommendations", @"C:\app\AgentStudio.SkillsMcp.exe");

        Assert.Equal(typeof(NoRunner), Assert.Single(services, d => d.ServiceType == typeof(IAuxiliaryAgentRunner)).ImplementationType);
    }

    [Fact]
    public void By_default_the_server_is_expected_next_to_the_app()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "AgentStudio.SkillsMcp.exe"), SkillsScreenServices.DefaultServerExecutable);
    }

    private sealed class NoRunner : IAuxiliaryAgentRunner
    {
        public Task<AuxiliaryRunResult> RunAsync(ProjectContext project, AuxiliaryRunRequest request, IProgress<AgentEvent>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Not used.");
    }
}
