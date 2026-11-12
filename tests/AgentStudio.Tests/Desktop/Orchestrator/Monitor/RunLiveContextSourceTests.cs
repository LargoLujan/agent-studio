using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Domain.Orchestration;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Monitor;

/// <summary>P52: the template and the dependencies of the plan of a run followed in the background come from what the run stored.</summary>
public sealed class RunLiveContextSourceTests
{
    private static readonly ProjectContext Project = new("proyecto-1", @"C:\proyectos\calc");
    private static readonly RunId Run = new("20261109-a1b2c3");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The stores of the tests of debates, by their full name: the ones of the orchestrator screen share their simple names.
    private static async Task<(AgentStudio.Tests.Debate.Fakes.FakeRunRegistry Runs, AgentStudio.Tests.Debate.Fakes.FakePlanStore Plans, PlanDefinition Plan)> StoredAsync(bool storePlan = true, RunId? id = null)
    {
        var runs = new AgentStudio.Tests.Debate.Fakes.FakeRunRegistry();
        var plans = new AgentStudio.Tests.Debate.Fakes.FakePlanStore();
        var plan = PlanFixtures.Plan();
        if (storePlan)
        {
            await plans.SaveAsync(Project.ProjectId, plan, Ct);
        }

        await runs.CreateAsync(
            new RunRecord(id ?? Run, Project.ProjectId, T0, plan.ContentHash, new string('d', 64), new EvidenceId("E-1"), "abc123", "main", new VerificationProfile("perfil", []), [AgentDef("exec1", "Ejecutor 1"), AgentDef("libre", "Libre")], new RunOptions(), null),
            Ct);
        return (runs, plans, plan);
    }

    [Fact]
    public async Task The_template_comes_from_the_header_of_the_run_and_the_dependencies_from_its_plan()
    {
        var (runs, plans, plan) = await StoredAsync();

        var context = await new RegistryRunLiveContextSource(runs, plans).ReadAsync(Project, Run, Ct);

        Assert.NotNull(context);
        Assert.Equal(["exec1", "libre"], context.Roster.Select(a => a.Id.Value));
        var expected = plan.Tasks.ToDictionary(t => t.Id, t => t.DependsOn.Select(d => d.Prerequisite).ToList());
        Assert.Equal(expected.Keys.Select(k => k.Value).Order(), context.Dependencies.Keys.Select(k => k.Value).Order());
        Assert.All(expected, e => Assert.Equal(e.Value, context.Dependencies[e.Key]));
        Assert.Contains(context.Dependencies, d => d.Value.Count > 0);
    }

    [Fact]
    public async Task A_plan_the_store_does_not_have_leaves_the_template_without_pulses_instead_of_giving_nothing()
    {
        var (runs, plans, _) = await StoredAsync(storePlan: false);

        var context = await new RegistryRunLiveContextSource(runs, plans).ReadAsync(Project, Run, Ct);

        Assert.NotNull(context);
        Assert.Equal(2, context.Roster.Count);
        Assert.Empty(context.Dependencies);
    }

    [Fact]
    public async Task A_run_without_a_header_or_a_store_that_fails_gives_nothing_and_never_throws()
    {
        var (runs, plans, _) = await StoredAsync();

        Assert.Null(await new RegistryRunLiveContextSource(runs, plans).ReadAsync(Project, new RunId("20261109-zzzzzz"), Ct));
        Assert.Null(await new RegistryRunLiveContextSource(new FailingRegistry(), plans).ReadAsync(Project, Run, Ct));
    }

    [Fact]
    public async Task A_cancelled_read_is_not_swallowed()
    {
        var (runs, plans, _) = await StoredAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RegistryRunLiveContextSource(new CancellingRegistry(runs), plans).ReadAsync(Project, Run, cancelled.Token));
    }

    [Fact]
    public async Task The_service_gives_the_monitor_the_free_agents_and_the_pulses_of_the_run_it_follows()
    {
        var (monitor, run, ui, time) = Open(
            View("T1", "exec1", WorkTaskStatus.Completed), View("T2", "exec1", WorkTaskStatus.Completed), View("T3", "exec1"));
        var (runs, plans, plan) = await StoredAsync(id: run.Id);
        await new MonitorServices(liveContext: new RegistryRunLiveContextSource(runs, plans)).GiveLiveContextAsync(monitor, Project, run.Id, Ct);

        Settle(time, ui);

        Assert.Equal(["exec1", "libre"], monitor.Roster.Select(a => a.Id.Value));
        Assert.Equal(AgentLiveStatus.Idle, monitor.Current!.Agents[new AgentId("libre")].Status);
        Assert.NotEmpty(monitor.PlanDependencies);
        Assert.Equal(plan.Tasks.Count, monitor.PlanDependencies.Count);
    }

    private sealed class FailingRegistry : IRunRegistry
    {
        public Task CreateAsync(RunRecord run, CancellationToken cancellationToken) => throw new InvalidOperationException("db");

        public Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken) => throw new InvalidOperationException("db");

        public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => throw new InvalidOperationException("db");
    }

    private sealed class CancellingRegistry(IRunRegistry inner) : IRunRegistry
    {
        public Task CreateAsync(RunRecord run, CancellationToken cancellationToken) => inner.CreateAsync(run, cancellationToken);

        public Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.FindAsync(projectId, id, cancellationToken);
        }

        public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => inner.ListAsync(projectId, cancellationToken);
    }
}
