using AgentStudio.Application.Decisions;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Observability.Estimates;
using AgentStudio.Tests.Observability.Usage;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Estimates;

/// <summary>
/// The estimator of the system (docs/16 D-20, F9-7 brought forward; docs/17 D-32 and F13-11): the 25th and 75th percentiles of
/// similar attempts of the verified log, each figure with its origin and the assumptions it rests on; «desconocida» with fewer
/// than three references, never a number made up; deterministic; and a plan by its critical path.
/// </summary>
public sealed class UsageEstimatorTests
{
    private static readonly ModelCatalog Catalog = BuiltInModelCatalog.Shipped.Catalog;

    private static readonly ProjectContext Project = new("p1", "C:\\repo");

    private static UsageAttempt Attempt(
        string run,
        string? task,
        int attempt = 1,
        string agent = "ejecutor",
        ProviderId? provider = null,
        string? configured = "sonnet",
        string? reported = "claude-sonnet-5-5",
        AgentRunPurpose? purpose = AgentRunPurpose.Executor,
        long input = 1000,
        long output = 100,
        decimal? cost = 0.10m,
        int minutes = 1,
        AttemptOutcome outcome = AttemptOutcome.Succeeded,
        bool launched = true,
        ReasoningEffort? effort = null)
    {
        var on = provider ?? ProviderId.ClaudeCode;
        var costFigure = cost is { } value ? MetricValue<decimal>.Estimated(value, "claude total_cost_usd") : MetricValue<decimal>.NotExposed("codex exec does not report cost");
        var finished = T0.AddHours(1).AddMinutes(minutes);
        return new UsageAttempt(
            "p1",
            run,
            task,
            attempt,
            purpose,
            new AgentId(agent),
            on,
            UsageLogBuilder.VendorOf(on),
            configured,
            reported,
            $"{run}-{task}-{attempt}",
            T0,
            launched ? T0.AddHours(1) : null,
            finished,
            outcome,
            new UsageFigures(
                MetricValue<long>.Exact(input, "usage"),
                MetricValue<long>.Exact(0, "usage"),
                MetricValue<long>.Exact(0, "usage"),
                MetricValue<long>.Exact(output, "usage"),
                MetricValue<long>.NotExposed("no reasoning"),
                costFigure),
            null,
            [],
            null,
            null)
        {
            RecordedVersion = 2,
            Effort = effort,
        };
    }

    private static UsageEstimateRequest Request(string agent = "ejecutor", string? model = "sonnet", AgentRunPurpose purpose = AgentRunPurpose.Executor, WorkSize? size = null, ProviderId? provider = null, ReasoningEffort? effort = null) =>
        new(Project, new AgentId(agent), provider ?? ProviderId.ClaudeCode, model, purpose, size, effort);

    /// <summary>Four attempts of the executor with sonnet: 1 000 to 4 000 tokens in, 1 to 4 minutes, 0.10 to 0.40.</summary>
    private static List<UsageAttempt> Four() =>
    [
        Attempt("r1", "T1", input: 1000, output: 100, cost: 0.10m, minutes: 1),
        Attempt("r1", "T2", input: 2000, output: 200, cost: 0.20m, minutes: 2),
        Attempt("r2", "T1", input: 3000, output: 300, cost: 0.30m, minutes: 3),
        Attempt("r2", "T2", input: 4000, output: 400, cost: 0.40m, minutes: 4),
    ];

    [Fact]
    public void An_attempt_is_estimated_from_the_25th_and_75th_percentiles_of_the_same_agent_model_and_purpose()
    {
        var estimate = UsageEstimation.Estimate(Four(), Catalog, Request());

        Assert.True(estimate.IsKnown);
        Assert.Equal(4, estimate.References);
        Assert.Equal((1000L, 3000L), (estimate.InputTokens.Low.Value!.Value, estimate.InputTokens.High.Value!.Value));
        Assert.Equal((100L, 300L), (estimate.OutputTokens.Low.Value!.Value, estimate.OutputTokens.High.Value!.Value));
        Assert.Equal((TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)), (estimate.Duration.Low.Value!.Value, estimate.Duration.High.Value!.Value));
        Assert.Equal((0.10m, 0.30m), (estimate.CostUsd.Low.Value!.Value, estimate.CostUsd.High.Value!.Value));
        Assert.Equal([EstimateAssumptions.SameAgentAndModel, EstimateAssumptions.SamePurpose, EstimateAssumptions.ListPrice], estimate.Assumptions);
        Assert.Equal(MetricOrigin.EstimatedBySystem, estimate.InputTokens.Low.Origin);
        Assert.Contains("4 attempts of the same agent, model and purpose", estimate.InputTokens.High.Source, StringComparison.Ordinal);
    }

    /// <summary>Nearest rank: each end is a figure some attempt really had, never an interpolation.</summary>
    [Fact]
    public void Each_end_of_the_interval_is_a_figure_some_attempt_had()
    {
        var attempts = Four().Concat([Attempt("r3", "T1", input: 9999)]).ToList();

        var estimate = UsageEstimation.Estimate(attempts, Catalog, Request());

        Assert.Contains(estimate.InputTokens.Low.Value!.Value, attempts.Select(a => a.Own.InputTokens.Value!.Value));
        Assert.Contains(estimate.InputTokens.High.Value!.Value, attempts.Select(a => a.Own.InputTokens.Value!.Value));
        Assert.Equal((2000L, 4000L), (estimate.InputTokens.Low.Value!.Value, estimate.InputTokens.High.Value!.Value));
    }

    [Fact]
    public void With_fewer_than_three_references_everything_is_unknown_and_nothing_is_made_up()
    {
        var estimate = UsageEstimation.Estimate(Four().Take(2).ToList(), Catalog, Request());

        Assert.False(estimate.IsKnown);
        Assert.Equal(2, estimate.References);
        Assert.All(
            [estimate.InputTokens.Low, estimate.InputTokens.High, estimate.OutputTokens.Low, estimate.OutputTokens.High],
            figure => Assert.Equal(((long?)null, MetricOrigin.Missing), (figure.Value, figure.Origin)));
        Assert.False(estimate.Duration.IsKnown);
        Assert.False(estimate.CostUsd.IsKnown);
        Assert.Equal([EstimateAssumptions.SameAgentAndModel, EstimateAssumptions.SamePurpose], estimate.Assumptions);
        Assert.False(UsageEstimation.Estimate([], Catalog, Request()).IsKnown);
    }

    /// <summary>An alias and its full name, with or without the date the CLI appends, are one model.</summary>
    [Fact]
    public void An_alias_and_its_full_name_are_the_same_model()
    {
        List<UsageAttempt> attempts =
        [
            Attempt("r1", "T1", configured: "sonnet", reported: "claude-sonnet-5-5-20261001"),
            Attempt("r1", "T2", configured: "claude-sonnet-5-5", reported: null),
            Attempt("r2", "T1", configured: "sonnet", reported: "claude-sonnet-5-5"),
        ];

        Assert.Equal(3, UsageEstimation.Estimate(attempts, Catalog, Request(model: "claude-sonnet-5-5")).References);
        Assert.Equal(3, UsageEstimation.Estimate(attempts, Catalog, Request(model: "SONNET")).References);
        Assert.Equal(0, UsageEstimation.Estimate(attempts, Catalog, Request(model: "opus")).References);
    }

    /// <summary>«Aprendido del modelo»: an agent with fewer than three attempts takes those of its model by any agent, and says so.</summary>
    [Fact]
    public void An_agent_without_three_attempts_takes_those_of_its_model_and_says_so()
    {
        List<UsageAttempt> attempts =
        [
            Attempt("r1", "T1", agent: "nuevo", input: 5000),
            Attempt("r1", "T2", agent: "ejecutor", input: 1000),
            Attempt("r2", "T1", agent: "ejecutor", input: 2000),
            Attempt("r2", "T2", agent: "otro", input: 3000),
        ];

        var estimate = UsageEstimation.Estimate(attempts, Catalog, Request(agent: "nuevo"));

        Assert.Equal(4, estimate.References);
        Assert.Equal([EstimateAssumptions.SameModel, EstimateAssumptions.SamePurpose, EstimateAssumptions.ListPrice], estimate.Assumptions);
        Assert.Contains("same model and purpose by any agent", estimate.InputTokens.Low.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_cli_purpose_or_model_and_attempts_without_a_result_are_not_references()
    {
        List<UsageAttempt> attempts =
        [
            .. Four().Take(2),
            Attempt("r3", "T1", purpose: AgentRunPurpose.Judge),
            Attempt("r3", "T2", configured: "opus", reported: "claude-opus-5-5"),
            Attempt("r3", "T3", provider: ProviderId.CodexCli, configured: "sonnet", reported: "sonnet"),
            Attempt("r3", "T4", outcome: AttemptOutcome.Cancelled),
            Attempt("r3", "T5", outcome: AttemptOutcome.Interrupted),
            Attempt("r3", "T6", purpose: null, configured: "sonnet", reported: "claude-sonnet-5-5") with { TaskId = null },
        ];

        Assert.Equal(2, UsageEstimation.Estimate(attempts, Catalog, Request()).References);
    }

    /// <summary>Codex does not expose the cost: «no expuesto», never zero, while its tokens are estimated.</summary>
    [Fact]
    public void A_cost_the_cli_does_not_expose_is_not_exposed_and_never_zero()
    {
        List<UsageAttempt> attempts =
        [
            Attempt("r1", "R", agent: "juez", provider: ProviderId.CodexCli, configured: "gpt-6-luna", reported: "gpt-6-luna", purpose: AgentRunPurpose.Judge, cost: null),
            Attempt("r2", "R", agent: "juez", provider: ProviderId.CodexCli, configured: "gpt-6-luna", reported: "gpt-6-luna", purpose: AgentRunPurpose.Judge, cost: null),
            Attempt("r3", "R", agent: "juez", provider: ProviderId.CodexCli, configured: "gpt-6-luna", reported: "gpt-6-luna", purpose: AgentRunPurpose.Judge, cost: null),
        ];

        var estimate = UsageEstimation.Estimate(attempts, Catalog, Request("juez", "gpt-6-luna", AgentRunPurpose.Judge, provider: ProviderId.CodexCli));

        Assert.True(estimate.InputTokens.IsKnown);
        Assert.Equal(((decimal?)null, MetricOrigin.NotExposed), (estimate.CostUsd.Low.Value, estimate.CostUsd.Low.Origin));
        Assert.Equal(((decimal?)null, MetricOrigin.NotExposed), (estimate.CostUsd.High.Value, estimate.CostUsd.High.Origin));
        Assert.Contains(EstimateAssumptions.CostNotExposed, estimate.Assumptions);
        Assert.DoesNotContain(EstimateAssumptions.ListPrice, estimate.Assumptions);
    }

    [Fact]
    public void A_figure_fewer_than_three_references_have_is_unknown_on_its_own()
    {
        List<UsageAttempt> attempts = [.. Four().Take(1), .. Four().Skip(1).Select(a => a with { LaunchedAt = null })];

        var estimate = UsageEstimation.Estimate(attempts, Catalog, Request());

        Assert.True(estimate.InputTokens.IsKnown);
        Assert.False(estimate.Duration.IsKnown);
        Assert.Contains("1 of 3", estimate.Duration.Low.Source, StringComparison.Ordinal);
    }

    /// <summary>D-32: the size the agent declares picks the third of the references with the same size of work.</summary>
    [Theory]
    [InlineData(WorkSize.Small, 1000L, 3000L)]
    [InlineData(WorkSize.Medium, 4000L, 6000L)]
    [InlineData(WorkSize.Large, 7000L, 9000L)]
    public void A_size_takes_its_third_of_the_references(WorkSize size, long low, long high)
    {
        var attempts = Enumerable.Range(1, 9).Select(i => Attempt("r" + i, "T1", input: i * 1000, output: 0)).Reverse().ToList();

        var estimate = UsageEstimation.Estimate(attempts, Catalog, Request(size: size));

        Assert.Equal(3, estimate.References);
        Assert.Equal((low, high), (estimate.InputTokens.Low.Value!.Value, estimate.InputTokens.High.Value!.Value));
        Assert.Equal([EstimateAssumptions.SameAgentAndModel, EstimateAssumptions.SamePurpose, EstimateAssumptions.SameSize, EstimateAssumptions.ListPrice], estimate.Assumptions);
        Assert.False(UsageEstimation.Estimate(attempts.Take(6).ToList(), Catalog, Request(size: size)).IsKnown);
    }

    [Fact]
    public void The_default_model_of_the_cli_is_estimated_from_attempts_configured_without_a_model()
    {
        List<UsageAttempt> attempts =
        [
            Attempt("r1", "T1", configured: null, reported: "claude-opus-5-5"),
            Attempt("r1", "T2", configured: null, reported: "claude-opus-5-5"),
            Attempt("r2", "T1", configured: null, reported: "claude-sonnet-5-5"),
            Attempt("r2", "T2", configured: "opus", reported: "claude-opus-5-5"),
        ];

        Assert.Equal(3, UsageEstimation.Estimate(attempts, Catalog, Request(model: null)).References);
        Assert.Equal(3, UsageEstimation.Estimate(attempts, Catalog, Request(model: "opus")).References);
    }

    // ---- the reasoning effort (docs/18 P47, ADR-0050) ----

    /// <summary>Three attempts of sonnet at each effort: 1 000 to 3 000 tokens in at <c>low</c>, 10 000 to 30 000 at <c>high</c>.</summary>
    private static List<UsageAttempt> LowAndHigh() =>
    [
        .. Enumerable.Range(1, 3).Select(i => Attempt("r" + i, "T1", input: 1000 * i, effort: ReasoningEffort.Low)),
        .. Enumerable.Range(1, 3).Select(i => Attempt("h" + i, "T1", input: 10_000 * i, effort: ReasoningEffort.High)),
    ];

    [Fact]
    public void Attempts_at_low_and_at_high_effort_of_the_same_model_are_not_references_of_each_other()
    {
        var low = UsageEstimation.Estimate(LowAndHigh(), Catalog, Request(effort: ReasoningEffort.Low));
        var high = UsageEstimation.Estimate(LowAndHigh(), Catalog, Request(effort: ReasoningEffort.High));

        Assert.Equal(3, low.References);
        Assert.Equal((1000L, 3000L), (low.InputTokens.Low.Value!.Value, low.InputTokens.High.Value!.Value));
        Assert.Equal(3, high.References);
        Assert.Equal((10_000L, 30_000L), (high.InputTokens.Low.Value!.Value, high.InputTokens.High.Value!.Value));
        Assert.Equal([EstimateAssumptions.SameAgentAndModel, EstimateAssumptions.SamePurpose, EstimateAssumptions.SameEffort, EstimateAssumptions.ListPrice], low.Assumptions);
        Assert.Contains("of 3 attempts of the same agent, model and purpose, low effort", low.InputTokens.Low.Source, StringComparison.Ordinal);
        Assert.Contains("of 3 attempts of the same agent, model and purpose, high effort", high.InputTokens.High.Source, StringComparison.Ordinal);

        // An effort no attempt had has no references, and the mixed six are never counted together.
        var medium = UsageEstimation.Estimate(LowAndHigh(), Catalog, Request(effort: ReasoningEffort.Medium));
        Assert.False(medium.IsKnown);
        Assert.Equal(0, medium.References);
        Assert.Equal(0, UsageEstimation.Estimate(LowAndHigh(), Catalog, Request()).References);
    }

    /// <summary>Attempts that recorded no effort (every start before the field) are comparable with each other, and not with one that has it.</summary>
    [Fact]
    public void Attempts_without_an_effort_are_references_of_each_other_and_not_of_an_agent_that_has_one()
    {
        var attempts = Four().Concat(Enumerable.Range(1, 3).Select(i => Attempt("h" + i, "T9", input: 50_000 * i, effort: ReasoningEffort.High))).ToList();

        var none = UsageEstimation.Estimate(attempts, Catalog, Request());
        var high = UsageEstimation.Estimate(attempts, Catalog, Request(effort: ReasoningEffort.High));

        Assert.Equal(4, none.References);
        Assert.Equal((1000L, 3000L), (none.InputTokens.Low.Value!.Value, none.InputTokens.High.Value!.Value));
        Assert.DoesNotContain(EstimateAssumptions.SameEffort, none.Assumptions);
        Assert.DoesNotContain("effort", none.InputTokens.Low.Source, StringComparison.Ordinal);
        Assert.Equal(3, high.References);
        Assert.Equal((50_000L, 150_000L), (high.InputTokens.Low.Value!.Value, high.InputTokens.High.Value!.Value));
        Assert.Equal(0, UsageEstimation.Estimate(Four(), Catalog, Request(effort: ReasoningEffort.High)).References);
    }

    /// <summary>The effort holds in both scopes: the agent's own attempts, and those of its model by any agent.</summary>
    [Fact]
    public void The_effort_is_compared_among_the_attempts_of_the_model_by_any_agent_too()
    {
        List<UsageAttempt> attempts =
        [
            Attempt("r1", "T1", agent: "nuevo", input: 5000, effort: ReasoningEffort.High),
            Attempt("r1", "T2", agent: "ejecutor", input: 1000, effort: ReasoningEffort.High),
            Attempt("r2", "T1", agent: "otro", input: 2000, effort: ReasoningEffort.High),
            Attempt("r2", "T2", agent: "ejecutor", input: 99_000, effort: ReasoningEffort.Low),
            Attempt("r3", "T1", agent: "ejecutor", input: 98_000),
        ];

        var estimate = UsageEstimation.Estimate(attempts, Catalog, Request(agent: "nuevo", effort: ReasoningEffort.High));

        Assert.Equal(3, estimate.References);
        Assert.Equal((1000L, 5000L), (estimate.InputTokens.Low.Value!.Value, estimate.InputTokens.High.Value!.Value));
        Assert.Equal([EstimateAssumptions.SameModel, EstimateAssumptions.SamePurpose, EstimateAssumptions.SameEffort, EstimateAssumptions.ListPrice], estimate.Assumptions);
        Assert.Contains("of 3 attempts of the same model and purpose by any agent, high effort", estimate.InputTokens.Low.Source, StringComparison.Ordinal);
    }

    /// <summary>A plan estimates each task with the effort of its agent, in the references of its figures and in the runs of similar tasks.</summary>
    [Fact]
    public void A_plan_estimates_each_task_with_the_effort_of_its_agent()
    {
        IReadOnlyList<AgentDefinition> roster = [Roster[0] with { Effort = ReasoningEffort.High }, Roster[1]];
        var request = PlanRequest([Spec("T1", WorkTaskKind.AgentWork, "ejecutor"), Spec("R", WorkTaskKind.Review, "juez", "T1")]) with { Roster = roster };

        // The history before the effort was recorded gives the executor none: the judge, who has no effort, keeps his.
        var before = UsageEstimation.EstimatePlan(History(), Catalog, request);
        Assert.False(before.Tasks[new TaskId("T1")].IsKnown);
        Assert.True(before.Tasks[new TaskId("R")].IsKnown);
        Assert.False(before.AgentRuns.IsKnown);

        var recorded = History().Select(a => a.Purpose == AgentRunPurpose.Executor ? a with { Effort = ReasoningEffort.High } : a).ToList();
        var after = UsageEstimation.EstimatePlan(recorded, Catalog, request);
        Assert.True(after.Tasks[new TaskId("T1")].IsKnown);
        Assert.Contains(EstimateAssumptions.SameEffort, after.Tasks[new TaskId("T1")].Assumptions);
        Assert.DoesNotContain(EstimateAssumptions.SameEffort, after.Tasks[new TaskId("R")].Assumptions);
        Assert.Equal((2L, 3L), (after.AgentRuns.Low.Value!.Value, after.AgentRuns.High.Value!.Value));
    }

    [Fact]
    public async Task The_service_reads_the_effort_of_each_attempt_from_its_start_in_the_log()
    {
        var log = new UsageLogBuilder();
        for (var i = 1; i <= 3; i++)
        {
            log.Start("r" + i, "T1", 1, model: "sonnet", version: AgentRunStartedPayload.ContextPartsVersion, effort: ReasoningEffort.XHigh)
                .Finish("r" + i, "T1", 1, session: "s" + i, usage: Snapshots.Claude(1000 * i, 0, 0, 100 * i, 0.1m * i), launchedAt: log.Now, reportedModel: "claude-sonnet-5-5");
        }

        var estimator = new UsageEstimator(
            new FakeLogReader(new ProjectLogSnapshot("p1", "C:\\repo", log.Entries, DecisionLogVerification.Valid, AnchorStatus.Anchored)),
            new BuiltInModelCatalog());

        var xhigh = await estimator.EstimateAsync(Request(effort: ReasoningEffort.XHigh), TestContext.Current.CancellationToken);
        var none = await estimator.EstimateAsync(Request(), TestContext.Current.CancellationToken);
        var low = await estimator.EstimateAsync(Request(effort: ReasoningEffort.Low), TestContext.Current.CancellationToken);

        Assert.Equal((3, 0, 0), (xhigh.References, none.References, low.References));
        Assert.Equal((1000L, 3000L), (xhigh.InputTokens.Low.Value!.Value, xhigh.InputTokens.High.Value!.Value));
    }

    [Fact]
    public void The_same_attempts_always_give_the_same_estimate_whatever_their_order()
    {
        var first = UsageEstimation.Estimate(Four(), Catalog, Request());
        var second = UsageEstimation.Estimate([.. Four().AsEnumerable().Reverse()], Catalog, Request());

        Assert.Equal(Shape(first), Shape(second));

        static string Shape(UsageEstimate e) =>
            $"{e.References}|{e.InputTokens}|{e.OutputTokens}|{e.Duration}|{e.CostUsd}|{string.Join(',', e.Assumptions)}";
    }

    /// <summary>
    /// D-32: the total of an attempt is estimated as such. Totals 1 100, 2 900, 3 100 and 4 400 give 1 100 to 3 100; adding
    /// the percentiles of each part (1 000 + 100 to 3 000 + 400) would give 1 100 to 3 400, which no attempt had.
    /// </summary>
    [Fact]
    public void The_total_tokens_are_the_percentiles_of_the_totals_never_the_sum_of_the_percentiles()
    {
        List<UsageAttempt> attempts =
        [
            Attempt("r1", "T1", input: 1000, output: 100),
            Attempt("r1", "T2", input: 2000, output: 900),
            Attempt("r2", "T1", input: 3000, output: 100),
            Attempt("r2", "T2", input: 4000, output: 400),
        ];

        var estimate = UsageEstimation.Estimate(attempts, Catalog, Request());

        Assert.Equal((1100L, 3100L), (estimate.TotalTokens!.Low.Value!.Value, estimate.TotalTokens.High.Value!.Value));
        Assert.Equal((1000L + 100L, 3000L + 400L), (estimate.InputTokens.Low.Value!.Value + estimate.OutputTokens.Low.Value!.Value, estimate.InputTokens.High.Value!.Value + estimate.OutputTokens.High.Value!.Value));
        Assert.Equal(MetricOrigin.EstimatedBySystem, estimate.TotalTokens.High.Origin);
        Assert.Contains("total tokens of 4 attempts", estimate.TotalTokens.High.Source, StringComparison.Ordinal);
    }

    /// <summary>The window of a question records each option as <c>OptionEstimateSnapshot</c>: the total with its origin and its references.</summary>
    [Fact]
    public void An_option_is_recorded_as_the_snapshot_of_its_total_with_its_origin_and_references()
    {
        var known = UsageEstimation.Estimate(Four(), Catalog, Request()).ToSnapshot("a");

        Assert.Equal(("a", 4), (known.Option, known.References));
        Assert.Equal((1100m, 3300m), (known.Low.Value, known.High.Value));
        Assert.Equal((MetricOrigin.EstimatedBySystem, MetricProvenance.Estimated), (known.Low.ResolvedOrigin(), known.Low.Provenance));
        Assert.Equal(1100L, known.Low.ToInt64().Value);

        var unknown = UsageEstimation.Estimate(Four().Take(2).ToList(), Catalog, Request()).ToSnapshot("b");
        Assert.Equal(2, unknown.References);
        Assert.Equal(((decimal?)null, MetricOrigin.Missing), (unknown.Low.Value, unknown.Low.ResolvedOrigin()));
        Assert.Equal(((decimal?)null, MetricOrigin.Missing), (unknown.High.Value, unknown.High.ResolvedOrigin()));

        // An estimate built without a total (by another estimator) still gives a snapshot, without a value, never a made-up one.
        var withoutTotal = UsageEstimation.Estimate(Four(), Catalog, Request()) with { TotalTokens = null };
        Assert.Equal(((decimal?)null, UsageEstimate.NoTotalReason), (withoutTotal.ToSnapshot("c").High.Value, withoutTotal.ToSnapshot("c").High.Source));
        Assert.Throws<ArgumentException>(() => withoutTotal.ToSnapshot(" "));
    }

    // ---- the plan ----

    private static AgentDefinition Member(string id, AgentRole role, ProviderId provider, string model) => new(
        new AgentId(id), id, role, new ModelBinding(provider, provider == ProviderId.CodexCli ? VendorId.OpenAI : VendorId.Anthropic, model), string.Empty, [], [], [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(10)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));

    private static TaskSpec Spec(string id, WorkTaskKind kind, string? agent, params string[] dependsOn) => new(
        new TaskId(id), id, kind, agent is null ? null : new AgentId(agent), "objetivo", string.Empty,
        [.. dependsOn.Select(d => new DependencySpec(new TaskId(d)))], JoinPolicy.AllCompleted, [], [], [], null, null, 1, null, WorkspaceMode.None);

    private static readonly IReadOnlyList<AgentDefinition> Roster =
    [
        Member("ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, "sonnet"),
        Member("juez", AgentRole.Judge, ProviderId.CodexCli, "gpt-6-luna"),
    ];

    /// <summary>
    /// Three earlier tasks of the executor (one retried: runs 1, 1 and 2; 1, 2, 3 and 4 minutes) and three reviews of the judge
    /// (one run each, 2 minutes, 500 tokens in).
    /// </summary>
    private static List<UsageAttempt> History() =>
    [
        Attempt("r1", "T1", 1, input: 1000, output: 100, minutes: 1),
        Attempt("r1", "T2", 1, input: 2000, output: 200, minutes: 2),
        Attempt("r2", "T1", 1, input: 3000, output: 300, minutes: 3),
        Attempt("r2", "T1", 2, input: 4000, output: 400, minutes: 4),
        .. Enumerable.Range(1, 3).Select(i => Attempt("r" + i, "R", agent: "juez", provider: ProviderId.CodexCli, configured: "gpt-6-luna", reported: "gpt-6-luna", purpose: AgentRunPurpose.Judge, input: 500, output: 50, cost: null, minutes: 2)),
    ];

    private static PlanEstimateRequest PlanRequest(IReadOnlyList<TaskSpec> tasks, int parallel = 3, IReadOnlyDictionary<ProviderId, int>? concurrency = null) =>
        new(Project, PlanDefinition.Create("plan", tasks, [], PlanSource.User, null), Roster, new RunOptions(MaxParallelTasks: parallel), concurrency);

    /// <summary>
    /// T1 then T2 then the review, and a check that adds nothing. Each executor task: 1 to 2 runs of 1 to 3 minutes and 1 000 to
    /// 3 000 tokens in; the review, one run of 2 minutes and 500 tokens. Low: 1 + 1 + 2 = 4 minutes; high: 6 + 6 + 2 = 14.
    /// </summary>
    [Fact]
    public void A_plan_adds_its_tasks_by_their_runs_and_lasts_its_critical_path()
    {
        var plan = UsageEstimation.EstimatePlan(
            History(),
            Catalog,
            PlanRequest([Spec("T1", WorkTaskKind.AgentWork, "ejecutor"), Spec("T2", WorkTaskKind.AgentWork, "ejecutor", "T1"), Spec("V", WorkTaskKind.ToolRun, null, "T2"), Spec("R", WorkTaskKind.Review, "juez", "V")]));

        Assert.Equal(["R", "T1", "T2"], plan.Tasks.Keys.Select(k => k.Value).Order(StringComparer.Ordinal));
        Assert.All(plan.Tasks.Values, t => Assert.True(t.IsKnown));
        Assert.Equal((3L, 5L), (plan.AgentRuns.Low.Value!.Value, plan.AgentRuns.High.Value!.Value));
        Assert.Equal((1000L + 1000L + 500L, 6000L + 6000L + 500L), (plan.InputTokens.Low.Value!.Value, plan.InputTokens.High.Value!.Value));
        Assert.Equal((TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(14)), (plan.Duration.Low.Value!.Value, plan.Duration.High.Value!.Value));
        Assert.Equal([EstimateAssumptions.CriticalPath, EstimateAssumptions.AgentsOnly], plan.Assumptions);
        Assert.Equal(MetricOrigin.EstimatedBySystem, plan.Duration.High.Origin);
    }

    /// <summary>Two independent tasks of the same CLI: together within the limits, one after the other when the tasks at once or the runs of the CLI are one.</summary>
    [Fact]
    public void The_duration_follows_the_tasks_at_once_and_the_runs_at_once_of_each_cli()
    {
        IReadOnlyList<TaskSpec> tasks = [Spec("T1", WorkTaskKind.AgentWork, "ejecutor"), Spec("T2", WorkTaskKind.AgentWork, "ejecutor")];

        TimeSpan High(PlanEstimateRequest request) => UsageEstimation.EstimatePlan(History(), Catalog, request).Duration.High.Value!.Value;

        Assert.Equal(TimeSpan.FromMinutes(6), High(PlanRequest(tasks)));
        Assert.Equal(TimeSpan.FromMinutes(12), High(PlanRequest(tasks, parallel: 1)));
        Assert.Equal(TimeSpan.FromMinutes(12), High(PlanRequest(tasks, concurrency: new Dictionary<ProviderId, int> { [ProviderId.ClaudeCode] = 1 })));
    }

    [Fact]
    public void A_plan_with_a_task_without_three_references_says_which_and_makes_nothing_up()
    {
        var plan = UsageEstimation.EstimatePlan(
            History(),
            Catalog,
            PlanRequest([Spec("T1", WorkTaskKind.AgentWork, "ejecutor"), Spec("T9", WorkTaskKind.AgentWork, "fantasma"), Spec("R", WorkTaskKind.Review, "juez", "T1")]) with
            {
                Roster = [.. Roster, Member("fantasma", AgentRole.Executor, ProviderId.ClaudeCode, "claude-haiku-5-5")],
            });

        Assert.False(plan.Tasks[new TaskId("T9")].IsKnown);
        Assert.True(plan.Tasks[new TaskId("T1")].IsKnown);
        Assert.All([plan.AgentRuns.Low, plan.InputTokens.High, plan.OutputTokens.Low], f => Assert.Equal(((long?)null, MetricOrigin.Missing), (f.Value, f.Origin)));
        Assert.False(plan.Duration.IsKnown);
        Assert.Contains("T9", plan.AgentRuns.Low.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("T1", plan.AgentRuns.Low.Source, StringComparison.Ordinal);
    }

    // ---- the service ----

    private static IReadOnlyList<DecisionEntry> Log()
    {
        var log = new UsageLogBuilder();
        for (var i = 1; i <= 3; i++)
        {
            var launched = log.Now;
            log.Start("r" + i, "T1", 1, model: "sonnet")
                .Advance(TimeSpan.FromMinutes(i))
                .Finish("r" + i, "T1", 1, session: "s" + i, usage: Snapshots.Claude(1000 * i, 0, 0, 100 * i, 0.1m * i), launchedAt: launched, reportedModel: "claude-sonnet-5-5");
        }

        return log.Entries;
    }

    private sealed class FakeLogReader(ProjectLogSnapshot snapshot) : IProjectLogReader
    {
        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct) => Task.FromResult(snapshot);
    }

    private static UsageEstimator Estimator(AnchorStatus anchor = AnchorStatus.Anchored, bool valid = true) =>
        new(new FakeLogReader(new ProjectLogSnapshot("p1", "C:\\repo", Log(), valid ? DecisionLogVerification.Valid : new DecisionLogVerification(false, 2, "broken"), anchor)), new BuiltInModelCatalog());

    [Fact]
    public async Task The_service_estimates_from_the_trusted_log_of_the_project()
    {
        var estimate = await Estimator().EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(3, estimate.References);
        Assert.Equal((1000L, 3000L), (estimate.InputTokens.Low.Value!.Value, estimate.InputTokens.High.Value!.Value));
        Assert.True(estimate.Duration.IsKnown);
    }

    [Fact]
    public async Task A_log_that_is_not_trusted_fails_closed_with_every_figure_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = PlanRequest([Spec("T1", WorkTaskKind.AgentWork, "ejecutor")]);

        foreach (var estimator in new[] { Estimator(AnchorStatus.Unanchored), Estimator(valid: false) })
        {
            var estimate = await estimator.EstimateAsync(Request(), ct);
            Assert.Equal(0, estimate.References);
            Assert.False(estimate.InputTokens.IsKnown);

            var planEstimate = await estimator.EstimatePlanAsync(plan, ct);
            Assert.False(planEstimate.AgentRuns.IsKnown);
            Assert.False(planEstimate.Duration.IsKnown);
            Assert.False(planEstimate.Tasks[new TaskId("T1")].IsKnown);
        }
    }

    /// <summary>
    /// A log whose text was changed after it was written does not verify: no figure, and every one says why, so the screen can
    /// tell «el registro no es de fiar» from «no hay intentos parecidos».
    /// </summary>
    [Fact]
    public async Task A_tampered_log_gives_no_estimate_and_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = Log().ToList();
        var finished = entries.FindIndex(e => e.Kind == DecisionKind.AgentRunFinished);
        entries[finished] = entries[finished] with { PayloadJson = entries[finished].PayloadJson.Replace("1000", "1", StringComparison.Ordinal) };
        var verification = DecisionLog.Load(entries).Verify();
        Assert.False(verification.IsValid);
        var estimator = new UsageEstimator(new FakeLogReader(new ProjectLogSnapshot("p1", "C:\\repo", entries, verification, AnchorStatus.Anchored)), new BuiltInModelCatalog());

        var estimate = await estimator.EstimateAsync(Request(), ct);

        Assert.Equal(0, estimate.References);
        Assert.All(
            [estimate.InputTokens.Low, estimate.OutputTokens.High, estimate.TotalTokens!.Low],
            figure => Assert.Equal(((long?)null, MetricOrigin.Missing, UsageEstimator.UntrustedLogReason), (figure.Value, figure.Origin, figure.Source)));
        Assert.Equal(UsageEstimator.UntrustedLogReason, estimate.CostUsd.High.Source);
        Assert.Equal(UsageEstimator.UntrustedLogReason, estimate.ToSnapshot("a").Low.Source);

        var plan = await estimator.EstimatePlanAsync(PlanRequest([Spec("T1", WorkTaskKind.AgentWork, "ejecutor")]), ct);
        Assert.Equal(UsageEstimator.UntrustedLogReason, plan.Tasks[new TaskId("T1")].Duration.Low.Source);
        Assert.Equal(UsageEstimator.UntrustedLogReason, plan.AgentRuns.High.Source);
    }

    [Fact]
    public void An_unknown_estimate_carries_its_reason_and_a_blank_one_is_refused()
    {
        var estimate = UsageEstimate.Unknown(1, "unknown: why");

        Assert.Equal(("unknown: why", "unknown: why"), (estimate.Duration.High.Source, estimate.TotalTokens!.High.Source));
        Assert.Equal("unknown: 2 of 3 references", UsageEstimate.Unknown(2).InputTokens.Low.Source);
        Assert.Throws<ArgumentException>(() => UsageEstimate.Unknown(0, " "));
    }

    /// <summary>D-24: the options of a model compared through the port, with the estimator of the system.</summary>
    [Fact]
    public async Task The_options_of_a_model_are_compared_with_the_estimator_through_its_port()
    {
        var options = await ModelOptionEstimates.CompareAsync(Estimator(), Request(), ["claude-sonnet-5-5", "opus"], TestContext.Current.CancellationToken);

        Assert.True(options[0].Estimate.IsKnown);
        Assert.False(options[1].Estimate.IsKnown);
    }

    [Fact]
    public void The_estimator_is_registered_once()
    {
        var services = new ServiceCollection().AddAgentStudioUsageEstimator().AddAgentStudioUsageEstimator();

        Assert.Single(services, d => d.ServiceType == typeof(IUsageEstimator));
    }
}
