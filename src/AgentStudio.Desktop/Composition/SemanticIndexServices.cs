using System.IO;
using System.Net.Http;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Memory;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Semantic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The Phase 12 composition (docs/17 §3 and §9.2, «Integración»; ADR-0047): the semantic index of the projects with its
/// store and its local model, the installer of the model, and the planner's tool <c>search_project_history</c>.
/// <list type="bullet">
/// <item>The store (<see cref="SemanticIndexStore"/>, WP2) is one singleton: its single writer belongs to the instance.
/// Its indexes live under <see cref="AppFolders.SemanticRoot"/> (<c>%LOCALAPPDATA%</c> for the current user).</item>
/// <item>The model (<see cref="OnnxEmbedderSource"/>) only ever reads its folder, and loads only files that give their
/// pinned hash (F12-6). The container builds it, so the container disposes it, and its ONNX session with it.</item>
/// <item>The installer (<see cref="HttpEmbeddingModelInstaller"/>) is the only part that reaches the network, and only when
/// the person confirms the download in «Memoria» (<see cref="EmbeddingModelViewModel"/>); nothing else calls it.</item>
/// <item><see cref="SemanticIndexServiceCollectionExtensions.AddAgentStudioSemanticIndex"/> (WP3) registers the index; with it,
/// the context packer of the memory ranks its tiers by relevance with the hybrid search (F12-7). Without the model,
/// everything goes by words, as before.</item>
/// <item>The planner's tool: the snapshots of what is trusted under <c>history-snapshots</c> of the local data of the app,
/// and the local MCP server next to the app, which serves them with <c>--history</c> to a read-only launch of the planner.</item>
/// </list>
/// Anything registered before for these ports is kept.
/// </summary>
internal static class SemanticIndexServices
{
    /// <summary>The folder of the snapshots of the planner's tool, under the local data of the app.</summary>
    public const string HistorySnapshotsFolder = "history-snapshots";

    internal static IServiceCollection AddSemanticIndexServices(this IServiceCollection services, AppFolders folders, string serverExecutable)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExecutable);
        var location = EmbeddingModelLocation.Under(folders.SemanticRoot, EmbeddingModels.MultilingualE5Small);

        services.TryAddSingleton(new SemanticIndexOptions(folders.SemanticRoot));
        services.TryAddSingleton<SemanticIndexStore>();
        services.TryAddSingleton<ISemanticIndexStore>(provider => provider.GetRequiredService<SemanticIndexStore>());
        services.TryAddSingleton(location);
        services.TryAddSingleton<IEmbedderSource>(provider => new OnnxEmbedderSource(provider.GetRequiredService<EmbeddingModelLocation>()));
        services.TryAddSingleton<ModelDownloadClient>();
        services.TryAddSingleton<IEmbeddingModelInstaller>(provider =>
            new HttpEmbeddingModelInstaller(provider.GetRequiredService<ModelDownloadClient>().Http, provider.GetRequiredService<EmbeddingModelLocation>()));
        services.AddAgentStudioSemanticIndex();

        services.TryAddSingleton<IProjectHistoryFolders>(new ProjectHistoryFolders(Path.Combine(folders.Local.BaseDirectory, HistorySnapshotsFolder)));
        services.TryAddSingleton(new ProjectHistoryToolOptions(serverExecutable));
        services.TryAddSingleton<IProjectHistoryTool, ProjectHistoryTool>();
        return services;
    }

    /// <summary>
    /// The HTTP client of the installer of the model, which belongs to whoever gives it (<see cref="HttpEmbeddingModelInstaller"/>):
    /// the container disposes it with the app. Its timeout covers only the wait for the headers of each file.
    /// </summary>
    internal sealed class ModelDownloadClient : IDisposable
    {
        public HttpClient Http { get; } = new();

        public void Dispose() => Http.Dispose();
    }
}
