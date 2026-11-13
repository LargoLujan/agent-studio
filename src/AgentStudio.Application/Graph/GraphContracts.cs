using AgentStudio.Application.Consistency;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Graph;

// Phase 6 contracts (docs/20 D-16 to D-22 and section 4.2, ADR-0062). The builder, the service, the store and the index are WP-G; the analysis
// itself runs in AgentStudio.GraphWorker (docs/20 D-18), which shares GraphWorkerProtocol and CodeGraphJson with its host.

/// <summary>
/// Runs the analysis process over an open copy of a commit and returns the static model with the files that declare each namespace (docs/20 D-18).
/// The P0 returned the model alone; the files that declare each namespace (F6-5) have nowhere in the model, so they travel beside it (<see cref="GraphBuild"/>).
/// </summary>
public interface ICodeGraphBuilder
{
    /// <exception cref="GraphAnalysisException">The process failed, crashed on every launch, ran too long or wrote something that is not a model.</exception>
    Task<GraphBuild> BuildAsync(IRepositorySnapshot snapshot, CancellationToken ct);
}

/// <summary>The analysis of a commit could not be completed. The message never quotes what the process wrote: it is data that is not trusted.</summary>
public sealed class GraphAnalysisException : Exception
{
    public GraphAnalysisException(string message)
        : base(message)
    {
    }

    public GraphAnalysisException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The graph and the discrepancies of one commit, calculated from the same copy (docs/20 D-19): both describe the same commit.</summary>
public sealed record CommitAnalysis
{
    /// <exception cref="ArgumentException">The two describe different commits.</exception>
    /// <param name="files">The files that declare each namespace of the graph (F6-5); none when the analysis does not list them.</param>
    public CommitAnalysis(CodeGraph graph, ConsistencyReport consistency, NamespaceFiles? files = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(consistency);
        if (graph.Commit != consistency.Commit)
        {
            throw new ArgumentException("The graph and the discrepancies of an analysis describe the same commit.", nameof(consistency));
        }

        Graph = graph;
        Consistency = consistency;
        Files = files ?? NamespaceFiles.Empty;
    }

    public CodeGraph Graph { get; }

    /// <summary>The files that declare each namespace of <see cref="Graph"/>; not part of its hash.</summary>
    public NamespaceFiles Files { get; }

    public ConsistencyReport Consistency { get; }

    public string Commit => Graph.Commit;
}

/// <summary>
/// The analysis of a commit, cached by commit under <c>%LOCALAPPDATA%\AgentStudio\graph</c> (docs/20 D-19). It opens one copy for the graph and the
/// checks and closes it. Opening the panel of Auto Doctor only asks <see cref="FindAsync"/>: it never launches the copy nor the analysis (D-30).
/// </summary>
public interface ICommitAnalysisService
{
    /// <summary>What is cached for the commit, or null. Never starts anything.</summary>
    Task<CommitAnalysis?> FindAsync(ProjectContext project, string commit, CancellationToken ct);

    /// <summary>Calculates, caches and returns the analysis of the commit. With a verification running it waits (it shares the lock of «una a la vez»).</summary>
    Task<CommitAnalysis> AnalyzeAsync(ProjectContext project, string commit, CancellationToken ct);
}

/// <summary>The relations an AI proposed, kept apart from the static model (docs/20 D-21). They never enter the hash of the model nor a generated diagram.</summary>
public interface IGraphInferenceStore
{
    /// <summary>The relations proposed for the project, newest proposal last. They are all <see cref="GraphEdgeOrigin.Inferred"/>.</summary>
    Task<IReadOnlyList<GraphEdge>> ListAsync(ProjectContext project, CancellationToken ct);

    /// <summary>
    /// Keeps the relations of one proposal. The store is derived data (the log has the ends and the kind of each relation, the evidence the text of the
    /// agent): losing it loses only the reasons and the proposals can be asked again. A relation that is already there is not kept twice.
    /// </summary>
    Task AddAsync(ProjectContext project, IReadOnlyList<GraphEdge> edges, CancellationToken ct);
}

/// <summary>A task whose attempts changed files under a folder (docs/20 D-22, F6-5): where the person goes from a node of the graph to its work.</summary>
public sealed record GraphTaskRef
{
    public GraphTaskRef(RunId run, TaskId task, string title, IReadOnlyList<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 200 || UntrustedText.HasHidden(title) || changedPaths.Any(p => !RepositoryPaths.IsSafeRelative(p)))
        {
            throw new ArgumentException("A task of the graph has a title of one line and the relative paths of what it changed.", nameof(title));
        }

        Run = run;
        Task = task;
        Title = title;
        ChangedPaths = [.. changedPaths];
    }

    public RunId Run { get; }

    public TaskId Task { get; }

    /// <summary>The title of the task, as the plan has it: text of the plan, shown as data.</summary>
    public string Title { get; }

    /// <summary>The files under the folder that the attempts of the task changed (<c>AttemptRecord.ChangedPaths</c>).</summary>
    public IReadOnlyList<string> ChangedPaths { get; }

    public bool Equals(GraphTaskRef? other) =>
        other is not null && Run == other.Run && Task == other.Task && Title == other.Title && ChangedPaths.SequenceEqual(other.ChangedPaths);

    public override int GetHashCode() => HashCode.Combine(Run, Task, Title, ChangedPaths.Count);
}

/// <summary>The tasks that touched a folder, from the attempts in the log (docs/20 D-22).</summary>
public interface IGraphTaskIndex
{
    /// <param name="folder">A folder of the repository, relative and with <c>/</c> (<c>RepositoryPaths.IsSafeRelative</c>); empty for the whole project.</param>
    Task<IReadOnlyList<GraphTaskRef>> TasksUnderAsync(ProjectContext project, string folder, CancellationToken ct);
}
