using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

public sealed class PersistenceRegistrationTests : IDisposable
{
    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The container of the app, with the anchors of the logs in a folder of the test's own.</summary>
    private ServiceProvider Build(Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(_anchors));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static TaskAssignedPayload Payload() => new(Tid("T1"), Aid("ejecutor")) { RunId = "20261015-a1b2c3" };

    [Fact]
    public async Task The_workspace_every_store_and_the_recorder_resolve_to_the_sqlite_implementations()
    {
        await using var provider = Build();

        Assert.IsType<ProjectWorkspace>(provider.GetRequiredService<IProjectWorkspace>());
        Assert.IsType<SqliteDecisionLogStore>(provider.GetRequiredService<IDecisionLogStore>());
        Assert.IsType<SqliteEvidenceStore>(provider.GetRequiredService<IEvidenceStore>());
        Assert.IsType<SqliteRequirementStore>(provider.GetRequiredService<IRequirementStore>());
        Assert.IsType<SqlitePlanStore>(provider.GetRequiredService<IPlanStore>());
        Assert.IsType<SqliteRunRegistry>(provider.GetRequiredService<IRunRegistry>());
        Assert.IsType<SqliteVerdictStore>(provider.GetRequiredService<IVerdictStore>());
        Assert.IsType<SqliteAgentRoster>(provider.GetRequiredService<IAgentRoster>());
        Assert.IsType<DecisionRecorder>(provider.GetRequiredService<IDecisionRecorder>());
        Assert.Same(provider.GetRequiredService<IDecisionRecorder>(), provider.GetRequiredService<IDecisionRecorder>());
    }

    [Fact]
    public async Task A_project_opened_through_the_container_is_written_by_the_recorder_on_the_sqlite_log()
    {
        var clock = new FakeTimeProvider(T0);
        await using var provider = Build(s => s.AddSingleton<TimeProvider>(clock));

        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);
        var entry = await provider.GetRequiredService<IDecisionRecorder>().RecordAsync(project.ProjectId, System, DecisionKind.TaskAssigned, "Asignada", Payload(), cancellationToken: Ct);

        Assert.Equal(T0, entry.RecordedAt);
        Assert.Equal([entry], await provider.GetRequiredService<IDecisionLogStore>().ReadAllAsync(project.ProjectId, Ct));
        Assert.Equal(_folder.DatabasePath, provider.GetRequiredService<ProjectDatabases>().Get(project.ProjectId).DatabasePath);
    }

    [Fact]
    public async Task The_recorder_anchors_the_head_of_each_log_in_the_folder_the_composition_gives()
    {
        await using var provider = Build();

        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);
        var entry = await provider.GetRequiredService<IDecisionRecorder>().RecordAsync(project.ProjectId, System, DecisionKind.TaskAssigned, "Asignada", Payload(), cancellationToken: Ct);

        Assert.IsType<FileDecisionLogAnchorStore>(provider.GetRequiredService<IDecisionLogAnchorStore>());
        Assert.Equal(new DecisionLogAnchor(1, entry.Hash), await provider.GetRequiredService<IDecisionLogAnchorStore>().ReadAsync(project.ProjectId, Ct));
        Assert.Single(Directory.GetFiles(_anchors));
    }

    [Fact]
    public async Task A_history_is_accepted_as_a_new_origin_by_the_same_recorder_that_writes_the_log()
    {
        await using var provider = Build();

        // One writer per log: a second recorder would have a lock of its own.
        Assert.Same(provider.GetRequiredService<IDecisionRecorder>(), provider.GetRequiredService<IDecisionLogOrigin>());
    }

    [Fact]
    public async Task Without_a_folder_the_anchors_go_to_the_local_application_data_of_agent_studio()
    {
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence();
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(DecisionLogAnchorOptions.ForCurrentUser(), provider.GetRequiredService<DecisionLogAnchorOptions>());
    }

    [Fact]
    public async Task Shutting_the_host_down_releases_the_project_for_the_next_instance()
    {
        string id;
        await using (var first = Build())
        {
            id = (await first.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct)).ProjectId;
        }

        await using var second = Build();
        var reopened = await second.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);

        Assert.Equal(id, reopened.ProjectId);
    }

    [Fact]
    public async Task A_project_closed_is_forgotten_by_the_recorder_so_the_first_entry_after_opening_it_again_rests_on_the_whole_log_read_again()
    {
        WholeReads? store = null;
        await using var provider = Build(s => s.AddSingleton<IDecisionLogStore>(p => store = new WholeReads(new SqliteDecisionLogStore(p.GetRequiredService<ProjectDatabases>()))));
        var workspace = provider.GetRequiredService<IProjectWorkspace>();
        var recorder = provider.GetRequiredService<IDecisionRecorder>();
        var project = await workspace.OpenAsync(_folder.Root, Ct);
        await recorder.RecordAsync(project.ProjectId, System, DecisionKind.TaskAssigned, "Asignada", Payload(), cancellationToken: Ct);
        await recorder.RecordAsync(project.ProjectId, System, DecisionKind.TaskAssigned, "Asignada otra vez", Payload(), cancellationToken: Ct);
        Assert.Equal(1, store!.Count); // the second entry was appended to the log the recorder holds

        provider.GetRequiredService<ProjectDatabases>().Close(project.ProjectId);
        var reopened = await workspace.OpenAsync(_folder.Root, Ct);
        var third = await recorder.RecordAsync(reopened.ProjectId, System, DecisionKind.TaskAssigned, "Tras abrirlo otra vez", Payload(), cancellationToken: Ct);

        Assert.Equal(2, store.Count);
        Assert.Equal(3, third.Sequence);
        Assert.True((await recorder.LoadAsync(reopened.ProjectId, Ct)).Verification.IsValid);
    }

    /// <summary>The real store, counting the reads of the whole log (every one, or the tail read with no mark).</summary>
    private sealed class WholeReads(SqliteDecisionLogStore inner) : IDecisionLogStore, IDecisionLogTailReader
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken) => inner.AppendAsync(projectId, entry, cancellationToken);

        public Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return inner.ReadAllAsync(projectId, cancellationToken);
        }

        public Task<DecisionLogTail> ReadTailAsync(string projectId, DecisionLogMark? after, CancellationToken cancellationToken)
        {
            if (after is null)
            {
                Interlocked.Increment(ref _count);
            }

            return inner.ReadTailAsync(projectId, after, cancellationToken);
        }
    }
}
