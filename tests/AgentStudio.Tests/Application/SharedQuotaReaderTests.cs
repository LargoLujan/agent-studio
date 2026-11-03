using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/18 P37: the engine (a limit cut an attempt of Codex) and the screens (a run of Codex ended) may ask the quota of Codex
/// at the same time; the app-server is launched once and both get its answer, off the thread that asks. The reader here is a
/// double: no app-server is ever launched.
/// </summary>
public sealed class SharedQuotaReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_asked_at_the_same_time_are_one_and_a_read_asked_later_is_a_new_one()
    {
        var inner = new GatedReader();
        var shared = new SharedQuotaReader(inner);

        var engine = shared.ReadAsync(Ct);
        var screen = shared.ReadAsync(Ct);
        inner.Release();

        Assert.Same(await engine, await screen);
        Assert.Equal(1, inner.Reads);

        await shared.ReadAsync(Ct);
        Assert.Equal(2, inner.Reads);
        Assert.Equal(ProviderId.CodexCli, shared.Provider);
    }

    [Fact]
    public async Task A_caller_that_gives_up_only_stops_waiting_and_the_read_of_the_others_goes_on()
    {
        var inner = new GatedReader();
        var shared = new SharedQuotaReader(inner);
        using var gaveUp = new CancellationTokenSource();

        var impatient = shared.ReadAsync(gaveUp.Token);
        var patient = shared.ReadAsync(Ct);
        await gaveUp.CancelAsync();
        inner.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        Assert.Equal(QuotaReadStatus.Read, (await patient).Status);
        Assert.Equal(1, inner.Reads);
        Assert.False(inner.SawCancellation);
    }

    [Fact]
    public async Task The_read_never_runs_on_the_thread_that_asks()
    {
        var inner = new GatedReader();
        inner.Release();
        var shared = new SharedQuotaReader(inner);

        var askedOn = await WpfHost.RunAsync(async () =>
        {
            var ui = Environment.CurrentManagedThreadId;
            await shared.ReadAsync(Ct);
            return ui;
        });

        Assert.NotEqual(0, inner.ReadOn);
        Assert.NotEqual(askedOn, inner.ReadOn);
    }

    /// <summary>A reader of Codex whose reads wait until the test lets them go, and that says where and how often it read.</summary>
    private sealed class GatedReader : IProviderQuotaReader
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public ProviderId Provider => ProviderId.CodexCli;

        public int Reads => Volatile.Read(ref _reads);

        public int ReadOn { get; private set; }

        public bool SawCancellation { get; private set; }

        public void Release() => _gate.TrySetResult();

        public async Task<QuotaReadResult> ReadAsync(CancellationToken ct)
        {
            ReadOn = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _reads);
            await _gate.Task.ConfigureAwait(false);
            SawCancellation = ct.IsCancellationRequested;
            return new QuotaReadResult(QuotaReadStatus.Read, []);
        }
    }
}
