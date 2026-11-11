using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Fakes;

internal sealed class FakeProjectAgentProviders(IReadOnlyList<IAgentProvider> providers) : IProjectAgentProviders
{
    public List<string> Roots { get; } = [];

    public IReadOnlyList<IAgentProvider> For(string projectRoot)
    {
        Roots.Add(projectRoot);
        return providers;
    }
}

/// <summary>
/// The engine with every port faked, but the parts that are not ports kept real: the <c>DecisionRecorder</c> over an
/// in-memory store, the <c>SkillProvisioner</c> and the provider pipeline (journal and concurrency limit) around the
/// scripted adapters. Time is a <see cref="FakeTimeProvider"/>: nothing that waits on the clock moves until a test
/// advances it.
/// </summary>
internal sealed class EngineHarness : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <param name="time">The clock of every part: a test's own fake clock, set at <see cref="OrchestrationFixtures.Start"/>; null is a plain one.</param>
    public EngineHarness(int claudeLimit = 2, int codexLimit = 2, IReadOnlyList<SkillActivation>? activations = null, IReadOnlyList<CatalogSkill>? catalog = null, IReadOnlyDictionary<string, string>? repositoryFiles = null, FakeTimeProvider? time = null)
    {
        Time = time ?? new FakeTimeProvider(OrchestrationFixtures.Start);
        LogStore = new InMemoryDecisionLogStore();
        Decisions = new DecisionRecorder(LogStore, Time);
        Evidence = new InMemoryEvidenceStore();
        Plans = new InMemoryPlanStore();
        Runs = new InMemoryRunRegistry();
        RequirementStore = new InMemoryRequirementStore();
        Roster = new InMemoryAgentRoster(OrchestrationFixtures.Roster());
        Journal = new InMemoryPromptJournal();
        Claude = new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, Time, claudeLimit);
        Codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, Time, codexLimit);
        Providers = new FakeProjectAgentProviders(
        [
            AgentProviderPipeline.Compose(Claude, Journal, Time),
            AgentProviderPipeline.Compose(Codex, Journal, Time),
        ]);
        RepositoryRoot = _temp.CreateDirectory("repo");
        Worktrees = new FakeWorktreeManager(_temp.CreateDirectory("wt"), RepositoryRoot, repositoryFiles);
        Verification = new FakeVerificationRunner(Evidence, Time);
        Integrator = new FakeIntegrator(Worktrees, Evidence, Verification);
        Review = new FakeIndependentReview(Decisions);
        Approvals = new FakeApprovalGate();
        Materializer = new RecordingSkillMaterializer();
        Skills = new SkillProvisioner(
            new FakeSkillCatalog([.. catalog ?? []]),
            new FakeSkillActivations([.. activations ?? []]),
            Materializer,
            new SkillProvisioningOptions(_temp.CreateDirectory("skills")));
        Paths = new OrchestrationPaths(_temp.Combine("wt"), _temp.CreateDirectory("dossiers"), _temp.Combine("skills"));
        Project = new ProjectContext("p1", RepositoryRoot);
    }

    public FakeTimeProvider Time { get; }

    public InMemoryDecisionLogStore LogStore { get; }

    public DecisionRecorder Decisions { get; }

    public InMemoryEvidenceStore Evidence { get; }

    public InMemoryPlanStore Plans { get; }

    public InMemoryRunRegistry Runs { get; }

    public InMemoryRequirementStore RequirementStore { get; }

    public InMemoryAgentRoster Roster { get; }

    public InMemoryPromptJournal Journal { get; }

    public ScriptedAgentProvider Claude { get; }

    public ScriptedAgentProvider Codex { get; }

    public FakeProjectAgentProviders Providers { get; set; }

    public string RepositoryRoot { get; }

    public FakeWorktreeManager Worktrees { get; }

    public FakeVerificationRunner Verification { get; }

    public FakeIntegrator Integrator { get; }

    public FakeIndependentReview Review { get; }

    /// <summary>The review the engine is composed with: the scripted <see cref="Review"/> unless a test puts another one here (the real one, say).</summary>
    public IIndependentReview? ReviewPort { get; set; }

    public FakeApprovalGate Approvals { get; }

    public RecordingSkillMaterializer Materializer { get; }

    public ISkillProvisioner Skills { get; set; }

    public OrchestrationPaths Paths { get; }

    public ProjectContext Project { get; }

    /// <summary>
    /// What the engine is told about the CLIs whose installed version was measured to keep an executor's writes in its
    /// worktree (ADR-0018, «Medición»). By default Claude Code is, which is counterfactual: no version is measured today
    /// (2.1.290 wrote outside its worktree) and the app fails closed. It lets the tests of a run that cannot write at
    /// medium integrity use the ordinary executor; the tests of a version never measured, of no probe and of a resumed
    /// run cover the real default.
    /// </summary>
    public IWriteConfinementProbe? WriteConfinement { get; set; } = new FixedWriteConfinement(ProviderId.ClaudeCode);

    /// <summary>
    /// What <see cref="WriteConfinement"/> answers now of the installed Claude Code: what the canvas reads when it shows a
    /// plan, and the engine when the run starts. False without a probe, as the engine says (fail closed). The fakes
    /// answer at once, so it is read synchronously.
    /// </summary>
    public bool ClaudeWritesConfined() =>
        WriteConfinement is { } probe
        && probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    public IReadOnlyList<DecisionEntry> Entries => LogStore.Entries(Project.ProjectId);

    /// <summary>Phase 13: the brief of the project the engine reads for the prompts of its executors; null, none.</summary>
    public AgentStudio.Application.Knowledge.IProjectBrief? Brief { get; set; }

    /// <summary>Phase 13: what redacts the questions of the agents and the answers of the person; null, nothing is redacted.</summary>
    public AgentStudio.Application.Security.ISecretRedactor? Redactor { get; set; }

    /// <summary>Phase 13: what estimates the options of a question; null, nothing is estimated.</summary>
    public AgentStudio.Application.Usage.IUsageEstimator? Estimator { get; set; }

    /// <summary>Phase 9 (docs/16 D-05): the budget of the project; null, none (the engine reads nothing for it).</summary>
    public AgentStudio.Application.Work.IProjectBudgetStore? Budgets { get; set; }

    /// <summary>Phase 9: what the budget gate reads what was used with.</summary>
    public AgentStudio.Application.Usage.IUsageReport? UsageReport { get; set; }

    /// <summary>Phase 9: where the headers (and the budget) of the milestones are read.</summary>
    public AgentStudio.Application.Work.IMilestoneRegistry? Milestones { get; set; }

    /// <summary>Phase 9: the log reader the gate finds the milestones of a run with.</summary>
    public AgentStudio.Application.Projects.IProjectLogReader? LogReader { get; set; }

    public Orchestrator CreateOrchestrator() =>
        new(Decisions, Evidence, Plans, Runs, RequirementStore, Roster, Providers, Worktrees, Verification, Integrator, ReviewPort ?? Review, Approvals, Skills, Paths, Time, WriteConfinement,
            brief: Brief, redactor: Redactor, estimator: Estimator, budgets: Budgets, usage: UsageReport, milestones: Milestones, logReader: LogReader);

    public PlanningRequest PlanningRequest(string request = "Añade Calc.Multiply(a,b) con sus pruebas", PlanLimits? limits = null) =>
        new(
            Project,
            request,
            OrchestrationFixtures.Requirements(),
            OrchestrationFixtures.Roster(),
            new AgentId("orquestador"),
            OrchestrationFixtures.Profile(),
            limits ?? new PlanLimits());

    /// <summary>The entries of one kind with their typed payloads, in log order.</summary>
    public IReadOnlyList<(DecisionEntry Entry, T Payload)> Payloads<T>()
        where T : DecisionPayload =>
        [.. Entries
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .Where(x => x.Payload is T)
            .Select(x => (x.Entry, (T)x.Payload!))];

    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var entry in Entries)
        {
            text.Append(entry.Sequence).Append(' ').Append(entry.Kind).Append(' ').Append(entry.Summary).Append(' ').AppendLine(entry.PayloadJson);
        }

        return text.ToString();
    }

    public void Dispose() => _temp.Dispose();
}
