using AgentStudio.Application.Graph;
using AgentStudio.Domain.Graph;

namespace AgentStudio.GraphWorker;

/// <summary>
/// The caps of the analysis (docs/20 D-18). Every one truncates: what comes after it is not read, a gap (<c>graph.limit</c>) says where and its node is partial, and the
/// analysis goes on. None throws, so a hostile repository cannot make the whole analysis fail by being big. A test gives small values so each cap can be reached.
/// </summary>
internal sealed record AnalysisLimits
{
    public int MaxProjects { get; init; } = 2_000;

    public int MaxCsFiles { get; init; } = 50_000;

    /// <summary>The projects a solution file may list.</summary>
    public int MaxSolutionEntries { get; init; } = 10_000;

    public int MaxNodes { get; init; } = CodeGraph.MaxNodes;

    public int MaxEdges { get; init; } = CodeGraph.MaxEdges;

    public int MaxGaps { get; init; } = CodeGraph.MaxGaps;

    public int MaxNamespaceFilesTotal { get; init; } = NamespaceFiles.MaxTotal;

    public int MaxPackagesPerProject { get; init; } = 2_000;

    public int MaxReferencesPerProject { get; init; } = 5_000;
}
