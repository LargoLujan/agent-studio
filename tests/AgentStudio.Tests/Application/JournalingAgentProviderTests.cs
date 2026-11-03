using System.IO;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Application;

public sealed class JournalingAgentProviderTests
{
    private sealed class FakeJournal(List<string> log) : IPromptJournal
    {
        public bool ThrowOnPrompt { get; set; }

        public bool ThrowOnOutcome { get; set; }

        public List<PromptJournalEntry> Entries { get; } = [];

        public List<(JournalEntryId Id, PromptOutcome Outcome)> Outcomes { get; } = [];

        public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct)
        {
            log.Add("journal.prompt");
            if (ThrowOnPrompt)
            {
                throw new IOException("disk full");
            }

            Entries.Add(entry);
            return Task.FromResult(new JournalEntryId(new DateOnly(2026, 3, 10), Entries.Count));
        }

        public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct)
        {
            log.Add("journal.outcome");
            if (ThrowOnOutcome)
            {
                throw new IOException("disk full");
            }

            Outcomes.Add((id, outcome));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRun(IReadOnlyList<AgentEvent> events) : IAgentRun
    {
        public TaskCompletionSource<AgentRunOutcome> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RunId => "run-1";

        public IAsyncEnumerable<AgentEvent> Events => Stream();

        public Task<AgentRunOutcome> Completion => Source.Task;

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

    private sealed class FakeProvider(List<string> log, FakeRun run) : IAgentProvider
    {
        public static readonly ProviderCapabilities Caps = new(true, true, false, false, true, false, 2);

        public AgentRunRequest? Received { get; private set; }

        public ProviderId Provider => ProviderId.CodexCli;

        public VendorId Vendor => VendorId.OpenAI;

        public ProviderCapabilities Capabilities => Caps;

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            log.Add("provider.start");
            Received = request;
            return Task.FromResult<IAgentRun>(run);
        }
    }

    private static AgentRunRequest Request(string prompt = "Haz esto\nlinea dos", string? purpose = null, IReadOnlyList<string>? tags = null) =>
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
            prompt,
            "C:/work",
            new PermissionProfile(false, false, false, [], [], []),
            Purpose: purpose,
            TaskId: "T-9",
            Tags: tags);

    private static UsageReported Usage(long input, long output) =>
        new(DateTimeOffset.UnixEpoch, new TokenUsage(
            MetricValue<long>.Exact(input, "u"),
            MetricValue<long>.Exact(output, "u"),
            MetricValue<long>.Unavailable("n/a"),
            MetricValue<long>.Unavailable("n/a"),
            MetricValue<decimal>.Unavailable("n/a")));

    private static AgentRunOutcome Outcome(AgentRunStatus status, string? error = null) => new(status, 0, "ok", null, null, error);

    private static (JournalingAgentProvider Sut, FakeJournal Journal, FakeProvider Inner, FakeRun Run, FakeClock Clock, List<string> Log) Build(
        IReadOnlyList<AgentEvent>? events = null)
    {
        var log = new List<string>();
        var run = new FakeRun(events ?? []);
        var inner = new FakeProvider(log, run);
        var journal = new FakeJournal(log);
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.FromHours(2)));
        return (new JournalingAgentProvider(inner, journal, clock), journal, inner, run, clock, log);
    }

    [Fact]
    public async Task Entry_is_written_before_the_provider_is_called()
    {
        var (sut, _, _, _, _, log) = Build();

        await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(["journal.prompt", "provider.start"], log);
    }

    [Fact]
    public async Task Entry_carries_actor_model_summary_tags_and_task()
    {
        var (sut, journal, _, _, clock, _) = Build();

        await sut.StartAsync(Request(tags: ["Fase 2"]), TestContext.Current.CancellationToken);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Ejecutor", entry.Actor);
        Assert.Equal(ProviderId.CodexCli, entry.Provider);
        Assert.Equal("gpt-x", entry.Model);
        Assert.Equal("Haz esto", entry.Summary);
        Assert.Equal("T-9", entry.TaskId);
        Assert.Equal(clock.GetUtcNow(), entry.At);
        Assert.Equal(["Fase 2", "#agente-exec-1"], entry.Tags);
    }

    /// <summary>docs/15 D-45: every turn and every judge of a debate or an audit goes through the journal, which says which.</summary>
    [Theory]
    [InlineData("deb-20261019-a1b2c3", "#debate")]
    [InlineData("aud-20261019-0a1b2c", "#auditoria")]
    public async Task A_run_of_a_debate_or_an_audit_is_journaled_with_its_id_and_its_tag(string debate, string tag)
    {
        var (sut, journal, _, _, _, _) = Build();

        await sut.StartAsync(Request(tags: ["#revision"]) with { DebateId = debate }, TestContext.Current.CancellationToken);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal(debate, entry.DebateId);
        Assert.Equal(["#revision", tag, "#agente-exec-1"], entry.Tags);
    }

    [Fact]
    public async Task A_run_outside_debates_and_audits_carries_neither_their_id_nor_their_tags()
    {
        var (sut, journal, _, _, _, _) = Build();

        await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        var entry = Assert.Single(journal.Entries);
        Assert.Null(entry.DebateId);
        Assert.DoesNotContain(entry.Tags, t => t is "#debate" or "#auditoria");
    }

    [Fact]
    public async Task A_tag_already_asked_for_is_not_repeated()
    {
        var (sut, journal, _, _, _, _) = Build();

        await sut.StartAsync(Request(tags: ["#debate"]) with { DebateId = "deb-20261019-a1b2c3" }, TestContext.Current.CancellationToken);

        Assert.Equal(["#debate", "#agente-exec-1"], Assert.Single(journal.Entries).Tags);
    }

    [Fact]
    public async Task Purpose_wins_over_the_prompt_and_long_first_lines_are_trimmed_to_120()
    {
        var (sut, journal, _, _, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        await sut.StartAsync(Request(purpose: "Revisar el diff"), ct);
        await sut.StartAsync(Request(prompt: new string('a', 300)), ct);

        Assert.Equal("Revisar el diff", journal.Entries[0].Summary);
        Assert.Equal(120, journal.Entries[1].Summary.Length);
    }

    [Fact]
    public async Task Provider_is_not_called_when_the_journal_fails()
    {
        var (sut, journal, inner, _, _, log) = Build();
        journal.ThrowOnPrompt = true;

        await Assert.ThrowsAsync<IOException>(() => sut.StartAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Null(inner.Received);
        Assert.Equal(["journal.prompt"], log);
    }

    [Fact]
    public async Task Outcome_is_recorded_after_completion_on_success_with_last_usage_and_duration()
    {
        var (sut, journal, _, run, clock, _) = Build([Usage(1, 2), Usage(1200, 340)]);
        var ct = TestContext.Current.CancellationToken;
        await using var wrapped = await sut.StartAsync(Request(), ct);

        await foreach (var _ in wrapped.Events.WithCancellation(ct))
        {
        }

        clock.Advance(TimeSpan.FromSeconds(5));
        run.Source.SetResult(Outcome(AgentRunStatus.Succeeded));
        var result = await wrapped.Completion;

        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        var (id, outcome) = Assert.Single(journal.Outcomes);
        Assert.Equal(1, id.Sequence);
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal(TimeSpan.FromSeconds(5), outcome.Duration);
        Assert.Equal(1200L, outcome.InputTokens.Value);
        Assert.Equal(340L, outcome.OutputTokens.Value);
    }

    [Fact]
    public async Task Tokens_are_unavailable_when_no_usage_was_reported()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        run.Source.SetResult(Outcome(AgentRunStatus.Succeeded));
        await wrapped.Completion;

        var outcome = Assert.Single(journal.Outcomes).Outcome;
        Assert.False(outcome.InputTokens.HasValue);
        Assert.Equal(MetricProvenance.Unavailable, outcome.InputTokens.Provenance);
        Assert.Equal("no reportado", outcome.InputTokens.Source);
    }

    [Fact]
    public async Task Outcome_is_recorded_on_failure_status_with_the_error_as_note()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        run.Source.SetResult(Outcome(AgentRunStatus.Failed, "boom"));
        await wrapped.Completion;

        var outcome = Assert.Single(journal.Outcomes).Outcome;
        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal("boom", outcome.Note);
    }

    [Fact]
    public async Task Outcome_is_recorded_on_exception_and_the_exception_still_surfaces()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        run.Source.SetException(new InvalidOperationException("crash"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrapped.Completion);
        var outcome = Assert.Single(journal.Outcomes).Outcome;
        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Contains("crash", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outcome_is_recorded_on_cancellation()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        run.Source.SetCanceled(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrapped.Completion);
        Assert.Equal(AgentRunStatus.Cancelled, Assert.Single(journal.Outcomes).Outcome.Status);
    }

    [Fact]
    public async Task A_failure_recording_the_outcome_does_not_hide_the_agent_result()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);
        journal.ThrowOnOutcome = true;

        run.Source.SetResult(Outcome(AgentRunStatus.Succeeded));
        var result = await wrapped.Completion;

        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Properties_pass_through_to_the_inner_provider_and_run()
    {
        var (sut, _, _, _, _, _) = Build();

        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderId.CodexCli, sut.Provider);
        Assert.Equal(VendorId.OpenAI, sut.Vendor);
        Assert.Same(FakeProvider.Caps, sut.Capabilities);
        Assert.Equal("run-1", wrapped.RunId);
    }

    [Fact]
    public async Task Tokens_are_recorded_even_if_nobody_enumerates_events()
    {
        var (sut, journal, _, run, _, _) = Build([Usage(1, 2), Usage(1200, 340)]);
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        run.Source.SetResult(Outcome(AgentRunStatus.Succeeded));
        await wrapped.Completion;

        var outcome = Assert.Single(journal.Outcomes).Outcome;
        Assert.Equal(1200L, outcome.InputTokens.Value);
        Assert.Equal(340L, outcome.OutputTokens.Value);
    }

    [Fact]
    public async Task Events_read_after_completion_are_still_delivered_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new AgentEvent[] { Usage(1, 2), Usage(3, 4) };
        var (sut, _, _, run, _, _) = Build(events);
        var wrapped = await sut.StartAsync(Request(), ct);
        run.Source.SetResult(Outcome(AgentRunStatus.Succeeded));
        await wrapped.Completion;

        var received = new List<AgentEvent>();
        await foreach (var e in wrapped.Events.WithCancellation(ct))
        {
            received.Add(e);
        }

        Assert.Equal(events, received);
    }

    [Fact]
    public async Task Disposing_before_completion_records_one_cancelled_outcome_and_never_a_second()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);

        await wrapped.DisposeAsync();
        run.Source.SetResult(Outcome(AgentRunStatus.Succeeded));
        await wrapped.Completion;

        var outcome = Assert.Single(journal.Outcomes).Outcome;
        Assert.Equal(AgentRunStatus.Cancelled, outcome.Status);
        Assert.Equal("descartado antes de terminar", outcome.Note);
    }

    [Fact]
    public async Task Reading_events_to_the_end_and_disposing_without_awaiting_completion_journals_the_real_outcome()
    {
        var finished = Outcome(AgentRunStatus.Succeeded);
        var (sut, journal, _, run, _, _) = Build([new RunFinished(DateTimeOffset.UnixEpoch, finished)]);
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);
        await foreach (var unused in wrapped.Events.WithCancellation(TestContext.Current.CancellationToken))
        {
        }

        // The inner run completes a moment after its event stream ended.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            run.Source.SetResult(finished);
        }, TestContext.Current.CancellationToken);
        await wrapped.DisposeAsync();

        Assert.Equal(AgentRunStatus.Succeeded, Assert.Single(journal.Outcomes).Outcome.Status);
    }

    [Fact]
    public async Task Disposing_after_the_inner_run_failed_journals_failed_exactly_once()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);
        run.Source.SetResult(Outcome(AgentRunStatus.Failed, "boom"));

        await wrapped.DisposeAsync();
        await wrapped.Completion;

        var outcome = Assert.Single(journal.Outcomes).Outcome;
        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal("boom", outcome.Note);
    }

    [Fact]
    public async Task Disposing_after_completion_does_not_record_a_second_outcome()
    {
        var (sut, journal, _, run, _, _) = Build();
        var wrapped = await sut.StartAsync(Request(), TestContext.Current.CancellationToken);
        run.Source.SetResult(Outcome(AgentRunStatus.Succeeded));
        await wrapped.Completion;

        await wrapped.DisposeAsync();

        Assert.Equal(AgentRunStatus.Succeeded, Assert.Single(journal.Outcomes).Outcome.Status);
    }
}
