using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Decisions;
using AgentStudio.Observability.Context;
using AgentStudio.Tests.Observability.Usage;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The windows each project learned (docs/18 P36), read from its log in the background: the base table until it is read,
/// what the log learned over the table once it is, and what was known when the log cannot be read.
/// </summary>
public sealed class ContextWindowCatalogsTests
{
    private const string Root = @"C:\proyectos\calc";

    private static ProjectLogSnapshot Learned(long window)
    {
        var log = new UsageLogBuilder()
            .Start("20261020-a1b2c3", "T1", 1)
            .Finish("20261020-a1b2c3", "T1", 1, session: "s1", usage: Snapshots.Claude(10, 0, 0, 1, 0m, Snapshots.Model("claude-sonnet-5-5", 10, 1, 0, 0, 0m, window: window)));
        return new ProjectLogSnapshot("p-1", Root, log.Entries, DecisionLogVerification.Valid, AnchorStatus.Anchored);
    }

    [Fact]
    public async Task Until_its_log_is_read_a_project_has_the_base_table_and_then_what_its_log_learned_over_it()
    {
        var catalogs = new ContextWindowCatalogs(new FakeLogReader(() => Learned(400_000)));

        Assert.Same(ModelWindowCatalog.Baseline, catalogs.Current(Root));

        var catalog = await catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);

        Assert.Equal(400_000L, catalog.WindowFor("claude-sonnet-5-5").Value);
        Assert.Equal(200_000L, catalog.WindowFor("claude-haiku-4-5").Value);
        Assert.Same(catalog, catalogs.Current(Root + @"\"));
    }

    [Fact]
    public async Task A_log_that_cannot_be_read_leaves_what_was_known_and_never_fails()
    {
        var reads = 0;
        var catalogs = new ContextWindowCatalogs(new FakeLogReader(() => ++reads == 1 ? Learned(400_000) : throw new IOException("El registro está bloqueado.")));
        var first = await catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);

        var second = await catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(400_000L, second.WindowFor("claude-sonnet-5-5").Value);
    }

    [Fact]
    public async Task Without_a_reader_every_project_has_the_base_table()
    {
        var catalogs = new ContextWindowCatalogs(null);

        Assert.Same(ModelWindowCatalog.Baseline, await catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken));
        Assert.Same(ModelWindowCatalog.Baseline, catalogs.Current(Root));
    }

    [Fact]
    public async Task A_log_that_is_not_trusted_takes_away_nothing_an_earlier_read_learned()
    {
        var reads = 0;
        var catalogs = new ContextWindowCatalogs(new FakeLogReader(() => ++reads == 1
            ? Learned(400_000)
            : Learned(900_000) with { Verification = new DecisionLogVerification(false, 2, "la cadena no verifica") }));
        await catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);

        var after = await catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);

        Assert.Equal(2, reads);
        Assert.Equal(400_000L, after.WindowFor("claude-sonnet-5-5").Value);
        Assert.Equal(400_000L, catalogs.Current(Root).WindowFor("claude-sonnet-5-5").Value);
    }

    [Fact]
    public async Task The_log_is_never_read_on_the_thread_that_asks()
    {
        // The reader checks folders, opens SQLite and verifies the chain before its first real wait: never on the UI thread.
        var readOn = 0;
        var catalogs = new ContextWindowCatalogs(new FakeLogReader(() =>
        {
            readOn = Environment.CurrentManagedThreadId;
            return Learned(400_000);
        }));

        var askedOn = await WpfHost.RunAsync(async () =>
        {
            var ui = Environment.CurrentManagedThreadId;
            await catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);
            return ui;
        });

        Assert.NotEqual(0, readOn);
        Assert.NotEqual(askedOn, readOn);
    }

    [Fact]
    public async Task One_read_at_a_time_per_project_and_whoever_asks_meanwhile_shares_the_next_one()
    {
        var reader = new GatedLogReader(() => Learned(400_000));
        var catalogs = new ContextWindowCatalogs(reader);

        var first = catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = catalogs.RefreshAsync(Root, TestContext.Current.CancellationToken);
        var third = catalogs.RefreshAsync(Root + @"\", TestContext.Current.CancellationToken);
        reader.Release();

        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The two asked during the first read share one read after it, which sees what was written meanwhile.
        Assert.Equal(1, reader.MostAtOnce);
        Assert.Equal(2, reader.Reads);
        Assert.Equal(400_000L, (await third).WindowFor("claude-sonnet-5-5").Value);
    }

    private sealed class FakeLogReader(Func<ProjectLogSnapshot> read) : IProjectLogReader
    {
        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct) => Task.FromResult(read());
    }

    /// <summary>A reader whose first read waits until the test lets it go, and that counts how many reads run at once.</summary>
    private sealed class GatedLogReader(Func<ProjectLogSnapshot> read) : IProjectLogReader
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _reads;
        private int _most;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads => Volatile.Read(ref _reads);

        public int MostAtOnce => Volatile.Read(ref _most);

        public void Release() => _gate.TrySetResult();

        public async Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _running);
            InterlockedMax(ref _most, now);
            var number = Interlocked.Increment(ref _reads);
            try
            {
                if (number == 1)
                {
                    Started.TrySetResult();
                    await _gate.Task.ConfigureAwait(false);
                }

                return read();
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }
    }
}
