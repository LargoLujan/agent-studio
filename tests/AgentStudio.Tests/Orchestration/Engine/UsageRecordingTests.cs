using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using static AgentStudio.Tests.Orchestration.Planning.PlanJson;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/14 WP1 and D-02, D-04, D-09, D-13: the planner and the executors record <c>AgentRunStarted</c> version 2 with what
/// the run is for, and <c>AgentRunFinished</c> with the usage by origin and model, the time the process was launched (after
/// the provider's queue), the model of the steps (never the one the session announced), the session it resumed, the step
/// with the largest context with the window of its model, and the compactions (not exposed when the provider does not
/// report context). Since Phase 8 the end is version 3 (docs/16 D-41): it also records its class, the adapter's or the one
/// the status implies.
/// </summary>
public sealed class UsageRecordingTests
{
    /// <summary>The scripted provider, with the class an adapter would have recognised in the outcome of every run.</summary>
    private sealed class EndingAs(IAgentProvider inner, AgentRunEnd end) : IAgentProvider
    {
        public ProviderId Provider => inner.Provider;

        public VendorId Vendor => inner.Vendor;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken) =>
            new Run(await inner.StartAsync(request, cancellationToken), end);

        private sealed class Run(IAgentRun inner, AgentRunEnd end) : IAgentRun
        {
            public string RunId => inner.RunId;

            public IAsyncEnumerable<AgentEvent> Events => inner.Events;

            public Task<AgentRunOutcome> Completion { get; } = inner.Completion.ContinueWith(t => t.Result with { End = end }, TaskScheduler.Default);

            public Task CancelAsync() => inner.CancelAsync();

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static readonly TimeSpan Launch = TimeSpan.FromSeconds(2);

    private static readonly MetricValue<long> NotYet = MetricValue<long>.Missing("claude aún no informa de la ventana");

    /// <summary>The scripted Claude with the capabilities of the real one (it reports the context of each step) and a launch that takes <see cref="Launch"/>.</summary>
    private sealed class LikeClaude(IAgentProvider inner, FakeTimeProvider time) : IAgentProvider
    {
        public ProviderId Provider => inner.Provider;

        public VendorId Vendor => inner.Vendor;

        public ProviderCapabilities Capabilities { get; } = inner.Capabilities with { ReportsContextOccupancy = true };

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
        {
            time.Advance(Launch);
            return inner.StartAsync(request, cancellationToken);
        }
    }

    private static EngineHarness Harness()
    {
        var harness = new EngineHarness();
        harness.Providers = new FakeProjectAgentProviders(
        [
            AgentProviderPipeline.Compose(new LikeClaude(harness.Claude, harness.Time), harness.Journal, harness.Time),
            AgentProviderPipeline.Compose(harness.Codex, harness.Journal, harness.Time),
        ]);
        return harness;
    }

    /// <summary>Three steps on claude-sonnet-5-5 (the second is the largest), one compaction, and the usage of the run.</summary>
    private static AgentEvent[] Steps(DateTimeOffset at) =>
    [
        new ContextObserved(at, "claude-sonnet-5-5", MetricValue<long>.Exact(12_000, "claude assistant.message.usage"), NotYet),
        new ContextObserved(at, "claude-sonnet-5-5", MetricValue<long>.Exact(30_822, "claude assistant.message.usage"), NotYet),
        new CompactionObserved(at, "auto", MetricValue<long>.Exact(30_000, "claude compact_boundary")),
        new ContextObserved(at, "claude-sonnet-5-5", MetricValue<long>.Exact(9_500, "claude assistant.message.usage"), NotYet),
        new UsageReported(at, new TokenUsage(
            MetricValue<long>.Exact(52_322, "claude result.usage"),
            MetricValue<long>.Exact(640, "claude result.usage"),
            MetricValue<long>.Exact(40_000, "claude result.usage"),
            MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"),
            MetricValue<decimal>.Estimated(0.05m, "estimación del CLI"))
        {
            CacheWriteInputTokens = MetricValue<long>.Exact(12_000, "claude result.usage"),
            PerModel =
            [
                new ModelUsage(
                    "claude-sonnet-5-5",
                    MetricValue<long>.Exact(52_322, "claude result.modelUsage"),
                    MetricValue<long>.Exact(640, "claude result.modelUsage"),
                    MetricValue<long>.Exact(40_000, "claude result.modelUsage"),
                    MetricValue<long>.Exact(12_000, "claude result.modelUsage"),
                    MetricValue<decimal>.Estimated(0.05m, "estimación del CLI"),
                    MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"),
                    "list"),
            ],
            Turns = 3,
        }),
    ];

    private static string ValidPlan() =>
        Plan(
            Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"], steps: ["build", "test"]),
            Task("I1", kind: "Integration", agent: null, workspace: "None", dependsOn: [Dependency("T1")], steps: ["build", "test"]),
            Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [Dependency("I1")])).ToJsonString();

    private static Task<PlanProposal> PlanAsync(EngineHarness harness) =>
        harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(), null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_planner_records_its_end_in_version_3_with_launch_time_model_of_the_steps_peak_compactions_turns_and_class()
    {
        using var harness = Harness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan(), Events = Steps(Start) });

        await PlanAsync(harness);

        var (startEntry, started) = Assert.Single(harness.Payloads<AgentRunStartedPayload>());
        Assert.Equal((AgentRunStartedPayload.ContextPartsVersion, AgentRunPurpose.Planner), (started.V, started.Purpose));
        Assert.Null(started.ContextPack);

        var (_, finished) = Assert.Single(harness.Payloads<AgentRunFinishedPayload>());
        Assert.Equal(AgentRunFinishedPayload.EndClassVersion, finished.V);
        Assert.True(finished.RecordsUsageWithOrigins());
        Assert.True(finished.RecordsEndClass());
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.Completed), finished.End);

        // The process started after the launch, not when AgentRunStarted was recorded.
        Assert.Equal(startEntry.RecordedAt + Launch, finished.LaunchedAt);

        // The scripted session announces the configured «haiku»; the steps ran on claude-sonnet-5-5 (D-13).
        Assert.Equal("claude-sonnet-5-5", finished.ReportedModel);
        Assert.Null(finished.ResumedSession);
        Assert.Equal((1, 3), (finished.Compactions, finished.Turns));

        // The largest step, with the window of its model from the breakdown at the end (exact, not the live «missing»).
        var peak = finished.ContextPeak!;
        Assert.Equal(("claude-sonnet-5-5", 30_822m, 1_000_000m), (peak.Model, peak.UsedTokens.Value, peak.ContextWindow.Value));
        Assert.Equal((MetricOrigin.ReportedByProvider, MetricOrigin.ReportedByProvider), (peak.UsedTokens.ResolvedOrigin(), peak.ContextWindow.ResolvedOrigin()));

        var usage = finished.Usage!;
        Assert.Equal(MetricOrigin.ReportedByProvider, usage.InputTokens.Origin);
        Assert.Equal(MetricOrigin.EstimatedByProvider, usage.CostUsd.Origin);
        Assert.Equal(12_000m, usage.CacheWriteInputTokens!.Value);
        Assert.Equal(["claude-sonnet-5-5"], usage.PerModel!.Select(m => m.Model));
    }

    [Fact]
    public async Task The_correction_of_the_planner_records_the_session_it_resumed()
    {
        using var harness = Harness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = Plan(Task("T1", agent: "orquestador", covers: ["RQ-01"])).ToJsonString() });
        harness.Claude.Script("orquestador", null, 2, new AgentScript { StructuredOutput = ValidPlan() });

        await PlanAsync(harness);

        Assert.Equal(
            [(1, (string?)null), (2, "session-orquestador-plan-1")],
            harness.Payloads<AgentRunFinishedPayload>().Select(p => (p.Payload.Attempt, p.Payload.ResumedSession)));
        Assert.All(harness.Payloads<AgentRunStartedPayload>(), p => Assert.Equal(AgentRunPurpose.Planner, p.Payload.Purpose));
    }

    [Fact]
    public async Task An_executor_records_its_start_with_its_purpose_and_its_end_in_version_3()
    {
        using var harness = Harness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Events = Steps(Start) });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await run.CompleteAsync(harness);

        var (startEntry, started) = Assert.Single(harness.Payloads<AgentRunStartedPayload>(), p => p.Payload.Task == new TaskId("T1"));
        Assert.Equal((AgentRunStartedPayload.ContextPartsVersion, AgentRunPurpose.Executor), (started.V, started.Purpose));
        var finished = Assert.Single(harness.Payloads<AgentRunFinishedPayload>(), p => p.Payload.Task == new TaskId("T1")).Payload;
        Assert.Equal((AgentRunFinishedPayload.EndClassVersion, "claude-sonnet-5-5", 1, 3), (finished.V, finished.ReportedModel, finished.Compactions, finished.Turns));
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.Completed), finished.End);
        Assert.Equal(startEntry.RecordedAt + Launch, finished.LaunchedAt);
        Assert.Equal(30_822m, finished.ContextPeak!.UsedTokens.Value);
        Assert.Equal(1_000_000m, finished.ContextPeak.ContextWindow.Value);
    }

    /// <summary>docs/18 P47: the effort the agent is set with goes to its start (version 3) by its token, and nothing is written when it sets none.</summary>
    [Fact]
    public async Task An_executor_records_the_effort_of_its_agent_in_its_start_and_one_without_an_effort_records_none()
    {
        using var harness = Harness();
        var executor = harness.Roster.Agents.Single(a => a.Id.Value == "ejecutor");
        await harness.Roster.SaveAsync(harness.Project.ProjectId, executor with { Effort = ReasoningEffort.High }, TestContext.Current.CancellationToken);

        await using var run = await harness.StartAsync(Plan(Work("T1"), Work("T2", "ejecutor2")));
        await run.CompleteAsync(harness);

        var starts = harness.Payloads<AgentRunStartedPayload>();
        var (withEntry, with) = Assert.Single(starts, p => p.Payload.Task == new TaskId("T1"));
        Assert.Equal((AgentRunStartedPayload.ContextPartsVersion, ReasoningEffort.High), (with.V, with.Effort));
        Assert.Contains("\"effort\":\"high\"", withEntry.PayloadJson, StringComparison.Ordinal);
        var (withoutEntry, without) = Assert.Single(starts, p => p.Payload.Task == new TaskId("T2"));
        Assert.Null(without.Effort);
        Assert.DoesNotContain("effort", withoutEntry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_planner_records_the_effort_of_the_orchestrator_when_it_sets_one()
    {
        using var harness = Harness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan(), Events = Steps(Start) });
        var request = harness.PlanningRequest() with
        {
            Roster = [.. Roster().Select(a => a.Id.Value == "orquestador" ? a with { Effort = ReasoningEffort.Medium } : a)],
        };

        await harness.CreateOrchestrator().PlanAsync(request, null, TestContext.Current.CancellationToken);

        var (entry, started) = Assert.Single(harness.Payloads<AgentRunStartedPayload>());
        Assert.Equal((AgentRunPurpose.Planner, ReasoningEffort.Medium), (started.Purpose, started.Effort));
        Assert.Contains("\"effort\":\"medium\"", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_that_resumes_the_session_records_which_one_it_resumed()
    {
        using var harness = Harness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing());

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await run.CompleteAsync(harness);

        Assert.Equal(
            [(1, (string?)null, "session-ejecutor-T1-1"), (2, "session-ejecutor-T1-1", "session-ejecutor-T1-2")],
            harness.Payloads<AgentRunFinishedPayload>().Where(p => p.Payload.Task == new TaskId("T1")).Select(p => (p.Payload.Attempt, p.Payload.ResumedSession, p.Payload.SessionId)));
    }

    [Fact]
    public async Task A_provider_without_context_signals_records_no_peak_and_its_compactions_as_not_exposed()
    {
        using var harness = Harness();
        harness.Codex.Script("ejecutorCodex", "T1", 1, AgentScript.Completed() with { Events = [AgentScript.Usage(Start, 1200, 30)] });

        await using var run = await harness.StartAsync(Plan(Work("T1", agent: "ejecutorCodex")));
        await run.CompleteAsync(harness);

        var finished = Assert.Single(harness.Payloads<AgentRunFinishedPayload>(), p => p.Payload.Task == new TaskId("T1")).Payload;
        Assert.Equal(AgentRunFinishedPayload.EndClassVersion, finished.V);
        Assert.Null(finished.Compactions);
        Assert.Null(finished.ContextPeak);
        Assert.Null(finished.ReportedModel);
        Assert.Equal(1200m, finished.Usage!.InputTokens.Value);
        Assert.NotNull(finished.LaunchedAt);
    }

    [Fact]
    public async Task A_launch_that_fails_is_recorded_in_version_3_without_a_launch_time_and_with_the_class_its_status_implies()
    {
        using var harness = Harness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { ThrowOnStart = new InvalidOperationException("sin CLI") });

        await PlanAsync(harness);

        var finished = Assert.Single(harness.Payloads<AgentRunFinishedPayload>()).Payload;
        Assert.Equal((AgentRunFinishedPayload.EndClassVersion, AttemptOutcome.Failed), (finished.V, finished.Status));
        Assert.Null(finished.LaunchedAt);
        Assert.Null(finished.Usage);
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.Other), finished.End);
    }

    /// <summary>
    /// docs/16 D-41 and F8-1: the class the adapter recognised in its CLI reaches the log as it came (the window that ran out
    /// and its reset), next to the status, which it refines and never replaces.
    /// </summary>
    [Fact]
    public async Task An_executor_records_the_class_its_adapter_gave_the_end_of_the_attempt()
    {
        var resets = Start.AddHours(2);
        using var harness = new EngineHarness();
        harness.Providers = new FakeProjectAgentProviders(
        [
            AgentProviderPipeline.Compose(new EndingAs(harness.Claude, new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", resets)), harness.Journal, harness.Time),
            AgentProviderPipeline.Compose(harness.Codex, harness.Journal, harness.Time),
        ]);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Status = AgentRunStatus.RateLimited, StructuredOutput = null, Error = "Límite de uso" });

        // Whatever the engine then does with the limit (WP1), the end of the attempt is in the log as soon as it ended.
        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await RunTestSupport.WaitUntilAsync(() => harness.Payloads<AgentRunFinishedPayload>().Any(p => p.Payload.Task == new TaskId("T1")), "the end of T1 in the log");

        var finished = harness.Payloads<AgentRunFinishedPayload>().First(p => p.Payload.Task == new TaskId("T1")).Payload;
        Assert.Equal((AgentRunFinishedPayload.EndClassVersion, AttemptOutcome.RateLimited), (finished.V, finished.Status));
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.WindowQuota, "five_hour", resets), finished.End);
        Assert.Equal(finished.End, finished.ResolvedEnd());
    }
}
