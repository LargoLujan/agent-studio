using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;
using AgentStudio.Infrastructure.Graph;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Infrastructure.Graph;

/// <summary>docs/20 D-19 and D-21 (WP-G): the cache of the analyses by commit and the store of the relations an AI proposed, on disk in a folder of the test.</summary>
public sealed class GraphFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "as-graph-files-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly ProjectContext Project = new("0f8fad5b-d9cb-469f-a165-70867728950e", @"C:\proyecto");

    public void Dispose() => TempFolders.Delete(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CommitAnalysis Analysis(string? commit = null)
    {
        commit ??= CommitA;
        var graph = new CodeGraph(
            commit,
            1,
            [
                new GraphNode("S.slnx", GraphNodeKind.Solution, "S", "S.slnx", false),
                new GraphNode("a/A.csproj", GraphNodeKind.Project, "A", "a/A.csproj", true),
                new GraphNode("a/A.csproj|N.Núcleo", GraphNodeKind.Namespace, "N.Núcleo", null, false),
            ],
            [
                new GraphEdge("S.slnx", "a/A.csproj", GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
                new GraphEdge("a/A.csproj", "a/A.csproj|N.Núcleo", GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
            ],
            [new GraphGap("a/A.csproj", GraphGapCodes.Unevaluated, "a/A.csproj", 4)]);
        var files = new NamespaceFiles(new Dictionary<string, IReadOnlyList<string>> { ["a/A.csproj|N.Núcleo"] = ["a/Uno.cs", "a/Dos.cs"] });
        var report = new ConsistencyReport(
            commit,
            [new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs/README.md", 3, [new DoctorFact("link", "a/missing.md")])],
            [ConsistencyCodes.RequirementUncovered],
            truncated: false);
        return new CommitAnalysis(graph, report, files);
    }

    [Fact]
    public async Task An_analysis_is_kept_by_commit_and_reads_back_equal_with_its_files_and_its_discrepancies()
    {
        var cache = new CommitAnalysisFileCache(_root);
        var analysis = Analysis();

        await cache.SaveAsync(Project, new CachedAnalysis(analysis, ConsistencyChecked: true), Ct);
        var back = await cache.LoadAsync(Project, CommitA, Ct);

        Assert.NotNull(back);
        Assert.True(back.ConsistencyChecked);
        Assert.Equal(analysis.Graph, back.Analysis.Graph);
        Assert.Equal(analysis.Graph.ModelHash, back.Analysis.Graph.ModelHash);
        Assert.Equal(analysis.Consistency, back.Analysis.Consistency);
        Assert.Equal(["a/Dos.cs", "a/Uno.cs"], back.Analysis.Files.For("a/A.csproj|N.Núcleo"));
        Assert.True(File.Exists(Path.Combine(_root, Project.ProjectId, CommitA + ".json")));
        Assert.Null(await cache.LoadAsync(Project, CommitB, Ct));
    }

    [Fact]
    public async Task A_file_that_does_not_read_back_is_not_an_analysis_and_is_left_as_it_is()
    {
        var cache = new CommitAnalysisFileCache(_root);
        await cache.SaveAsync(Project, new CachedAnalysis(Analysis(), true), Ct);
        var path = Path.Combine(_root, Project.ProjectId, CommitA + ".json");
        var good = await File.ReadAllTextAsync(path, Ct);

        foreach (var bad in new[] { "", "not json", "[]", good.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal), good.Replace("\"consistencyChecked\"", "\"extra\":1,\"consistencyChecked\"", StringComparison.Ordinal), good.Replace(CommitA, CommitB, StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(path, bad, Ct);

            Assert.Null(await cache.LoadAsync(Project, CommitA, Ct));
            Assert.Equal(bad, await File.ReadAllTextAsync(path, Ct));
        }
    }

    [Fact]
    public async Task A_project_id_that_is_not_plain_never_makes_a_path()
    {
        var cache = new CommitAnalysisFileCache(_root);
        var hostile = new ProjectContext(@"..\..\elsewhere", @"C:\proyecto");

        await cache.SaveAsync(hostile, new CachedAnalysis(Analysis(), false), Ct);

        Assert.DoesNotContain(Directory.EnumerateDirectories(_root), d => d.Contains("elsewhere", StringComparison.Ordinal));
        Assert.Single(Directory.EnumerateDirectories(_root));
        Assert.NotNull(await cache.LoadAsync(hostile, CommitA, Ct));
    }

    // ---- the store of inferred relations ----

    private static GraphEdge Inferred(string from, string to, string reason = "porque sí") =>
        new(from, to, GraphEdgeKind.NamespaceUse, GraphEdgeOrigin.Inferred, new GraphInference("relaciones-grafo", "haiku", new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), reason));

    [Fact]
    public async Task The_relations_of_an_ai_are_kept_apart_listed_back_with_who_proposed_them_and_never_twice()
    {
        var store = new GraphInferenceFileStore(_root);

        await store.AddAsync(Project, [Inferred("a|X", "b|Y")], Ct);
        await store.AddAsync(Project, [Inferred("a|X", "b|Y", "otro motivo"), Inferred("b|Y", "a|X")], Ct);
        var all = await store.ListAsync(Project, Ct);

        Assert.Equal([("a|X", "b|Y", "porque sí"), ("b|Y", "a|X", "porque sí")], all.Select(e => (e.From, e.To, e.Inference!.Reason)));
        Assert.All(all, e => Assert.Equal(GraphEdgeOrigin.Inferred, e.Origin));
        Assert.Equal(("relaciones-grafo", "haiku"), (all[0].Inference!.Agent, all[0].Inference!.Model));
    }

    [Fact]
    public async Task A_static_relation_is_not_kept_by_the_store_and_a_file_that_does_not_read_back_is_an_empty_list()
    {
        var store = new GraphInferenceFileStore(_root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Project, [new GraphEdge("a", "b", GraphEdgeKind.NamespaceUse, GraphEdgeOrigin.Static)], Ct));
        await store.AddAsync(Project, [Inferred("a|X", "b|Y")], Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, Project.ProjectId, "inferred.json"), "[{\"from\":1}]", Ct);

        Assert.Empty(await store.ListAsync(Project, Ct));
    }

    [Fact]
    public async Task The_store_keeps_only_the_newest_thousand_relations()
    {
        var store = new GraphInferenceFileStore(_root);

        await store.AddAsync(Project, [.. Enumerable.Range(0, GraphInferenceFileStore.MaxRelations + 5).Select(i => Inferred("a|X" + i, "b|Y"))], Ct);
        var all = await store.ListAsync(Project, Ct);

        Assert.Equal(GraphInferenceFileStore.MaxRelations, all.Count);
        Assert.Equal("a|X1004", all[^1].From);
    }
}
