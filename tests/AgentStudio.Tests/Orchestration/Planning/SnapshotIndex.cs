using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>An index that only gives snapshots (the planner's tool asks nothing else), counted; it may fail.</summary>
internal sealed class SnapshotIndex(params HistoryFragment[] fragments) : ISemanticIndex
{
    private int _snapshots;

    public int Snapshots => Volatile.Read(ref _snapshots);

    public Exception? Failure { get; set; }

    public Task<ProjectHistorySnapshot> SnapshotAsync(ProjectContext project, int maxFragments, CancellationToken ct)
    {
        Interlocked.Increment(ref _snapshots);
        return Failure is { } failure
            ? Task.FromException<ProjectHistorySnapshot>(failure)
            : Task.FromResult(new ProjectHistorySnapshot(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), null, 0, [.. fragments.Take(maxFragments)]));
    }

    public Task<SemanticIndexRefresh> RefreshAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException("The planner never refreshes the index.");

    public Task<SemanticSearchResult> SearchAsync(ProjectContext project, SemanticQuery query, CancellationToken ct) => throw new NotSupportedException("The tool never searches the index.");

    public Task<SemanticIndexStatus> StatusAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException("The tool never reads the state of the index.");

    public Task DeleteAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException("The tool never deletes the index.");
}
