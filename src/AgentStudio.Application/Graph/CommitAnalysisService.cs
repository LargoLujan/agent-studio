using AgentStudio.Application.Consistency;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Application.Graph;

/// <summary>
/// What the cache keeps of the analysis of a commit: the analysis and whether the discrepancies in it were really checked. An analysis made while the
/// checks (<see cref="IConsistencyService"/>) did not exist has no findings and every code as «no aplica»: it is kept, but it is not an answer once the checks
/// exist, so the service asks for it again then.
/// </summary>
public sealed record CachedAnalysis(CommitAnalysis Analysis, bool ConsistencyChecked);

/// <summary>The cache of the analyses by commit, under <c>%LOCALAPPDATA%\AgentStudio\graph\&lt;project&gt;\&lt;commit&gt;.json</c> (docs/20 D-19). Derived data: it is deleted and calculated again.</summary>
public interface ICommitAnalysisCache
{
    /// <summary>What is kept for the commit, or null: nothing, or something that does not read back as an analysis.</summary>
    Task<CachedAnalysis?> LoadAsync(ProjectContext project, string commit, CancellationToken ct);

    Task SaveAsync(ProjectContext project, CachedAnalysis analysis, CancellationToken ct);
}

/// <summary>
/// The analysis of a commit (docs/20 D-19, ADR-0062): opens one copy of the commit, lets the process of analysis read it, hands the same copy to the checks of
/// the discrepancies, closes it and keeps both results by commit. A single analysis runs at a time (a queue of one: the copy shares the lock of the
/// verification, «una a la vez»), and one that was asked while another of the same commit ran takes its result instead of reading the commit again.
/// </summary>
/// <param name="consistency">The checks (WP-D). Without them the discrepancies of the analysis are empty and every code is «no aplica», and the cache says so.</param>
public sealed class CommitAnalysisService(
    IRepositorySnapshots snapshots,
    ICodeGraphBuilder builder,
    ICommitAnalysisCache cache,
    IConsistencyService? consistency = null) : ICommitAnalysisService
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private long _finished;
    private (string Project, string Commit, CommitAnalysis Analysis)? _last;

    public async Task<CommitAnalysis?> FindAsync(ProjectContext project, string commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        RequireCommit(commit);
        var cached = await cache.LoadAsync(project, commit, ct).ConfigureAwait(false);
        return IsAnswer(cached) ? cached!.Analysis : null;
    }

    public async Task<CommitAnalysis> AnalyzeAsync(ProjectContext project, string commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        RequireCommit(commit);
        var seen = Interlocked.Read(ref _finished);
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // «Calculates, caches and returns» (docs/20 D-19): what is cached is what FindAsync gives, and asking to analyze again reads the commit again (a file that
            // timed out on a busy machine is given another chance). Only a caller that waited behind a run of the same commit that finished meanwhile takes its result.
            if (_last is { } last && last.Project == project.ProjectId && last.Commit == commit && Interlocked.Read(ref _finished) != seen)
            {
                return last.Analysis;
            }

            var snapshot = await snapshots.OpenAsync(project, commit, ct).ConfigureAwait(false);
            await using (snapshot.ConfigureAwait(false))
            {
                var build = await builder.BuildAsync(snapshot, ct).ConfigureAwait(false);
                var report = consistency is null
                    ? new ConsistencyReport(commit, [], ConsistencyCodes.All, truncated: false)
                    : await consistency.RunAsync(project, snapshot, ct).ConfigureAwait(false);
                var analysis = new CommitAnalysis(build.Graph, report, build.Files);
                await cache.SaveAsync(project, new CachedAnalysis(analysis, consistency is not null), ct).ConfigureAwait(false);
                _last = (project.ProjectId, commit, analysis);
                Interlocked.Increment(ref _finished);
                return analysis;
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private bool IsAnswer(CachedAnalysis? cached) => cached is not null && (cached.ConsistencyChecked || consistency is null);

    private static void RequireCommit(string commit)
    {
        if (!TraceRef.IsFullCommitId(commit))
        {
            throw new ArgumentException("The commit is a full id (40 or 64 lowercase hexadecimal characters).", nameof(commit));
        }
    }
}
