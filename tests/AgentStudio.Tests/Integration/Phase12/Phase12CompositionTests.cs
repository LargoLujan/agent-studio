using System.IO;
using System.Reflection;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Skills;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Context;
using AgentStudio.Memory.Semantic;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Engine = AgentStudio.Orchestration.Engine;

namespace AgentStudio.Tests.Integration.Phase12;

/// <summary>
/// The composition of Phase 12 (docs/17 §9.2, «Integración»; ADR-0047) in the app as it composes itself
/// (<see cref="AppComposition.AddAgentStudioApp"/>, what App.OnStartup calls), over temporary folders: one store of the
/// index, the index of WP3 over the local model of WP2, the installer, the planner's tool given to the engine, «Memoria»
/// searching through the index, and its upkeep. Without the model the app builds, packs and searches by words and says so,
/// and nothing is downloaded. Nothing is shown, no app is created and no CLI is launched.
/// </summary>
public sealed class Phase12CompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AppFolders FoldersIn(TempDirectory temp) =>
        new(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null);

    private static ServiceProvider Build(AppFolders folders)
    {
        var services = new ServiceCollection();
        services.AddWindowsDoubles();

        // The folders of the composition in %LOCALAPPDATA% go to the temporary folder (Windows denies the real one at Low integrity).

        services.AddTestLocalData(folders.Local.BaseDirectory);
        services.AddAgentStudioApp(folders);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static AgentDefinition Agent(string id, AgentRole role) => new(
        new AgentId(id), id, role, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), "i", [], ["Read", "Grep", "Glob"], [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.ReadSelected, new ReviewPolicy(false, false));

    [Fact]
    public void The_index_its_store_its_model_the_installer_and_the_planners_tool_are_composed_once_under_the_local_folders()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        using var provider = Build(folders);

        // One store: its single writer belongs to the instance.
        var store = Assert.IsType<SemanticIndexStore>(provider.GetRequiredService<ISemanticIndexStore>());
        Assert.Same(store, provider.GetRequiredService<ISemanticIndexStore>());
        Assert.Same(store, provider.GetRequiredService<SemanticIndexStore>());
        Assert.Equal(temp.Path, provider.GetRequiredService<SemanticIndexOptions>().LocalAppData);
        Assert.Equal(temp.Path, folders.SemanticRoot);

        var index = Assert.IsType<SemanticIndex>(provider.GetRequiredService<ISemanticIndex>());
        Assert.Same(index, provider.GetRequiredService<ISemanticIndex>());
        var model = Assert.IsType<OnnxEmbedderSource>(provider.GetRequiredService<IEmbedderSource>());
        Assert.Equal(EmbeddingModelLocation.Under(temp.Path, EmbeddingModels.MultilingualE5Small), model.Location);
        var installer = Assert.IsType<HttpEmbeddingModelInstaller>(provider.GetRequiredService<IEmbeddingModelInstaller>());
        Assert.Equal(model.Location, installer.Location);
        Assert.IsType<ContextPacker>(provider.GetRequiredService<IContextPacker>());

        // The planner's tool, over the MCP server next to the app, and the snapshots under the local data of the app.
        var tool = Assert.IsType<ProjectHistoryTool>(provider.GetRequiredService<IProjectHistoryTool>());
        Assert.Equal(SkillsScreenServices.DefaultServerExecutable, provider.GetRequiredService<ProjectHistoryToolOptions>().ServerExecutable);
        Assert.Equal(Path.Combine(folders.Local.BaseDirectory, "history-snapshots"), Assert.IsType<ProjectHistoryFolders>(provider.GetRequiredService<IProjectHistoryFolders>()).Root);
        Assert.Same(tool, HistoryOf(Assert.IsType<Engine.Orchestrator>(provider.GetRequiredService<IOrchestrator>())));

        // «Memoria» searches through the index, its upkeep refreshes it, and the upkeep of the memory asks it to.
        var memory = provider.GetRequiredService<MemoryViewModel>();
        Assert.True(memory.Model.IsIndexAvailable);
        Assert.True(provider.GetRequiredService<SemanticIndexUpkeep>().IsAvailable);
        Assert.True(provider.GetRequiredService<ProjectMemoryUpkeep>().IsAvailable);
        Assert.NotNull(provider.GetRequiredService<MainViewModel>());

        // Composing wrote nothing: no index, no model, no snapshot.
        Assert.False(Directory.Exists(Path.Combine(temp.Path, SemanticIndexLocation.AppFolder)), "Composing the app wrote the index or the model.");
        Assert.False(Directory.Exists(Path.Combine(folders.Local.BaseDirectory, "history-snapshots")));
    }

    [Fact]
    public void For_the_current_user_the_index_and_the_model_are_those_of_WP2()
    {
        var folders = AppFolders.ForCurrentUser();

        Assert.Equal(SemanticIndexOptions.ForCurrentUser().LocalAppData, folders.SemanticRoot);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folders.SemanticRoot);
        Assert.Equal(EmbeddingModelLocation.ForCurrentUser(), EmbeddingModelLocation.Under(folders.SemanticRoot, EmbeddingModels.MultilingualE5Small));
        Assert.Equal(Path.Combine(folders.SemanticRoot, "AgentStudio"), folders.Local.BaseDirectory);
    }

    [Fact]
    public async Task The_switch_in_Ajustes_turns_the_history_search_of_the_orchestrator_off_for_the_next_planning_without_touching_code()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        await using var provider = Build(folders);
        var root = temp.CreateDirectory("calc");
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        await provider.GetRequiredService<IProjectMemory>().RecordAsync(
            project,
            new MemoryDraft(MemoryKind.Decision, "Calc", "Usamos decimal en Calc.", MemoryOrigin.System, "nota:1"),
            Ct);
        var tool = provider.GetRequiredService<IProjectHistoryTool>();
        var orchestrator = Agent("orquestador", AgentRole.Orchestrator);
        var readOnly = new AgentStudio.Application.Providers.PermissionProfile(false, false, false, ["Read", "Grep", "Glob"], [], []);
        var settings = provider.GetRequiredService<AgentStudio.Desktop.Settings.SettingsDocumentViewModel>();

        // On by default: the planner gets it.
        Assert.True(settings.Memory!.PlannerHistoryToolEnabled);
        using (var on = await tool.OfferAsync(project, orchestrator, readOnly, Ct))
        {
            Assert.True(on.Offered, on.Withheld);
        }

        // Turned off and saved in «Ajustes»: the next planning goes without it, and says why.
        settings.Memory.PlannerHistoryToolEnabled = false;
        settings.SaveCommand.Execute(null);
        Assert.Null(settings.ErrorMessage);
        using (var off = await tool.OfferAsync(project, orchestrator, readOnly, Ct))
        {
            Assert.Equal((false, ProjectHistoryTool.DisabledReason), (off.Offered, off.Withheld));
        }

        Assert.False(provider.GetRequiredService<IAppSettingsStore>().Load().PlannerHistoryToolEnabled);
    }

    [Fact]
    public async Task Without_the_model_the_app_starts_packs_and_searches_by_words_says_so_and_downloads_nothing()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        await using var provider = Build(folders);
        var root = temp.CreateDirectory("calc");
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        var entry = await provider.GetRequiredService<IProjectMemory>().RecordAsync(
            project,
            new MemoryDraft(MemoryKind.KnownError, "Facturas", "Test en rojo en el módulo de facturas.", MemoryOrigin.System, "nota:1"),
            Ct);

        // The upkeep refreshes the index without the model: the fragments go in, by their words, with no vector.
        var refresh = await provider.GetRequiredService<SemanticIndexUpkeep>().RefreshAsync(project).WaitAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.True(refresh!.Completed, refresh.Reason);
        Assert.Equal(0, refresh.Embedded);
        Assert.False(await provider.GetRequiredService<SemanticIndexUpkeep>().WarmUpAsync());

        // The index searches by words and says why.
        var found = await provider.GetRequiredService<ISemanticIndex>().SearchAsync(project, new SemanticQuery("facturas"), Ct);
        Assert.Equal((SemanticSearchMode.WordsOnly, WordsOnlyReason.ModelMissing), (found.Mode, found.Reason));
        Assert.Contains(found.Hits, h => h.Entry?.Id == entry.Id);

        // The packer of every agent with memory ranks by words, and its pack says so.
        var pack = await provider.GetRequiredService<IContextPacker>().PackAsync(project, new ContextPackRequest(Agent("ejecutor", AgentRole.Executor), "facturas", [], 10_000), Ct);
        Assert.Contains(pack.Items, i => i.Id == entry.Id);
        Assert.Equal(ContextRelevance.WordsOnly(WordsOnlyReason.ModelMissing), pack.Relevance);

        // «Memoria» opens the project, offers the download (only with the person's OK) and searches by words.
        var memory = provider.GetRequiredService<MemoryViewModel>();
        await memory.SetProjectAsync(root);
        memory.SearchText = "facturas";
        await memory.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Buscar por palabras", memory.SearchLabel);
        Assert.Equal(SemanticSearchTexts.WordsOnly(WordsOnlyReason.ModelMissing), memory.SearchNotice);
        Assert.Contains(memory.Results.OfType<MemoryEntryViewModel>(), r => r.Id == entry.Id);
        Assert.True(memory.Model.CanDownload);

        // Nothing was downloaded: the folder of the model does not even exist.
        Assert.False(Directory.Exists(provider.GetRequiredService<EmbeddingModelLocation>().ModelDirectory));
        var indexFile = provider.GetRequiredService<SemanticIndexStore>().IndexFileOf(project);
        Assert.StartsWith(Path.Combine(temp.Path, SemanticIndexLocation.AppFolder, SemanticIndexLocation.IndexFolder) + Path.DirectorySeparatorChar, indexFile, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(indexFile), "The index lives under the local folders of the test.");
    }

    /// <summary>The history tool of the ports of an orchestrator, which only its planner reads.</summary>
    private static object? HistoryOf(Engine.Orchestrator orchestrator)
    {
        var ports = typeof(Engine.Orchestrator).GetField("_ports", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(orchestrator)!;
        return ports.GetType().GetProperty("History")!.GetValue(ports);
    }
}
