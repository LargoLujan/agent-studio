using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Usage;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Observability.Usage.Snapshots;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// docs/14 D-01 to D-04 (ADR-0019): the usage of a project is a projection of its decision log. Each attempt pairs its
/// <c>AgentRunStarted</c> with the <c>AgentRunFinished</c> that closes it, and its own usage is deduplicated by session,
/// because Claude and Codex report the total of the session and retries resume it. Every figure keeps its origin.
/// </summary>
public sealed class UsageProjectorTests
{
    private const string Run = "20261025-a1b2c3";

    private static IReadOnlyList<UsageAttempt> Project(UsageLogBuilder log) => UsageProjector.Project(UsageLogBuilder.ProjectId, log.Entries).Attempts;

    // ---- pairing ----

    [Fact]
    public void An_attempt_takes_who_and_where_from_its_start_and_how_it_ended_from_its_end()
    {
        var log = new UsageLogBuilder();
        var startedAt = log.Now;
        log.Start(Run, "T1", 1, agent: "ejecutor", model: "claude-sonnet-5-5", purpose: AgentRunPurpose.Executor)
            .Advance(TimeSpan.FromSeconds(4));
        var launchedAt = log.Now;
        log.Advance(TimeSpan.FromSeconds(30));
        var finishedAt = log.Now;
        log.Finish(Run, "T1", 1, session: "s1", usage: Claude(1000, 200, 300, 50, 0.01m), launchedAt: launchedAt, reportedModel: "claude-sonnet-5-5-20261001");

        var attempt = Assert.Single(Project(log));

        Assert.Equal((UsageLogBuilder.ProjectId, Run, "T1", 1), (attempt.ProjectId, attempt.RunId, attempt.TaskId, attempt.Attempt));
        Assert.Equal((AgentRunPurpose.Executor, Aid("ejecutor"), ProviderId.ClaudeCode, VendorId.Anthropic), (attempt.Purpose, attempt.Agent, attempt.Provider, attempt.Vendor));
        Assert.Equal(("claude-sonnet-5-5", "claude-sonnet-5-5-20261001", "s1"), (attempt.ConfiguredModel, attempt.ReportedModel, attempt.SessionId));
        Assert.Equal((startedAt, (DateTimeOffset?)launchedAt, finishedAt), (attempt.RecordedAt, attempt.LaunchedAt, attempt.FinishedAt));
        Assert.Equal((AttemptOutcome.Succeeded, 2), (attempt.Outcome, attempt.RecordedVersion));
    }

    /// <summary>docs/18 P47: the effort the start recorded (version 3) reaches the attempt; a start that recorded none leaves it null.</summary>
    [Fact]
    public void An_attempt_keeps_the_reasoning_effort_its_start_recorded_and_none_when_it_recorded_none()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1, version: AgentRunStartedPayload.ContextPartsVersion, effort: ReasoningEffort.High)
            .Finish(Run, "T1", 1, session: "s1", usage: Claude(1000, 0, 0, 50, 0.01m))
            .Start(Run, "T2", 1, version: AgentRunStartedPayload.ContextPartsVersion)
            .Finish(Run, "T2", 1, session: "s2", usage: Claude(1000, 0, 0, 50, 0.01m))
            .Start(Run, "T3", 1)
            .Finish(Run, "T3", 1, session: "s3", usage: Claude(1000, 0, 0, 50, 0.01m));

        Assert.Equal([ReasoningEffort.High, null, null], Project(log).Select(a => a.Effort));
    }

    [Fact]
    public void An_end_closes_the_last_open_start_of_the_same_run_task_and_attempt()
    {
        // The planner and the judge both number their launches from 1 and carry no task.
        var log = new UsageLogBuilder()
            .Start(Run, null, 1, agent: "orquestador", purpose: AgentRunPurpose.Planner)
            .Start("20261025-ffffff", null, 1, agent: "otro")
            .Finish(Run, null, 1, session: "p1", usage: Claude(10, 0, 0, 1, 0.001m))
            .Start(Run, null, 1, agent: "juez", provider: ProviderId.CodexCli, model: "gpt-6", purpose: AgentRunPurpose.Judge)
            .Finish(Run, null, 1, session: "j1", usage: Codex(20, 0, 0, 2, 0));

        var attempts = Project(log);

        Assert.Equal(["orquestador", "juez"], attempts.Select(a => a.Agent.Value));
        Assert.Equal(["p1", "j1"], attempts.Select(a => a.SessionId));
        Assert.Equal([VendorId.Anthropic, VendorId.OpenAI], attempts.Select(a => a.Vendor));
    }

    [Fact]
    public void A_start_that_did_not_end_yet_is_not_an_attempt()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Noise(Run);

        var projection = UsageProjector.Project(UsageLogBuilder.ProjectId, log.Entries);

        Assert.Empty(projection.Attempts);
        Assert.Empty(projection.Warnings);
    }

    [Fact]
    public void An_end_without_its_start_is_left_out_and_says_so()
    {
        var log = new UsageLogBuilder().Finish(Run, "T9", 3, session: "s1", usage: Claude(10, 0, 0, 1, 0.001m));

        var projection = UsageProjector.Project(UsageLogBuilder.ProjectId, log.Entries);

        Assert.Empty(projection.Attempts);
        var warning = Assert.Single(projection.Warnings);
        Assert.Contains("T9", warning, StringComparison.Ordinal);
        Assert.Contains("entrada 1", warning, StringComparison.Ordinal);
    }

    // ---- figures and their origins (D-03) ----

    [Fact]
    public void Version_2_figures_keep_the_origin_and_the_source_the_cli_gave_them()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(1000, 200, 300, 50, 0.01m));

        var own = Assert.Single(Project(log)).Own;

        Assert.Equal(MetricValue<long>.Exact(1000, ClaudeUsage), own.InputTokens);
        Assert.Equal(MetricValue<long>.Exact(200, ClaudeUsage), own.CacheReadInputTokens);
        Assert.Equal(MetricValue<long>.Exact(300, ClaudeUsage), own.CacheWriteInputTokens);
        Assert.Equal(MetricValue<long>.Exact(50, ClaudeUsage), own.OutputTokens);
        Assert.Equal(MetricValue<long>.NotExposed("claude no informa del razonamiento"), own.ReasoningOutputTokens);
        Assert.Equal(MetricValue<decimal>.Estimated(0.01m, ClaudeCost), own.CostUsd);
    }

    [Fact]
    public void A_version_1_end_is_read_by_its_provenance_and_has_no_cache_writes_reasoning_nor_breakdown()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1, version: 1).Finish(Run, "T1", 1, session: "s1", usage: ClaudeV1(1000, 200, 50, 0.01m), version: 1);

        var attempt = Assert.Single(Project(log));

        Assert.Equal(1, attempt.RecordedVersion);
        Assert.Null(attempt.Purpose);
        Assert.Null(attempt.LaunchedAt);
        Assert.Empty(attempt.PerModel);
        Assert.Equal(MetricOrigin.ReportedByProvider, attempt.Own.InputTokens.Origin);
        Assert.Equal(MetricValue<long>.Exact(200, ClaudeUsage), attempt.Own.CacheReadInputTokens);
        Assert.Equal(MetricOrigin.EstimatedByProvider, attempt.Own.CostUsd.Origin);
        Assert.Equal(MetricOrigin.Missing, attempt.Own.CacheWriteInputTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, attempt.Own.ReasoningOutputTokens.Origin);
        Assert.Contains("anterior a la Fase 4", attempt.Own.CacheWriteInputTokens.Source, StringComparison.Ordinal);
        Assert.Null(attempt.Compactions);
        Assert.NotNull(attempt.ContextPeak);
        Assert.Equal(MetricOrigin.Missing, attempt.ContextPeak.UsedTokens.Origin);
    }

    [Fact]
    public void An_attempt_without_a_result_has_no_figure_at_all()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Interrupted, version: 1, error: "La aplicación se cerró durante el intento.");

        var own = Assert.Single(Project(log)).Own;

        Assert.All(Figures(own), f => Assert.Equal(MetricOrigin.Missing, f.Origin));
        Assert.Equal(MetricOrigin.Missing, own.CostUsd.Origin);
        Assert.False(own.CostUsd.HasValue);
    }

    [Fact]
    public void A_codex_usage_of_all_zeros_means_no_usage_notification_came_so_it_is_no_figure()
    {
        // codex exec prints Usage::default() when no usage notification arrived (docs/14 section 3.2).
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1, provider: ProviderId.CodexCli, model: "gpt-6", version: 1)
            .Finish(Run, "T1", 1, session: "th1", usage: CodexV1(0, 0, 0), version: 1);

        var own = Assert.Single(Project(log)).Own;

        Assert.All(Figures(own), f => Assert.Equal(MetricOrigin.Missing, f.Origin));
        Assert.Contains("a cero", own.InputTokens.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_claude_usage_of_zero_tokens_is_still_what_the_cli_reported()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(0, 0, 0, 0, 0m));

        Assert.Equal(MetricValue<long>.Exact(0, ClaudeUsage), Assert.Single(Project(log)).Own.InputTokens);
    }

    // ---- deduplication by session (D-02) ----

    [Fact]
    public void A_retry_that_resumes_its_session_counts_only_what_it_added()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(1000, 200, 300, 100, 0.10m))
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1", resumed: "s1", usage: Claude(1600, 500, 400, 160, 0.16m));

        var attempts = Project(log);

        var retry = attempts[1].Own;
        Assert.Equal((600L, 300L, 100L, 60L), (retry.InputTokens.Value!.Value, retry.CacheReadInputTokens.Value!.Value, retry.CacheWriteInputTokens.Value!.Value, retry.OutputTokens.Value!.Value));
        Assert.Equal(0.06m, retry.CostUsd.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, retry.InputTokens.Origin);
        Assert.Equal(MetricOrigin.EstimatedByProvider, retry.CostUsd.Origin);
        Assert.Contains("intento anterior de la sesión", retry.InputTokens.Source, StringComparison.Ordinal);
        Assert.Equal(1000L, attempts[0].Own.InputTokens.Value);
    }

    [Fact]
    public void Every_attempt_of_a_shared_session_carries_the_total_of_the_session_and_a_lone_one_none()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(1000, 0, 0, 100, 0.10m))
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1", resumed: "s1", usage: Claude(1600, 0, 0, 160, 0.16m))
            .Start(Run, "T2", 1).Finish(Run, "T2", 1, session: "s2", usage: Claude(50, 0, 0, 5, 0.01m));

        var attempts = Project(log);

        Assert.Equal(1600L, attempts[0].SessionTotal?.InputTokens.Value);
        Assert.Equal(1600L, attempts[1].SessionTotal?.InputTokens.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, attempts[1].SessionTotal!.InputTokens.Origin);
        Assert.Null(attempts[2].SessionTotal);
    }

    [Fact]
    public void The_correction_of_the_planner_and_that_of_the_judge_are_deduplicated_in_their_own_sessions()
    {
        var log = new UsageLogBuilder()
            .Start(Run, null, 1, agent: "orquestador", purpose: AgentRunPurpose.Planner).Finish(Run, null, 1, session: "plan", usage: Claude(400, 0, 0, 40, 0.04m))
            .Start(Run, null, 2, agent: "orquestador", purpose: AgentRunPurpose.Planner).Finish(Run, null, 2, session: "plan", resumed: "plan", usage: Claude(700, 0, 0, 70, 0.07m))
            .Start(Run, null, 1, agent: "juez", provider: ProviderId.CodexCli, model: "gpt-6", purpose: AgentRunPurpose.Judge).Finish(Run, null, 1, session: "plan", usage: Codex(900, 100, 0, 90, 30))
            .Start(Run, null, 2, agent: "juez", provider: ProviderId.CodexCli, model: "gpt-6", purpose: AgentRunPurpose.Judge).Finish(Run, null, 2, session: "plan", resumed: "plan", usage: Codex(1500, 300, 0, 150, 50));

        var attempts = Project(log);

        // The same session id under two providers is two sessions: the judge's first attempt is never a difference.
        Assert.Equal([400L, 300L, 900L, 600L], attempts.Select(a => a.Own.InputTokens.Value!.Value));
        Assert.Equal([30L, 20L], attempts.Skip(2).Select(a => a.Own.ReasoningOutputTokens.Value!.Value));
    }

    [Fact]
    public void After_an_interrupted_attempt_the_next_one_subtracts_the_last_attempt_that_ended_with_a_result()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(1000, 0, 0, 100, 0.10m))
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, AttemptOutcome.Interrupted, version: 1)
            .Start(Run, "T1", 3).Finish(Run, "T1", 3, session: "s1", resumed: "s1", usage: Claude(1800, 0, 0, 180, 0.18m));

        var attempts = Project(log);

        Assert.Equal(MetricOrigin.Missing, attempts[1].Own.InputTokens.Origin);
        Assert.Equal(800L, attempts[2].Own.InputTokens.Value);
    }

    [Fact]
    public void When_a_figure_goes_down_the_cli_did_not_accumulate_and_the_whole_snapshot_counts_as_an_estimate()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(1000, 200, 0, 100, 0.10m))
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1", resumed: "s1", usage: Claude(1200, 100, 0, 150, 0.12m));

        var retry = Project(log)[1].Own;

        Assert.Equal((1200L, 100L, 150L), (retry.InputTokens.Value!.Value, retry.CacheReadInputTokens.Value!.Value, retry.OutputTokens.Value!.Value));
        Assert.Equal(0.12m, retry.CostUsd.Value);
        Assert.All(Figures(retry).Where(f => f.HasValue), f => Assert.Equal(MetricOrigin.EstimatedBySystem, f.Origin));
        Assert.Equal(MetricOrigin.EstimatedBySystem, retry.CostUsd.Origin);
        Assert.Equal(MetricOrigin.NotExposed, retry.ReasoningOutputTokens.Origin);
    }

    [Fact]
    public void An_attempt_that_resumed_a_session_under_a_new_id_still_shares_it()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(1000, 0, 0, 100, 0.10m))
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1-bis", resumed: "s1", usage: Claude(1300, 0, 0, 130, 0.13m))
            .Start(Run, "T1", 3).Finish(Run, "T1", 3, session: "s1-ter", resumed: "s1-bis", usage: Claude(1500, 0, 0, 150, 0.15m));

        Assert.Equal([1000L, 300L, 200L], Project(log).Select(a => a.Own.InputTokens.Value!.Value));
    }

    [Fact]
    public void A_figure_the_previous_attempt_lacks_cannot_be_told_apart_and_is_an_estimate()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1, version: 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: ClaudeV1(1000, 200, 100, 0.10m), version: 1)
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1", resumed: "s1", usage: Claude(1500, 300, 250, 150, 0.15m));

        var retry = Project(log)[1].Own;

        Assert.Equal(500L, retry.InputTokens.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, retry.InputTokens.Origin);
        Assert.Equal(250L, retry.CacheWriteInputTokens.Value);
        Assert.Equal(MetricOrigin.EstimatedBySystem, retry.CacheWriteInputTokens.Origin);
        Assert.Contains("acumulado de la sesión", retry.CacheWriteInputTokens.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_codex_usage_is_never_the_reference_of_the_next_attempt()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1, provider: ProviderId.CodexCli, model: "gpt-6").Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "th1", usage: Codex(500, 0, 0, 50, 0))
            .Start(Run, "T1", 2, provider: ProviderId.CodexCli, model: "gpt-6").Finish(Run, "T1", 2, AttemptOutcome.Failed, session: "th1", resumed: "th1", usage: Codex(0, 0, 0, 0, 0))
            .Start(Run, "T1", 3, provider: ProviderId.CodexCli, model: "gpt-6").Finish(Run, "T1", 3, session: "th1", resumed: "th1", usage: Codex(800, 0, 0, 80, 0));

        Assert.Equal([500L, null, 300L], Project(log).Select(a => a.Own.InputTokens.Value));
    }

    [Fact]
    public void The_breakdown_per_model_is_deduplicated_model_by_model()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(1000, 0, 0, 100, 0.10m, Model("claude-sonnet-5-5", 900, 90, 0, 0, 0.09m)))
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, session: "s1", resumed: "s1", usage: Claude(1700, 0, 0, 170, 0.17m,
                Model("claude-sonnet-5-5", 1500, 150, 0, 0, 0.15m),
                Model("claude-haiku-4-5", 100, 10, 0, 0, 0.01m, window: 200_000)));

        var retry = Project(log)[1];

        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-4-5"], retry.PerModel.Select(m => m.Model));
        var sonnet = retry.PerModel[0];
        Assert.Equal((600L, 60L, 0.06m), (sonnet.Figures.InputTokens.Value!.Value, sonnet.Figures.OutputTokens.Value!.Value, sonnet.Figures.CostUsd.Value!.Value));
        Assert.Equal(MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), sonnet.ContextWindow);
        Assert.Equal("list", sonnet.CostBasis);
        var haiku = retry.PerModel[1];
        Assert.Equal(100L, haiku.Figures.InputTokens.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, haiku.Figures.InputTokens.Origin);
        Assert.Equal(MetricOrigin.NotExposed, haiku.Figures.ReasoningOutputTokens.Origin);
    }

    [Fact]
    public void An_interrupted_attempt_belongs_to_the_session_it_resumed()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, session: "s1", usage: Claude(1000, 0, 0, 100, 0.10m))
            .Start(Run, "T1", 2).Finish(Run, "T1", 2, AttemptOutcome.Cancelled, resumed: "s1");

        Assert.Equal("s1", Project(log)[1].SessionId);
    }

    // ---- context, compactions ----

    [Fact]
    public void The_context_peak_and_the_compactions_come_from_a_version_2_end()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, session: "s1", usage: Claude(61_222, 30_391, 30_814, 50, 0.01m), peak: Peak("claude-sonnet-5-5", 30_822, 1_000_000), compactions: 2);

        var attempt = Assert.Single(Project(log));

        Assert.Equal("claude-sonnet-5-5", attempt.ContextPeak?.Model);
        Assert.Equal(MetricValue<long>.Exact(30_822, "claude assistant.message.usage"), attempt.ContextPeak!.UsedTokens);
        Assert.Equal(MetricValue<long>.Exact(1_000_000, "claude result.modelUsage"), attempt.ContextPeak.ContextWindow);
        Assert.Equal(2, attempt.Compactions);
    }

    [Fact]
    public void A_provider_that_does_not_report_context_has_a_peak_that_is_not_exposed()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1, provider: ProviderId.CodexCli, model: "gpt-6")
            .Finish(Run, "T1", 1, session: "th1", usage: Codex(500, 0, 0, 50, 0), launchedAt: T0);

        var attempt = Assert.Single(Project(log));

        Assert.Null(attempt.Compactions);
        Assert.Equal(MetricOrigin.NotExposed, attempt.ContextPeak?.UsedTokens.Origin);
        Assert.Equal(MetricOrigin.NotExposed, attempt.ContextPeak?.ContextWindow.Origin);
    }

    [Fact]
    public void A_claude_attempt_that_ended_before_its_first_step_has_a_missing_peak()
    {
        var log = new UsageLogBuilder().Start(Run, "T1", 1).Finish(Run, "T1", 1, AttemptOutcome.Failed, launchedAt: T0, compactions: 0);

        Assert.Equal(MetricOrigin.Missing, Assert.Single(Project(log)).ContextPeak?.UsedTokens.Origin);
    }

    [Fact]
    public void An_attempt_whose_process_never_started_has_a_missing_peak_whatever_the_provider()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1, provider: ProviderId.CodexCli, model: "gpt-6")
            .Finish(Run, "T1", 1, AttemptOutcome.Failed, error: "No se pudo lanzar el agente: boom");

        var peak = Assert.Single(Project(log)).ContextPeak;

        Assert.Equal(MetricOrigin.Missing, peak?.UsedTokens.Origin);
        Assert.Contains("no llegó a arrancar", peak?.UsedTokens.Source, StringComparison.Ordinal);
    }

    // ---- the log as a whole ----

    [Fact]
    public void Attempts_come_in_the_order_they_ended_and_starts_are_counted_for_the_reconciliation()
    {
        var log = new UsageLogBuilder()
            .Start(Run, "T1", 1, version: 1)
            .Start(Run, "T2", 1)
            .Finish(Run, "T2", 1, session: "b", usage: Claude(1, 0, 0, 1, 0m))
            .Finish(Run, "T1", 1, session: "a", usage: ClaudeV1(1, 0, 1, 0m), version: 1)
            .Start(Run, "T3", 1);

        var projection = UsageProjector.Project(UsageLogBuilder.ProjectId, log.Entries);

        Assert.Equal(["T2", "T1"], projection.Attempts.Select(a => a.TaskId));
        Assert.Equal(3, projection.Starts.Count);
        Assert.Equal(log.Entries[1].RecordedAt, projection.Phase4Since);
    }

    private static IEnumerable<MetricValue<long>> Figures(UsageFigures figures) =>
        [figures.InputTokens, figures.CacheReadInputTokens, figures.CacheWriteInputTokens, figures.OutputTokens, figures.ReasoningOutputTokens];
}
