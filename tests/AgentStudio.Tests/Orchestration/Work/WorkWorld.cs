using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Work;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Infrastructure.Persistence;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>A project that is busy or idle, as the test says.</summary>
internal sealed class SwitchActivity : IRunActivity
{
    public volatile bool Busy;

    public bool IsBusy(string projectId) => Busy;
}

/// <summary>
/// A real project (SQLite, the decision log with its hash chain) with the Phase 9 services on top, and what it takes to write the log of a
/// run the way the engine does: only the entries the board and the trace read.
/// </summary>
internal sealed class WorkWorld : IDisposable
{
    public const string Run1 = "20261111-a1b2c3";

    private readonly TempProjectFolder _folder;

    private WorkWorld(TempProjectFolder folder, ProjectInstance instance, TimeProvider gateTime)
    {
        _folder = folder;
        Instance = instance;
        Activity = new SwitchActivity();
        Gate = new WorkWriteGate(Activity, gateTime);
        Registry = new SqliteMilestoneRegistry(instance.Databases);
        BacklogStore = new SqliteBacklogStore(instance.Databases);
        Milestones = new MilestoneService(instance.Recorder, Registry, instance.Requirements, instance.Evidence, Gate, instance.Clock);
        Backlog = new BacklogService(instance.Recorder, BacklogStore, Gate, instance.Clock);
        Trace = new TraceLinkService(instance.Recorder, Gate, instance.Clock);
        Board = new BoardProjector(instance.Recorder, Milestones, Backlog, instance.Requirements, instance.Runs, instance.Plans, instance.Verdicts);
    }

    public ProjectInstance Instance { get; }

    public SwitchActivity Activity { get; }

    public WorkWriteGate Gate { get; }

    public SqliteMilestoneRegistry Registry { get; }

    public SqliteBacklogStore BacklogStore { get; }

    public MilestoneService Milestones { get; }

    public BacklogService Backlog { get; }

    public TraceLinkService Trace { get; }

    public BoardProjector Board { get; }

    public AgentStudio.Application.Projects.ProjectContext Project => Instance.Project;

    public string ProjectId => Instance.ProjectId;

    public static async Task<WorkWorld> OpenAsync(TimeProvider? gateTime = null)
    {
        var folder = new TempProjectFolder();
        var instance = await ProjectInstance.OpenAsync(folder.Root);
        var world = new WorkWorld(folder, instance, gateTime ?? instance.Clock);
        return world;
    }

    public void Dispose()
    {
        Instance.Dispose();
        _folder.Dispose();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task<MilestoneView> OpenMilestoneAsync(string name = "Primera entrega", string objective = "Que multiplique.", FrozenRequirementSet? requirements = null, WorkBudget? budget = null) =>
        Milestones.OpenAsync(new OpenMilestoneRequest(Project, name, objective, requirements ?? PlanFixtures.Requirements(), budget, "manuel"), Ct);

    /// <summary>Stores a run header and records its start, as the engine does, and returns its id.</summary>
    public async Task<string> StartRunAsync(string id = Run1, string? follows = null)
    {
        var record = await SeedRunAsync(Instance, id, follows is null ? null : new RunId(follows));
        await Instance.Runs.CreateAsync(record, Ct);
        await RecordAsync(id, DecisionKind.RunStarted, new RunStartedPayload(record.PlanHash, record.RequirementSetHash, record.BaseCommit, record.BaseRef, "p", [], follows) { RunId = id });
        return id;
    }

    public Task TransitionAsync(string run, string task, WorkTaskStatus from, WorkTaskStatus to, string? blockReason = null) =>
        RecordAsync(run, DecisionKind.TaskStatusChanged, new TaskStatusChangedPayload(Tid(task), from, to, "test", blockReason, to == WorkTaskStatus.Completed ? [Eid("E1")] : [], 1) { RunId = run });

    public async Task RunAndCompleteAsync(string run, string task)
    {
        await TransitionAsync(run, task, WorkTaskStatus.Pending, WorkTaskStatus.Running);
        await TransitionAsync(run, task, WorkTaskStatus.Running, WorkTaskStatus.Completed);
    }

    public Task FinishAsync(string run, RunPhase phase) =>
        RecordAsync(run, DecisionKind.RunFinished, new RunFinishedPayload(phase, "fin", null, null) { RunId = run });

    public Task DeliverAsync(string run) =>
        RecordAsync(run, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, "main", [], new string('a', 40), [], true) { RunId = run });

    /// <summary>A verdict of the run: stored (valid or not) and announced in the log (valid or not).</summary>
    public async Task<string> VerdictAsync(string run, VerdictDecision decision, bool validInLog = true, bool validInStore = true, string id = "v1", bool store = true)
    {
        var verdict = ValidVerdict(Dossier(), decision);
        if (store)
        {
            await Instance.Verdicts.AppendAsync(ProjectId, new StoredVerdict(id, verdict, run, "T7", AuditScope.CurrentPlan, validInStore, [], null), Ct);
        }

        await RecordAsync(run, DecisionKind.VerdictIssued, new VerdictIssuedPayload(id, null, decision, validInLog, [], verdict.RequirementSetHash, verdict.DecisionLogHeadHash, null, null) { RunId = run });
        return id;
    }

    public Task RecordAsUserAsync(DecisionKind kind, DecisionPayload payload) =>
        Instance.Recorder.RecordAsync(ProjectId, new Actor(ActorKind.User, "manuel"), kind, "test", payload, null, Ct);

    public Task RecordAsync(string run, DecisionKind kind, DecisionPayload payload) =>
        Instance.Recorder.RecordAsync(ProjectId, new Actor(ActorKind.System, "agent-studio"), kind, "test", payload, null, Ct);

    /// <summary>An entry of the log that gives a unit its id (a run or a debate to link), without anything else of it.</summary>
    public Task MentionUnitAsync(string unit) =>
        RecordAsync(unit, DecisionKind.AuditAlertRaised, new AuditAlertPayload("test.unit", [], "a unit of the log") { RunId = unit });
}
