using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Memory;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Memory.MemorySearchSamples;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The upkeep of the semantic index (docs/17 D-04; ADR-0047, decision 2; the integration of Phase 12), over fakes: a refresh
/// of the index of a project after each derivation that completed, never two at once for a project (a queue with a single
/// consumer, a burst merged into one more refresh), out of the UI thread, and never failing its caller; after the model was
/// downloaded, a refresh of the project that was open and the state on the screen read again; and the model opened ahead
/// when a project is opened only if its files are already in place, without downloading anything.
/// </summary>
public sealed class SemanticIndexUpkeepTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly ProjectContext Project = new("p-1", Root);

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryDerivationResult Completed() => new([], [], []);

    [Fact]
    public async Task After_each_derivation_that_completed_the_index_is_refreshed_in_the_background_and_a_burst_costs_one_more_refresh()
    {
        var index = new RefreshingIndex { Hold = new TaskCompletionSource() };
        using var upkeep = new SemanticIndexUpkeep(index, null);
        var refreshed = new List<SemanticIndexRefreshedEventArgs>();
        upkeep.Refreshed += (_, e) =>
        {
            lock (refreshed)
            {
                refreshed.Add(e);
            }
        };
        var memory = new ProjectMemoryUpkeep(new FakeProjectWorkspace(), new FakeMemoryDerivation(Completed()), null, upkeep);

        // The first derivation starts a refresh, which is held; four more derivations arrive while it runs.
        await memory.AfterRunAsync(Root, Ct);
        await WaitUntilAsync(() => index.Refreshes == 1);
        for (var i = 0; i < 4; i++)
        {
            await memory.AfterRunAsync(Root, Ct);
        }

        // Nothing waited for the refresh: the derivations returned while it was held.
        Assert.Equal(1, index.Refreshes);
        index.Hold.SetResult();
        await WaitUntilAsync(() =>
        {
            lock (refreshed)
            {
                return refreshed.Count == 2;
            }
        });

        Assert.Equal(2, index.Refreshes);
        Assert.Equal(1, index.MostAtOnce);
        Assert.All(index.Projects, p => Assert.Equal(Project, p));
        Assert.All(index.OnThePool, Assert.True);
        Assert.All(refreshed, e => Assert.True(e.Refresh.Completed));
    }

    [Fact]
    public async Task A_derivation_that_did_not_complete_asks_for_no_refresh_and_without_an_index_nothing_is_asked()
    {
        var index = new RefreshingIndex();
        using var upkeep = new SemanticIndexUpkeep(index, null);
        var memory = new ProjectMemoryUpkeep(new FakeProjectWorkspace(), new FakeMemoryDerivation(MemoryDerivationResult.NotDerived("el registro no verifica")), null, upkeep);
        using var withoutIndex = new SemanticIndexUpkeep(null, null);

        await memory.AfterRunAsync(Root, Ct);
        var none = await withoutIndex.RefreshAsync(Project);

        Assert.Equal(0, index.Refreshes);
        Assert.Null(none);
        Assert.False(withoutIndex.IsAvailable);
    }

    [Fact]
    public async Task Opening_a_project_with_storage_derives_then_refreshes_its_index()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        Directory.CreateDirectory(Path.Combine(root, ".agentstudio"));
        await File.WriteAllTextAsync(Path.Combine(root, ".agentstudio", "agentstudio.db"), string.Empty, Ct);
        var index = new RefreshingIndex();
        using var upkeep = new SemanticIndexUpkeep(index, null);
        var done = new TaskCompletionSource<SemanticIndexRefreshedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        upkeep.Refreshed += (_, e) => done.TrySetResult(e);

        await new ProjectMemoryUpkeep(new FakeProjectWorkspace(), new FakeMemoryDerivation(Completed()), null, upkeep).OnProjectOpenedAsync(root, Ct);
        var refresh = await done.Task.WaitAsync(Guard, Ct);

        Assert.Equal(root, refresh.Project.RootPath);
        Assert.True(refresh.Refresh.Completed);
    }

    [Fact]
    public async Task A_refresh_that_fails_ends_its_request_with_the_reason_and_the_next_one_runs()
    {
        var index = new RefreshingIndex { Failure = new IOException("el índice está bloqueado") };
        using var upkeep = new SemanticIndexUpkeep(index, null);

        var failed = await upkeep.RefreshAsync(Project).WaitAsync(Guard, Ct);
        index.Failure = null;
        var next = await upkeep.RefreshAsync(Project).WaitAsync(Guard, Ct);

        Assert.Equal(SemanticIndexUpkeep.FailedReason("el índice está bloqueado"), failed!.Reason);
        Assert.True(next!.Completed);
        Assert.Equal(2, index.Refreshes);
    }

    [Fact]
    public async Task A_follower_that_throws_is_skipped_the_others_still_hear_and_the_consumer_goes_on()
    {
        var index = new RefreshingIndex();
        using var upkeep = new SemanticIndexUpkeep(index, null);
        var heard = 0;
        upkeep.Refreshed += (_, _) => throw new InvalidOperationException("La pantalla falló.");
        upkeep.Refreshed += (_, _) => Interlocked.Increment(ref heard);

        var first = await upkeep.RefreshAsync(Project).WaitAsync(Guard, Ct);
        await WaitUntilAsync(() => Volatile.Read(ref heard) == 1);
        var second = await upkeep.RefreshAsync(Project).WaitAsync(Guard, Ct);
        await WaitUntilAsync(() => Volatile.Read(ref heard) == 2);

        Assert.True(first!.Completed);
        Assert.True(second!.Completed);
        Assert.Equal(2, index.Refreshes);
    }

    [Fact]
    public async Task Closing_the_app_stops_the_refresh_and_ends_what_waits_without_waiting_for_it()
    {
        var index = new RefreshingIndex { Hold = new TaskCompletionSource() };
        var upkeep = new SemanticIndexUpkeep(index, null);
        var first = upkeep.RefreshAsync(Project);
        await WaitUntilAsync(() => index.Refreshes == 1);
        var waiting = upkeep.RefreshAsync(Project);

        upkeep.Dispose();
        var results = await Task.WhenAll(first, waiting).WaitAsync(Guard, Ct);
        var after = await upkeep.RefreshAsync(Project);

        Assert.All(results, r => Assert.Equal(SemanticIndexUpkeep.StoppedReason, r!.Reason));
        Assert.Equal(SemanticIndexUpkeep.StoppedReason, after!.Reason);
        Assert.Equal(1, index.Refreshes);
    }

    [Fact]
    public async Task Each_project_has_its_own_queue()
    {
        var index = new RefreshingIndex { Hold = new TaskCompletionSource() };
        using var upkeep = new SemanticIndexUpkeep(index, null);

        var calc = upkeep.RefreshAsync(Project);
        var other = upkeep.RefreshAsync(new ProjectContext("p-2", @"C:\proyectos\otro"));
        await WaitUntilAsync(() => index.Refreshes == 2);
        index.Hold.SetResult();
        await Task.WhenAll(calc, other).WaitAsync(Guard, Ct);

        Assert.Equal(["p-1", "p-2"], index.Projects.Select(p => p.ProjectId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Opening_a_project_opens_the_model_ahead_only_when_its_files_are_in_place_and_never_downloads()
    {
        var missing = new CountingEmbedderSource(EmbeddingModelState.Missing);
        var ready = new CountingEmbedderSource(EmbeddingModelState.Ready);
        using var withoutModel = new SemanticIndexUpkeep(new RefreshingIndex(), missing);
        using var withModel = new SemanticIndexUpkeep(new RefreshingIndex(), ready);

        await new ProjectMemoryUpkeep(new FakeProjectWorkspace(), null, null, withoutModel).OnProjectOpenedAsync(Root, Ct);
        await new ProjectMemoryUpkeep(new FakeProjectWorkspace(), null, null, withModel).OnProjectOpenedAsync(Root, Ct);
        var notOpened = await withoutModel.WarmUpAsync().WaitAsync(Guard, Ct);
        var opened = await withModel.WarmUpAsync().WaitAsync(Guard, Ct);
        var again = await withModel.WarmUpAsync().WaitAsync(Guard, Ct);

        Assert.False(notOpened);
        Assert.Equal(0, missing.Openings);
        Assert.True(missing.Checks >= 1);
        Assert.True(opened && again);
        Assert.Equal(1, ready.Openings);
        Assert.All(ready.CheckedOnThePool.Concat(missing.CheckedOnThePool), Assert.True);
    }

    [Fact]
    public async Task Once_the_model_is_installed_the_index_of_the_open_project_is_refreshed_off_the_UI_thread_and_the_screen_reads_its_state_again()
    {
        var index = new RefreshingIndex { Hold = new TaskCompletionSource(), Status = Missing() };
        var installer = new FakeModelInstaller();
        var memory = new MemoryViewModel(
            new FakeProjectWorkspace(),
            new FakeProjectMemory(),
            new FakeMemoryExporter(),
            new FakeRunActivity(),
            new DailySummaryViewModel(new FakeDailySummaryService(), new FakeClock(At)),
            new FakeClock(At),
            new MemorySearchBackend(index, null, installer));
        using var upkeep = new SemanticIndexUpkeep(index, null);
        upkeep.Follow(memory);
        await memory.SetProjectAsync(Root);

        memory.Model.AskDownloadCommand.Execute(null);
        await memory.Model.ConfirmDownloadCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => index.Refreshes == 1);
        var readsBefore = index.StatusReads;
        index.Status = Ready();
        index.Hold.SetResult();

        // When the refresh ends, «Memoria» reads the state of the model and of the index once more.
        await WaitUntilAsync(() => index.StatusReads > readsBefore && memory.Model.IsHybridReady);
        Assert.Equal(Project, Assert.Single(index.Projects));
        Assert.True(Assert.Single(index.OnThePool));
        Assert.Single(installer.Consents);
    }

    [Fact]
    public async Task A_download_without_a_project_open_refreshes_nothing()
    {
        var index = new RefreshingIndex { Status = Missing() };
        var installer = new FakeModelInstaller();
        var memory = new MemoryViewModel(
            new FakeProjectWorkspace(),
            new FakeProjectMemory(),
            new FakeMemoryExporter(),
            new FakeRunActivity(),
            new DailySummaryViewModel(new FakeDailySummaryService(), new FakeClock(At)),
            new FakeClock(At),
            new MemorySearchBackend(index, null, installer));
        using var upkeep = new SemanticIndexUpkeep(index, null);
        upkeep.Follow(memory);
        await memory.SetProjectAsync(null);

        memory.Model.AskDownloadCommand.Execute(null);
        await memory.Model.ConfirmDownloadCommand.ExecuteAsync(null);

        Assert.Equal(0, index.Refreshes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Guard;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10, Ct);
        }
    }
}

/// <summary>
/// An index whose refresh is counted and may be held or fail; it records the project of each refresh, whether it ran on
/// the thread pool, and how many ran at once. Its state is the one the test sets, counted; it searches nothing.
/// </summary>
internal sealed class RefreshingIndex : ISemanticIndex
{
    private readonly System.Threading.Lock _gate = new();
    private int _running;
    private int _statusReads;

    public TaskCompletionSource? Hold { get; set; }

    public Exception? Failure { get; set; }

    public SemanticIndexStatus Status { get; set; } = MemorySearchSamples.Ready();

    public int Refreshes { get { lock (_gate) { return Projects.Count; } } }

    public int MostAtOnce { get; private set; }

    public int StatusReads => Volatile.Read(ref _statusReads);

    public List<ProjectContext> Projects { get; } = [];

    public List<bool> OnThePool { get; } = [];

    public async Task<SemanticIndexRefresh> RefreshAsync(ProjectContext project, CancellationToken ct)
    {
        lock (_gate)
        {
            Projects.Add(project);
            OnThePool.Add(Thread.CurrentThread.IsThreadPoolThread);
            _running++;
            MostAtOnce = Math.Max(MostAtOnce, _running);
        }

        try
        {
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return Failure is { } failure ? throw failure : new SemanticIndexRefresh(1, 0, 0, 0);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }

    public Task<SemanticSearchResult> SearchAsync(ProjectContext project, SemanticQuery query, CancellationToken ct) =>
        Task.FromResult(new SemanticSearchResult([], SemanticSearchMode.WordsOnly, WordsOnlyReason.ModelMissing));

    public Task<SemanticIndexStatus> StatusAsync(ProjectContext project, CancellationToken ct)
    {
        Interlocked.Increment(ref _statusReads);
        return Task.FromResult(Status);
    }

    public Task<ProjectHistorySnapshot> SnapshotAsync(ProjectContext project, int maxFragments, CancellationToken ct) => throw new NotSupportedException("The upkeep takes no snapshot.");

    public Task DeleteAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException("The upkeep never deletes the index.");
}

/// <summary>A local model whose files are in the state the test sets: its checks and openings are counted, and it never downloads.</summary>
internal sealed class CountingEmbedderSource(EmbeddingModelState state) : IEmbedderSource
{
    private int _checks;
    private int _openings;

    public int Checks => Volatile.Read(ref _checks);

    public int Openings => Volatile.Read(ref _openings);

    public List<bool> CheckedOnThePool { get; } = [];

    public EmbeddingModelStatus Check()
    {
        Interlocked.Increment(ref _checks);
        lock (CheckedOnThePool)
        {
            CheckedOnThePool.Add(Thread.CurrentThread.IsThreadPoolThread);
        }

        return new EmbeddingModelStatus(state, MemorySearchSamples.ModelFolder);
    }

    public Task<EmbedderOpening> OpenAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _openings);
        return Task.FromResult(new EmbedderOpening(new NoEmbedder(), Check()));
    }

    public void Dispose()
    {
    }

    private sealed class NoEmbedder : IEmbedder
    {
        public EmbeddingModelDescriptor Model => EmbeddingModels.MultilingualE5Small;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct) =>
            throw new NotSupportedException("Opening the model ahead embeds nothing.");
    }
}
