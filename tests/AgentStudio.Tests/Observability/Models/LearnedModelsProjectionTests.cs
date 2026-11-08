using AgentStudio.Application.Decisions;
using AgentStudio.Application.Models;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Observability.Models;
using AgentStudio.Tests.Observability.Usage;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Models;

/// <summary>
/// What the verified log teaches of each model (docs/17 D-22 and D-23, F13-11): its tasks as executor, first-pass approvals,
/// retries and rejections, the last window its CLI reported and the cost of a task, each with its origin. A log that is not
/// trusted teaches nothing, and the same log always teaches the same.
/// </summary>
public sealed class LearnedModelsProjectionTests
{
    private static readonly ModelCatalog Catalog = BuiltInModelCatalog.Shipped.Catalog;

    /// <summary>
    /// Four runs. r1: Claude with sonnet, three tasks (one retried) and the judge of Codex, approved. r2: sonnet again, one task
    /// rejected and one cancelled. r3: an executor of Codex without a verdict yet. r4: version 1 starts, an executor and a judge
    /// with a task of review, approved. Plus a planner with a model the catalog does not know, and an attempt with no model.
    /// </summary>
    private static IReadOnlyList<DecisionEntry> Log()
    {
        var log = new LearnedLog();
        log.RunStarted("r1", ("ejecutor", AgentRole.Executor), ("juez", AgentRole.Judge))
            .Attempt("r1", null, 1, "orquestador", model: "claude-mythos-5-1", reported: "claude-mythos-5-1-20261001", purpose: AgentRunPurpose.Planner, cost: 0.05m)
            .Attempt("r1", "T1", 1, reported: "claude-sonnet-5-5", cost: 0.10m, window: 1_000_000).Status("r1", "T1", WorkTaskStatus.Completed, 1)
            .Attempt("r1", "T2", 1, reported: "claude-sonnet-5-5", cost: 0.20m, status: AttemptOutcome.Failed)
            .Attempt("r1", "T2", 2, reported: "claude-sonnet-5-5", cost: 0.30m).Status("r1", "T2", WorkTaskStatus.Completed, 2)
            .Attempt("r1", "T3", 1, reported: "claude-sonnet-5-5", cost: 0.40m).Status("r1", "T3", WorkTaskStatus.Completed, 1)
            .Attempt("r1", "R", 1, "juez", ProviderId.CodexCli, "gpt-6-luna", "gpt-6-luna", AgentRunPurpose.Judge)
            .Verdict("r1", VerdictDecision.Approved);
        log.RunStarted("r2", ("ejecutor", AgentRole.Executor))
            .Attempt("r2", "T1", 1, reported: "claude-sonnet-5-5", cost: 0.50m, window: 900_000).Status("r2", "T1", WorkTaskStatus.Completed, 1)
            .Attempt("r2", "T2", 1, reported: null, status: AttemptOutcome.Cancelled).Status("r2", "T2", WorkTaskStatus.Cancelled, 1)
            .Attempt("r2", "T3", 1, model: null, reported: null)
            .Verdict("r2", VerdictDecision.Approved, valid: false)
            .Verdict("r2", VerdictDecision.Rejected);
        log.RunStarted("r3", ("ejecutor-codex", AgentRole.Executor))
            .Attempt("r3", "T1", 1, "ejecutor-codex", ProviderId.CodexCli, "gpt-6-luna", "gpt-6-luna").Status("r3", "T1", WorkTaskStatus.Completed, 1);
        log.RunStarted("r4", ("ejecutor-v1", AgentRole.Executor), ("juez-v1", AgentRole.Judge))
            .Attempt("r4", "T1", 1, "ejecutor-v1", model: "haiku", reported: null, version: 1, cost: 0.01m).Status("r4", "T1", WorkTaskStatus.Completed, 1)
            .Attempt("r4", "R", 1, "juez-v1", model: "haiku", reported: null, version: 1, cost: 0.01m).Status("r4", "R", WorkTaskStatus.Completed, 1)
            .Verdict("r4", VerdictDecision.Approved);
        return log.Entries;
    }

    private static LearnedModels Learned(IReadOnlyList<DecisionEntry>? entries = null) =>
        LearnedModelsProjection.FromLog(new DecisionLogState(entries ?? Log(), DecisionLogVerification.Valid), Catalog);

    [Fact]
    public void Every_model_the_log_saw_is_learned_once_under_its_entry_in_a_stable_order()
    {
        var learned = Learned();

        Assert.Equal(
            [("claude-code", "claude-mythos-5-1"), ("claude-code", "claude-sonnet-5-5"), ("claude-code", "haiku"), ("codex-cli", "gpt-6-luna")],
            learned.Models.Select(m => (m.Provider.Value, m.Model)));
        Assert.Equal(["claude-sonnet-5-5", "sonnet"], learned.Find(ProviderId.ClaudeCode, "sonnet")!.ReportedAs);
        Assert.Equal(["claude-mythos-5-1-20261001"], learned.Find(ProviderId.ClaudeCode, "claude-mythos-5-1")!.ReportedAs);
        Assert.Same(learned.Find(ProviderId.ClaudeCode, "claude-sonnet-5-5"), learned.Find(ProviderId.ClaudeCode, "SONNET"));
        Assert.Null(learned.Find(ProviderId.CodexCli, "sonnet"));
        Assert.Equal(learned.Models.Select(m => m.Performance), learned.Performances);
    }

    /// <summary>
    /// sonnet: four tasks that ended (the cancelled one says nothing), two approved at the first attempt, one retry, and one
    /// rejection (the last valid verdict of r2). Its seven attempts include the cancelled one, reported only by its configured name.
    /// </summary>
    [Fact]
    public void The_performance_counts_the_tasks_that_ended_their_first_pass_approvals_retries_and_rejections()
    {
        var sonnet = Learned().Find(ProviderId.ClaudeCode, "sonnet")!;

        Assert.Equal(new ModelPerformance(ProviderId.ClaudeCode, "claude-sonnet-5-5", 4, 2, 1, 1), sonnet.Performance);
        Assert.True(sonnet.Performance.IsKnown);
        Assert.Equal(6, sonnet.Attempts);
    }

    [Fact]
    public void A_task_without_a_verdict_yet_is_counted_without_an_approval_and_the_judge_has_no_tasks()
    {
        var luna = Learned().Find(ProviderId.CodexCli, "gpt-6-luna")!;

        Assert.Equal(new ModelPerformance(ProviderId.CodexCli, "gpt-6-luna", 1, 0, 0, 0), luna.Performance);
        Assert.False(luna.Performance.IsKnown);
        Assert.Equal(2, luna.Attempts);
    }

    /// <summary>Before version 2 of the start there is no purpose: the roster of the run says that the judge's attempt is not a task of an executor.</summary>
    [Fact]
    public void A_start_of_version_1_counts_as_an_executor_unless_the_roster_of_its_run_says_otherwise()
    {
        var haiku = Learned().Find(ProviderId.ClaudeCode, "haiku")!;

        Assert.Equal(new ModelPerformance(ProviderId.ClaudeCode, "haiku", 1, 1, 0, 0), haiku.Performance);
        Assert.Equal(2, haiku.Attempts);
        Assert.Equal(new ModelPerformance(ProviderId.ClaudeCode, "claude-mythos-5-1", 0, 0, 0, 0), Learned().Find(ProviderId.ClaudeCode, "claude-mythos-5-1")!.Performance);
    }

    [Fact]
    public void The_window_is_the_last_one_the_cli_reported_as_an_estimate_and_missing_when_it_never_did()
    {
        var learned = Learned();
        var window = learned.Find(ProviderId.ClaudeCode, "sonnet")!.ContextWindow;

        Assert.Equal(((long?)900_000L, MetricOrigin.EstimatedBySystem), (window.Value, window.Origin));
        Assert.Contains("claude-sonnet-5-5", window.Source, StringComparison.Ordinal);
        Assert.Equal(MetricOrigin.Missing, learned.Find(ProviderId.CodexCli, "gpt-6-luna")!.ContextWindow.Origin);
        Assert.False(learned.Find(ProviderId.CodexCli, "gpt-6-luna")!.ContextWindow.HasValue);
    }

    /// <summary>sonnet's tasks cost 0.10, 0.50 (0.20 and 0.30), 0.40 and 0.50: the median is 0.45, at the CLI's list price.</summary>
    [Fact]
    public void The_cost_of_a_task_is_the_median_of_its_tasks_from_three_on_and_codex_does_not_expose_it()
    {
        var learned = Learned();
        var sonnet = learned.Find(ProviderId.ClaudeCode, "sonnet")!.CostPerTask;
        var luna = learned.Find(ProviderId.CodexCli, "gpt-6-luna")!.CostPerTask;
        var haiku = learned.Find(ProviderId.ClaudeCode, "haiku")!.CostPerTask;

        Assert.Equal(((decimal?)0.45m, MetricOrigin.EstimatedBySystem), (sonnet.Value, sonnet.Origin));
        Assert.Equal(((decimal?)null, MetricOrigin.NotExposed), (luna.Value, luna.Origin));
        Assert.Equal(((decimal?)null, MetricOrigin.Missing), (haiku.Value, haiku.Origin));
        Assert.Contains("1 of 3", haiku.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_is_not_trusted_teaches_nothing()
    {
        var entries = Log();

        Assert.Same(LearnedModels.Empty, LearnedModelsProjection.FromLog(new DecisionLogState(entries, new DecisionLogVerification(false, 3, "broken")), Catalog));
        Assert.Same(LearnedModels.Empty, LearnedModelsProjection.FromLog(new ProjectLogSnapshot("p", "C:\\r", entries, DecisionLogVerification.Valid, AnchorStatus.Unanchored), Catalog));
        Assert.Equal(4, LearnedModelsProjection.FromLog(new ProjectLogSnapshot("p", "C:\\r", entries, DecisionLogVerification.Valid, AnchorStatus.Anchored), Catalog).Models.Count);
    }

    [Fact]
    public void The_same_log_always_teaches_the_same()
    {
        var first = Learned();
        var second = Learned();

        Assert.Equal(first.Models.Select(Shape), second.Models.Select(Shape));

        static string Shape(LearnedModel m) =>
            $"{m.Provider}|{m.Model}|{string.Join(',', m.ReportedAs)}|{m.Performance}|{m.ContextWindow.Value}|{m.ContextWindow.Origin}|{m.CostPerTask.Value}|{m.CostPerTask.Origin}|{m.Attempts}";
    }

    /// <summary>What the log teaches is what the recommendation reads: sonnet's 2 of 4 do not pass half, so the role decides.</summary>
    [Fact]
    public void What_is_learned_feeds_the_recommendation()
    {
        var input = new ModelRecommendationInput(AgentRole.Executor, ProviderId.ClaudeCode, [VendorId.Anthropic], 100, 2, 0, "dotnet", 12, false, Learned().Performances);

        Assert.Equal(ModelRecommendationReasons.RoleDefault, ModelRecommender.Recommend(Catalog, input)!.ReasonCode);
    }

    [Fact]
    public void A_model_is_learned_under_its_entry_with_or_without_the_date_and_the_1m_window_and_else_under_its_id_without_the_date()
    {
        Assert.Equal("claude-sonnet-5-5", LearnedModelsProjection.KeyOf(Catalog, ProviderId.ClaudeCode, "sonnet"));
        Assert.Equal("claude-haiku-4-5", LearnedModelsProjection.KeyOf(Catalog, ProviderId.ClaudeCode, "claude-haiku-4-5-20251001"));
        Assert.Equal("claude-opus-5-5", LearnedModelsProjection.KeyOf(Catalog, ProviderId.ClaudeCode, "opus[1m]"));
        Assert.Equal("claude-mythos-5-1", LearnedModelsProjection.KeyOf(Catalog, ProviderId.ClaudeCode, "Claude-Mythos-5-1-20261001"));
        Assert.Equal("sonnet", LearnedModelsProjection.KeyOf(Catalog, ProviderId.CodexCli, "sonnet"));
        Assert.Null(LearnedModelsProjection.KeyOf(Catalog, ProviderId.ClaudeCode, " "));
    }

    [Fact]
    public async Task The_service_reads_the_log_of_the_project_through_its_port_and_is_registered_once()
    {
        var reader = new FakeLogReader(new ProjectLogSnapshot("p", "C:\\r", Log(), DecisionLogVerification.Valid, AnchorStatus.Anchored));
        var learning = new ProjectModelLearning(reader, new BuiltInModelCatalog());

        var learned = await learning.LearnAsync("C:\\r", TestContext.Current.CancellationToken);

        Assert.Equal(["C:\\r"], reader.Read);
        Assert.Equal(4, learned.Models.Count);

        var services = new ServiceCollection().AddAgentStudioModelLearning().AddAgentStudioModelLearning();
        Assert.Single(services, d => d.ServiceType == typeof(IModelLearning));
    }

    private sealed class FakeLogReader(ProjectLogSnapshot snapshot) : IProjectLogReader
    {
        public List<string> Read { get; } = [];

        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct)
        {
            Read.Add(projectRoot);
            return Task.FromResult(snapshot);
        }
    }

    /// <summary>A log of runs as the recorders write it: starts and ends of attempts, statuses of tasks, verdicts and the roster of each run.</summary>
    private sealed class LearnedLog
    {
        private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

        private readonly DecisionLog _log = new();
        private DateTimeOffset _now = T0;
        private int _verdicts;

        public IReadOnlyList<DecisionEntry> Entries => _log.Entries;

        public LearnedLog RunStarted(string run, params (string Agent, AgentRole Role)[] roster) =>
            Append(DecisionKind.RunStarted, new RunStartedPayload(
                HashA, HashB, CommitA, "main", HashA,
                [.. roster.Select(r => new RosterEntry(new AgentId(r.Agent), r.Role, r.Role == AgentRole.Judge ? ProviderId.CodexCli : ProviderId.ClaudeCode, r.Role == AgentRole.Judge ? VendorId.OpenAI : VendorId.Anthropic))],
                null) { RunId = run });

        public LearnedLog Attempt(
            string run,
            string? task,
            int attempt,
            string agent = "ejecutor",
            ProviderId? provider = null,
            string? model = "sonnet",
            string? reported = "claude-sonnet-5-5",
            AgentRunPurpose purpose = AgentRunPurpose.Executor,
            int version = 2,
            decimal? cost = null,
            long? window = null,
            AttemptOutcome status = AttemptOutcome.Succeeded)
        {
            var on = provider ?? ProviderId.ClaudeCode;
            var session = $"{run}-{task}-{attempt}";
            Append(DecisionKind.AgentRunStarted, new AgentRunStartedPayload(
                task is null ? null : new TaskId(task),
                attempt,
                new AgentId(agent),
                on,
                UsageLogBuilder.VendorOf(on),
                model,
                "C:\\worktrees\\t",
                new EvidenceId("ev-" + session),
                HashA,
                [],
                version >= AgentRunStartedPayload.PurposeVersion ? purpose : null) { RunId = run, V = version });

            UsageSnapshot usage = on == ProviderId.CodexCli
                ? Snapshots.Codex(1000, 0, 0, 100, 0)
                : version == 1
                    ? Snapshots.ClaudeV1(1000, 0, 100, cost ?? 0.01m)
                    : Snapshots.Claude(1000, 0, 0, 100, cost ?? 0.01m, window is { } tokens ? [Snapshots.Model(reported ?? model ?? "x", 1000, 100, 0, 0, cost ?? 0.01m, tokens)] : []);
            return Append(DecisionKind.AgentRunFinished, new AgentRunFinishedPayload(
                task is null ? null : new TaskId(task),
                attempt,
                status,
                status == AttemptOutcome.Succeeded ? 0 : 1,
                session,
                null,
                usage,
                null,
                version >= AgentRunFinishedPayload.UsageVersion ? _now : null,
                version >= AgentRunFinishedPayload.UsageVersion ? reported : null) { RunId = run, V = version });
        }

        public LearnedLog Status(string run, string task, WorkTaskStatus to, int attempt) =>
            Append(DecisionKind.TaskStatusChanged, new TaskStatusChangedPayload(new TaskId(task), WorkTaskStatus.Running, to, "done", null, [], attempt) { RunId = run });

        public LearnedLog Verdict(string run, VerdictDecision decision, bool valid = true) =>
            Append(DecisionKind.VerdictIssued, new VerdictIssuedPayload("v" + ++_verdicts, new TaskId("R"), decision, valid, [], HashA, HashB, null, null) { RunId = run });

        private LearnedLog Append(DecisionKind kind, DecisionPayload payload)
        {
            _log.Append(_now, SystemActor, kind, kind.ToString(), DecisionPayloadSerializer.Serialize(payload));
            _now += TimeSpan.FromSeconds(1);
            return this;
        }
    }
}
