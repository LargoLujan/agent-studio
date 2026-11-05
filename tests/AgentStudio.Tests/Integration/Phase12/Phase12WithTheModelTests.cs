using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Memory.Semantic;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase12;

/// <summary>
/// The app as it composes itself with the real model on disk (multilingual-e5-small, in the folder
/// <c>AGENTSTUDIO_E5_MODEL_DIR</c> names): the upkeep opens it ahead and refreshes the index with its vectors, the search and
/// the pack go by meaning and by words, and the snapshot of the planner's tool carries the vectors. Skipped without that
/// variable: nothing is ever downloaded here.
/// </summary>
public sealed class Phase12WithTheModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentDefinition Agent(string id, AgentRole role) => new(
        new AgentId(id), id, role, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), "i", [], ["Read", "Grep", "Glob"], [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.ReadSelected, new ReviewPolicy(false, false));

    [Fact]
    public async Task With_the_model_the_app_refreshes_the_index_with_its_vectors_and_searches_and_packs_by_meaning()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(LocalModel.DirectoryVariable) is { Length: > 0 } && LocalModel.IsOnDisk(Path.GetTempPath(), out _),
            $"Set {LocalModel.DirectoryVariable} to the folder of the model to run the app with it.");
        LocalModel.IsOnDisk(Path.GetTempPath(), out var modelDirectory);
        using var temp = new TempDirectory();
        var services = new ServiceCollection();
        services.AddWindowsDoubles();

        // The model where the variable says, instead of the folder of the app: registered first, the composition keeps it.
        services.AddSingleton(new EmbeddingModelLocation(modelDirectory, EmbeddingModels.MultilingualE5Small));
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var root = temp.CreateDirectory("calc");
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        var red = await provider.GetRequiredService<IProjectMemory>().RecordAsync(
            project,
            new MemoryDraft(MemoryKind.KnownError, "Facturas", "Test en rojo en el módulo de facturas.", MemoryOrigin.System, "nota:1"),
            Ct);
        await provider.GetRequiredService<IProjectMemory>().RecordAsync(
            project,
            new MemoryDraft(MemoryKind.Decision, "Interfaz", "Los botones de la barra superior usan iconos de Segoe Fluent.", MemoryOrigin.System, "nota:2"),
            Ct);
        var upkeep = provider.GetRequiredService<SemanticIndexUpkeep>();

        Assert.True(await upkeep.WarmUpAsync().WaitAsync(TimeSpan.FromMinutes(1), Ct));
        var refresh = await upkeep.RefreshAsync(project).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.True(refresh!.Completed, refresh.Reason);
        Assert.True(refresh.Embedded >= 2, $"Only {refresh.Embedded} fragments were embedded.");

        var found = await provider.GetRequiredService<ISemanticIndex>().SearchAsync(project, new SemanticQuery("pruebas que fallan"), Ct);
        Assert.Equal(SemanticSearchMode.Hybrid, found.Mode);
        Assert.Equal(red.Id, found.Hits[0].Entry?.Id);

        var pack = await provider.GetRequiredService<IContextPacker>().PackAsync(project, new ContextPackRequest(Agent("ejecutor", AgentRole.Executor), "pruebas que fallan", [], 10_000), Ct);
        Assert.Equal(SemanticSearchMode.Hybrid, pack.Relevance?.Mode);
        Assert.Contains(pack.Items, i => i.Id == red.Id);

        using var offer = await provider.GetRequiredService<IProjectHistoryTool>().OfferAsync(
            project, Agent("orquestador", AgentRole.Orchestrator), new(false, false, false, ["Read", "Grep", "Glob"], [], []), Ct);
        var snapshot = ProjectHistorySnapshotJson.Read(await File.ReadAllTextAsync(offer.Server!.Arguments[1], Ct));
        Assert.Equal(EmbeddingModels.MultilingualE5Small.Dimensions, snapshot.Dimensions);
        Assert.All(snapshot.Fragments, f => Assert.Equal(snapshot.Dimensions, f.Vector?.Length));
    }
}
