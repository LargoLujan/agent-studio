using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Graph;
using AgentStudio.Tests.Domain.Orchestration;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Orchestration.Graph;

/// <summary>
/// docs/20 D-22 and F6-5 (WP-G): from a folder of the graph to the tasks whose attempts changed files under it, read from the records the engine anchored in the
/// decision log: the latest attempt of each task counts, a record is read only as the log anchored it, and a log that does not verify gives nothing.
/// </summary>
public sealed class GraphTaskIndexTests
{
    private static readonly ProjectContext Project = new("6f1c2b0e-0000-4000-8000-0000000000ab", @"C:\repo");

    private static readonly PlanDefinition Plan = PlanFixtures.Plan();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World
    {
        private readonly List<DecisionEntry> _entries = [];
        private readonly Dictionary<EvidenceId, byte[]> _content = [];
        private int _evidence;

        public bool LogIsValid { get; set; } = true;

        public GraphTaskIndex Index => new(new Recorder(this), new Evidence(this), new Plans());

        public void Run(string run, string planHash = "plan-1") =>
            Add(DecisionKind.RunStarted, new RunStartedPayload(planHash, HashA, CommitA, null, HashB, [], null) { RunId = run });

        public void Attempt(string run, string task, int attempt, string[] paths, string? tamperedHash = null, string? json = null)
        {
            var record = json ?? "{\"v\":1,\"kind\":\"agent-studio/attempt-record\",\"run\":\"" + run + "\",\"task\":\"" + task + "\",\"attempt\":" + attempt
                + ",\"outcome\":\"Succeeded\",\"attemptBase\":null,\"head\":null,\"changedPaths\":[" + string.Join(',', paths.Select(p => "\"" + p + "\"")) + "],\"outputs\":[],\"tests\":null,\"reportEvidence\":null,\"reportSha256\":null}";
            var bytes = Encoding.UTF8.GetBytes(record);
            var id = new EvidenceId("ev-" + ++_evidence);
            _content[id] = bytes;
            Add(
                DecisionKind.EvidenceRegistered,
                new EvidenceRegisteredPayload(id, EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, tamperedHash ?? ContentHash.Sha256Hex(bytes), new TaskId(task), attempt, "Registro del intento " + task + " #" + attempt)
                {
                    RunId = run,
                });
        }

        private void Add(DecisionKind kind, DecisionPayload payload) =>
            _entries.Add(new DecisionEntry(_entries.Count + 1, DateTimeOffset.UnixEpoch.AddSeconds(_entries.Count), new Actor(ActorKind.System, "agent-studio"), kind, "x", DecisionPayloadSerializer.Serialize(payload), null, HashA, HashB));

        private sealed class Recorder(World world) : IDecisionRecorder
        {
            public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) =>
                Task.FromResult(new DecisionLogState(world._entries, new DecisionLogVerification(world.LogIsValid, null, null)));

            public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => LoadAsync(projectId, cancellationToken);

            public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class Evidence(World world) : IEvidenceStore
        {
            public Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken) =>
                Task.FromResult<Stream>(new MemoryStream(world._content[id]));

            public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class Plans : IPlanStore
        {
            public Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken cancellationToken) =>
                Task.FromResult<PlanDefinition?>(hash == "plan-1" ? Plan : null);
        }
    }

    [Fact]
    public async Task The_tasks_that_changed_files_under_a_folder_come_with_their_title_and_only_the_paths_under_it()
    {
        var world = new World();
        world.Run("20261112-aaaaaa");
        world.Attempt("20261112-aaaaaa", "T1", 1, ["src/Calc/Mul.cs", "src/Other/X.cs", "docs/a.md"]);
        world.Attempt("20261112-aaaaaa", "T2", 1, ["src/Other/X.cs"]);

        var tasks = await world.Index.TasksUnderAsync(Project, "src/Calc", Ct);

        var task = Assert.Single(tasks);
        Assert.Equal((new RunId("20261112-aaaaaa"), new TaskId("T1"), "Task T1"), (task.Run, task.Task, task.Title));
        Assert.Equal(["src/Calc/Mul.cs"], task.ChangedPaths);
    }

    [Fact]
    public async Task The_whole_project_is_the_empty_folder_and_a_folder_is_not_a_prefix_of_a_name()
    {
        var world = new World();
        world.Run("20261112-aaaaaa");
        world.Attempt("20261112-aaaaaa", "T1", 1, ["src/Calc/Mul.cs"]);
        world.Attempt("20261112-aaaaaa", "T2", 1, ["src/Calculator/Other.cs"]);

        Assert.Equal(["T1", "T2"], (await world.Index.TasksUnderAsync(Project, string.Empty, Ct)).Select(t => t.Task.Value));
        Assert.Equal(["T1"], (await world.Index.TasksUnderAsync(Project, "src/Calc", Ct)).Select(t => t.Task.Value));
    }

    [Fact]
    public async Task Only_the_latest_attempt_of_a_task_counts()
    {
        var world = new World();
        world.Run("20261112-aaaaaa");
        world.Attempt("20261112-aaaaaa", "T1", 1, ["src/Calc/Old.cs"]);
        world.Attempt("20261112-aaaaaa", "T1", 2, ["src/Calc/New.cs"]);

        var task = Assert.Single(await world.Index.TasksUnderAsync(Project, "src/Calc", Ct));

        Assert.Equal(["src/Calc/New.cs"], task.ChangedPaths);
    }

    [Fact]
    public async Task A_task_of_a_plan_that_cannot_be_found_is_named_by_its_id()
    {
        var world = new World();
        world.Run("20261112-aaaaaa", planHash: "plan-missing");
        world.Attempt("20261112-aaaaaa", "T1", 1, ["src/Calc/Mul.cs"]);

        Assert.Equal("T1", Assert.Single(await world.Index.TasksUnderAsync(Project, "src", Ct)).Title);
    }

    [Fact]
    public async Task A_record_that_is_not_what_the_log_anchored_is_not_read()
    {
        var world = new World();
        world.Run("20261112-aaaaaa");
        world.Attempt("20261112-aaaaaa", "T1", 1, ["src/Calc/Mul.cs"], tamperedHash: new string('f', 64));
        world.Attempt("20261112-aaaaaa", "T2", 1, ["src/Calc/Ok.cs"]);

        Assert.Equal(["T2"], (await world.Index.TasksUnderAsync(Project, "src", Ct)).Select(t => t.Task.Value));
    }

    [Fact]
    public async Task A_record_of_another_task_than_the_one_it_was_registered_for_or_that_does_not_parse_is_not_read()
    {
        var world = new World();
        world.Run("20261112-aaaaaa");
        world.Attempt("20261112-aaaaaa", "T1", 1, [], json: "{\"v\":1,\"kind\":\"agent-studio/attempt-record\",\"run\":\"20261112-aaaaaa\",\"task\":\"T9\",\"attempt\":1,\"outcome\":\"Succeeded\",\"attemptBase\":null,\"head\":null,\"changedPaths\":[\"src/a.cs\"],\"outputs\":[],\"tests\":null,\"reportEvidence\":null,\"reportSha256\":null}");
        world.Attempt("20261112-aaaaaa", "T2", 1, [], json: "not json");

        Assert.Empty(await world.Index.TasksUnderAsync(Project, string.Empty, Ct));
    }

    [Fact]
    public async Task A_path_that_is_not_safe_never_reaches_the_screen()
    {
        var world = new World();
        world.Run("20261112-aaaaaa");
        world.Attempt("20261112-aaaaaa", "T1", 1, ["src/ok.cs", "..\\\\..\\\\secret.cs", "C:\\\\Windows\\\\x.cs"]);

        var task = Assert.Single(await world.Index.TasksUnderAsync(Project, string.Empty, Ct));

        Assert.Equal(["src/ok.cs"], task.ChangedPaths);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_gives_no_task_and_a_folder_that_leads_out_is_refused()
    {
        var world = new World();
        world.Run("20261112-aaaaaa");
        world.Attempt("20261112-aaaaaa", "T1", 1, ["src/Calc/Mul.cs"]);
        world.LogIsValid = false;

        Assert.Empty(await world.Index.TasksUnderAsync(Project, string.Empty, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Index.TasksUnderAsync(Project, "../outside", Ct));
    }
}
