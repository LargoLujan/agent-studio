using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Orchestration.Planning;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/15 D-05 (decision 12): one activity with agents per project in this process. <see cref="OrchestrationActivity"/> is
/// the <see cref="IProjectActivity"/> of the process, and so the <see cref="IRunActivity"/> the memory waits on: the planning
/// and the run of the orchestrator share a project, a debate or an audit shares it with nothing, and the orchestrator does
/// not plan, start or resume while a debate or an audit holds it. A run still releases its project before its end can be
/// seen (docs/14 D-22), and the container builds the activity before the memory and the orchestrator without a cycle.
/// </summary>
public sealed class ProjectActivityTests
{
    private const string ProjectId = "p1";

    private static readonly ProjectActivityKind[] Kinds = Enum.GetValues<ProjectActivityKind>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the activity itself ----

    [Fact]
    public void An_idle_project_has_no_activity_and_is_not_busy()
    {
        var activity = new OrchestrationActivity();

        Assert.Null(activity.Current(ProjectId));
        Assert.False(activity.IsBusy(ProjectId));
        Assert.IsAssignableFrom<IRunActivity>(activity);
    }

    [Theory]
    [InlineData(ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Audit)]
    public void A_debate_or_an_audit_holds_the_project_alone_not_even_with_another_of_its_kind(ProjectActivityKind kind)
    {
        var activity = new OrchestrationActivity();

        using var lease = activity.TryEnter(ProjectId, kind);

        Assert.NotNull(lease);
        Assert.Equal(kind, activity.Current(ProjectId));
        Assert.True(activity.IsBusy(ProjectId));
        Assert.All(Kinds, other => Assert.Null(activity.TryEnter(ProjectId, other)));
        Assert.Equal(kind, activity.Current(ProjectId));

        // Another project is another activity.
        using var elsewhere = activity.TryEnter("otro-proyecto", kind);
        Assert.NotNull(elsewhere);
    }

    [Fact]
    public void The_planning_and_the_run_of_the_orchestrator_share_the_project_and_a_run_wins_over_a_planning()
    {
        var activity = new OrchestrationActivity();

        var planning = activity.TryEnter(ProjectId, ProjectActivityKind.Planning);
        Assert.Equal(ProjectActivityKind.Planning, activity.Current(ProjectId));
        var run = activity.TryEnter(ProjectId, ProjectActivityKind.Run);
        var another = activity.TryEnter(ProjectId, ProjectActivityKind.Run);

        Assert.NotNull(planning);
        Assert.NotNull(run);
        Assert.NotNull(another);
        Assert.Equal(ProjectActivityKind.Run, activity.Current(ProjectId));

        run.Dispose();
        Assert.Equal(ProjectActivityKind.Run, activity.Current(ProjectId));
        another.Dispose();
        Assert.Equal(ProjectActivityKind.Planning, activity.Current(ProjectId));
        planning.Dispose();
        Assert.Null(activity.Current(ProjectId));
        Assert.False(activity.IsBusy(ProjectId));
    }

    [Theory]
    [InlineData(ProjectActivityKind.Run, ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Run, ProjectActivityKind.Audit)]
    [InlineData(ProjectActivityKind.Planning, ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Planning, ProjectActivityKind.Audit)]
    public void Neither_a_debate_nor_an_audit_enters_while_the_orchestrator_holds_the_project(ProjectActivityKind holder, ProjectActivityKind asking)
    {
        var activity = new OrchestrationActivity();
        var held = activity.TryEnter(ProjectId, holder);

        Assert.Null(activity.TryEnter(ProjectId, asking));
        Assert.Equal(holder, activity.Current(ProjectId));

        held!.Dispose();
        using var entered = activity.TryEnter(ProjectId, asking);
        Assert.NotNull(entered);
    }

    [Fact]
    public void Disposing_a_lease_releases_the_project_at_once_and_only_once()
    {
        var activity = new OrchestrationActivity();
        var first = activity.TryEnter(ProjectId, ProjectActivityKind.Run)!;
        var second = activity.TryEnter(ProjectId, ProjectActivityKind.Run)!;

        // Disposing one lease twice must not release the other run's hold.
        first.Dispose();
        first.Dispose();
        Assert.Equal(ProjectActivityKind.Run, activity.Current(ProjectId));
        Assert.Null(activity.TryEnter(ProjectId, ProjectActivityKind.Debate));

        second.Dispose();
        Assert.False(activity.IsBusy(ProjectId));
        var debate = activity.TryEnter(ProjectId, ProjectActivityKind.Debate)!;
        debate.Dispose();
        debate.Dispose();
        Assert.Null(activity.Current(ProjectId));
        Assert.NotNull(activity.TryEnter(ProjectId, ProjectActivityKind.Audit));
    }

    [Fact]
    public async Task Of_many_debates_and_audits_asking_at_once_exactly_one_holds_the_project()
    {
        var activity = new OrchestrationActivity();
        using var start = new ManualResetEventSlim();
        var leases = new IDisposable?[64];
        var askers = Enumerable.Range(0, leases.Length)
            .Select(i => Task.Run(
                () =>
                {
                    start.Wait(Ct);
                    leases[i] = activity.TryEnter(ProjectId, i % 2 == 0 ? ProjectActivityKind.Debate : ProjectActivityKind.Audit);
                },
                Ct))
            .ToArray();

        start.Set();
        await Task.WhenAll(askers);

        var held = Assert.Single(leases, l => l is not null);
        Assert.NotNull(activity.Current(ProjectId));
        held!.Dispose();
        Assert.False(activity.IsBusy(ProjectId));
    }

    [Fact]
    public void An_unknown_kind_or_no_project_is_refused()
    {
        var activity = new OrchestrationActivity();

        Assert.Throws<ArgumentOutOfRangeException>(() => activity.TryEnter(ProjectId, (ProjectActivityKind)42));
        Assert.Throws<ArgumentNullException>(() => activity.TryEnter(null!, ProjectActivityKind.Debate));
        Assert.Throws<ArgumentNullException>(() => activity.Current(null!));
        Assert.Throws<ArgumentNullException>(() => activity.IsBusy(null!));
        Assert.False(activity.IsBusy(ProjectId));
    }

    // ---- the orchestrator does not plan, start or resume while a debate or an audit holds the project ----

    [Theory]
    [InlineData(ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Audit)]
    public async Task The_orchestrator_does_not_plan_while_a_debate_or_an_audit_holds_the_project(ProjectActivityKind holder)
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan() });
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        var held = activity.TryEnter(ProjectId, holder)!;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.PlanAsync(harness.PlanningRequest(), null, Ct));

        Assert.Contains(holder.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Entries);
        Assert.Empty(harness.Claude.Starts);
        Assert.Equal(holder, activity.Current(ProjectId));

        held.Dispose();
        var proposal = await orchestrator.PlanAsync(harness.PlanningRequest(), null, Ct);
        Assert.NotNull(proposal);
        Assert.Single(harness.Claude.Starts);
        Assert.False(activity.IsBusy(ProjectId));
    }

    [Theory]
    [InlineData(ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Audit)]
    public async Task The_orchestrator_does_not_start_a_run_while_a_debate_or_an_audit_holds_the_project_and_writes_nothing(ProjectActivityKind holder)
    {
        using var harness = new EngineHarness();
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        var held = activity.TryEnter(ProjectId, holder)!;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartAsync(harness.Request(Plan(Work("T1"))), Ct));

        Assert.Contains(holder.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Entries);
        Assert.Empty(harness.Worktrees.Calls);
        Assert.Empty(await harness.Runs.ListAsync(ProjectId, Ct));
        Assert.Empty(harness.Claude.Starts);

        held.Dispose();
        await using var run = await orchestrator.StartAsync(harness.Request(Plan(Work("T1"))), Ct);
        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
    }

    [Theory]
    [InlineData(ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Audit)]
    public async Task The_orchestrator_does_not_resume_a_run_while_a_debate_or_an_audit_holds_the_project(ProjectActivityKind holder)
    {
        using var harness = new EngineHarness();
        var stored = new RunId("20261019-c0ffee");
        await harness.StoreStartedRunAsync(stored, Plan(Work("T1")));
        var entries = harness.Entries.Count;
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        var held = activity.TryEnter(ProjectId, holder)!;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ResumeAsync(harness.Project, stored, Ct));

        Assert.Contains(holder.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.Equal(entries, harness.Entries.Count);
        Assert.Empty(harness.Claude.Starts);

        held.Dispose();
        await using var resumed = await orchestrator.ResumeAsync(harness.Project, stored, Ct);
        Assert.Equal(RunPhase.Succeeded, (await resumed.CompleteAsync(harness)).Phase);
    }

    [Fact]
    public async Task A_debate_cannot_enter_while_a_planning_goes_on_and_can_once_it_ends()
    {
        using var harness = new EngineHarness();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan(), Gate = gate, Started = started });
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);

        var planning = orchestrator.PlanAsync(harness.PlanningRequest(), null, Ct);
        await started.Task.WaitAsync(RunTestSupport.Guard, Ct);

        Assert.Equal(ProjectActivityKind.Planning, activity.Current(ProjectId));
        Assert.Null(activity.TryEnter(ProjectId, ProjectActivityKind.Debate));
        gate.SetResult();
        await planning;
        using var debate = activity.TryEnter(ProjectId, ProjectActivityKind.Debate);
        Assert.NotNull(debate);
    }

    [Fact]
    public async Task An_audit_cannot_enter_while_a_run_waits_for_a_person()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { StructuredOutput = AgentScript.Report(status: "blocked", summary: "Necesito una decisión.", openIssues: ["¿Qué nombre?"]) });
        var activity = new OrchestrationActivity();

        await using var run = await harness.WithActivity(activity).StartAsync(harness.Request(Plan(Work("T1"))), Ct);
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);

        Assert.Equal(ProjectActivityKind.Run, activity.Current(ProjectId));
        Assert.Null(activity.TryEnter(ProjectId, ProjectActivityKind.Audit));
        await run.CancelAsync("fin de la prueba");
        await run.CompleteAsync(harness);
        await RunTestSupport.WaitUntilAsync(() => !activity.IsBusy(ProjectId), "the project is no longer busy");
        using var audit = activity.TryEnter(ProjectId, ProjectActivityKind.Audit);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task A_run_releases_its_project_before_its_end_can_be_seen_so_a_debate_can_enter_at_once()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed());
        var activity = new OrchestrationActivity();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        guard.CancelAfter(TimeSpan.FromMinutes(2));

        await using var run = await harness.WithActivity(activity).StartAsync(harness.Request(Plan(Work("T1"))), Ct);
        await foreach (var _ in run.Events.WithCancellation(guard.Token))
        {
        }

        // What follows a run (the auditor, the memory, a debate the user opens) finds the project free as soon as the end of
        // the run can be seen.
        Assert.Null(activity.Current(ProjectId));
        using var debate = activity.TryEnter(ProjectId, ProjectActivityKind.Debate);
        Assert.NotNull(debate);
        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
    }

    // ---- composition ----

    [Fact]
    public async Task The_container_gives_the_orchestrator_the_memory_and_the_debates_one_activity_without_a_cycle()
    {
        using var harness = new EngineHarness();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan(), Gate = gate, Started = started });
        var services = new ServiceCollection()
            .AddSingleton<IDecisionRecorder>(harness.Decisions)
            .AddSingleton<AgentStudio.Application.Persistence.IEvidenceStore>(harness.Evidence)
            .AddSingleton<AgentStudio.Application.Persistence.IPlanStore>(harness.Plans)
            .AddSingleton<AgentStudio.Application.Persistence.IRunRegistry>(harness.Runs)
            .AddSingleton<AgentStudio.Application.Persistence.IRequirementStore>(harness.RequirementStore)
            .AddSingleton<AgentStudio.Application.Persistence.IAgentRoster>(harness.Roster)
            .AddSingleton<AgentStudio.Application.Providers.IProjectAgentProviders>(harness.Providers)
            .AddSingleton<AgentStudio.Application.Git.IWorktreeManager>(harness.Worktrees)
            .AddSingleton<AgentStudio.Application.Verification.IVerificationRunner>(harness.Verification)
            .AddSingleton<AgentStudio.Application.Git.IIntegrator>(harness.Integrator)
            .AddSingleton<AgentStudio.Application.Judging.IIndependentReview>(harness.Review)
            .AddSingleton<AgentStudio.Application.Security.IApprovalGate>(harness.Approvals)
            .AddSingleton(harness.Skills)
            .AddSingleton(harness.Paths)
            .AddSingleton<TimeProvider>(harness.Time)
            .AddSingleton<IContextPacker>(sp => new PackerThatNeedsActivity(sp.GetRequiredService<IRunActivity>()))
            .AddSingleton(sp => new CoordinatorThatNeedsActivity(sp.GetRequiredService<IProjectActivity>()));
        AgentStudio.Orchestration.OrchestrationServiceCollectionExtensions.AddAgentStudioOrchestration(services);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var orchestrator = Assert.IsType<Orchestrator>(provider.GetRequiredService<IOrchestrator>());
        var activity = provider.GetRequiredService<IProjectActivity>();
        var coordinator = provider.GetRequiredService<CoordinatorThatNeedsActivity>();
        var packer = (PackerThatNeedsActivity)provider.GetRequiredService<IContextPacker>();

        Assert.IsType<OrchestrationActivity>(activity);
        Assert.Same(activity, provider.GetRequiredService<IRunActivity>());
        Assert.Same(activity, provider.GetRequiredService<OrchestrationActivity>());
        Assert.Same(activity, orchestrator.Activity);
        Assert.Same(activity, packer.Activity);
        Assert.Same(activity, coordinator.Activity);

        // A debate the coordinator holds keeps the orchestrator out and the memory waiting; a planning keeps the debate out.
        var debate = coordinator.Activity.TryEnter(harness.Project.ProjectId, ProjectActivityKind.Debate)!;
        Assert.True(packer.Activity.IsBusy(harness.Project.ProjectId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.PlanAsync(harness.PlanningRequest(), null, Ct));
        debate.Dispose();

        var planning = orchestrator.PlanAsync(harness.PlanningRequest(), null, Ct);
        await started.Task.WaitAsync(RunTestSupport.Guard, Ct);
        Assert.Null(coordinator.Activity.TryEnter(harness.Project.ProjectId, ProjectActivityKind.Debate));
        gate.SetResult();
        await planning;
        Assert.False(packer.Activity.IsBusy(harness.Project.ProjectId));
    }

    private static string ValidPlan() =>
        PlanJson.Plan(
            PlanJson.Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"], steps: ["build", "test"]),
            PlanJson.Task("I1", kind: "Integration", agent: null, workspace: "None", dependsOn: [PlanJson.Dependency("T1")], steps: ["build", "test"]),
            PlanJson.Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [PlanJson.Dependency("I1")])).ToJsonString();

    /// <summary>A packer that, like the real one through the memory, needs the activity when it is built.</summary>
    private sealed class PackerThatNeedsActivity(IRunActivity activity) : IContextPacker
    {
        public IRunActivity Activity { get; } = activity;

        public Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Nobody here has ReadSelected: nothing is packed.");
    }

    /// <summary>What the coordinator of debates and the audits need of the activity: the same instance, built without a cycle.</summary>
    private sealed class CoordinatorThatNeedsActivity(IProjectActivity activity)
    {
        public IProjectActivity Activity { get; } = activity;
    }
}

/// <summary>How the activity tests compose the engine.</summary>
internal static class ActivityHarness
{
    /// <summary>The orchestrator of the harness reporting to <paramref name="activity"/>, shared with whoever else holds projects.</summary>
    public static Orchestrator WithActivity(this EngineHarness harness, IProjectActivity activity) =>
        new(harness.Decisions, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers, harness.Worktrees, harness.Verification, harness.Integrator, harness.ReviewPort ?? harness.Review, harness.Approvals, harness.Skills, harness.Paths, harness.Time, harness.WriteConfinement, activity: activity);
}
