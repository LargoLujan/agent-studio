using AgentStudio.Application.Analysis;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Analysis;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Analysis;

/// <summary>
/// F13-15 (docs/17 D-34, ADR-0052): the deterministic analysis of consumption, over prepared logs. Each of the twelve codes has
/// a case that finds it and one that does not; every finding says what it cost with its origin, never a saving, and never a
/// zero for a figure that is not there.
/// </summary>
public sealed class ConsumptionRulesTests
{
    private static readonly ModelCatalog Catalog = new(
        1,
        [
            new ModelCatalogEntry(ProviderId.ClaudeCode, "2.1.290", ["claude-opus-5-5", "opus"], ModelTier.Maximum, [ReasoningEffort.High]),
            new ModelCatalogEntry(ProviderId.ClaudeCode, "2.1.290", ["claude-sonnet-5-5", "sonnet"], ModelTier.Balanced, [ReasoningEffort.Medium]),
        ],
        ModelRecommendationRules.Default);

    private static ConsumptionReport Analyze(ConsumptionLog log, IReadOnlyDictionary<AttemptRef, AttemptChange>? changes = null, ModelCatalog? catalog = null) =>
        ConsumptionRules.Analyze(new ConsumptionInput(ConsumptionLog.ProjectId, ConsumptionLog.Run, log.Entries, T0)
        {
            Catalog = catalog,
            Changes = changes ?? new Dictionary<AttemptRef, AttemptChange>(),
        });

    private static IReadOnlyList<ConsumptionFinding> Of(ConsumptionReport report, string code) => [.. report.Findings.Where(f => f.Code == code)];

    private static ConsumptionLog Clean() =>
        new ConsumptionLog().Started().Attempt("T1", 1).Status("T1", WorkTaskStatus.Completed).Finished(RunPhase.Succeeded);

    [Fact]
    public void A_clean_run_has_no_finding_and_the_report_names_the_head_it_read()
    {
        var log = Clean();

        var report = Analyze(log, catalog: Catalog);

        Assert.Empty(report.Findings);
        Assert.Equal((ConsumptionLog.Run, log.Entries[^1].Hash, T0), (report.Run, report.LogHeadHash, report.At));
    }

    // ---- ctx.pack-omitted ----

    [Fact]
    public void A_pack_that_left_entries_out_is_thin_context_with_its_attempts_and_their_input()
    {
        var log = new ConsumptionLog().Started().Attempt("T1", 1, omitted: 2, input: 12_000).Attempt("T2", 1, omitted: 1, input: 8_000).Attempt("T3", 1, agent: "otro");

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.PackOmitted));

        Assert.Equal(new ConsumptionTarget(ConsumptionTargetKind.Agent, "ejecutor"), finding.Target);
        Assert.Equal((20_000L, MetricOrigin.ReportedByProvider), (finding.Tokens.Value!.Value, finding.Tokens.Origin));
        Assert.Equal(2, finding.Attempts);
        Assert.Equal(["ejecutor", 3, 2], finding.Arguments);
    }

    [Fact]
    public void A_pack_with_everything_in_is_not_thin_context() =>
        Assert.Empty(Of(Analyze(new ConsumptionLog().Started().Attempt("T1", 1, omitted: 0)), ConsumptionFindingCodes.PackOmitted));

    // ---- ctx.high-peak ----

    [Fact]
    public void A_peak_over_the_threshold_of_compression_is_found_with_its_share_of_the_window()
    {
        var log = new ConsumptionLog().Started().Attempt("T1", 1, peak: (180_000, 200_000)).Attempt("T2", 1, peak: (160_000, 200_000));

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.HighPeak));

        Assert.Equal(new ConsumptionTarget(ConsumptionTargetKind.Attempt, "T1#1"), finding.Target);
        Assert.Equal((180_000L, MetricOrigin.ReportedByProvider), (finding.Tokens.Value!.Value, finding.Tokens.Origin));
        Assert.Equal(["T1#1", 90, "claude-sonnet-5-5"], finding.Arguments);
    }

    [Fact]
    public void A_peak_without_a_window_or_without_one_says_nothing() =>
        Assert.Empty(Of(Analyze(new ConsumptionLog().Started().Attempt("T1", 1, provider: ProviderId.CodexCli, model: "gpt-6", compactions: null)), ConsumptionFindingCodes.HighPeak));

    // ---- ctx.compacted ----

    [Fact]
    public void An_attempt_whose_context_was_compacted_is_found_with_its_compactions()
    {
        var finding = Assert.Single(Of(Analyze(new ConsumptionLog().Started().Attempt("T1", 2, compactions: 3)), ConsumptionFindingCodes.Compacted));

        Assert.Equal(["T1#2", 3], finding.Arguments);
        Assert.Equal(10_000, finding.Tokens.Value);
    }

    [Fact]
    public void Codex_does_not_report_compactions_and_is_never_found_compacted() =>
        Assert.Empty(Of(Analyze(new ConsumptionLog().Started().Attempt("T1", 1, provider: ProviderId.CodexCli, model: "gpt-6", compactions: null)), ConsumptionFindingCodes.Compacted));

    // ---- ctx.heavy-preamble ----

    [Fact]
    public void Large_skills_and_memory_in_attempts_that_ended_soon_are_a_heavy_preamble_estimated_by_agent_studio()
    {
        IReadOnlyList<ContextPartSnapshot> heavy = [new(ContextPartKind.Protocol, 6_000), new(ContextPartKind.Memory, 10_000), new(ContextPartKind.Skills, 8_000)];
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, parts: heavy, duration: TimeSpan.FromMinutes(1))
            .Attempt("T2", 1, parts: heavy, duration: TimeSpan.FromMinutes(20))
            .Attempt("T3", 1, parts: heavy, duration: TimeSpan.FromMinutes(1), status: AttemptOutcome.Failed);

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.HeavyPreamble));

        Assert.Equal(new ConsumptionTarget(ConsumptionTargetKind.Agent, "ejecutor"), finding.Target);
        Assert.Equal((4_500L, MetricOrigin.EstimatedBySystem), (finding.Tokens.Value!.Value, finding.Tokens.Origin));
        Assert.Equal(["ejecutor", 2_000L, 2_500L, 1], finding.Arguments);
    }

    [Fact]
    public void A_light_preamble_or_a_start_without_parts_is_not_heavy()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, parts: [new(ContextPartKind.Protocol, 60_000), new(ContextPartKind.Skills, 400)], duration: TimeSpan.FromMinutes(1))
            .Attempt("T2", 1, duration: TimeSpan.FromMinutes(1));

        Assert.Empty(Of(Analyze(log), ConsumptionFindingCodes.HeavyPreamble));
    }

    // ---- retry.same-class ----

    [Fact]
    public void Attempts_in_a_row_with_the_same_end_are_found_with_their_input()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, status: AttemptOutcome.Failed, end: AttemptEndClass.ContextExhausted, input: 5_000)
            .Attempt("T1", 2, status: AttemptOutcome.Failed, end: AttemptEndClass.ContextExhausted, input: 7_000)
            .Attempt("T1", 3);

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.RetrySameClass));

        Assert.Equal(new ConsumptionTarget(ConsumptionTargetKind.Task, "T1"), finding.Target);
        Assert.Equal((12_000L, 2), (finding.Tokens.Value!.Value, finding.Attempts));
        Assert.Equal(["T1", 2, "ContextExhausted"], finding.Arguments);
    }

    [Fact]
    public void Different_ends_or_completed_attempts_are_not_the_same_class()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, status: AttemptOutcome.Failed, end: AttemptEndClass.ContextExhausted)
            .Attempt("T1", 2, status: AttemptOutcome.Failed, end: AttemptEndClass.Network)
            .Attempt("T2", 1)
            .Attempt("T2", 2);

        Assert.Empty(Of(Analyze(log), ConsumptionFindingCodes.RetrySameClass));
    }

    // ---- retry.same-failing-tests ----

    [Fact]
    public void Attempts_in_a_row_with_the_same_tests_in_red_are_found()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1).Tests("T1", 1, failed: 2, passed: 10)
            .Attempt("T1", 2).Tests("T1", 2, failed: 2, passed: 10)
            .Attempt("T1", 3).Tests("T1", 3, failed: 0, passed: 12);

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.RetrySameFailingTests));

        Assert.Equal(["T1", 2, 2, "Pruebas (test)"], finding.Arguments);
        Assert.Equal(20_000, finding.Tokens.Value);
    }

    [Fact]
    public void Different_failing_counters_are_not_the_same_tests()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1).Tests("T1", 1, failed: 2, passed: 10)
            .Attempt("T1", 2).Tests("T1", 2, failed: 1, passed: 11);

        Assert.Empty(Of(Analyze(log), ConsumptionFindingCodes.RetrySameFailingTests));
    }

    // ---- plan.corrected and verdict.corrected ----

    [Fact]
    public void A_correction_of_the_plan_made_in_its_planning_is_found_for_the_run_with_the_planner_s_second_attempt()
    {
        var log = new ConsumptionLog()
            .Attempt(null, 1, agent: "orquestador", purpose: AgentRunPurpose.Planner, input: 30_000, run: "20261107-plan01")
            .PlanRevised(run: "20261107-plan01")
            .Attempt(null, 2, agent: "orquestador", purpose: AgentRunPurpose.Planner, input: 25_000, run: "20261107-plan01")
            .Started();

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.PlanCorrected));

        Assert.Equal(new ConsumptionTarget(ConsumptionTargetKind.Run, ConsumptionLog.Run.Value), finding.Target);
        Assert.Equal((25_000L, 1), (finding.Tokens.Value!.Value, finding.Attempts));
        Assert.Equal([1], finding.Arguments);
    }

    [Fact]
    public void A_plan_revised_for_another_plan_is_not_a_correction_of_this_one()
    {
        var log = new ConsumptionLog().PlanRevised(run: "20261107-otro", planHash: "otro-plan").Started();

        Assert.Empty(Of(Analyze(log), ConsumptionFindingCodes.PlanCorrected));
    }

    [Fact]
    public void A_verdict_that_supersedes_another_is_a_correction_with_the_judge_s_later_attempts()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("R1", 1, agent: "juez", purpose: AgentRunPurpose.Judge, provider: ProviderId.CodexCli, model: "gpt-6", input: 40_000, compactions: null).Verdict("v1", null)
            .Attempt("R1", 2, agent: "juez", purpose: AgentRunPurpose.Judge, provider: ProviderId.CodexCli, model: "gpt-6", input: 15_000, compactions: null).Verdict("v2", "v1");

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.VerdictCorrected));

        Assert.Equal((15_000L, 1), (finding.Tokens.Value!.Value, finding.Attempts));
        Assert.Equal([1], finding.Arguments);
        Assert.Empty(Of(Analyze(new ConsumptionLog().Started().Verdict("v1", null)), ConsumptionFindingCodes.VerdictCorrected));
    }

    // ---- model.oversized ----

    [Fact]
    public void Level_maximum_on_a_small_change_is_oversized_and_names_its_model()
    {
        var log = new ConsumptionLog().Started().Attempt("T1", 1, model: "claude-opus-5-5", input: 50_000).Attempt("T2", 1, model: "claude-opus-5-5");
        var changes = new Dictionary<AttemptRef, AttemptChange>
        {
            [new AttemptRef(new TaskId("T1"), 1)] = new(2, 30),
            [new AttemptRef(new TaskId("T2"), 1)] = new(8, 400),
        };

        Assert.Equal([new AttemptRef(new TaskId("T1"), 1), new AttemptRef(new TaskId("T2"), 1)], ConsumptionRules.OversizedCandidates(log.Entries, ConsumptionLog.Run, Catalog));
        var finding = Assert.Single(Of(Analyze(log, changes, Catalog), ConsumptionFindingCodes.ModelOversized));

        Assert.Equal(["ejecutor", "claude-opus-5-5", 1], finding.Arguments);
        Assert.Equal(50_000, finding.Tokens.Value);
    }

    [Fact]
    public void Without_a_catalog_a_balanced_model_or_lines_unknown_nothing_is_oversized()
    {
        var log = new ConsumptionLog().Started().Attempt("T1", 1, model: "claude-opus-5-5").Attempt("T2", 1, model: "claude-sonnet-5-5");
        var changes = new Dictionary<AttemptRef, AttemptChange>
        {
            [new AttemptRef(new TaskId("T1"), 1)] = new(1, null),
            [new AttemptRef(new TaskId("T2"), 1)] = new(1, 5),
        };

        Assert.Empty(ConsumptionRules.OversizedCandidates(log.Entries, ConsumptionLog.Run, null));
        Assert.Empty(Of(Analyze(log, changes), ConsumptionFindingCodes.ModelOversized));
        Assert.Empty(Of(Analyze(log, changes, Catalog), ConsumptionFindingCodes.ModelOversized));
    }

    // ---- cache.low-reuse ----

    [Fact]
    public void A_resumed_session_that_read_little_from_the_cache_is_found_with_the_input_it_did_not_read()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, session: "s1", input: 10_000, read: 0)
            .Attempt("T1", 2, session: "s1", resumed: "s1", input: 30_000, read: 2_000);

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.CacheLowReuse));

        Assert.Equal(new ConsumptionTarget(ConsumptionTargetKind.Attempt, "T1#2"), finding.Target);
        Assert.Equal(18_000, finding.Tokens.Value);
        Assert.Equal(["T1#2", 10], finding.Arguments);
    }

    [Fact]
    public void A_resumed_session_that_read_its_cache_or_a_new_session_is_fine()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, session: "s1", input: 10_000, read: 0)
            .Attempt("T1", 2, session: "s1", resumed: "s1", input: 30_000, read: 15_000)
            .Attempt("T2", 1, input: 50_000, read: 0);

        Assert.Empty(Of(Analyze(log), ConsumptionFindingCodes.CacheLowReuse));
    }

    // ---- work.discarded ----

    [Fact]
    public void The_attempts_of_a_task_that_failed_are_discarded_and_those_of_one_that_succeeded_after_retrying_are_not()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, status: AttemptOutcome.Failed, input: 4_000).Attempt("T1", 2).Status("T1", WorkTaskStatus.Completed, 2)
            .Attempt("T2", 1, status: AttemptOutcome.Failed, input: 6_000).Status("T2", WorkTaskStatus.Failed)
            .Attempt("T3", 1, status: AttemptOutcome.Cancelled, input: 1_000).Status("T3", WorkTaskStatus.Cancelled)
            .Finished(RunPhase.Succeeded);

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.WorkDiscarded));

        Assert.Equal((7_000L, 2), (finding.Tokens.Value!.Value, finding.Attempts));
        Assert.Equal([2, 2], finding.Arguments);
        Assert.Null(ConsumptionTexts.Suggestion(finding));
    }

    [Fact]
    public void A_run_that_failed_discards_every_attempt_and_one_that_waits_for_its_delivery_discards_nothing()
    {
        var failed = new ConsumptionLog().Started().Attempt("T1", 1).Status("T1", WorkTaskStatus.Completed).Attempt("T2", 1).Finished(RunPhase.Failed);

        Assert.Equal([2, 2], Assert.Single(Of(Analyze(failed), ConsumptionFindingCodes.WorkDiscarded)).Arguments);
        Assert.Empty(Of(Analyze(Clean()), ConsumptionFindingCodes.WorkDiscarded));
    }

    // ---- wait.queue ----

    [Fact]
    public void Long_waits_in_the_queue_of_a_provider_are_found_with_their_time_and_no_tokens()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, queue: TimeSpan.FromMinutes(5))
            .Attempt("T2", 1, queue: TimeSpan.FromMinutes(3))
            .Attempt("T3", 1, queue: TimeSpan.FromSeconds(30));

        var finding = Assert.Single(Of(Analyze(log), ConsumptionFindingCodes.WaitQueue));

        Assert.Equal(new ConsumptionTarget(ConsumptionTargetKind.Setting, "provider-concurrency:claude-code"), finding.Target);
        Assert.Equal(MetricOrigin.Missing, finding.Tokens.Origin);
        Assert.Null(finding.Tokens.Value);
        Assert.Equal(["claude-code", 2, TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)], finding.Arguments);
    }

    [Fact]
    public void Short_waits_are_not_found() =>
        Assert.Empty(Of(Analyze(new ConsumptionLog().Started().Attempt("T1", 1, queue: TimeSpan.FromSeconds(90))), ConsumptionFindingCodes.WaitQueue));

    // ---- the report ----

    [Fact]
    public void Findings_go_in_the_order_of_the_codes_and_only_of_this_run()
    {
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, queue: TimeSpan.FromMinutes(5), compactions: 1, omitted: 1)
            .Attempt("T9", 1, omitted: 4, run: "20261107-ffffff");

        var report = Analyze(log);

        Assert.Equal([ConsumptionFindingCodes.PackOmitted, ConsumptionFindingCodes.Compacted, ConsumptionFindingCodes.WaitQueue], report.Findings.Select(f => f.Code));
        Assert.Equal([ConsumptionTexts.SuggestionCode(ConsumptionFindingCodes.PackOmitted)], [ConsumptionTexts.Suggestion(report.Findings[0])!.Code]);
    }

    [Fact]
    public void A_sum_without_figures_says_why_and_a_partial_one_is_an_estimate()
    {
        var notExposed = ConsumptionRules.Sum([MetricValue<long>.NotExposed("codex"), MetricValue<long>.NotExposed("codex")]);
        var none = ConsumptionRules.Sum([MetricValue<long>.Missing("x"), null]);
        var partial = ConsumptionRules.Sum([MetricValue<long>.Exact(10, "cli"), MetricValue<long>.Missing("x")]);
        var whole = ConsumptionRules.Sum([MetricValue<long>.Exact(10, "cli"), MetricValue<long>.Estimated(5, "cli")]);

        Assert.Equal((MetricOrigin.NotExposed, (long?)null), (notExposed.Origin, notExposed.Value));
        Assert.Equal((MetricOrigin.Missing, (long?)null), (none.Origin, none.Value));
        Assert.Equal((MetricOrigin.EstimatedBySystem, (long?)10), (partial.Origin, partial.Value));
        Assert.Equal((MetricOrigin.EstimatedByProvider, (long?)15), (whole.Origin, whole.Value));
    }
}
