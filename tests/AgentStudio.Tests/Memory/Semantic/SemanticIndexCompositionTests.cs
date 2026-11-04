using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory;
using AgentStudio.Memory.Semantic;
using AgentStudio.Security;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory.Store;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// The semantic index as the composition root registers it (<see cref="SemanticIndexServiceCollectionExtensions.AddAgentStudioSemanticIndex"/>)
/// over the real memory: once it is registered the context packer ranks by it, and without it the packer still builds and
/// ranks by words, as before (F12-7).
/// </summary>
public sealed class SemanticIndexCompositionTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly TestSemanticStore _store = new();

    public void Dispose()
    {
        _store.Dispose();
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentDefinition Executor => new(
        Aid("ejecutor"), "Ejecutor", AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), "i", [], [], [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.ReadSelected, new ReviewPolicy(false, false));

    private ServiceCollection Services(bool withIndex)
    {
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(_anchors));
        services.AddAgentStudioSecurity();
        services.AddAgentStudioProjectMemory();
        services.AddSingleton<IRunActivity>(new FakeRunActivity());
        if (withIndex)
        {
            services.AddSingleton<ISemanticIndexStore>(_store);
            services.AddSingleton<IEmbedderSource>(new TestEmbedderSource(new TestEmbedder(TestModels.Hashing, IndexWorld.Concepts)));
            services.AddAgentStudioSemanticIndex();
        }

        return services;
    }

    [Fact]
    public async Task With_the_index_registered_the_packer_brings_by_meaning_what_words_alone_miss()
    {
        await using var provider = Services(withIndex: true).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);
        var index = provider.GetRequiredService<ISemanticIndex>();
        var red = await provider.GetRequiredService<IProjectMemory>().RecordAsync(project, new MemoryDraft(MemoryKind.KnownError, "Facturas", "Test en rojo en el módulo de facturas.", MemoryOrigin.System, "nota:1"), Ct);
        await index.RefreshAsync(project, Ct);

        var pack = await provider.GetRequiredService<IContextPacker>().PackAsync(project, new ContextPackRequest(Executor, "pruebas que fallan", [], 1000), Ct);

        Assert.IsType<SemanticIndex>(index);
        Assert.Same(index, provider.GetRequiredService<ISemanticIndex>());
        Assert.Equal([red.Id], pack.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Without_the_index_the_packer_still_builds_and_ranks_by_words()
    {
        await using var provider = Services(withIndex: false).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);
        var red = await provider.GetRequiredService<IProjectMemory>().RecordAsync(project, new MemoryDraft(MemoryKind.KnownError, "Facturas", "Test en rojo en el módulo de facturas.", MemoryOrigin.System, "nota:1"), Ct);
        var packer = provider.GetRequiredService<IContextPacker>();

        var byMeaning = await packer.PackAsync(project, new ContextPackRequest(Executor, "pruebas que fallan", [], 1000), Ct);
        var byWords = await packer.PackAsync(project, new ContextPackRequest(Executor, "facturas", [], 1000), Ct);

        Assert.Null(provider.GetService<ISemanticIndex>());
        Assert.Empty(byMeaning.Items);
        Assert.Equal([red.Id], byWords.Items.Select(i => i.Id));
    }
}
