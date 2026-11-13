using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Graph;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application.Graph;

/// <summary>docs/20 D-19 (WP-G): one copy of the commit for the graph and the checks, closed afterwards, and a cache by commit.</summary>
public sealed class CommitAnalysisServiceTests
{
    private static readonly ProjectContext Project = new("0f8fad5b-d9cb-469f-a165-70867728950e", @"C:\proyecto");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CodeGraph Model = new(CommitA, 1, [new GraphNode("S.slnx", GraphNodeKind.Solution, "S", "S.slnx", false)], [], []);

    private sealed class Snapshots(List<string> log) : IRepositorySnapshots
    {
        public int Opened { get; private set; }

        public int Disposed { get; set; }

        public Task<IRepositorySnapshot> OpenAsync(ProjectContext project, string commit, CancellationToken ct)
        {
            Opened++;
            log.Add("open");
            return Task.FromResult<IRepositorySnapshot>(new Snapshot(this, commit, log));
        }
    }

    private sealed class Snapshot(Snapshots owner, string commit, List<string> log) : IRepositorySnapshot
    {
        public string Commit => commit;

        public string Root => @"C:\area\src";

        public IReadOnlyList<string> Paths => [];

        public Stream OpenRead(string relativePath, long maxBytes) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            owner.Disposed++;
            log.Add("close");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Builder(List<string> log, bool fail = false, Task? gate = null) : ICodeGraphBuilder
    {
        public IRepositorySnapshot? Saw { get; private set; }

        public async Task<GraphBuild> BuildAsync(IRepositorySnapshot snapshot, CancellationToken ct)
        {
            Saw = snapshot;
            log.Add("graph");
            if (gate is not null)
            {
                await gate;
            }

            return fail ? throw new GraphAnalysisException("fails") : new GraphBuild(Model, NamespaceFiles.Empty);
        }
    }

    private sealed class Checks(List<string> log) : IConsistencyService
    {
        public IRepositorySnapshot? Saw { get; private set; }

        public Task<ConsistencyReport> RunAsync(ProjectContext project, IRepositorySnapshot snapshot, CancellationToken ct)
        {
            Saw = snapshot;
            log.Add("checks");
            return Task.FromResult(new ConsistencyReport(snapshot.Commit, [new ConsistencyFinding(ConsistencyCodes.LinkBroken, "a.md", 1, [])], [], false));
        }
    }

    private sealed class MemoryCache : ICommitAnalysisCache
    {
        public Dictionary<string, CachedAnalysis> Entries { get; } = [];

        public Task<CachedAnalysis?> LoadAsync(ProjectContext project, string commit, CancellationToken ct) => Task.FromResult(Entries.GetValueOrDefault(commit));

        public Task SaveAsync(ProjectContext project, CachedAnalysis analysis, CancellationToken ct)
        {
            Entries[analysis.Analysis.Commit] = analysis;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_graph_and_the_checks_read_the_same_copy_which_is_opened_once_and_closed_after_both()
    {
        var log = new List<string>();
        var snapshots = new Snapshots(log);
        var builder = new Builder(log);
        var checks = new Checks(log);
        var service = new CommitAnalysisService(snapshots, builder, new MemoryCache(), checks);

        var analysis = await service.AnalyzeAsync(Project, CommitA, Ct);

        Assert.Equal(["open", "graph", "checks", "close"], log);
        Assert.Same(builder.Saw, checks.Saw);
        Assert.Equal(Model, analysis.Graph);
        Assert.Equal(1, analysis.Consistency.Count(ConsistencyCodes.LinkBroken));
        Assert.Equal(CommitA, analysis.Commit);
    }

    [Fact]
    public async Task What_is_cached_by_commit_is_what_find_gives_and_asking_to_analyze_again_reads_the_commit_again()
    {
        var log = new List<string>();
        var snapshots = new Snapshots(log);
        var service = new CommitAnalysisService(snapshots, new Builder(log), new MemoryCache(), new Checks(log));

        Assert.Null(await service.FindAsync(Project, CommitA, Ct));
        Assert.Equal(0, snapshots.Opened);

        var first = await service.AnalyzeAsync(Project, CommitA, Ct);
        var found = await service.FindAsync(Project, CommitA, Ct);

        Assert.Equal(1, snapshots.Opened);
        Assert.Equal(first, found);
        Assert.Null(await service.FindAsync(Project, CommitB, Ct));

        // «Actualizar»: the commit is read again (a file that timed out on a busy machine gets another chance); the cache is rewritten.
        var again = await service.AnalyzeAsync(Project, CommitA, Ct);

        Assert.Equal(2, snapshots.Opened);
        Assert.Equal(first, again);
    }

    [Fact]
    public async Task Without_the_checks_the_discrepancies_are_empty_and_every_code_is_not_applicable_and_the_cache_says_it_was_not_checked()
    {
        var log = new List<string>();
        var cache = new MemoryCache();
        var service = new CommitAnalysisService(new Snapshots(log), new Builder(log), cache);

        var analysis = await service.AnalyzeAsync(Project, CommitA, Ct);

        Assert.Empty(analysis.Consistency.Findings);
        Assert.Equal(ConsistencyCodes.All, analysis.Consistency.NotApplicable);
        Assert.False(cache.Entries[CommitA].ConsistencyChecked);
    }

    [Fact]
    public async Task An_analysis_cached_before_the_checks_existed_is_calculated_again_once_they_do()
    {
        var log = new List<string>();
        var cache = new MemoryCache();
        await new CommitAnalysisService(new Snapshots(log), new Builder(log), cache).AnalyzeAsync(Project, CommitA, Ct);
        var withChecks = new CommitAnalysisService(new Snapshots(log), new Builder(log), cache, new Checks(log));

        Assert.Null(await withChecks.FindAsync(Project, CommitA, Ct));
        var again = await withChecks.AnalyzeAsync(Project, CommitA, Ct);

        Assert.Equal(1, again.Consistency.Count(ConsistencyCodes.LinkBroken));
        Assert.True(cache.Entries[CommitA].ConsistencyChecked);
    }

    [Fact]
    public async Task A_failed_analysis_closes_the_copy_and_caches_nothing()
    {
        var log = new List<string>();
        var snapshots = new Snapshots(log);
        var cache = new MemoryCache();
        var service = new CommitAnalysisService(snapshots, new Builder(log, fail: true), cache);

        await Assert.ThrowsAsync<GraphAnalysisException>(() => service.AnalyzeAsync(Project, CommitA, Ct));

        Assert.Equal(1, snapshots.Disposed);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public async Task A_commit_that_is_not_a_full_id_is_refused_before_anything_is_opened()
    {
        var log = new List<string>();
        var snapshots = new Snapshots(log);
        var service = new CommitAnalysisService(snapshots, new Builder(log), new MemoryCache());

        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(Project, "HEAD", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindAsync(Project, "abc", Ct));

        Assert.Equal(0, snapshots.Opened);
    }

    [Fact]
    public async Task A_cancelled_analysis_closes_the_copy_caches_nothing_and_lets_the_next_one_run()
    {
        var log = new List<string>();
        var snapshots = new Snapshots(log);
        var cache = new MemoryCache();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var service = new CommitAnalysisService(snapshots, new Builder(log, gate: gate.Task.WaitAsync(cancel.Token)), cache);

        var running = service.AnalyzeAsync(Project, CommitA, cancel.Token);
        await Task.Delay(50, Ct);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, snapshots.Disposed);
        Assert.Empty(cache.Entries);
        var next = await new CommitAnalysisService(snapshots, new Builder(log), cache).AnalyzeAsync(Project, CommitA, Ct);
        Assert.Equal(CommitA, next.Commit);
    }

    [Fact]
    public async Task A_second_analysis_of_the_same_commit_asked_while_the_first_runs_takes_its_result_instead_of_reading_the_commit_again()
    {
        var log = new List<string>();
        var snapshots = new Snapshots(log);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CommitAnalysisService(snapshots, new Builder(log, gate: gate.Task), new MemoryCache());

        var first = service.AnalyzeAsync(Project, CommitA, Ct);
        var second = service.AnalyzeAsync(Project, CommitA, Ct);
        await Task.Delay(100, Ct);

        Assert.Equal(1, snapshots.Opened);
        gate.SetResult();
        var both = await Task.WhenAll(first, second);

        Assert.Equal(both[0], both[1]);
        Assert.Equal(1, snapshots.Opened);
    }
}
