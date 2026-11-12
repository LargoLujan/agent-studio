using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Observability.Usage;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Observability.Usage;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/16 D-05 and F9-8 (ADR-0040): the engine does not launch an attempt that would pass the budget of the project or of the milestone,
/// says why, spends no quota to find out, and fails closed.
/// </summary>
public sealed class ProjectBudgetTests
{
    private static readonly ProjectContext Project = new("p1", "C:\\repo");
    private static readonly MilestoneId Mil = new("mil-20261110-aaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static UsageEstimateRequest Request => new(Project, new AgentId("ejecutor"), ProviderId.ClaudeCode, "claude-sonnet-5-5", AgentRunPurpose.Executor);

    private static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(Now);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    private static UsageAttempt Used(long tokens, DateTimeOffset? finished = null, params string[] milestones) =>
        UsageAttempts.Make(finished ?? Now.AddMinutes(-5), own: UsageAttempts.Tokens(tokens - 10, 10), milestones: milestones);

    private static ProjectBudgetGate Gate(Fakes fakes) =>
        new(fakes.Budgets, fakes.Usage, fakes.Estimator, fakes.Registry, fakes.Logs, fakes.Time);

    // ---- the gate ----

    [Fact]
    public async Task Without_a_budget_everything_goes_through_and_nothing_else_is_read()
    {
        var fakes = new Fakes { Budget = null };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.True(verdict.Allowed);
        Assert.Equal(0, fakes.Usage.Queries);
        Assert.Equal(0, fakes.Estimator.Asked);
    }

    [Fact]
    public async Task The_milestones_of_a_run_are_read_from_the_log_once_per_run_and_a_day_is_the_day_of_the_local_zone()
    {
        var fakes = new Fakes { Budget = new WorkBudget(1_000_000, null), EstimateTotal = 1, Header = new MilestoneHeader(Mil, "Calc", "Una calculadora", HashA, new WorkBudget(1_000, null), T0) };
        var gate = Gate(fakes);

        await gate.CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        await gate.CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        await gate.CheckAsync(Project, "R2", Request, 0, TestContext.Current.CancellationToken);

        Assert.Equal(2, fakes.Logs.Reads);

        // 23:30 UTC on the 10th is already the 11th in Madrid (UTC+1 in November): it belongs to "today" there.
        var madrid = new FakeTimeProvider(Now);
        madrid.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"));
        var inMadrid = new Fakes { Budget = new WorkBudget(1_000, null, BudgetPeriod.Day), Attempts = [Used(900, new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.Zero))], EstimateTotal = 200 };

        var verdict = await new ProjectBudgetGate(inMadrid.Budgets, inMadrid.Usage, inMadrid.Estimator, null, null, madrid).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public async Task The_usage_query_is_bounded_to_the_widest_period_of_the_budgets_that_apply()
    {
        var day = new Fakes { Budget = new WorkBudget(1_000, null, BudgetPeriod.Day), EstimateTotal = 1 };
        await Gate(day).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        var from = day.Usage.LastQuery!.From;
        Assert.True(from > DateTimeOffset.MinValue && from <= Now && Now - from < TimeSpan.FromDays(1));

        var total = new Fakes { Budget = new WorkBudget(1_000, null), EstimateTotal = 1 };
        await Gate(total).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        Assert.Equal(DateTimeOffset.MinValue, total.Usage.LastQuery!.From);
    }

    [Fact]
    public async Task Reaching_the_limit_exactly_is_within_it_and_passing_it_by_one_token_is_not()
    {
        var within = new Fakes { Budget = new WorkBudget(1_000, null), Attempts = [Used(800)], EstimateTotal = 200 };
        var over = new Fakes { Budget = new WorkBudget(1_000, null), Attempts = [Used(801)], EstimateTotal = 200 };

        var allowed = await Gate(within).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        var refused = await Gate(over).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.True(allowed.Allowed);
        Assert.False(refused.Allowed);
        Assert.Equal(BudgetLimit.Tokens, refused.Reached);
        Assert.Equal(ProjectBudgetGate.ExceededCode, refused.Code);
        Assert.Contains("1000", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("1001", refused.Reason, StringComparison.Ordinal);

        // The coded text carries the same figures, for the screen in the language in use; the Reason of the log stays as it is.
        Assert.Equal(
            UserText.Of(
                OrchestrationUserTexts.ListSpace,
                UserText.Of(OrchestrationUserTexts.BudgetExceededTokens, UserText.Of(OrchestrationUserTexts.BudgetNameProjectTotal), 1_000L, 801L, 1_001L),
                UserText.Of(OrchestrationUserTexts.BudgetRaise)),
            refused.Text);
    }

    [Fact]
    public async Task The_limit_of_launches_counts_the_attempts_that_ended_and_the_ones_in_flight()
    {
        var fakes = new Fakes { Budget = new WorkBudget(null, 3), Attempts = [Used(10), Used(10)], EstimateTotal = 1 };

        var none = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        var one = await Gate(fakes).CheckAsync(Project, "R1", Request, 1, TestContext.Current.CancellationToken);

        Assert.True(none.Allowed); // 2 used + this one = 3: reaching the limit is within it
        Assert.False(one.Allowed); // 2 used + 1 in flight + this one = 4
        Assert.Equal(BudgetLimit.AgentRuns, one.Reached);
    }

    [Fact]
    public async Task The_tokens_of_the_attempts_in_flight_count_as_what_this_one_is_estimated_at()
    {
        var fakes = new Fakes { Budget = new WorkBudget(1_000, null), Attempts = [Used(500)], EstimateTotal = 300 };

        var alone = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        var withOther = await Gate(fakes).CheckAsync(Project, "R1", Request, 1, TestContext.Current.CancellationToken);

        Assert.True(alone.Allowed);
        Assert.False(withOther.Allowed);
    }

    [Fact]
    public async Task Without_an_estimate_the_attempt_goes_only_while_the_tokens_used_are_under_the_limit_and_the_verdict_says_so()
    {
        var under = new Fakes { Budget = new WorkBudget(1_000, null), Attempts = [Used(999)], EstimateTotal = null };
        var at = new Fakes { Budget = new WorkBudget(1_000, null), Attempts = [Used(1_000)], EstimateTotal = null };

        var allowed = await Gate(under).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        var refused = await Gate(at).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.True(allowed.Allowed);
        Assert.True(allowed.ProjectionUnknown);
        Assert.False(refused.Allowed);
        Assert.True(refused.ProjectionUnknown);
        Assert.Contains("sin estimación", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_estimator_that_fails_is_an_unknown_estimate_not_a_failure_of_the_gate()
    {
        var fakes = new Fakes { Budget = new WorkBudget(1_000, null), Attempts = [Used(10)], EstimatorFails = true };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.True(verdict.Allowed);
        Assert.True(verdict.ProjectionUnknown);
    }

    [Fact]
    public async Task A_day_budget_counts_only_what_ended_today_in_the_local_zone()
    {
        var fakes = new Fakes
        {
            Budget = new WorkBudget(1_000, null, BudgetPeriod.Day),
            Attempts = [Used(900, Now.AddDays(-1)), Used(100, Now.AddHours(-3))],
            EstimateTotal = 800,
        };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.True(verdict.Allowed); // 100 today + 800 = 900; yesterday's 900 is not today's
        Assert.Contains("hoy", (await Gate(new Fakes { Budget = new WorkBudget(50, null, BudgetPeriod.Day), Attempts = [Used(100)], EstimateTotal = 1 }).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_week_and_a_month_budget_cover_the_iso_week_and_the_calendar_month()
    {
        // 2026-11-11 is a Wednesday: its ISO week starts on Monday the 9th.
        var week = new Fakes { Budget = new WorkBudget(1_000, null, BudgetPeriod.Week), Attempts = [Used(900, new DateTimeOffset(2026, 11, 8, 23, 0, 0, TimeSpan.Zero)), Used(100, new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero))], EstimateTotal = 800 };
        var month = new Fakes { Budget = new WorkBudget(1_000, null, BudgetPeriod.Month), Attempts = [Used(900, new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero)), Used(100, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero))], EstimateTotal = 800 };

        Assert.True((await Gate(week).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken)).Allowed);
        Assert.True((await Gate(month).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken)).Allowed);
        Assert.False((await Gate(new Fakes { Budget = new WorkBudget(1_000, null, BudgetPeriod.Week), Attempts = [Used(900, Now.AddDays(-1))], EstimateTotal = 200 }).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task A_budget_that_cannot_be_read_fails_closed()
    {
        var fakes = new Fakes { StoreFails = true };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Equal(ProjectBudgetGate.UnreadableCode, verdict.Code);
        Assert.Contains("no se pudo comprobar", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.BudgetUnreadable, UserText.Of(OrchestrationUserTexts.BudgetWhyFailed)), verdict.Text);
    }

    [Fact]
    public async Task A_budget_with_no_way_to_read_what_was_used_or_a_usage_that_fails_or_an_untrusted_log_fails_closed()
    {
        var noUsage = new ProjectBudgetGate(new Fakes { Budget = new WorkBudget(1, null) }.Budgets, null, null, null, null, Clock());
        var usageFails = new Fakes { Budget = new WorkBudget(1_000, null), UsageFails = true };
        var untrusted = new Fakes { Budget = new WorkBudget(1_000, null), Excluded = true };
        var invalid = new Fakes { Budget = new WorkBudget(null, null) };

        foreach (var verdict in new[]
        {
            await noUsage.CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken),
            await Gate(usageFails).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken),
            await Gate(untrusted).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken),
            await Gate(invalid).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken),
        })
        {
            Assert.False(verdict.Allowed);
            Assert.Equal(ProjectBudgetGate.UnreadableCode, verdict.Code);
        }
    }

    [Fact]
    public async Task The_budget_of_a_milestone_counts_every_attempt_of_the_milestone_and_none_of_the_others()
    {
        var log = new UsageLogBuilder()
            .Record(DecisionKind.RunStarted, new RunStartedPayload(HashA, HashA, "c0ffee", null, HashA, [], null) { RunId = "R1" })
            .Record(DecisionKind.MilestoneLinked, MilestoneLinkedPayload.Linking(Mil, MilestoneTargetKind.Run, "R1", "agent-studio"));
        var fakes = new Fakes
        {
            Header = new MilestoneHeader(Mil, "Calc", "Una calculadora", HashA, new WorkBudget(1_000, null), T0),
            Entries = log.Entries,
            Attempts = [Used(900, null, Mil.Value), Used(5_000)],
            EstimateTotal = 200,
        };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        var other = await Gate(fakes).CheckAsync(Project, "R2", Request, 0, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Equal(Mil.Value, verdict.Scope);
        Assert.Contains("hito", verdict.Reason, StringComparison.Ordinal);
        Assert.True(other.Allowed); // R2 belongs to no milestone: nothing applies
    }

    [Fact]
    public async Task The_project_and_the_milestone_both_apply_and_the_first_to_pass_decides()
    {
        var log = new UsageLogBuilder()
            .Record(DecisionKind.MilestoneLinked, MilestoneLinkedPayload.Linking(Mil, MilestoneTargetKind.Run, "R1", "agent-studio"));
        var fakes = new Fakes
        {
            Budget = new WorkBudget(10_000, null),
            Header = new MilestoneHeader(Mil, "Calc", "Una calculadora", HashA, new WorkBudget(null, 1), T0),
            Entries = log.Entries,
            Attempts = [Used(100, null, Mil.Value)],
            EstimateTotal = 100,
        };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Equal(Mil.Value, verdict.Scope);
        Assert.Equal(BudgetLimit.AgentRuns, verdict.Reached);
    }

    [Fact]
    public async Task An_attempt_without_a_figure_of_tokens_counts_for_what_it_gives_and_the_refusal_says_how_many_were_left_out()
    {
        var gaps = UsageAttempts.Make(Now.AddMinutes(-1), own: UsageAttempts.NoFigures());
        var fakes = new Fakes { Budget = new WorkBudget(500, null), Attempts = [Used(500), gaps], EstimateTotal = 10 };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Contains("1 intentos sin cifra", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_refusal_of_the_gate_gives_the_code_it_carries_live_when_it_is_derived_from_its_sentence_in_the_log()
    {
        var milestoneLog = new UsageLogBuilder()
            .Record(DecisionKind.MilestoneLinked, MilestoneLinkedPayload.Linking(Mil, MilestoneTargetKind.Run, "R1", "agent-studio"));
        var gaps = UsageAttempts.Make(Now.AddMinutes(-1), own: UsageAttempts.NoFigures());
        var scenarios = new Dictionary<string, Fakes>
        {
            ["tokens"] = new() { Budget = new WorkBudget(1_000, null), Attempts = [Used(801)], EstimateTotal = 200 },
            ["tokens without an estimate"] = new() { Budget = new WorkBudget(1_000, null), Attempts = [Used(1_000)], EstimateTotal = null },
            ["tokens and attempts without a figure"] = new() { Budget = new WorkBudget(500, null), Attempts = [Used(500), gaps], EstimateTotal = 10 },
            ["runs"] = new() { Budget = new WorkBudget(null, 2), Attempts = [Used(10), Used(10)], EstimateTotal = 1 },
            ["day"] = new() { Budget = new WorkBudget(50, null, BudgetPeriod.Day), Attempts = [Used(100)], EstimateTotal = 1 },
            ["week"] = new() { Budget = new WorkBudget(1_000, null, BudgetPeriod.Week), Attempts = [Used(900, Now.AddDays(-1))], EstimateTotal = 200 },
            ["month"] = new() { Budget = new WorkBudget(1_000, null, BudgetPeriod.Month), Attempts = [Used(900, Now.AddDays(-1))], EstimateTotal = 200 },
            ["milestone"] = new()
            {
                Header = new MilestoneHeader(Mil, "Calc", "Una calculadora", HashA, new WorkBudget(1_000, null), T0),
                Entries = milestoneLog.Entries,
                Attempts = [Used(900, null, Mil.Value)],
                EstimateTotal = 200,
            },
            ["store fails"] = new() { StoreFails = true },
            ["usage fails"] = new() { Budget = new WorkBudget(1_000, null), UsageFails = true },
            ["untrusted log"] = new() { Budget = new WorkBudget(1_000, null), Excluded = true },
            ["invalid budget"] = new() { Budget = new WorkBudget(null, null) },
        };
        var verdicts = new Dictionary<string, BudgetGateVerdict>();
        foreach (var (scenario, fakes) in scenarios)
        {
            verdicts[scenario] = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        }

        verdicts["no way to read the usage"] = await new ProjectBudgetGate(new Fakes { Budget = new WorkBudget(1, null) }.Budgets, null, null, null, null, Clock()).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        foreach (var (name, verdict) in verdicts)
        {
            Assert.False(verdict.Allowed, name);
            Assert.NotNull(verdict.Text);
            Assert.Equal(verdict.Text, LoggedTexts.TaskReason(verdict.Reason));
        }
    }

    // ---- the engine ----

    private static EngineHarness Harness(Fakes fakes)
    {
        var harness = new EngineHarness { Estimator = fakes.Estimator, Budgets = fakes.Budgets, UsageReport = fakes.Usage };
        return harness;
    }

    [Fact]
    public async Task The_engine_does_not_launch_an_attempt_that_would_pass_the_budget_and_says_why()
    {
        var fakes = new Fakes { Budget = new WorkBudget(null, 1), Attempts = [Used(10)], EstimateTotal = 1 };
        using var harness = Harness(fakes);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);

        // Not Failed: the task waits for the person, so raising the budget and retrying it goes on.
        Assert.Equal(RunPhase.AwaitingHuman, run.Snapshot().Phase);
        Assert.Empty(harness.Claude.Starts);
        Assert.Equal(0, run.Snapshot().AgentRunsUsed);
        Assert.Equal(WorkTaskStatus.Blocked, run.Snapshot().View("T1").Status);
        Assert.Equal("AwaitingHuman", run.Snapshot().View("T1").BlockReason);
        var alert = Assert.Single(harness.Alerts(ProjectBudgetGate.ExceededCode));
        Assert.Contains("ejecuciones de agente", alert.Payload.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts("run.budget-exhausted"));

        fakes.Budget = new WorkBudget(null, 10);
        await run.RetryBlockedAsync(new TaskId("T1"));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Claude.Starts);
    }

    [Fact]
    public async Task A_transient_failure_reading_the_budget_blocks_the_task_never_fails_the_run_and_the_retry_reads_again()
    {
        var fakes = new Fakes { StoreFails = true, Budget = new WorkBudget(1_000_000, 100), EstimateTotal = 1 };
        using var harness = Harness(fakes);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);

        Assert.Equal(WorkTaskStatus.Blocked, run.Snapshot().View("T1").Status);
        Assert.Empty(harness.Claude.Starts);
        Assert.Single(harness.Alerts(ProjectBudgetGate.UnreadableCode));

        fakes.StoreFails = false;
        await run.RetryBlockedAsync(new TaskId("T1"));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Claude.Starts);
    }

    [Fact]
    public async Task Without_any_budget_a_log_that_is_not_trusted_does_not_stop_the_dispatch()
    {
        var fakes = new Fakes { Budget = null, UntrustedLog = true, Header = new MilestoneHeader(Mil, "Calc", "Una calculadora", HashA, null, T0) };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.True(verdict.Allowed);
        Assert.Equal(0, fakes.Logs.Reads);
        Assert.Equal(0, fakes.Usage.Queries);
    }

    [Fact]
    public async Task With_a_milestone_budget_and_a_log_that_is_not_trusted_the_gate_fails_closed()
    {
        var fakes = new Fakes { Budget = null, UntrustedLog = true, Header = new MilestoneHeader(Mil, "Calc", "Una calculadora", HashA, new WorkBudget(1_000, null), T0) };

        var verdict = await Gate(fakes).CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allowed);
        Assert.Equal(ProjectBudgetGate.UnreadableCode, verdict.Code);
    }

    [Fact]
    public async Task A_resumed_run_reads_its_milestones_again()
    {
        var fakes = new Fakes { Header = new MilestoneHeader(Mil, "Calc", "Una calculadora", HashA, new WorkBudget(1_000, null), T0), EstimateTotal = 1 };
        var gate = Gate(fakes);

        await gate.CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);
        gate.Forget("R1");
        await gate.CheckAsync(Project, "R1", Request, 0, TestContext.Current.CancellationToken);

        Assert.Equal(2, fakes.Logs.Reads);
    }

    [Fact]
    public async Task A_run_that_fits_in_the_budget_runs_as_always_and_the_gate_is_asked_once_per_launch()
    {
        var fakes = new Fakes { Budget = new WorkBudget(1_000_000, 100), Attempts = [], EstimateTotal = 100 };
        using var harness = Harness(fakes);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Claude.Starts);
        Assert.Equal(1, fakes.Usage.Queries);
        Assert.Empty(harness.Alerts(ProjectBudgetGate.ExceededCode));
    }

    [Fact]
    public async Task The_engine_fails_closed_when_the_budget_cannot_be_read_and_launches_nothing()
    {
        var fakes = new Fakes { StoreFails = true };
        using var harness = Harness(fakes);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);

        Assert.Empty(harness.Claude.Starts);
        Assert.Single(harness.Alerts(ProjectBudgetGate.UnreadableCode));
    }

    [Fact]
    public async Task Without_a_budget_the_engine_never_reads_the_usage()
    {
        var fakes = new Fakes { Budget = null };
        using var harness = Harness(fakes);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(0, fakes.Usage.Queries);
    }

    // ---- fakes ----

    private sealed class Fakes
    {
        public Fakes()
        {
            Time = Clock();
            Budgets = new Store(this);
            Usage = new UsageSource(this);
            Estimator = new EstimatorSource(this);
            Registry = new RegistrySource(this);
            Logs = new LogSource(this);
        }

        public WorkBudget? Budget { get; set; }

        public bool UntrustedLog { get; init; }

        public bool StoreFails { get; set; }

        public bool UsageFails { get; init; }

        public bool Excluded { get; init; }

        public bool EstimatorFails { get; init; }

        public long? EstimateTotal { get; init; }

        public IReadOnlyList<UsageAttempt> Attempts { get; init; } = [];

        public MilestoneHeader? Header { get; init; }

        public IReadOnlyList<DecisionEntry>? Entries { get; init; }

        public FakeTimeProvider Time { get; }

        public Store Budgets { get; }

        public UsageSource Usage { get; }

        public EstimatorSource Estimator { get; }

        public IMilestoneRegistry? Registry { get; }

        public LogSource Logs { get; }

        public sealed class Store(Fakes owner) : IProjectBudgetStore
        {
            public Task<WorkBudget?> LoadAsync(ProjectContext project, CancellationToken cancellationToken) =>
                owner.StoreFails ? throw new IOException("the store is locked") : Task.FromResult(owner.Budget);

            public Task SaveAsync(ProjectContext project, WorkBudget? budget, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        public sealed class UsageSource(Fakes owner) : IUsageReport
        {
            public int Queries { get; private set; }

            public UsageQuery? LastQuery { get; private set; }

            public Task<UsageReport> QueryAsync(IReadOnlyList<string> projectRoots, UsageQuery query, CancellationToken ct)
            {
                Queries++;
                LastQuery = query;
                if (owner.UsageFails)
                {
                    throw new IOException("the log cannot be read");
                }

                IReadOnlyList<ExcludedProject> excluded = owner.Excluded ? [new ExcludedProject("C:\\repo", "p1", "El registro no verifica")] : [];
                return Task.FromResult(new UsageReport(UsageAggregator.Select(owner.Attempts, query), [], excluded, new UsageReconciliation(null, owner.Attempts.Count, [])));
            }
        }

        public sealed class EstimatorSource(Fakes owner) : IUsageEstimator
        {
            public int Asked { get; private set; }

            public Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct)
            {
                Asked++;
                if (owner.EstimatorFails)
                {
                    throw new InvalidOperationException("no log");
                }

                return Task.FromResult(owner.EstimateTotal is { } total
                    ? new UsageEstimate(
                        5,
                        new(MetricValue<long>.EstimatedBySystem(1, "p25"), MetricValue<long>.EstimatedBySystem(total, "p75")),
                        new(MetricValue<long>.EstimatedBySystem(1, "p25"), MetricValue<long>.EstimatedBySystem(1, "p75")),
                        EstimateInterval<TimeSpan>.Unknown("n/a"),
                        EstimateInterval<decimal>.Unknown("n/a"),
                        [],
                        new(MetricValue<long>.EstimatedBySystem(1, "p25 total"), MetricValue<long>.EstimatedBySystem(total, "p75 total")))
                    : UsageEstimate.Unknown(1));
            }

            public Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct) => throw new NotSupportedException();
        }

        public sealed class RegistrySource(Fakes owner) : IMilestoneRegistry
        {
            public Task CreateAsync(string projectId, MilestoneHeader header, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<MilestoneHeader?> FindAsync(string projectId, MilestoneId id, CancellationToken cancellationToken) =>
                Task.FromResult(owner.Header is { } header && header.Id == id ? header : null);

            public Task<IReadOnlyList<MilestoneHeader>> ListAsync(string projectId, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<MilestoneHeader>>(owner.Header is { } header ? [header] : []);
        }

        public sealed class LogSource(Fakes owner) : IProjectLogReader
        {
            public int Reads { get; private set; }

            public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct)
            {
                Reads++;
                return Task.FromResult(new ProjectLogSnapshot("p1", projectRoot, owner.Entries ?? [], owner.UntrustedLog ? new DecisionLogVerification(false, 1, "broken") : DecisionLogVerification.Valid, AnchorStatus.Anchored));
            }
        }
    }
}
