using AgentStudio.Application.Consistency;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Graph;

/// <summary>The doubles of the section «Grafo» and the small solution they hold: a partial project, a relation read, one an AI proposed and two gaps.</summary>
internal static class GraphDesktopFakes
{
    public static readonly ProjectContext Project = new("p-graph", @"C:\work\calc");

    public static readonly string Commit = new('a', 40);

    public static CodeGraph Graph { get; } = new(
        Commit,
        1,
        [
            new GraphNode("Calc.slnx", GraphNodeKind.Solution, "Calc", "Calc.slnx", false),
            new GraphNode("src/Core/Core.csproj", GraphNodeKind.Project, "Core", "src/Core/Core.csproj", false),
            new GraphNode("src/App/App.csproj", GraphNodeKind.Project, "App", "src/App/App.csproj", true),
            new GraphNode("src/Web/Web.csproj", GraphNodeKind.Project, "Web", "src/Web/Web.csproj", false),
            new GraphNode("src/Core/Core.csproj|Calc.Core", GraphNodeKind.Namespace, "Calc.Core", null, false),
            new GraphNode("src/App/App.csproj|Calc.App", GraphNodeKind.Namespace, "Calc.App", null, false),
            new GraphNode("package:Json", GraphNodeKind.Package, "Json", null, false),
        ],
        [
            new GraphEdge("Calc.slnx", "src/Core/Core.csproj", GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
            new GraphEdge("Calc.slnx", "src/App/App.csproj", GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
            new GraphEdge("Calc.slnx", "src/Web/Web.csproj", GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
            new GraphEdge("src/Core/Core.csproj", "src/Core/Core.csproj|Calc.Core", GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
            new GraphEdge("src/App/App.csproj", "src/App/App.csproj|Calc.App", GraphEdgeKind.Contains, GraphEdgeOrigin.Static),
            new GraphEdge("src/App/App.csproj", "src/Core/Core.csproj", GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static),
            new GraphEdge("src/Web/Web.csproj", "src/App/App.csproj", GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Static),
            new GraphEdge("src/Core/Core.csproj", "package:Json", GraphEdgeKind.PackageReference, GraphEdgeOrigin.Static),
            new GraphEdge("src/App/App.csproj|Calc.App", "src/Core/Core.csproj|Calc.Core", GraphEdgeKind.NamespaceUse, GraphEdgeOrigin.Static),
        ],
        [
            new GraphGap("src/App/App.csproj", GraphGapCodes.Unevaluated, "src/App/App.csproj", 7),
            new GraphGap("src/App/App.csproj", GraphGapCodes.FileSkipped, "src/App/Big.cs", null),
        ]);

    public static readonly GraphEdge Proposed = new(
        "src/Web/Web.csproj",
        "src/Core/Core.csproj",
        GraphEdgeKind.ProjectReference,
        GraphEdgeOrigin.Inferred,
        new GraphInference("relaciones-grafo", "haiku", T0, "Web usa a Core por el nombre"));

    public sealed class Analysis(string commit) : ICommitAnalysisService
    {
        public Analysis()
            : this(Commit)
        {
        }

        public int Analyzed { get; private set; }

        private CommitAnalysis Cached => new(
            new CodeGraph(commit, Graph.GeneratorVersion, Graph.Nodes, Graph.Edges, Graph.Gaps),
            new ConsistencyReport(commit, [], ConsistencyCodes.All, truncated: false),
            new NamespaceFiles(new Dictionary<string, IReadOnlyList<string>> { ["src/Core/Core.csproj|Calc.Core"] = ["src/Core/Calc.cs", "src/Core/Calc2.cs"] }));

        public bool HasCache { get; set; } = true;

        public Task<CommitAnalysis?> FindAsync(ProjectContext project, string requested, CancellationToken ct) =>
            Task.FromResult(HasCache && requested == commit ? Cached : null);

        public Task<CommitAnalysis> AnalyzeAsync(ProjectContext project, string requested, CancellationToken ct)
        {
            Analyzed++;
            HasCache = true;
            return Task.FromResult(Cached);
        }
    }

    public sealed class History(bool changes = true, string? head = null, bool accepted = true) : IRepositoryHistoryReader
    {
        public Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken) =>
            Task.FromResult(new RepositorySummary(project.RootPath, accepted, "main", head ?? Commit, changes, []));

        public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public sealed class Inference(params GraphEdge[] edges) : IGraphInferenceStore
    {
        public List<GraphEdge> Edges { get; } = [.. edges];

        public Task<IReadOnlyList<GraphEdge>> ListAsync(ProjectContext project, CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphEdge>>([.. Edges]);

        public Task AddAsync(ProjectContext project, IReadOnlyList<GraphEdge> added, CancellationToken ct)
        {
            Edges.AddRange(added);
            return Task.CompletedTask;
        }
    }

    public sealed class Tasks : IGraphTaskIndex
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<GraphTaskRef>> TasksUnderAsync(ProjectContext project, string folder, CancellationToken ct)
        {
            Asked.Add(folder);
            return Task.FromResult<IReadOnlyList<GraphTaskRef>>([new GraphTaskRef(new RunId("20261111-a1b2c3"), new TaskId("T1"), "Tarea T1", ["src/Core/Calc.cs"])]);
        }
    }

    public sealed class Proposer(Func<GraphProposalRequest, GraphProposalReply>? answer = null) : IGraphRelationProposer
    {
        public List<GraphProposalRequest> Requests { get; } = [];

        public Task<GraphProposalReply> ProposeAsync(GraphProposalRequest request, IProgress<AgentEvent>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer?.Invoke(request) ?? new GraphProposalReply(new RunId("aux-20261112-abcdef"), AgentRunStatus.Succeeded, null, [], 0, null));
        }
    }
}
