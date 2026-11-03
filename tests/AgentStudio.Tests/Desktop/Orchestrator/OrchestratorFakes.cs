using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// An <see cref="IOrchestrationRun"/> driven by the test: <see cref="Emit"/> writes events to the single-consumer stream,
/// <see cref="Finish"/> ends it with a result and <see cref="Fail"/> with an error. It records the commands it receives.
/// No engine behind it: the monitor must show exactly what it is told.
/// </summary>
internal sealed class FakeRun : IOrchestrationRun
{
    private readonly Channel<OrchestrationEvent> _events = Channel.CreateUnbounded<OrchestrationEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<RunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeRun(RunSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        Id = snapshot.Id;
    }

    public RunId Id { get; }

    /// <summary>What <see cref="Snapshot"/> returns; a test changes it to move the run's state forward.</summary>
    public RunSnapshot CurrentSnapshot { get; set; }

    public ConcurrentQueue<string> Calls { get; } = new();

    public bool Disposed { get; private set; }

    public IAsyncEnumerable<OrchestrationEvent> Events => _events.Reader.ReadAllAsync();

    public Task<RunResult> Completion => _completion.Task;

    public RunSnapshot Snapshot() => CurrentSnapshot;

    public Task PauseAsync()
    {
        Calls.Enqueue("pause");
        return Task.CompletedTask;
    }

    public Task ResumeDispatchAsync()
    {
        Calls.Enqueue("resume");
        return Task.CompletedTask;
    }

    public Task RetryBlockedAsync(TaskId task)
    {
        Calls.Enqueue($"retry:{task}");
        return Task.CompletedTask;
    }

    public Task CancelAsync(string reason)
    {
        Calls.Enqueue($"cancel:{reason}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Emit(params OrchestrationEvent[] events)
    {
        foreach (var e in events)
        {
            Assert.True(_events.Writer.TryWrite(e));
        }
    }

    public void Finish(RunResult result)
    {
        _events.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    public void Fail(Exception error)
    {
        _events.Writer.TryComplete();
        _completion.TrySetException(error);
    }

    public static TaskView View(string id, WorkTaskKind kind = WorkTaskKind.AgentWork, WorkTaskStatus status = WorkTaskStatus.Pending, string? blockReason = null) =>
        new(new TaskId(id), $"Tarea {id}", kind, status, null, Attempt: 1, RetryCount: 0, WorktreePath: null, Ref: null, blockReason, []);

    public static GraphHealthReport Healthy { get; } = new([], [], [], false);

    public static RunSnapshot SnapshotOf(RunPhase phase, params TaskView[] tasks) =>
        new(new RunId("20260101-abc123"), phase, tasks, Healthy, AgentRunsUsed: 0);
}

/// <summary>
/// An <see cref="IDeliveryService"/> that answers what the test sets and records what it was asked: the checks
/// (project and run) and the deliveries (project, run and mode). No git and no approval behind it.
/// </summary>
internal sealed class FakeDeliveryService : IDeliveryService
{
    public DeliveryReadiness Readiness { get; set; } = new(true, "c0ffee0000000000000000000000000000000000", "El juez aprobó la integración c0ffee0000.");

    public Func<DeliveryResult> Result { get; set; } = () => new(true, "c0ffee0000000000000000000000000000000000", "main", "Entregado: main está en c0ffee0000.");

    public List<(ProjectContext Project, RunId Run)> Checks { get; } = [];

    public List<(ProjectContext Project, RunId Run, DeliveryMode Mode)> Deliveries { get; } = [];

    public Task<DeliveryReadiness> CheckAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        Checks.Add((project, run));
        return Task.FromResult(Readiness);
    }

    public Task<DeliveryResult> DeliverAsync(ProjectContext project, RunId run, DeliveryMode mode, CancellationToken cancellationToken)
    {
        Deliveries.Add((project, run, mode));
        return Task.FromResult(Result());
    }
}

/// <summary>A UI context that only queues: nothing posted runs until <see cref="RunAll"/>, on the test's thread.</summary>
internal sealed class QueueSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public int Pending => _queue.Count;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("The monitor must post, never send.");

    /// <summary>Runs everything queued, with this context as the current one (as a dispatcher does).</summary>
    public void RunAll()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}

/// <summary>A UI context that runs what is posted at once, on the posting thread (whatever context the test runner has).</summary>
internal sealed class InlineSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);

    public override void Send(SendOrPostCallback d, object? state) => d(state);
}

/// <summary>An <see cref="IOrchestrator"/> that answers what the test sets and records what it was asked.</summary>
internal sealed class FakeOrchestrator : IOrchestrator
{
    public Func<PlanningRequest, IProgress<AgentEvent>?, CancellationToken, Task<PlanProposal>> Plan { get; set; } =
        (_, _, _) => throw new InvalidOperationException("No plan was scripted.");

    public Func<RunRequest, IOrchestrationRun> Start { get; set; } = _ => throw new InvalidOperationException("No run was scripted.");

    public Func<RunId, IOrchestrationRun> Resume { get; set; } = _ => throw new InvalidOperationException("No resume was scripted.");

    public List<RunSummary> Runs { get; } = [];

    public List<PlanningRequest> PlanRequests { get; } = [];

    public List<RunRequest> RunRequests { get; } = [];

    public List<RunId> Resumed { get; } = [];

    public Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken)
    {
        PlanRequests.Add(request);
        return Plan(request, progress, cancellationToken);
    }

    public Task<IOrchestrationRun> StartAsync(RunRequest request, CancellationToken cancellationToken)
    {
        RunRequests.Add(request);
        return Task.FromResult(Start(request));
    }

    public Task<IOrchestrationRun> ResumeAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        Resumed.Add(run);
        return Task.FromResult(Resume(run));
    }

    public Task<IReadOnlyList<RunSummary>> ListRunsAsync(ProjectContext project, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunSummary>>([.. Runs]);
}

internal sealed class FakeWorkspace : IProjectWorkspace
{
    public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) =>
        Task.FromResult(new ProjectContext("p-1", projectRoot));
}

/// <summary>
/// Frozen sets in memory, with the semantics of the SQLite store: saving a set whose hash is already stored writes
/// nothing, and the latest set is the last one stored. <see cref="Saved"/> records every save it was asked for.
/// </summary>
internal sealed class FakeRequirementStore(params FrozenRequirementSet[]? history) : IRequirementStore
{
    private readonly List<FrozenRequirementSet> _sets = [.. history ?? []];

    public List<FrozenRequirementSet> Saved { get; } = [];

    /// <summary>When set, every save throws it (and stores nothing).</summary>
    public Exception? SaveFailure { get; set; }

    public Task SaveAsync(string projectId, FrozenRequirementSet requirements, CancellationToken cancellationToken)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        Saved.Add(requirements);
        if (!_sets.Any(s => s.ContentHash == requirements.ContentHash))
        {
            _sets.Add(requirements);
        }

        return Task.CompletedTask;
    }

    public Task<FrozenRequirementSet?> FindAsync(string projectId, string contentHash, CancellationToken cancellationToken) =>
        Task.FromResult(_sets.FirstOrDefault(s => s.ContentHash == contentHash));

    public Task<FrozenRequirementSet?> LatestAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(_sets.LastOrDefault());
}

/// <summary>A roster read from <c>list</c>; what is saved is recorded and, when no list is given, also listed.</summary>
internal sealed class FakeRoster(Func<IReadOnlyList<AgentDefinition>>? list = null) : IAgentRoster
{
    public List<AgentDefinition> Saved { get; } = [];

    /// <summary>When set, every save throws it.</summary>
    public Exception? SaveFailure { get; set; }

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult(list is null ? (IReadOnlyList<AgentDefinition>)[.. Saved] : list());

    public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        Saved.Add(agent);
        return Task.CompletedTask;
    }
}

/// <summary>A profile source with a fixed profile (null: the project has none) and a proposal; it records what is saved.</summary>
internal sealed class FakeProfileSource(VerificationProfile? profile, VerificationProfile? proposal = null) : IVerificationProfileSource
{
    public VerificationProfile? Current { get; private set; } = profile;

    public List<VerificationProfile> Saved { get; } = [];

    /// <summary>When set, loading throws it (a file that cannot be used).</summary>
    public Exception? LoadFailure { get; set; }

    public Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken) =>
        LoadFailure is null ? Task.FromResult(Current) : Task.FromException<VerificationProfile?>(LoadFailure);

    public VerificationProfile? Propose(ProjectContext project) => proposal;

    public Task SaveAsync(ProjectContext project, VerificationProfile profile, CancellationToken cancellationToken)
    {
        Saved.Add(profile);
        Current = profile;
        return Task.CompletedTask;
    }
}

internal sealed class FakePlanStore : IPlanStore
{
    public Dictionary<string, PlanDefinition> Plans { get; } = [];

    public Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.GetValueOrDefault(hash));
}

/// <summary>The backend of the tests: the plan fixtures' requirements and roster, and a profile with build, test and lint.</summary>
internal static class Backends
{
    public static VerificationProfile Profile { get; } = new(
        "pruebas",
        [
            new VerificationStep("build", "Compilar", "dotnet", ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(5)),
            new VerificationStep("test", "Probar", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.Trx, "out.trx", TimeSpan.FromMinutes(5)),
            new VerificationStep("lint", "Analizar", "dotnet", ["format", "--verify-no-changes"], EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(5)),
        ]);

    public static OrchestrationBackend Complete(FakeOrchestrator? orchestrator = null, FakePlanStore? plans = null) =>
        new(orchestrator, new FakeWorkspace(), new FakeRequirementStore(PlanFixtures.Requirements()), new FakeRoster(PlanFixtures.Roster), new FakeProfileSource(Profile), plans);
}
