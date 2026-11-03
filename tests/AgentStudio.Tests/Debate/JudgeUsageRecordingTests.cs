using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;

namespace AgentStudio.Tests.Debate;

/// <summary>
/// docs/14 WP1: the judge records <c>AgentRunStarted</c> version 2 (its purpose, and never any memory) and
/// <c>AgentRunFinished</c> with the usage by origin and model, when its run was seen running, the model of its steps (never
/// the one its session announced, D-13), the session its correction resumed (D-02), the step with the largest context with
/// the window of its model, and the compactions (not exposed without context signals). Since Phase 8 the end is version 3
/// (docs/16 D-41): it also records its class, the adapter's or the one the status implies.
/// </summary>
public sealed class JudgeUsageRecordingTests
{
    private static readonly TimeSpan Launch = TimeSpan.FromSeconds(2);

    private static readonly MetricValue<long> NotYet = MetricValue<long>.Missing("ventana aún no informada");

    private static ProviderCapabilities Capabilities(bool context) =>
        new(true, true, true, false, true, false, 1) { ReportsContextOccupancy = context };

    private static ReviewRequest Request(JudgingWorld world) =>
        new(world.ProjectContext, JudgingWorld.Run, JudgingWorld.Review, JudgingWorld.Judge, world.Workspace);

    private static IndependentReview Review(JudgingWorld world, IAgentProvider judgeProvider)
    {
        var providers = new FakeProjectAgentProviders(judgeProvider);
        return new IndependentReview(
            world.Recorder,
            new JudgeDossierBuilder(world.Recorder, world.Runs, world.Plans, world.Requirements, world.Evidence, new JudgingOptions(world.DossierRoot)),
            world.Runs,
            new AgentJudgeFactory(providers, world.Clock),
            world.Verdicts,
            world.Evidence,
            world.Clock,
            providers);
    }

    /// <summary>A judge run that answers <paramref name="answer"/> with two steps, a compaction and its usage.</summary>
    private static ScriptedRun Run(System.Text.Json.Nodes.JsonNode answer, string session, bool steps) =>
        new(
            new AgentRunOutcome(AgentRunStatus.Succeeded, 0, answer.ToJsonString(), answer.ToJsonString(), session, null),
            [
                new SessionStarted(DateTimeOffset.UnixEpoch, session, "gpt-5-codex"),
                .. steps
                    ? (AgentEvent[])
                    [
                        new ContextObserved(DateTimeOffset.UnixEpoch, "gpt-5.5", MetricValue<long>.Exact(40_000, "steps"), NotYet),
                        new ContextObserved(DateTimeOffset.UnixEpoch, "gpt-5.5", MetricValue<long>.Exact(25_000, "steps"), NotYet),
                        new CompactionObserved(DateTimeOffset.UnixEpoch, "auto", MetricValue<long>.Exact(39_000, "steps")),
                    ]
                    : [],
                new UsageReported(DateTimeOffset.UnixEpoch, new TokenUsage(
                    MetricValue<long>.Exact(65_000, "usage"),
                    MetricValue<long>.Exact(900, "usage"),
                    MetricValue<long>.Exact(30_000, "usage"),
                    MetricValue<long>.NotExposed("no window"),
                    MetricValue<decimal>.NotExposed("no cost"))
                {
                    PerModel = steps
                        ? [new ModelUsage("gpt-5.5", MetricValue<long>.Exact(65_000, "m"), MetricValue<long>.Exact(900, "m"), MetricValue<long>.Exact(30_000, "m"), MetricValue<long>.Exact(0, "m"), MetricValue<decimal>.NotExposed("no cost"), MetricValue<long>.Exact(400_000, "m"), null)]
                        : [],
                    Turns = 4,
                }),
            ]);

    private static ScriptedAgentProvider Judge(JudgingWorld world, bool context, Func<AgentRunRequest, int, ScriptedRun> script) =>
        new(ProviderId.CodexCli, VendorId.OpenAI, script)
        {
            Capabilities = Capabilities(context),
            OnStart = _ => world.Clock.Advance(Launch),
        };

    private static List<(DecisionEntry Entry, T Payload)> Payloads<T>(JudgingWorld world)
        where T : DecisionPayload =>
        [.. world.LogStore.Of(JudgingWorld.Project)
            .Where(e => e.Kind == (typeof(T) == typeof(AgentRunStartedPayload) ? DecisionKind.AgentRunStarted : DecisionKind.AgentRunFinished))
            .Select(e => (e, DecisionPayloadSerializer.Deserialize<T>(e.PayloadJson)))
            .Where(x => x.Item2.RunId == JudgingWorld.Run.Value && (x.Item2 is not AgentRunStartedPayload s || s.Task is null) && (x.Item2 is not AgentRunFinishedPayload f || f.Task is null))];

    [Fact]
    public async Task The_judge_records_its_purpose_and_its_end_in_version_3_with_the_model_of_its_steps_its_peak_its_compactions_and_its_class()
    {
        using var world = await JudgingWorld.CreateAsync();
        var judge = Judge(world, context: true, (request, _) => Run(PromptData.EchoingApproval(request.Prompt, world.TestEvidence.Id), "thread-1", steps: true));

        var outcome = await Review(world, judge).ReviewRunAsync(Request(world), null, TestContext.Current.CancellationToken);

        Assert.Equal(ReviewDisposition.Approved, outcome.Disposition);
        var (startEntry, started) = Assert.Single(Payloads<AgentRunStartedPayload>(world));
        Assert.Equal((AgentRunStartedPayload.PurposeVersion, AgentRunPurpose.Judge), (started.V, started.Purpose));
        Assert.Null(started.ContextPack);

        var (_, finished) = Assert.Single(Payloads<AgentRunFinishedPayload>(world));
        Assert.Equal(AgentRunFinishedPayload.EndClassVersion, finished.V);
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.Completed), finished.End);
        Assert.Equal(startEntry.RecordedAt + Launch, finished.LaunchedAt);
        Assert.Equal("gpt-5.5", finished.ReportedModel);
        Assert.Null(finished.ResumedSession);
        Assert.Equal((1, 4), (finished.Compactions, finished.Turns));
        Assert.Equal((40_000m, 400_000m), (finished.ContextPeak!.UsedTokens.Value, finished.ContextPeak.ContextWindow.Value));
        Assert.Equal(MetricOrigin.NotExposed, finished.Usage!.CostUsd.Origin);
        Assert.Equal(["gpt-5.5"], finished.Usage.PerModel!.Select(m => m.Model));
    }

    [Fact]
    public async Task The_correction_of_the_judge_records_the_session_it_resumed()
    {
        using var world = await JudgingWorld.CreateAsync();
        string? evaluation = null;
        var judge = Judge(world, context: true, (request, n) =>
        {
            // The correction of a resumed session does not repeat the dossier: the answer echoes the first prompt.
            evaluation ??= request.Prompt;
            var answer = PromptData.EchoingApproval(evaluation, world.TestEvidence.Id);
            if (n == 1)
            {
                answer["decisionLogHeadHash"] = new string('9', 64);
            }

            return Run(answer, $"thread-{n}", steps: true);
        });

        var outcome = await Review(world, judge).ReviewRunAsync(Request(world), null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Corrected);
        Assert.Equal(
            [(1, (string?)null, "thread-1"), (2, "thread-1", "thread-2")],
            Payloads<AgentRunFinishedPayload>(world).Select(p => (p.Payload.Attempt, p.Payload.ResumedSession, p.Payload.SessionId)));
        Assert.All(Payloads<AgentRunStartedPayload>(world), p => Assert.Equal(AgentRunPurpose.Judge, p.Payload.Purpose));
    }

    [Fact]
    public async Task Without_context_signals_the_judge_records_no_peak_its_compactions_as_not_exposed_and_never_the_session_model()
    {
        using var world = await JudgingWorld.CreateAsync();
        var judge = Judge(world, context: false, (request, _) => Run(PromptData.EchoingApproval(request.Prompt, world.TestEvidence.Id), "thread-1", steps: false));

        await Review(world, judge).ReviewRunAsync(Request(world), null, TestContext.Current.CancellationToken);

        var (_, finished) = Assert.Single(Payloads<AgentRunFinishedPayload>(world));
        Assert.Equal(AgentRunFinishedPayload.EndClassVersion, finished.V);
        Assert.Null(finished.Compactions);
        Assert.Null(finished.ContextPeak);

        // SessionStarted announced «gpt-5-codex»: that is the configured model of the session, not one the CLI reported.
        Assert.Null(finished.ReportedModel);
        Assert.Equal(65_000m, finished.Usage!.InputTokens.Value);
    }

    /// <summary>docs/16 D-41 and F8-1: the class the adapter of the judge recognised reaches the log as it came, next to the status.</summary>
    [Fact]
    public async Task The_judge_records_the_class_its_adapter_gave_the_end_of_its_run()
    {
        var resets = new DateTimeOffset(2026, 10, 15, 17, 20, 0, TimeSpan.Zero);
        using var world = await JudgingWorld.CreateAsync();
        var judge = Judge(world, context: false, (_, _) => new ScriptedRun(
            new AgentRunOutcome(AgentRunStatus.RateLimited, 1, null, null, "thread-1", "Límite de uso", new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", resets)),
            new SessionStarted(DateTimeOffset.UnixEpoch, "thread-1", null),
            new RateLimitSignaled(DateTimeOffset.UnixEpoch, "rejected", resets, "five_hour")));

        await Review(world, judge).ReviewRunAsync(Request(world), null, TestContext.Current.CancellationToken);

        var (_, finished) = Assert.Single(Payloads<AgentRunFinishedPayload>(world));
        Assert.Equal((AgentRunFinishedPayload.EndClassVersion, AttemptOutcome.RateLimited), (finished.V, finished.Status));
        Assert.Equal(new AttemptEndSnapshot(AttemptEndClass.WindowQuota, "five_hour", resets), finished.End);
    }

    [Fact]
    public async Task The_judge_is_never_given_project_memory()
    {
        using var world = await JudgingWorld.CreateAsync();
        var judge = Judge(world, context: true, (request, _) => Run(PromptData.EchoingApproval(request.Prompt, world.TestEvidence.Id), "thread-1", steps: true));

        await Review(world, judge).ReviewRunAsync(Request(world), null, TestContext.Current.CancellationToken);

        var request = Assert.Single(judge.Requests);
        Assert.DoesNotContain("<project_memory", request.Prompt, StringComparison.Ordinal);
        Assert.All(Payloads<AgentRunStartedPayload>(world), p => Assert.Null(p.Payload.ContextPack));
    }
}
