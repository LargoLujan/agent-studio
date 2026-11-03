using System.IO;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/14 D-08: an agent run outside an orchestration (the assisted skill recommendation, the narrative daily summary)
/// is recorded like the judge: the prompt as evidence, <c>AgentRunStarted</c> before the launch, <c>AgentRunFinished</c>
/// version 2 at the end, under a run id of its own. A log that does not verify launches nothing.
/// </summary>
public sealed class AuxiliaryAgentRunnerTests
{
    private static readonly ProjectContext Project = new("6f1c2b0e-0000-4000-8000-0000000000aa", "C:\\repo");

    private static readonly AgentDefinition Recommender = new(
        Aid("recomendador"),
        "Recomendador",
        AgentRole.Critic,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5"),
        "Recomienda skills.",
        [],
        ["Read", "Grep", "Glob"],
        [],
        new ExecutionBudget(4, TimeSpan.FromMinutes(5)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    private static AgentRunRequest Request(string? resume = null) =>
        new(Recommender, "¿Qué skills necesita esta tarea?", "C:\\runs\\aux", new PermissionProfile(false, false, false, ["Read", "Grep", "Glob"], [], []), ResumeSessionId: resume);

    private sealed class World
    {
        public World(Func<AgentRunRequest, IAgentRun>? start = null, ProviderCapabilities? capabilities = null)
        {
            Clock = new FakeTimeProvider(T0);
            Recorder = new DecisionRecorder(Store, Clock);
            Evidence = new FakeEvidenceStore(Clock);
            Provider = new FakeProvider(this, start ?? (_ => new FakeRun([], Succeeded("{\"recommendations\":[]}"))), capabilities);
            Runner = new AuxiliaryAgentRunner(Recorder, Evidence, new FakeProviders(Provider), Clock);
        }

        public FakeTimeProvider Clock { get; }

        public InMemoryDecisionLogStore Store { get; } = new();

        public DecisionRecorder Recorder { get; }

        public FakeEvidenceStore Evidence { get; }

        public FakeProvider Provider { get; }

        public AuxiliaryAgentRunner Runner { get; }

        public IReadOnlyList<DecisionEntry> Entries => Store.Of(Project.ProjectId);

        public Task<AuxiliaryRunResult> RunAsync(AgentRunPurpose purpose = AgentRunPurpose.SkillRecommendation, AgentRunRequest? request = null, IProgress<AgentEvent>? progress = null, CancellationToken? ct = null) =>
            Runner.RunAsync(Project, new AuxiliaryRunRequest(purpose, request ?? Request()), progress, ct ?? TestContext.Current.CancellationToken);

        public T Payload<T>(DecisionKind kind)
            where T : DecisionPayload => DecisionPayloadSerializer.Deserialize<T>(Assert.Single(Entries, e => e.Kind == kind).PayloadJson);
    }

    private static AgentRunOutcome Succeeded(string json) => new(AgentRunStatus.Succeeded, 0, null, json, "sess-aux", null);

    private sealed class FakeProviders(params IAgentProvider[] providers) : IProjectAgentProviders
    {
        public IReadOnlyList<IAgentProvider> For(string projectRoot) => providers;
    }

    private sealed class FakeProvider(World world, Func<AgentRunRequest, IAgentRun> start, ProviderCapabilities? capabilities) : IAgentProvider
    {
        public ProviderId Provider => ProviderId.ClaudeCode;

        public VendorId Vendor => VendorId.Anthropic;

        public ProviderCapabilities Capabilities { get; } = capabilities ?? new ProviderCapabilities(true, true, true, false, true, true, 1) { ReportsContextOccupancy = true };

        public List<AgentRunRequest> Requests { get; } = [];

        /// <summary>The decision log as it was when the launch happened.</summary>
        public IReadOnlyList<DecisionEntry>? LogAtLaunch { get; private set; }

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LogAtLaunch = world.Entries;
            world.Clock.Advance(TimeSpan.FromSeconds(2)); // the wait in the provider's queue
            return Task.FromResult(start(request));
        }
    }

    private sealed class FakeRun(IReadOnlyList<AgentEvent> events, AgentRunOutcome outcome, TaskCompletionSource? hold = null) : IAgentRun
    {
        public string RunId => "fake";

        public bool Cancelled { get; private set; }

        public IAsyncEnumerable<AgentEvent> Events => Stream();

        public Task<AgentRunOutcome> Completion => Finish();

        public Task CancelAsync()
        {
            Cancelled = true;
            hold?.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<AgentRunOutcome> Finish()
        {
            if (hold is not null)
            {
                await hold.Task;
            }

            return Cancelled ? outcome with { Status = AgentRunStatus.Cancelled } : outcome;
        }

        private async IAsyncEnumerable<AgentEvent> Stream([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var agentEvent in events)
            {
                await Task.Yield();
                yield return agentEvent;
            }

            if (hold is not null)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class ListProgress : IProgress<AgentEvent>
    {
        public List<AgentEvent> Reported { get; } = [];

        public void Report(AgentEvent value) => Reported.Add(value);
    }

    [Fact]
    public async Task The_start_is_recorded_in_version_2_with_its_purpose_and_prompt_before_the_agent_is_launched()
    {
        var world = new World();

        var result = await world.RunAsync();

        var atLaunch = Assert.Single(world.Provider.LogAtLaunch!);
        Assert.Equal(DecisionKind.AgentRunStarted, atLaunch.Kind);
        var started = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(atLaunch.PayloadJson);
        Assert.Equal((AgentRunStartedPayload.PurposeVersion, AgentRunPurpose.SkillRecommendation, result.Run.Value), (started.V, started.Purpose, started.RunId));
        Assert.Equal((null, 1, Recommender.Id, ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5"), (started.Task, started.Attempt, started.Agent, started.Provider, started.Vendor, started.Model));
        Assert.Null(started.ContextPack);
        var prompt = Assert.Single(await world.Evidence.ListAsync(Project.ProjectId, TestContext.Current.CancellationToken), e => e.Id == started.PromptEvidenceId);
        Assert.Equal((prompt.ContentSha256, EvidenceOrigin.CapturedBySystem), (started.PromptSha256, prompt.Origin));
        Assert.Equal("¿Qué skills necesita esta tarea?", world.Evidence.ContentText(prompt.Id));
        Assert.Equal(result.PromptEvidence, prompt.Id);
        Assert.Equal(new ActorKind[] { ActorKind.System, ActorKind.System }, world.Entries.Select(e => e.Actor.Kind));
    }

    /// <summary>docs/17 D-02: the launch carries the start just recorded, so the journal can name it at the end of its result line.</summary>
    [Fact]
    public async Task The_launch_carries_the_start_it_was_announced_with()
    {
        var world = new World();

        var result = await world.RunAsync();

        var started = world.Payload<AgentRunStartedPayload>(DecisionKind.AgentRunStarted);
        Assert.Equal(new AgentRunAnchor(result.Run.Value, null, 1), Assert.Single(world.Provider.Requests).Anchor);
        Assert.Equal((started.RunId, started.Task, started.Attempt), (result.Run.Value, null, 1));
    }

    [Fact]
    public async Task The_end_is_recorded_in_version_2_with_launch_time_usage_context_peak_compactions_and_answer()
    {
        var usage = new TokenUsage(
            MetricValue<long>.Exact(30_822, "claude result.usage"),
            MetricValue<long>.Exact(512, "claude result.usage"),
            MetricValue<long>.Exact(30_391, "claude result.usage"),
            MetricValue<long>.Exact(200_000, "claude result.modelUsage"),
            MetricValue<decimal>.Estimated(0.01m, "claude total_cost_usd"))
        {
            Turns = 3,
        };
        AgentEvent[] events =
        [
            new SessionStarted(T0, "sess-aux", "claude-haiku-4-5"),
            new ContextObserved(T0, "claude-haiku-4-5", MetricValue<long>.Exact(12_000, "claude assistant.message.usage"), MetricValue<long>.Missing("window not known yet")),
            new ContextObserved(T0, "claude-haiku-4-5", MetricValue<long>.Exact(30_822, "claude assistant.message.usage"), MetricValue<long>.Exact(200_000, "claude result.modelUsage")),
            new ContextObserved(T0, "claude-haiku-4-5", MetricValue<long>.Exact(20_000, "claude assistant.message.usage"), MetricValue<long>.Exact(200_000, "claude result.modelUsage")),
            new CompactionObserved(T0, "auto", MetricValue<long>.Exact(30_000, "claude compact_boundary")),
            new UsageReported(T0, usage),
        ];
        var world = new World(_ => new FakeRun(events, Succeeded("{\"recommendations\":[]}")));
        var progress = new ListProgress();
        var launchedAt = T0.AddSeconds(2);

        var result = await world.RunAsync(request: Request(resume: "sess-old"), progress: progress);

        var finished = world.Payload<AgentRunFinishedPayload>(DecisionKind.AgentRunFinished);
        Assert.Equal((AgentRunFinishedPayload.UsageVersion, result.Run.Value, AttemptOutcome.Succeeded), (finished.V, finished.RunId, finished.Status));
        Assert.Equal((launchedAt, "claude-haiku-4-5", "sess-old", "sess-aux"), (finished.LaunchedAt!.Value, finished.ReportedModel, finished.ResumedSession, finished.SessionId));
        Assert.Equal((1, 3), (finished.Compactions, finished.Turns));
        Assert.Equal(30_822m, finished.ContextPeak!.UsedTokens.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, finished.ContextPeak.ContextWindow.Origin);
        Assert.Equal(MetricOrigin.EstimatedByProvider, finished.Usage!.CostUsd.Origin);
        Assert.Equal(MetricOrigin.NotExposed, finished.Usage.CacheWriteInputTokens!.Origin);
        var answer = Assert.Single(await world.Evidence.ListAsync(Project.ProjectId, TestContext.Current.CancellationToken), e => e.Id == finished.ReportEvidenceId);
        Assert.Equal((EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent), (answer.Kind, answer.Origin));
        Assert.Equal("{\"recommendations\":[]}", world.Evidence.ContentText(answer.Id));
        Assert.Equal((AgentRunStatus.Succeeded, answer.Id), (result.Outcome.Status, result.AnswerEvidence!.Value));
        Assert.Same(usage, result.Usage);
        Assert.Equal(events, progress.Reported);
    }

    [Fact]
    public async Task The_peak_takes_the_window_of_its_model_from_the_breakdown_that_comes_at_the_end()
    {
        // Claude gives the window only in result.modelUsage, after every step was observed without it.
        var usage = new TokenUsage(
            MetricValue<long>.Exact(9_000, "claude result.usage"),
            MetricValue<long>.Exact(300, "claude result.usage"),
            MetricValue<long>.Exact(8_000, "claude result.usage"),
            MetricValue<long>.Exact(200_000, "claude result.modelUsage"),
            MetricValue<decimal>.Estimated(0.01m, "claude total_cost_usd"))
        {
            PerModel =
            [
                new ModelUsage(
                    "claude-haiku-4-5-20251001",
                    MetricValue<long>.Exact(9_000, "claude result.modelUsage"),
                    MetricValue<long>.Exact(300, "claude result.modelUsage"),
                    MetricValue<long>.Exact(8_000, "claude result.modelUsage"),
                    MetricValue<long>.Exact(0, "claude result.modelUsage"),
                    MetricValue<decimal>.Estimated(0.01m, "claude result.modelUsage"),
                    MetricValue<long>.Exact(200_000, "claude result.modelUsage"),
                    "list price"),
            ],
        };
        AgentEvent[] events =
        [
            new ContextObserved(T0, "claude-haiku-4-5-20251001", MetricValue<long>.Exact(9_000, "claude assistant.message.usage"), MetricValue<long>.Missing("window not known yet")),
            new UsageReported(T0, usage),
        ];
        var world = new World(_ => new FakeRun(events, Succeeded("{\"recommendations\":[]}")));

        await world.RunAsync();

        var peak = world.Payload<AgentRunFinishedPayload>(DecisionKind.AgentRunFinished).ContextPeak!;
        Assert.Equal(9_000m, peak.UsedTokens.Value);
        Assert.Equal(200_000m, peak.ContextWindow.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, peak.ContextWindow.Origin);
    }

    [Fact]
    public async Task Without_context_signals_the_compactions_are_not_exposed_and_there_is_no_peak()
    {
        var world = new World(capabilities: new ProviderCapabilities(true, true, true, false, true, false, 1));

        await world.RunAsync(AgentRunPurpose.DailySummary);

        var finished = world.Payload<AgentRunFinishedPayload>(DecisionKind.AgentRunFinished);
        Assert.Null(finished.Compactions);
        Assert.Null(finished.ContextPeak);
        Assert.Null(finished.ReportedModel);
        Assert.Null(finished.Usage);
        Assert.Equal(AgentRunPurpose.DailySummary, world.Payload<AgentRunStartedPayload>(DecisionKind.AgentRunStarted).Purpose);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_stops_the_run_before_anything_is_captured_or_launched()
    {
        var world = new World();
        await world.RunAsync();
        world.Store.Tamper(Project.ProjectId, 1, e => e with { Summary = "reescrita" });
        var drafts = world.Evidence.Drafts.Count;
        world.Provider.Requests.Clear();

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => world.RunAsync());

        Assert.Empty(world.Provider.Requests);
        Assert.Equal(drafts, world.Evidence.Drafts.Count);
        Assert.Equal(2, world.Entries.Count);
    }

    [Fact]
    public async Task A_start_that_cannot_be_recorded_launches_nothing()
    {
        var world = new World();
        world.Store.FailBeforeStoring = 1;

        await Assert.ThrowsAnyAsync<IOException>(() => world.RunAsync());

        Assert.Empty(world.Provider.Requests);
        Assert.Empty(world.Entries);
    }

    [Theory]
    [InlineData(AgentRunPurpose.Planner)]
    [InlineData(AgentRunPurpose.Executor)]
    [InlineData(AgentRunPurpose.Judge)]
    [InlineData(AgentRunPurpose.DebateParticipant)]
    [InlineData(AgentRunPurpose.DebateJudge)]
    [InlineData(AgentRunPurpose.ProjectAudit)]
    public async Task The_runs_of_the_engine_and_of_the_review_are_never_auxiliary(AgentRunPurpose purpose)
    {
        var world = new World();

        await Assert.ThrowsAsync<ArgumentException>(() => world.RunAsync(purpose));

        Assert.Empty(world.Entries);
        Assert.Empty(world.Provider.Requests);
    }

    [Fact]
    public async Task An_agent_the_domain_refuses_is_never_launched()
    {
        var world = new World();
        var proposer = Request() with { Agent = Recommender with { Memory = MemoryPolicy.ReadAndPropose } };

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => world.RunAsync(request: proposer));

        Assert.Contains("agent.memory-proposals-not-available", refused.Message, StringComparison.Ordinal);
        Assert.Empty(world.Entries);
    }

    [Fact]
    public async Task An_agent_without_a_provider_is_refused_before_anything_is_recorded()
    {
        var world = new World();
        var codex = Request() with { Agent = Recommender with { Model = new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI) } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.RunAsync(request: codex));

        Assert.Empty(world.Entries);
    }

    [Fact]
    public async Task A_launch_that_fails_is_recorded_as_a_failed_attempt_and_returned()
    {
        var world = new World(_ => throw new InvalidOperationException("claude no está instalado"));

        var result = await world.RunAsync();

        Assert.Equal(AgentRunStatus.Failed, result.Outcome.Status);
        Assert.Contains("claude no está instalado", result.Outcome.Error, StringComparison.Ordinal);
        var finished = world.Payload<AgentRunFinishedPayload>(DecisionKind.AgentRunFinished);
        Assert.Equal((AttemptOutcome.Failed, AgentRunFinishedPayload.UsageVersion), (finished.Status, finished.V));
        Assert.Null(finished.LaunchedAt);
        Assert.Null(result.AnswerEvidence);
    }

    [Fact]
    public async Task A_cancelled_run_is_stopped_recorded_as_cancelled_and_the_caller_is_told()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRun? run = null;
        var world = new World(_ => run = new FakeRun([], Succeeded("{}"), hold));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var running = world.RunAsync(ct: cancel.Token);
        await Task.Yield();
        while (run is null)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(run.Cancelled);
        Assert.Equal(AttemptOutcome.Cancelled, world.Payload<AgentRunFinishedPayload>(DecisionKind.AgentRunFinished).Status);
    }

    /// <summary>A stream that breaks, even with a cancellation nobody asked for, does not decide the outcome: the end is still recorded.</summary>
    [Fact]
    public async Task An_event_stream_that_breaks_still_ends_with_the_attempt_recorded()
    {
        var world = new World(_ => new BrokenRun(new OperationCanceledException("the CLI's pipe was closed")));

        var result = await world.RunAsync();

        Assert.Equal(AgentRunStatus.Failed, result.Outcome.Status);
        Assert.Equal(AttemptOutcome.Failed, world.Payload<AgentRunFinishedPayload>(DecisionKind.AgentRunFinished).Status);
    }

    private sealed class BrokenRun(Exception failure) : IAgentRun
    {
        public string RunId => "broken";

        public IAsyncEnumerable<AgentEvent> Events => Stream();

        public Task<AgentRunOutcome> Completion => Task.FromResult(new AgentRunOutcome(AgentRunStatus.Failed, 1, null, null, null, "se cortó"));

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<AgentEvent> Stream()
        {
            await Task.Yield();
            yield return new MessageEmitted(T0, "hola", false);
            throw failure;
        }
    }

    [Fact]
    public void The_runner_is_asked_for_a_purpose_and_a_request_and_answers_with_its_own_run()
    {
        var run = typeof(IAuxiliaryAgentRunner).GetMethod(nameof(IAuxiliaryAgentRunner.RunAsync))!;

        Assert.Equal([typeof(ProjectContext), typeof(AuxiliaryRunRequest), typeof(IProgress<AgentEvent>), typeof(CancellationToken)], run.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<AuxiliaryRunResult>), run.ReturnType);
        Assert.Single(typeof(IAuxiliaryAgentRunner).GetMethods());
    }

    [Fact]
    public async Task Every_auxiliary_run_gets_a_run_id_of_its_own_that_no_orchestration_run_can_have()
    {
        var world = new World();

        var first = await world.RunAsync();
        var second = await world.RunAsync();

        Assert.NotEqual(first.Run, second.Run);
        Assert.All([first.Run, second.Run], run => Assert.StartsWith(AuxiliaryAgentRunner.RunPrefix, run.Value, StringComparison.Ordinal));
        Assert.Matches("^aux-20260101-[0-9a-f]{6}$", first.Run.Value);
        Assert.Equal(4, world.Entries.Count);
    }
}
