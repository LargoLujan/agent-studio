using System.Threading.Channels;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Composition;

namespace AgentStudio.Tests.Providers.Composition;

public sealed class ProviderCompositionTests
{
    private sealed class FakeRun(string id) : IAgentRun
    {
        public TaskCompletionSource<AgentRunOutcome> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RunId => id;

        public IAsyncEnumerable<AgentEvent> Events => Channel.CreateUnbounded<AgentEvent>().Reader.ReadAllAsync();

        public Task<AgentRunOutcome> Completion => Source.Task;

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProvider(int maxConcurrent) : IAgentProvider
    {
        public List<FakeRun> Runs { get; } = [];

        public bool ThrowOnStart { get; set; }

        public ProviderId Provider => ProviderId.CodexCli;

        public VendorId Vendor => VendorId.OpenAI;

        public ProviderCapabilities Capabilities => new(true, true, false, false, true, false, maxConcurrent);

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("boom");
            }

            var run = new FakeRun("r" + Runs.Count);
            Runs.Add(run);
            return Task.FromResult<IAgentRun>(run);
        }
    }

    private sealed class FakeJournal : IPromptJournal
    {
        public int Prompts { get; private set; }

        public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(new JournalEntryId(new DateOnly(2026, 3, 10), Prompts));
        }

        public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct) => Task.CompletedTask;
    }

    private static AgentRunRequest Request() =>
        new(
            new AgentDefinition(
                new AgentId("Exec-1"),
                "Ejecutor",
                AgentRole.Executor,
                new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-x"),
                "sys",
                [],
                [],
                [],
                new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
                3,
                MemoryPolicy.None,
                new ReviewPolicy(false, false)),
            "Haz esto",
            "C:/work",
            new PermissionProfile(false, false, false, [], [], []));

    private static AgentRunOutcome Done() => new(AgentRunStatus.Succeeded, 0, "ok", null, null, null);

    private static async Task<bool> CompletesQuicklyAsync(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(150, TestContext.Current.CancellationToken));
        return finished == task;
    }

    [Fact]
    public async Task Concurrency_SecondRunWaitsUntilFirstCompletionNotUntilStartReturns()
    {
        var inner = new FakeProvider(1);
        var limited = new ConcurrencyLimitedAgentProvider(inner);
        var ct = TestContext.Current.CancellationToken;

        var first = await limited.StartAsync(Request(), ct);
        var second = limited.StartAsync(Request(), ct);

        Assert.False(await CompletesQuicklyAsync(second));
        Assert.Single(inner.Runs);

        inner.Runs[0].Source.SetResult(Done());
        await first.Completion;
        var secondRun = await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(2, inner.Runs.Count);
        Assert.NotNull(secondRun);
    }

    [Fact]
    public async Task Concurrency_PermitIsReleasedOnFailure()
    {
        var inner = new FakeProvider(1);
        var limited = new ConcurrencyLimitedAgentProvider(inner);
        var ct = TestContext.Current.CancellationToken;

        var first = await limited.StartAsync(Request(), ct);
        var second = limited.StartAsync(Request(), ct);
        inner.Runs[0].Source.SetException(new InvalidOperationException("fail"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Completion);

        Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task Concurrency_PermitIsReleasedOnDispose()
    {
        var inner = new FakeProvider(1);
        var limited = new ConcurrencyLimitedAgentProvider(inner);
        var ct = TestContext.Current.CancellationToken;

        var first = await limited.StartAsync(Request(), ct);
        var second = limited.StartAsync(Request(), ct);
        Assert.False(await CompletesQuicklyAsync(second));

        await first.DisposeAsync();
        Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task Concurrency_PermitIsReleasedWhenInnerStartThrows()
    {
        var inner = new FakeProvider(1) { ThrowOnStart = true };
        var limited = new ConcurrencyLimitedAgentProvider(inner);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => limited.StartAsync(Request(), ct));
        inner.ThrowOnStart = false;
        Assert.NotNull(await limited.StartAsync(Request(), ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task Concurrency_WaitingStartHonoursCancellation()
    {
        var inner = new FakeProvider(1);
        var limited = new ConcurrencyLimitedAgentProvider(inner);
        var ct = TestContext.Current.CancellationToken;
        await limited.StartAsync(Request(), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiting = limited.StartAsync(Request(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(inner.Runs);
    }

    [Fact]
    public void Concurrency_LimitDefaultsToCapabilitiesAndIsNeverBelowOne()
    {
        Assert.Equal(3, new ConcurrencyLimitedAgentProvider(new FakeProvider(3)).Limit);
        Assert.Equal(1, new ConcurrencyLimitedAgentProvider(new FakeProvider(0)).Limit);
        Assert.Equal(1, new ConcurrencyLimitedAgentProvider(new FakeProvider(4), -2).Limit);
        Assert.Equal(2, new ConcurrencyLimitedAgentProvider(new FakeProvider(4), 2).Limit);
    }

    [Fact]
    public async Task Concurrency_AllowsUpToLimitSimultaneously()
    {
        var inner = new FakeProvider(1);
        var limited = new ConcurrencyLimitedAgentProvider(inner, 2);
        var ct = TestContext.Current.CancellationToken;

        await limited.StartAsync(Request(), ct);
        await limited.StartAsync(Request(), ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(await CompletesQuicklyAsync(limited.StartAsync(Request(), ct)));
    }

    [Fact]
    public void Pipeline_ComposeAlwaysReturnsConcurrencyOverJournaling()
    {
        var adapter = new FakeProvider(2);
        var composed = AgentProviderPipeline.Compose(adapter, new FakeJournal(), TimeProvider.System, null);

        var limited = Assert.IsType<ConcurrencyLimitedAgentProvider>(composed);
        Assert.IsType<JournalingAgentProvider>(limited.Inner);
        Assert.Equal(2, limited.Limit);
        Assert.True(AgentProviderPipeline.IsJournaled(composed));
    }

    [Fact]
    public void Pipeline_IsJournaledIsFalseForABareAdapterAndOnlyConcurrency()
    {
        var adapter = new FakeProvider(2);
        Assert.False(AgentProviderPipeline.IsJournaled(adapter));
        Assert.False(AgentProviderPipeline.IsJournaled(new ConcurrencyLimitedAgentProvider(adapter)));
    }

    [Fact]
    public async Task Pipeline_JournalEntryIsWrittenOnlyWhenTheQueuedRunReallyStarts()
    {
        var adapter = new FakeProvider(1);
        var journal = new FakeJournal();
        var composed = AgentProviderPipeline.Compose(adapter, journal, TimeProvider.System, 1);
        var ct = TestContext.Current.CancellationToken;

        var first = await composed.StartAsync(Request(), ct);
        var second = composed.StartAsync(Request(), ct);
        Assert.False(await CompletesQuicklyAsync(second));
        Assert.Equal(1, journal.Prompts);

        adapter.Runs[0].Source.SetResult(Done());
        await first.Completion;
        await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(2, journal.Prompts);
    }
}
