using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Memory.Semantic;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory.Store;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// A project with its real memory and decision log (<see cref="MemoryWorld"/>), the semantic index over the test store and
/// a local model the test sets (<see cref="TestEmbedder"/> with a few shared concepts, so «pruebas que fallan» and «test en
/// rojo» are close in meaning while sharing no word).
/// </summary>
internal sealed partial class IndexWorld : IDisposable
{
    public static readonly IReadOnlyDictionary<string, string> Concepts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rojo"] = "fallo",
        ["fallan"] = "fallo",
        ["falla"] = "fallo",
        ["fallo"] = "fallo",
        ["test"] = "prueba",
        ["tests"] = "prueba",
        ["prueba"] = "prueba",
        ["pruebas"] = "prueba",
    };

    private int _sources;

    private IndexWorld(MemoryWorld world, EmbeddingModelState state, TestEmbedder? embedder, SemanticSearchTuning? tuning)
    {
        World = world;
        Embedder = embedder ?? new TestEmbedder(TestModels.Hashing, Concepts);
        Model = new TestEmbedderSource(Embedder, state);
        Index = new SemanticIndex(world.Memory, Store, Model, world.Clock, Embedder.Model, tuning ?? TestModels.Tuning);
    }

    public MemoryWorld World { get; }

    public TestEmbedder Embedder { get; }

    public TestEmbedderSource Model { get; }

    public TestSemanticStore Store { get; } = new();

    public SemanticIndex Index { get; }

    public ProjectContext Project => World.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<IndexWorld> CreateAsync(EmbeddingModelState state = EmbeddingModelState.Ready) => new(await MemoryWorld.CreateAsync(), state, null, null);

    /// <summary>A world whose model is <paramref name="embedder"/> and whose index has <paramref name="tuning"/>.</summary>
    public static async Task<IndexWorld> CreateAsync(TestEmbedder embedder, SemanticSearchTuning tuning) => new(await MemoryWorld.CreateAsync(), EmbeddingModelState.Ready, embedder, tuning);

    /// <summary>Another index over the same memory, store and model, with <paramref name="tuning"/>.</summary>
    public SemanticIndex With(SemanticSearchTuning tuning) => new(World.Memory, Store, Model, World.Clock, Embedder.Model, tuning);

    /// <summary>Records an entry one minute after the last one, citing a source of its own.</summary>
    public async Task<MemoryEntry> RecordAsync(MemoryKind kind, string topic, string content, MemoryOrigin origin = MemoryOrigin.System, string? supersedes = null)
    {
        World.Clock.Advance(TimeSpan.FromMinutes(1));
        var source = "nota:" + Interlocked.Increment(ref _sources);
        return await World.Memory.RecordAsync(Project, new MemoryDraft(kind, topic, content, origin, source, supersedes), Ct);
    }

    public Task<SemanticIndexRefresh> RefreshAsync() => Index.RefreshAsync(Project, Ct);

    public Task<SemanticSearchResult> SearchAsync(string text, int top = 10, bool trustedOnly = true, IReadOnlyList<MemoryKind>? kinds = null) =>
        Index.SearchAsync(Project, new SemanticQuery(text, top) { TrustedOnly = trustedOnly, Kinds = kinds }, Ct);

    /// <summary>The ids of the entries a search gives, in its order.</summary>
    public async Task<string[]> FoundAsync(string text, int top = 10, bool trustedOnly = true, IReadOnlyList<MemoryKind>? kinds = null) =>
        [.. (await SearchAsync(text, top, trustedOnly, kinds)).Hits.Select(h => h.Entry!.Id)];

    /// <summary>The ids of the fragments the index holds, in order.</summary>
    public async Task<string[]> HeldAsync() => [.. (await Store.ReadStateAsync(Project, Ct)).Select(s => s.Id)];

    /// <summary>Makes the decision log stop verifying: its anchor outside the project now says it had more entries.</summary>
    public async Task BreakTheLogAsync()
    {
        foreach (var anchor in Directory.GetFiles(World.AnchorFolder))
        {
            var json = await File.ReadAllTextAsync(anchor, Ct);
            var cut = EntryCount().Replace(json, m => $"\"entryCount\": {long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 7}");
            Assert.NotEqual(json, cut);
            await File.WriteAllTextAsync(anchor, cut, Ct);
        }
    }

    /// <summary>Rewrites the row of a memory entry behind the store, as whoever can drop its triggers could.</summary>
    public void RewriteMemoryRow(string entryId, string content)
    {
        using var connection = World.Instance.Connect();
        PersistenceTestData.Exec(connection, "DROP TRIGGER IF EXISTS memory_entries_no_update;");
        PersistenceTestData.Exec(connection, $"UPDATE memory_entries SET content = '{content.Replace("'", "''", StringComparison.Ordinal)}' WHERE id = '{entryId}';");
    }

    public void Dispose()
    {
        Store.Dispose();
        World.Dispose();
    }

    [GeneratedRegex("\"entryCount\": (\\d+)")]
    private static partial Regex EntryCount();
}
