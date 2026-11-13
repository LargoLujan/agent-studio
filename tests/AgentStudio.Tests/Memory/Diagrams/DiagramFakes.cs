using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Memory.Diagrams;

namespace AgentStudio.Tests.Memory.Diagrams;

/// <summary>The doubles of the catalog of diagrams: a model of the commit the project is at, its history, the log, the plans and the activity. None touches the disk but the temporary folder a test gives.</summary>
internal static class DiagramFakes
{
    public static ProjectContext Project(string root) => new("p-diagrams", root);

    public sealed class Analysis : ICommitAnalysisService
    {
        public CodeGraph? Graph { get; set; }

        public int Analyzed { get; private set; }

        public bool Cached { get; set; } = true;

        /// <summary>The discrepancies the analysis saved; none by default.</summary>
        public IReadOnlyList<ConsistencyFinding> Findings { get; set; } = [];

        public IReadOnlyList<string> NotApplicable { get; set; } = [];

        public bool Truncated { get; set; }

        public int Found { get; private set; }

        public Task<CommitAnalysis?> FindAsync(ProjectContext project, string commit, CancellationToken ct)
        {
            Found++;
            return Task.FromResult(Cached && Graph is { } graph && graph.Commit == commit ? Make(graph) : null);
        }

        public Task<CommitAnalysis> AnalyzeAsync(ProjectContext project, string commit, CancellationToken ct)
        {
            Analyzed++;
            Cached = true;
            return Task.FromResult(Make(Graph!));
        }

        private CommitAnalysis Make(CodeGraph graph) => new(graph, new ConsistencyReport(graph.Commit, Findings, NotApplicable, Truncated));
    }

    public sealed class History(string commit) : IRepositoryHistoryReader
    {
        public string Commit { get; set; } = commit;

        /// <summary>The thread the last question was answered on.</summary>
        public int ThreadId { get; private set; }

        public Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult(new RepositorySummary(project.RootPath, true, "main", Commit, false, []));
        }

        public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public sealed class Recorder(IReadOnlyList<DecisionEntry>? entries = null, bool valid = true) : IDecisionRecorder
    {
        public List<DecisionEntry> Entries { get; } = [.. entries ?? []];

        /// <summary>The thread the last read of the log was answered on.</summary>
        public int ThreadId { get; private set; }

        public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult(new DecisionLogState([.. Entries], valid ? DecisionLogVerification.Valid : new DecisionLogVerification(false, 1, "broken")));
        }

        public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => LoadAsync(projectId, cancellationToken);

        public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public sealed class Plans : IPlanStore
    {
        public Dictionary<string, PlanDefinition> Stored { get; } = [];

        public Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken cancellationToken)
        {
            Stored[plan.ContentHash] = plan;
            return Task.FromResult(plan.ContentHash);
        }

        /// <summary>A store whose plans do not read back (the hash does not match, the content is broken): asking for one throws.</summary>
        public bool Broken { get; set; }

        public Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken cancellationToken) =>
            Broken ? throw new ArgumentException("The plan does not read back.") : Task.FromResult(Stored.GetValueOrDefault(hash));
    }

    public sealed class Activity(bool busy = false) : IRunActivity
    {
        private int _asked;

        public bool Busy { get; set; } = busy;

        /// <summary>When set, the first so many questions are answered «no» and the next ones follow <see cref="Busy"/>: a run that starts between two questions of one call.</summary>
        public int FreeFor { get; set; }

        public bool IsBusy(string projectId) => _asked++ < FreeFor ? false : Busy;
    }

    /// <summary>The catalog over a temporary cache, with the doubles above. The clock is the one of the test.</summary>
    public sealed class World : IDisposable
    {
        public World(CodeGraph? graph = null, bool withLog = true)
        {
            Root = Directory.CreateTempSubdirectory("as-diagrams-project-").FullName;
            Cache = Directory.CreateTempSubdirectory("as-diagrams-cache-").FullName;
            Project = DiagramFakes.Project(Root);
            Analysis = new Analysis { Graph = graph ?? DiagramFixtures.Graph() };
            History = new History(DiagramFixtures.Commit);
            Recorder = new Recorder(withLog ? DiagramFixtures.Log() : []);
            Plans = new Plans();
            Plans.Stored[DiagramFixtures.Plan().ContentHash] = DiagramFixtures.Plan();
            Activity = new Activity();
            Clock = new FakeClock(DiagramFixtures.At);
            Graphs = new GraphModels(Analysis, History);
            Runs = new RunModels(Recorder, Plans);
            Catalog = new DiagramCatalog(Cache, Graphs, Runs, Activity, Clock);
            Staleness = new DiagramStaleness(Catalog.Store, Runs, Clock);
        }

        public DiagramStaleness Staleness { get; }

        public string Root { get; }

        public string Cache { get; }

        public ProjectContext Project { get; }

        public Analysis Analysis { get; }

        public History History { get; }

        public Recorder Recorder { get; }

        public Plans Plans { get; }

        public Activity Activity { get; }

        public FakeClock Clock { get; }

        public GraphModels Graphs { get; }

        public RunModels Runs { get; }

        public DiagramCatalog Catalog { get; }

        public void Dispose()
        {
            Delete(Root);
            Delete(Cache);
        }

        private static void Delete(string folder)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temporary folder that is still open is left to the system.
            }
        }
    }
}
