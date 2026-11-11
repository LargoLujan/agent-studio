using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Memory.Context;
using AgentStudio.Observability.Context;
using AgentStudio.Observability.Estimates;
using AgentStudio.Tests.Domain.Orchestration.Analysis;
using AgentStudio.Tests.Observability.Usage;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Preparation;

/// <summary>
/// The facts of the draft that cost something to know (docs/17 D-16 and D-18, P49): how much context each task starts with and
/// how many agent runs the plan is expected to take. The draft only reads them; what is missing is asked once, in the background,
/// and the analysis runs again when it arrives. Ports are doubles, so nothing reads the disk, a log or a CLI.
/// </summary>
public sealed partial class MontageFactsCacheTests
{
    private static readonly ProjectContext Project = new("p-1", @"C:\repo");

    // The fixtures of the analyzer, by their short names (MontageFixtures.Task would hide the type Task if it were imported).
    private static IReadOnlyList<AgentDefinition> Roster() => MontageFixtures.Roster();

    private static PlanDefinition Plan(IReadOnlyList<TaskSpec> tasks) => MontageFixtures.Plan(tasks);

    private static TaskSpec Writing(string id, string agent, params string[] scope) => MontageFixtures.Writing(id, agent, scope);

    private static MontageDraft Draft(IReadOnlyList<AgentDefinition> roster, PlanDefinition plan) => MontageFixtures.Draft(roster, plan);

    private static ModelCatalog Catalog() => MontageFixtures.Catalog();

    private static FrozenRequirementSet Requirements(string mandatory = "Add Calc.Multiply") =>
        FrozenRequirementSet.Freeze(
            [
                Req("R1", RequirementPriority.Mandatory, mandatory, Crit("c1")),
                Req("R2", RequirementPriority.Desirable, "Document it", Crit("c2", VerificationMethod.HumanReview)),
            ],
            T0);

    /// <summary>T1 (ejecutor, with memory, covers R1) and T2 (ejecutor2, no memory, covers R2) after it.</summary>
    private static PlanDefinition TwoTasks(string objective = "Objetivo de T1", string[]? covers = null) =>
        Plan(
        [
            Writing("T1", "ejecutor", "src/Calc/") with { Covers = [.. (covers ?? ["R1"]).Select(Rid)], Objective = objective },
            Writing("T2", "ejecutor2", "src/Other/").After("T1") with { Covers = [Rid("R2")] },
        ]);

    private static MontageFactsSubject Subject(PlanDefinition plan, ProjectBrief? brief = null, LearnedModels? learned = null, FrozenRequirementSet? requirements = null) =>
        new(Project, plan, Roster(), requirements ?? Requirements(), Backends.Profile, brief, learned, ModelWindowCatalog.Baseline, new RunOptions(MaxParallelTasks: 3, MaxAgentRuns: 12));

    private static ContextPack Pack(int characters) =>
        new(
            [new ContextItem("m1", ContextTier.Decision, "Decision", "System", new string('x', characters), HashA)],
            [],
            characters,
            MetricValue<long>.EstimatedBySystem((characters + 3L) / 4, ContextPacker.TokenEstimateSource),
            HashB);

    /// <summary>A packer that answers when the test says so, and counts what it was asked.</summary>
    private sealed class GatedPacker : IContextPacker
    {
        private readonly TaskCompletionSource<ContextPack> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>How often it was asked; the cache asks off the thread of the test, so this is read after waiting for <see cref="Called"/> or the cache.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Completes when the packer was asked for the first time.</summary>
        public Task Called => _called.Task;

        public Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            _called.TrySetResult();
            return _gate.Task;
        }

        public void Answer(ContextPack pack) => _gate.SetResult(pack);
    }

    private sealed class ThrowingPacker(Exception problem) : IContextPacker
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromException<ContextPack>(problem);
        }
    }

    private static PlanEstimate Runs(long low, long high) =>
        new(
            new Dictionary<TaskId, UsageEstimate>(),
            new EstimateInterval<long>(MetricValue<long>.EstimatedBySystem(low, "test"), MetricValue<long>.EstimatedBySystem(high, "test")),
            EstimateInterval<long>.Unknown("test"),
            EstimateInterval<long>.Unknown("test"),
            EstimateInterval<TimeSpan>.Unknown("test"),
            []);

    /// <summary>An estimator that answers what the test sets for the plan it is asked about, and counts the questions.</summary>
    private sealed class FakeEstimator(Func<PlanEstimateRequest, PlanEstimate> answer) : IUsageEstimator
    {
        public System.Collections.Concurrent.ConcurrentQueue<PlanEstimateRequest> Asked { get; } = new();

        public Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct)
        {
            Asked.Enqueue(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class FakeLogReader(ProjectLogSnapshot snapshot) : IProjectLogReader
    {
        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct) => Task.FromResult(snapshot);
    }

    /// <summary>The real estimator over a log where the sonnet executor did a task <paramref name="runs"/> times each in <paramref name="tasks"/> tasks.</summary>
    private static UsageEstimator RealEstimator(int tasks, int runs)
    {
        var log = new UsageLogBuilder();
        for (var t = 1; t <= tasks; t++)
        {
            for (var attempt = 1; attempt <= runs; attempt++)
            {
                var launched = log.Now;
                log.Start("r" + t, "T1", attempt, model: "sonnet")
                    .Advance(TimeSpan.FromMinutes(1))
                    .Finish("r" + t, "T1", attempt, session: $"s{t}-{attempt}", usage: Snapshots.Claude(1000, 0, 0, 100, 0.1m), launchedAt: launched, reportedModel: "claude-sonnet-5-5");
            }
        }

        return new UsageEstimator(
            new FakeLogReader(new ProjectLogSnapshot("p1", @"C:\repo", log.Entries, DecisionLogVerification.Valid, AnchorStatus.Anchored)),
            new BuiltInModelCatalog());
    }

    /// <summary>Two tasks of the sonnet executor: what the log above knows how to estimate.</summary>
    private static PlanDefinition SonnetOnly() =>
        Plan(
        [
            Writing("T1", "ejecutor", "src/Calc/") with { Covers = [Rid("R1")] },
            Writing("T2", "ejecutor", "src/Other/").After("T1"),
        ]);

    // ---- the context of each task ----

    [Fact]
    public void A_plan_of_two_tasks_gives_two_facts_with_what_the_engine_would_start_each_one_with()
    {
        var cache = new MontageFactsCache(BuilderServices.None, () => { });

        var facts = cache.Read(Subject(TwoTasks())).TaskContexts;

        Assert.Equal(["T1", "T2"], facts.Select(f => f.Task.Value));
        var (first, second) = (facts[0], facts[1]);
        Assert.All(facts, f => Assert.True(f.PreambleTokens > 0));

        // T1: the executor with memory, covering a mandatory requirement, first in the graph. Its window is the one of its model
        // in the base table, estimated by Agent Studio.
        Assert.Equal((true, false, false), (first.HasMemory, first.HasBrief, first.HasUpstream));
        Assert.Equal(ExecutorContextBudget, first.BudgetCharacters);
        Assert.True(first.MandatoryCharacters > 0);
        Assert.Equal((1_000_000L, MetricOrigin.EstimatedBySystem), (first.Window.Value!.Value, first.Window.Origin));

        // T2: no memory, so no pack and no budget to be over; it only covers a desirable requirement; T1 comes before it.
        Assert.Equal((false, false, true), (second.HasMemory, second.HasBrief, second.HasUpstream));
        Assert.Equal(int.MaxValue, second.BudgetCharacters);
        Assert.Equal(0, second.MandatoryCharacters);
        Assert.Equal((200_000L, MetricOrigin.EstimatedBySystem), (second.Window.Value!.Value, second.Window.Origin));
    }

    private const int ExecutorContextBudget = 24_000;

    [Fact]
    public void The_tasks_that_are_not_agent_work_have_no_facts_and_a_task_of_an_unknown_agent_neither()
    {
        var plan = Plan(
        [
            Writing("T1", "ejecutor", "src/Calc/"),
            MontageFixtures.Task("I1", WorkTaskKind.Integration).After("T1"),
            MontageFixtures.Task("R1", WorkTaskKind.Review, "juez").After("I1"),
            Writing("T2", "fantasma", "src/Other/"),
        ]);
        var cache = new MontageFactsCache(BuilderServices.None, () => { });

        var facts = cache.Read(Subject(plan)).TaskContexts;

        Assert.Equal(["T1"], facts.Select(f => f.Task.Value));
    }

    [Fact]
    public void The_window_is_what_the_log_taught_of_the_model_and_the_base_table_only_when_it_taught_none()
    {
        var learned = new LearnedModels(
        [
            new LearnedModel(ProviderId.ClaudeCode, "claude-sonnet", ["claude-sonnet-5-5"], new ModelPerformance(ProviderId.ClaudeCode, "claude-sonnet", 5, 4, 1, 0), MetricValue<long>.Exact(10_000, "log"), MetricValue<decimal>.Missing("test"), 5),
        ])
        {
            Catalog = Catalog(),
        };
        var cache = new MontageFactsCache(BuilderServices.None, () => { });

        var facts = cache.Read(Subject(TwoTasks(), learned: learned)).TaskContexts;

        Assert.Equal((10_000L, MetricOrigin.ReportedByProvider), (facts[0].Window.Value!.Value, facts[0].Window.Origin));
        Assert.Equal(200_000L, facts[1].Window.Value!.Value);
    }

    [Fact]
    public void The_brief_counts_for_every_executor_and_shows_in_the_flag()
    {
        var brief = new ProjectBrief("brief-1", new string('b', 800), HashA, T0);
        var cache = new MontageFactsCache(BuilderServices.None, () => { });
        var without = cache.Read(Subject(TwoTasks())).TaskContexts;

        var with = cache.Read(Subject(TwoTasks(), brief: brief)).TaskContexts;

        Assert.All(with, f => Assert.True(f.HasBrief));
        Assert.All(without, f => Assert.False(f.HasBrief));
        Assert.All(with.Zip(without), pair => Assert.True(pair.First.PreambleTokens >= pair.Second.PreambleTokens + 200));
    }

    [Fact]
    public void Typing_the_text_of_a_task_changes_its_size_at_once()
    {
        var cache = new MontageFactsCache(BuilderServices.None, () => { });
        var before = cache.Read(Subject(TwoTasks())).TaskContexts[0].PreambleTokens;

        var after = cache.Read(Subject(TwoTasks("Objetivo de T1" + new string('y', 400)))).TaskContexts[0].PreambleTokens;

        Assert.Equal(before + 100, after);
    }

    // ---- the memory ----

    [Fact]
    public async Task The_memory_is_asked_in_the_background_and_the_draft_never_waits_for_it()
    {
        var packer = new GatedPacker();
        var changed = 0;
        var cache = new MontageFactsCache(new BuilderServices(ContextPacker: packer), () => changed++);

        var before = cache.Read(Subject(TwoTasks())).TaskContexts[0].PreambleTokens;

        // The packer has not answered: the draft was built without its memory, and nothing told the analysis to run again yet.
        await packer.Called.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (packer.Calls, changed));
        Assert.False(cache.Pending.IsCompleted);

        packer.Answer(Pack(4_000));
        await cache.Pending;
        var after = cache.Read(Subject(TwoTasks())).TaskContexts[0].PreambleTokens;

        Assert.Equal(1, changed);
        Assert.True(after >= before + 1_000);

        // Only the agent that reads memory was asked, once; the second task has none.
        Assert.Equal(1, packer.Calls);
    }

    [Fact]
    public async Task Editing_the_text_of_a_task_does_not_read_the_memory_again_but_its_requirements_and_a_new_read_do()
    {
        var packer = new GatedPacker();
        packer.Answer(Pack(400));
        var cache = new MontageFactsCache(new BuilderServices(ContextPacker: packer), () => { });
        cache.Read(Subject(TwoTasks()));
        await cache.Pending;
        Assert.Equal(1, packer.Calls);

        // The objective, the title… of the task: the pack does not depend on them.
        cache.Read(Subject(TwoTasks("Otro objetivo, más largo que el anterior")));
        cache.Read(Subject(TwoTasks("Y otro más")));
        await cache.Pending;
        Assert.Equal(1, packer.Calls);

        // The requirements it covers do change what is packed with them.
        cache.Read(Subject(TwoTasks(covers: ["R1", "R2"])));
        await cache.Pending;
        Assert.Equal(2, packer.Calls);

        // The project was read again: nothing of what was learned is kept.
        cache.Reset();
        cache.Read(Subject(TwoTasks()));
        await cache.Pending;
        Assert.Equal(3, packer.Calls);
    }

    // ---- the skills ----

    [Fact]
    public async Task The_skills_of_each_agent_are_asked_once_and_counted_in_the_preamble_of_its_tasks()
    {
        var asked = new List<string>();
        var changed = 0;
        var cache = new MontageFactsCache(
            new BuilderServices(SkillCharacters: (_, agent, _) =>
            {
                asked.Add(agent.Id.Value);
                return Task.FromResult(agent.Id.Value == "ejecutor" ? 4_000 : 0);
            }),
            () => changed++);

        var before = cache.Read(Subject(TwoTasks())).TaskContexts;
        await cache.Pending;
        var after = cache.Read(Subject(TwoTasks("Un objetivo distinto"))).TaskContexts;
        await cache.Pending;

        // 4 000 characters are 1 000 tokens more for the tasks of the agent that has skills, and nothing for the other one; each agent
        // was asked once, however many times the draft was built and whatever its tasks said.
        Assert.Equal(["ejecutor", "ejecutor2"], asked.Order(StringComparer.Ordinal));
        Assert.Equal(1, changed);
        Assert.True(after[0].PreambleTokens >= before[0].PreambleTokens + 1_000);
        Assert.Equal(before[1].PreambleTokens, after[1].PreambleTokens);
    }

    [Fact]
    public async Task A_catalog_that_fails_counts_no_skills_and_is_not_asked_again()
    {
        var asked = 0;
        var cache = new MontageFactsCache(
            new BuilderServices(SkillCharacters: (_, _, _) =>
            {
                asked++;
                throw new IOException("the catalog is gone");
            }),
            () => { });

        var first = cache.Read(Subject(TwoTasks())).TaskContexts;
        await cache.Pending;
        var second = cache.Read(Subject(TwoTasks())).TaskContexts;
        await cache.Pending;

        Assert.Equal(2, asked);
        Assert.Equal(first.Select(f => f.PreambleTokens), second.Select(f => f.PreambleTokens));
    }

    [Fact]
    public async Task A_packer_that_fails_leaves_the_task_measured_without_memory_and_is_asked_once()
    {
        foreach (var problem in new Exception[] { new ContextBudgetTooSmallException(30_000, 24_000), new InvalidOperationException("the store failed") })
        {
            var packer = new ThrowingPacker(problem);
            var changed = 0;
            var cache = new MontageFactsCache(new BuilderServices(ContextPacker: packer), () => changed++);

            var first = cache.Read(Subject(TwoTasks())).TaskContexts;
            await cache.Pending;
            var second = cache.Read(Subject(TwoTasks())).TaskContexts;
            await cache.Pending;

            Assert.Equal(first[0].PreambleTokens, second[0].PreambleTokens);
            Assert.Equal((1, 1), (packer.Calls, changed));
        }
    }

    [Fact]
    public async Task A_reset_while_the_memory_is_on_its_way_drops_the_answer_that_comes_for_the_old_project()
    {
        var packer = new GatedPacker();
        var changed = 0;
        var cache = new MontageFactsCache(new BuilderServices(ContextPacker: packer), () => changed++);
        cache.Read(Subject(TwoTasks()));
        await packer.Called.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The packer ignores the token and answers late, as a port that is in the middle of reading may: the answer is dropped.
        cache.Reset();
        packer.Answer(Pack(4_000));
        await cache.Pending;

        Assert.Equal(0, changed);
    }

    // ---- the estimate of the plan ----

    [Fact]
    public async Task The_agent_runs_are_the_high_end_of_the_estimate_and_never_an_old_one_for_another_plan()
    {
        var estimator = new FakeEstimator(request => Runs(low: 4, high: 10L * request.Plan.Tasks.Count));
        var changed = 0;
        var cache = new MontageFactsCache(new BuilderServices(Estimator: estimator), () => changed++);

        // Not asked yet: no figure, and an analysis again once there is one.
        Assert.Null(cache.Read(Subject(TwoTasks())).EstimatedAgentRuns);
        await cache.Pending;
        Assert.Equal(1, changed);
        Assert.Equal(20, cache.Read(Subject(TwoTasks())).EstimatedAgentRuns);

        // A plan of another shape: the 20 of the old one is not said for it.
        var three = Plan([.. TwoTasks().Tasks, Writing("T3", "ejecutor", "src/Third/")]);
        Assert.Null(cache.Read(Subject(three)).EstimatedAgentRuns);
        await cache.Pending;
        Assert.Equal(30, cache.Read(Subject(three)).EstimatedAgentRuns);
        Assert.Equal(2, estimator.Asked.Count);
    }

    [Fact]
    public async Task The_estimator_is_asked_once_for_each_shape_of_the_plan_and_not_for_each_letter_typed()
    {
        var estimator = new FakeEstimator(_ => Runs(4, 10));
        var cache = new MontageFactsCache(new BuilderServices(Estimator: estimator), () => { });
        cache.Read(Subject(TwoTasks()));
        await cache.Pending;

        // The text of a task, the budget of the run and the requirements it covers do not change the shape.
        cache.Read(Subject(TwoTasks("Un objetivo distinto")));
        cache.Read(Subject(TwoTasks(covers: ["R2"])));
        cache.Read(Subject(TwoTasks()) with { Options = new RunOptions(MaxParallelTasks: 3, MaxAgentRuns: 40) });
        await cache.Pending;
        Assert.Single(estimator.Asked);

        // Giving a task to another agent does.
        var reassigned = Plan([Writing("T1", "ejecutor2", "src/Calc/") with { Covers = [Rid("R1")] }, TwoTasks().Tasks[1]]);
        cache.Read(Subject(reassigned));
        await cache.Pending;
        Assert.Equal(2, estimator.Asked.Count);
    }

    [Fact]
    public async Task An_estimator_that_does_not_know_leaves_the_runs_empty_and_does_not_keep_asking()
    {
        var unknown = new FakeEstimator(_ => new PlanEstimate(new Dictionary<TaskId, UsageEstimate>(), EstimateInterval<long>.Unknown("unknown: 2 of 3 references"), EstimateInterval<long>.Unknown("x"), EstimateInterval<long>.Unknown("x"), EstimateInterval<TimeSpan>.Unknown("x"), []));
        var cache = new MontageFactsCache(new BuilderServices(Estimator: unknown), () => { });
        cache.Read(Subject(TwoTasks()));
        await cache.Pending;

        Assert.Null(cache.Read(Subject(TwoTasks())).EstimatedAgentRuns);
        await cache.Pending;
        Assert.Single(unknown.Asked);
    }

    [Fact]
    public async Task An_estimator_that_fails_is_asked_again_a_few_times_and_then_the_runs_stay_unknown()
    {
        var failing = new FakeEstimator(_ => throw new IOException("the log is busy"));
        var cache = new MontageFactsCache(new BuilderServices(Estimator: failing), () => { });

        for (var read = 0; read < 6; read++)
        {
            Assert.Null(cache.Read(Subject(TwoTasks())).EstimatedAgentRuns);
            await cache.Pending;
        }

        Assert.Equal(3, failing.Asked.Count);
    }

    [Fact]
    public async Task A_failure_that_passes_does_not_leave_the_shape_without_its_runs()
    {
        var calls = 0;
        var estimator = new FakeEstimator(_ => ++calls == 1 ? throw new IOException("the log is busy") : Runs(low: 4, high: 7));
        var cache = new MontageFactsCache(new BuilderServices(Estimator: estimator), () => { });
        cache.Read(Subject(TwoTasks()));
        await cache.Pending;

        cache.Read(Subject(TwoTasks()));
        await cache.Pending;

        Assert.Equal(7, cache.Read(Subject(TwoTasks())).EstimatedAgentRuns);
        Assert.Equal(2, estimator.Asked.Count);
    }

    [Fact]
    public async Task Without_an_estimator_nothing_is_estimated()
    {
        var cache = new MontageFactsCache(BuilderServices.None, () => { });

        Assert.Null(cache.Read(Subject(TwoTasks())).EstimatedAgentRuns);
        await cache.Pending;
        Assert.Null(cache.Read(Subject(TwoTasks())).EstimatedAgentRuns);
    }

    [Fact]
    public async Task With_the_estimator_of_the_system_the_runs_are_known_from_three_references_and_not_before()
    {
        // Three tasks of the sonnet executor, each done in three runs: the runs of the plan are three for each of its two tasks.
        var enough = new MontageFactsCache(new BuilderServices(Estimator: RealEstimator(tasks: 3, runs: 3)), () => { });
        enough.Read(Subject(SonnetOnly()));
        await enough.Pending;
        Assert.Equal(6, enough.Read(Subject(SonnetOnly())).EstimatedAgentRuns);

        // With two there is nothing to say: the contract of the draft is null, not a number made up.
        var few = new MontageFactsCache(new BuilderServices(Estimator: RealEstimator(tasks: 2, runs: 3)), () => { });
        few.Read(Subject(SonnetOnly()));
        await few.Pending;
        Assert.Null(few.Read(Subject(SonnetOnly())).EstimatedAgentRuns);
    }

    // ---- the rules that were silent ----

    private static MontageDraft DraftWith(PlanDefinition plan, MontageFacts facts) =>
        Draft(Roster(), plan) with
        {
            TaskContexts = facts.TaskContexts,
            Learned = new MontageLearnedFacts([], facts.EstimatedAgentRuns),
        };

    [Fact]
    public async Task What_the_cache_gives_makes_pack_too_small_window_tight_and_over_budget_speak()
    {
        var packer = new GatedPacker();
        packer.Answer(Pack(4_000));

        // A window of 12 000 tokens, a mandatory requirement of 30 000 characters and a plan that takes more runs than the budget.
        var learned = new LearnedModels(
        [
            new LearnedModel(ProviderId.ClaudeCode, "claude-sonnet", ["claude-sonnet-5-5"], new ModelPerformance(ProviderId.ClaudeCode, "claude-sonnet", 5, 4, 1, 0), MetricValue<long>.Exact(12_000, "log"), MetricValue<decimal>.Missing("test"), 5),
        ])
        {
            Catalog = Catalog(),
        };
        var cache = new MontageFactsCache(new BuilderServices(ContextPacker: packer, Estimator: RealEstimator(tasks: 3, runs: 7)), () => { });
        var subject = Subject(SonnetOnly(), learned: learned, requirements: Requirements(new string('m', 30_000)));
        cache.Read(subject);
        await cache.Pending;

        var findings = MontageAnalyzer.Analyze(DraftWith(SonnetOnly(), cache.Read(subject))).Findings;

        Assert.Equal(["T1"], findings.Where(f => f.Code == FindingCodes.PackTooSmall).Select(f => f.Nodes[0]));
        Assert.Equal(["T1"], findings.Where(f => f.Code == FindingCodes.WindowTight).Select(f => f.Nodes[0]));
        Assert.Single(findings, f => f.Code == FindingCodes.CostOverBudget);
    }

    [Fact]
    public void Without_memory_nor_brief_nor_anything_before_it_a_task_that_writes_is_thin()
    {
        var alone = Plan([Writing("T1", "ejecutor2", "src/Calc/") with { Covers = [Rid("R1")] }]);
        var cache = new MontageFactsCache(BuilderServices.None, () => { });

        var thin = MontageAnalyzer.Analyze(DraftWith(alone, cache.Read(Subject(alone)))).Findings;
        var brief = new ProjectBrief("brief-1", "Una calculadora en C#.", HashA, T0);
        var withBrief = MontageAnalyzer.Analyze(DraftWith(alone, cache.Read(Subject(alone, brief: brief)))).Findings;

        Assert.Contains(thin, f => f.Code == FindingCodes.ContextThin && f.Nodes[0] == "T1");
        Assert.DoesNotContain(withBrief, f => f.Code == FindingCodes.ContextThin);
    }

    [Fact]
    public void A_task_that_reads_no_memory_has_no_budget_to_be_over_however_long_its_requirements_are()
    {
        var alone = Plan([Writing("T1", "ejecutor2", "src/Calc/") with { Covers = [Rid("R1")] }]);
        var cache = new MontageFactsCache(BuilderServices.None, () => { });

        var findings = MontageAnalyzer.Analyze(DraftWith(alone, cache.Read(Subject(alone, requirements: Requirements(new string('m', 30_000)))))).Findings;

        Assert.DoesNotContain(findings, f => f.Code == FindingCodes.PackTooSmall);
    }
}
