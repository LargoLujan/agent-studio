using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Work;
using AgentStudio.Orchestration.Work;
using AgentStudio.Tests.Orchestration.WorkTracking;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>A workspace that gives the project of the world: the one a real shell would open from the path of its folder.</summary>
internal sealed class WorldWorkspace(ProjectContext project) : IProjectWorkspace
{
    public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(project);
}

/// <summary>
/// A real project (SQLite, the decision log with its hash chain, the services of Phase 9 on top: milestones, backlog, board, trace links,
/// the chain and the participation) with the «Trabajo» document over it, as the composition root wires it. What a test records in the
/// log with <see cref="WorkWorld"/> is what the document reads: no section is given a list of its own.
/// </summary>
internal sealed class WorkTestWorld : IDisposable
{
    private readonly TempDirectory _budgets = new();

    private WorkTestWorld(WorkWorld world, Func<WorkWorld, WorkBackend, WorkBackend>? tweak)
    {
        World = world;
        Time = new FakeTimeProvider(AgentStudio.Tests.Domain.TestData.T0);
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Budgets = new LocalProjectBudgetStore(Path.Combine(_budgets.Path, "budgets"));
        var backend = new WorkBackend(
            new WorldWorkspace(world.Project),
            world.Milestones,
            null,
            world.Backlog,
            world.Board,
            world.Trace,
            null,
            new TraceChainReader(world.Instance.Recorder, world.Instance.Requirements, world.Instance.Plans, world.Instance.Verdicts),
            new ParticipationProjector(world.Instance.Recorder, world.Instance.Verdicts),
            Budgets,
            null,
            world.Instance.Runs,
            null,
            world.Instance.Evidence,
            world.Instance.Verdicts);
        Backend = tweak is null ? backend : tweak(world, backend);
        Document = new WorkDocumentViewModel(Backend, Time);
    }

    public WorkWorld World { get; }

    public FakeTimeProvider Time { get; }

    public LocalProjectBudgetStore Budgets { get; }

    public WorkBackend Backend { get; }

    public WorkDocumentViewModel Document { get; }

    public static async Task<WorkTestWorld> OpenAsync(Func<WorkWorld, WorkBackend, WorkBackend>? tweak = null)
    {
        var world = await WorkWorld.OpenAsync();
        var test = new WorkTestWorld(world, tweak);
        await test.Document.SetProjectAsync(world.Instance.Root);
        return test;
    }

    public void Dispose()
    {
        World.Dispose();
        _budgets.Dispose();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
