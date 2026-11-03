using System.Runtime.CompilerServices;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Providers.Composition;

/// <summary>
/// The result line of the prompt journal (docs/14 WP1): a usage that never came is «sin dato» (the CLI offers it), and the
/// usage of a resumed session is the total of the whole session, so the line says so (D-02).
/// </summary>
public sealed class JournalingUsageNoteTests
{
    private sealed class Journal : IPromptJournal
    {
        public List<PromptOutcome> Outcomes { get; } = [];

        public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct) =>
            Task.FromResult(new JournalEntryId(new DateOnly(2026, 10, 18), 1));

        public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class Run(IReadOnlyList<AgentEvent> events, AgentRunOutcome outcome) : IAgentRun
    {
        public string RunId => "run";

        public IAsyncEnumerable<AgentEvent> Events => Stream();

        public Task<AgentRunOutcome> Completion { get; } = Task.FromResult(outcome);

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<AgentEvent> Stream([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class FakeProvider(Run run) : IAgentProvider
    {
        public ProviderId Provider => ProviderId.ClaudeCode;

        public VendorId Vendor => VendorId.Anthropic;

        public ProviderCapabilities Capabilities { get; } = new(true, true, true, false, true, true, 1);

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken) => Task.FromResult<IAgentRun>(run);
    }

    private static readonly UsageReported Usage = new(
        DateTimeOffset.UnixEpoch,
        new TokenUsage(
            MetricValue<long>.Exact(1200, "claude result.usage"),
            MetricValue<long>.Exact(340, "claude result.usage"),
            MetricValue<long>.Exact(1000, "claude result.usage"),
            MetricValue<long>.Exact(200_000, "claude result.modelUsage"),
            MetricValue<decimal>.Estimated(0.01m, "estimación del CLI")));

    private static AgentRunRequest Request(string? resume) =>
        new(
            new AgentDefinition(
                new AgentId("ejecutor"),
                "Ejecutor",
                AgentRole.Executor,
                new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5"),
                string.Empty,
                [],
                [],
                [],
                new ExecutionBudget(4, TimeSpan.FromMinutes(5)),
                1,
                MemoryPolicy.None,
                new ReviewPolicy(false, false)),
            "Haz esto",
            "C:/work",
            new PermissionProfile(false, false, false, [], [], []),
            ResumeSessionId: resume);

    private static async Task<PromptOutcome> OutcomeOfAsync(AgentRunRequest request, IReadOnlyList<AgentEvent> events, AgentRunOutcome outcome)
    {
        var journal = new Journal();
        var sut = new JournalingAgentProvider(new FakeProvider(new Run(events, outcome)), journal, new FakeClock(DateTimeOffset.UnixEpoch));
        await using var run = await sut.StartAsync(request, TestContext.Current.CancellationToken);
        await run.Completion;
        return Assert.Single(journal.Outcomes);
    }

    private static AgentRunOutcome Ended(AgentRunStatus status = AgentRunStatus.Succeeded, string? error = null) => new(status, 0, "ok", null, "sess-1", error);

    [Fact]
    public async Task Tokens_that_never_came_are_missing_not_unexposed()
    {
        var outcome = await OutcomeOfAsync(Request(resume: null), [], Ended());

        Assert.Equal((MetricOrigin.Missing, MetricOrigin.Missing), (outcome.InputTokens.Origin, outcome.OutputTokens.Origin));
        Assert.Null(outcome.Note);
    }

    [Fact]
    public async Task The_usage_of_a_resumed_session_is_noted_as_the_total_of_the_session()
    {
        var outcome = await OutcomeOfAsync(Request(resume: "sess-1"), [Usage], Ended());

        Assert.Equal(1200L, outcome.InputTokens.Value);
        Assert.Equal("(acumulado de la sesión)", outcome.Note);
    }

    [Fact]
    public async Task A_new_session_reports_its_own_usage_without_the_note()
    {
        var outcome = await OutcomeOfAsync(Request(resume: null), [Usage], Ended());

        Assert.Null(outcome.Note);
    }

    [Fact]
    public async Task A_resumed_session_that_failed_keeps_the_note_and_the_error()
    {
        var outcome = await OutcomeOfAsync(Request(resume: "sess-1"), [Usage], Ended(AgentRunStatus.Failed, "boom"));

        Assert.Equal("(acumulado de la sesión) · boom", outcome.Note);
    }

    [Fact]
    public async Task A_resumed_session_without_usage_says_nothing_about_the_session()
    {
        var outcome = await OutcomeOfAsync(Request(resume: "sess-1"), [], Ended(AgentRunStatus.Failed, "boom"));

        Assert.Equal("boom", outcome.Note);
    }
}
