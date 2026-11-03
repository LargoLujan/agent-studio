using System.Runtime.CompilerServices;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Application;

/// <summary>
/// A provider that never completes its event stream (a contract violation) must not leave the outcome unrecorded:
/// the decorator waits a bounded time and records the usage seen so far.
/// </summary>
public sealed class JournalingEventDrainTests
{
    private sealed class RecordingJournal : IPromptJournal
    {
        public List<PromptOutcome> Outcomes { get; } = [];

        public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct) =>
            Task.FromResult(new JournalEntryId(new DateOnly(2026, 3, 10), 1));

        public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct)
        {
            lock (Outcomes)
            {
                Outcomes.Add(outcome);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class HangingRun : IAgentRun
    {
        public TaskCompletionSource<AgentRunOutcome> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RunId => "hanging";

        public IAsyncEnumerable<AgentEvent> Events => Stream();

        public Task<AgentRunOutcome> Completion => Source.Task;

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<AgentEvent> Stream([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new UsageReported(DateTimeOffset.UnixEpoch, new TokenUsage(
                MetricValue<long>.Exact(1200, "test"),
                MetricValue<long>.Exact(340, "test"),
                MetricValue<long>.Unavailable("n/a"),
                MetricValue<long>.Unavailable("n/a"),
                MetricValue<decimal>.Unavailable("n/a")));

            // Never completes until cancelled: the faulty behaviour under test.
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed class HangingProvider(HangingRun run) : IAgentProvider
    {
        public ProviderId Provider => ProviderId.CodexCli;

        public VendorId Vendor => VendorId.OpenAI;

        public ProviderCapabilities Capabilities => new(true, false, false, false, true, false, 1);

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IAgentRun>(run);
    }

    [Fact]
    public async Task Outcome_is_recorded_even_if_the_event_stream_never_completes()
    {
        var run = new HangingRun();
        var journal = new RecordingJournal();
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero));
        var sut = new JournalingAgentProvider(new HangingProvider(run), journal, clock, TimeSpan.FromMilliseconds(200));

        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // let the pump read the usage event
        run.Source.SetResult(new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "ok", null, null, null));

        var result = await wrapped.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        var outcome = Assert.Single(journal.Outcomes);
        Assert.Equal(1200, outcome.InputTokens.Value);
        Assert.Equal(340, outcome.OutputTokens.Value);
        await wrapped.DisposeAsync();
        Assert.Single(journal.Outcomes); // disposing afterwards never adds a second outcome
    }

    private static AgentRunRequest Request() =>
        new(
            new AgentDefinition(
                new AgentId("exec-1"),
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
}
