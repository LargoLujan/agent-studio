using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Models;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Observability.Estimates;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Domain.Orchestration.Analysis;
using AgentStudio.Tests.Observability.Usage;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Preparation;

/// <summary>
/// The draft of the «Orquestador» document with the data it did not have (docs/17 D-13, D-16 and D-18, P49): the context of each
/// task, the agent runs the estimator expects and the size of the project. Through the document, as the person uses it: a plan on
/// the canvas, the band that analyses it, and the findings that were silent. Every port is a double, so nothing reads the disk, a
/// log or a CLI, and every test fixes what it needs of the machine.
/// </summary>
public sealed class DraftFactsDocumentTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly BuiltInModelCatalog Models = new();

    // The fixtures of the analyzer, by their short names (MontageFixtures.Task would hide the type Task if it were imported).
    private static IReadOnlyList<AgentDefinition> Roster() => MontageFixtures.Roster();

    private static TaskSpec Writing(string id, string agent, params string[] scope) => MontageFixtures.Writing(id, agent, scope);

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public CanvasLayout Saved { get; private set; } = CanvasLayout.Empty;

        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => Saved;

        public void Save(string projectRoot, CanvasLayout layout) => Saved = layout;
    }

    private sealed class FakeLearning(LearnedModels learned) : IModelLearning
    {
        public Task<LearnedModels> LearnAsync(string projectRoot, CancellationToken ct) => Task.FromResult(learned);
    }

    private sealed class CountingPacker(ContextPack pack) : IContextPacker
    {
        public int Calls { get; private set; }

        public Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(pack);
        }
    }

    private sealed class CountingEstimator(UsageEstimator inner) : IUsageEstimator
    {
        public int PlanCalls { get; private set; }

        public Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct) => inner.EstimateAsync(request, ct);

        public Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct)
        {
            PlanCalls++;
            return inner.EstimatePlanAsync(request, ct);
        }
    }

    private sealed class FakeLogReader(ProjectLogSnapshot snapshot) : IProjectLogReader
    {
        public Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct) => Task.FromResult(snapshot);
    }

    /// <summary>The real estimator over a log where the sonnet executor did three tasks in seven runs each: the runs of a plan of two are 14.</summary>
    private static CountingEstimator Estimator()
    {
        var log = new UsageLogBuilder();
        for (var task = 1; task <= 3; task++)
        {
            for (var attempt = 1; attempt <= 7; attempt++)
            {
                var launched = log.Now;
                log.Start("r" + task, "T1", attempt, model: "sonnet")
                    .Advance(TimeSpan.FromMinutes(1))
                    .Finish("r" + task, "T1", attempt, session: $"s{task}-{attempt}", usage: Snapshots.Claude(1000, 0, 0, 100, 0.1m), launchedAt: launched, reportedModel: "claude-sonnet-5-5");
            }
        }

        return new CountingEstimator(new UsageEstimator(new FakeLogReader(new ProjectLogSnapshot("p1", Root, log.Entries, DecisionLogVerification.Valid, AnchorStatus.Anchored)), Models));
    }

    private static FrozenRequirementSet Requirements(string mandatory = "Add Calc.Multiply") =>
        FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Mandatory, mandatory, Crit("c1")), Req("R2", RequirementPriority.Desirable, "Document it", Crit("c2", VerificationMethod.HumanReview))], T0);

    private static async Task<OrchestratorDocumentViewModel> OpenAsync(BuilderServices services, FrozenRequirementSet? requirements = null)
    {
        var backend = new OrchestrationBackend(
            new FakeOrchestrator(),
            new FakeWorkspace(),
            new FakeRequirementStore(requirements ?? Requirements()),
            new FakeRoster(Roster),
            new FakeProfileSource(Backends.Profile));
        var document = new OrchestratorDocumentViewModel(
            Root,
            new OrchestrationSession(backend),
            new MemoryLayoutStore(),
            new FakeTimeProvider(T0),
            new InlineSynchronizationContext(),
            null,
            drafts: null,
            builder: services with { WindowsAnimates = () => true });
        await document.OpenAsync();
        return document;
    }

    /// <summary>The analysis of the plan on the canvas: once, which asks what is missing; and again when it has come in.</summary>
    private static async Task<IReadOnlyList<Finding>> AnalyseAsync(OrchestratorDocumentViewModel document, IReadOnlyList<TaskSpec> tasks)
    {
        document.Canvas.LoadTasks(tasks, CanvasLayout.Empty);
        var builder = document.Builder!;
        await builder.Band.RefreshAsync();
        await builder.FactsReady;
        await builder.Band.RefreshAsync();
        return [.. builder.Band.Findings.Select(f => f.Finding)];
    }

    private static LearnedModels LearnedWindow(long window)
    {
        var key = ModelKeys.Of(Models.Current.Catalog, ProviderId.ClaudeCode, "sonnet")!;
        return new LearnedModels(
        [
            new LearnedModel(ProviderId.ClaudeCode, key, ["claude-sonnet-5-5"], new ModelPerformance(ProviderId.ClaudeCode, key, 5, 4, 1, 0), MetricValue<long>.Exact(window, "log"), MetricValue<decimal>.Missing("test"), 5),
        ])
        {
            Catalog = Models.Current.Catalog,
        };
    }

    private static ContextPack Pack(int characters) =>
        new(
            [new ContextItem("m1", ContextTier.Decision, "Decision", "System", new string('x', characters), HashA)],
            [],
            characters,
            MetricValue<long>.EstimatedBySystem((characters + 3L) / 4, "test"),
            HashB);

    private static IReadOnlyList<TaskSpec> SonnetTasks() =>
    [
        Writing("T1", "ejecutor", "src/Calc/") with { Covers = [Rid("R1")] },
        Writing("T2", "ejecutor", "src/Other/").After("T1"),
    ];

    // ---- the four rules that were silent ----

    [Fact]
    public async Task A_requirement_that_does_not_fit_and_a_window_that_is_too_small_are_said_for_the_task_that_has_them()
    {
        var packer = new CountingPacker(Pack(4_000));
        var services = new BuilderServices(Learning: new FakeLearning(LearnedWindow(12_000)), ContextPacker: packer);
        var document = await OpenAsync(services, Requirements(new string('m', 30_000)));

        var findings = await AnalyseAsync(document, SonnetTasks());

        Assert.Equal(["T1"], findings.Where(f => f.Code == FindingCodes.PackTooSmall).Select(f => f.Nodes[0]));
        Assert.Equal(["T1"], findings.Where(f => f.Code == FindingCodes.WindowTight).Select(f => f.Nodes[0]));

        // pack-too-small is an error, as what the engine would do: the task would not start.
        Assert.Equal(FindingSeverity.Error, findings.Single(f => f.Code == FindingCodes.PackTooSmall).Severity);
        Assert.Equal(FindingSeverity.Warning, findings.Single(f => f.Code == FindingCodes.WindowTight).Severity);
    }

    [Fact]
    public async Task A_plan_that_is_expected_to_take_more_runs_than_the_budget_says_so_and_the_fix_raises_the_budget_without_asking_again()
    {
        var estimator = Estimator();
        var document = await OpenAsync(new BuilderServices(Estimator: estimator));

        var findings = await AnalyseAsync(document, SonnetTasks());

        var over = Assert.Single(findings, f => f.Code == FindingCodes.CostOverBudget);
        Assert.Equal(new RaiseRunBudgetFix(14), over.Fix);
        Assert.Equal(1, estimator.PlanCalls);

        await document.Builder!.Band.Findings.Single(f => f.Finding.Code == FindingCodes.CostOverBudget).ApplyFixCommand.ExecuteAsync(null);
        await document.Builder.Band.RefreshAsync();

        Assert.Equal(14, document.Builder.MaxAgentRuns);
        Assert.DoesNotContain(document.Builder.Band.Findings, f => f.Finding.Code == FindingCodes.CostOverBudget);
        Assert.Equal(1, estimator.PlanCalls);
    }

    [Fact]
    public async Task A_task_that_writes_with_nothing_of_the_project_is_thin_until_the_brief_is_written()
    {
        var plan = (IReadOnlyList<TaskSpec>)[Writing("T1", "ejecutor2", "src/Calc/") with { Covers = [Rid("R1")] }];
        var without = await OpenAsync(BuilderServices.None);
        var withBrief = await OpenAsync(new BuilderServices(Brief: new FakeBrief { Stored = new ProjectBrief("brief-1", "Una calculadora en C#.", HashA, T0) }));

        Assert.Contains(await AnalyseAsync(without, plan), f => f.Code == FindingCodes.ContextThin);
        Assert.DoesNotContain(await AnalyseAsync(withBrief, plan), f => f.Code == FindingCodes.ContextThin);
    }

    [Fact]
    public async Task Saving_the_brief_runs_the_analysis_again_and_the_task_stops_being_thin()
    {
        var brief = new FakeBrief();
        var document = await OpenAsync(new BuilderServices(Brief: brief));
        var tasks = (IReadOnlyList<TaskSpec>)[Writing("T1", "ejecutor2", "src/Calc/") with { Covers = [Rid("R1")] }];
        Assert.Contains(await AnalyseAsync(document, tasks), f => f.Code == FindingCodes.ContextThin);

        document.Builder!.Brief.Brief = "Una calculadora en C#.";
        await document.Builder.Brief.SaveCommand.ExecuteAsync(null);

        Assert.Equal(AnalysisState.Pending, document.Builder.Band.State);
        await document.Builder.Band.RefreshAsync();
        Assert.DoesNotContain(document.Builder.Band.Findings, f => f.Finding.Code == FindingCodes.ContextThin);
    }

    [Fact]
    public async Task What_costs_is_asked_once_and_editing_the_text_of_a_task_asks_nothing_again()
    {
        var packer = new CountingPacker(Pack(400));
        var estimator = Estimator();
        var document = await OpenAsync(new BuilderServices(ContextPacker: packer, Estimator: estimator));
        await AnalyseAsync(document, SonnetTasks());
        Assert.Equal((2, 1), (packer.Calls, estimator.PlanCalls));

        var edited = (IReadOnlyList<TaskSpec>)[.. SonnetTasks().Select(t => t with { Objective = t.Objective + " con más detalle" })];
        await AnalyseAsync(document, edited);

        Assert.Equal((2, 1), (packer.Calls, estimator.PlanCalls));
    }

    [Fact]
    public async Task Without_the_services_the_draft_is_the_one_of_before_and_says_nothing_it_does_not_know()
    {
        var document = await OpenAsync(BuilderServices.None);

        var findings = await AnalyseAsync(document, SonnetTasks());

        Assert.DoesNotContain(findings, f => f.Code is FindingCodes.PackTooSmall or FindingCodes.WindowTight or FindingCodes.CostOverBudget);
        Assert.Null(document.Builder!.ProjectFiles);
    }

    // ---- the size of the project ----

    [Fact]
    public async Task The_size_of_the_project_reaches_the_recommendation_of_a_model()
    {
        var asked = new List<string>();
        var services = new BuilderServices(
            Catalog: Models,
            ProjectFiles: (root, _) =>
            {
                asked.Add(root);
                return Task.FromResult(new ProjectFileCount(2_500, IsTruncated: false));
            });
        var document = await OpenAsync(services);

        document.Canvas.Team.Select(document.Canvas.Team.Agents.Single(a => a.Id.Value == "orquestador"));
        var recommendation = document.Builder!.Editor.Inspector!.Recommendation;

        Assert.Equal([Root], asked);
        Assert.Equal(new ProjectFileCount(2_500, false), document.Builder.ProjectFiles);
        Assert.Equal(2_500, document.Builder.Editor.Context.ProjectFiles);
        Assert.Equal(ModelRecommendationReasons.LargeProject, recommendation!.ReasonCode);
    }

    [Fact]
    public async Task Without_a_count_the_size_is_unknown_and_the_recommendation_is_the_one_of_the_role()
    {
        var failing = new BuilderServices(Catalog: Models, ProjectFiles: (_, _) => throw new DirectoryNotFoundException("gone"));
        foreach (var services in new[] { new BuilderServices(Catalog: Models), failing })
        {
            var document = await OpenAsync(services);

            document.Canvas.Team.Select(document.Canvas.Team.Agents.Single(a => a.Id.Value == "orquestador"));

            Assert.Null(document.Builder!.ProjectFiles);
            Assert.Null(document.Builder.Editor.Context.ProjectFiles);
            Assert.Equal(ModelRecommendationReasons.RoleDefault, document.Builder.Editor.Inspector!.Recommendation!.ReasonCode);
        }
    }
}
