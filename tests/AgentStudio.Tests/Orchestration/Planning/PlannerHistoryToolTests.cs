using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Memory;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Planning.PlanJson;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>
/// The planner's tool <c>search_project_history</c> (docs/17 D-06, F12-9; ADR-0024, addendum of 2026-11-03), over a fake
/// MCP configuration: the server is a path nothing launches, the index is a fake that gives a snapshot, and the snapshots
/// are real files of <see cref="ProjectHistoryFolders"/> in a temporary folder. A planner with memory on Claude Code gets the
/// server with <c>--history</c> in both of its runs and one sentence in its prompt, and its snapshot is deleted when the
/// planning ends; on Codex, without memory, with nothing trusted or without the tool composed, it plans as before, and the
/// proposal says why. The executors never get it.
/// </summary>
public sealed class PlannerHistoryToolTests : IDisposable
{
    /// <summary>A fake MCP configuration: an absolute path with a space that no test launches.</summary>
    private const string ServerExecutable = @"C:\Agent Studio\AgentStudio.SkillsMcp.exe";

    private static readonly DateTimeOffset At = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(1));

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SnapshotsRoot => _temp.Combine("history-snapshots");

    private static string ValidPlan() =>
        Plan(
            Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"], steps: ["build", "test"]),
            Task("I1", kind: "Integration", agent: null, workspace: "None", dependsOn: [Dependency("T1")], steps: ["build", "test"]),
            Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [Dependency("I1")])).ToJsonString();

    private static HistoryFragment[] History() =>
    [
        new("mem:m1#0", "decisión", At, "Decidimos usar decimal para las operaciones de Calc."),
        new("mem:m2#0", "error conocido", At.AddDays(-1), "El test de división falla con cero."),
    ];

    private (ProjectHistoryTool Tool, SnapshotIndex Index) Tool(params HistoryFragment[] fragments)
    {
        var index = new SnapshotIndex(fragments);
        return (new ProjectHistoryTool(index, new ProjectHistoryFolders(SnapshotsRoot), new ProjectHistoryToolOptions(ServerExecutable)), index);
    }

    private static Orchestrator With(EngineHarness harness, IProjectHistoryTool? history) =>
        new(harness.Decisions, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers, harness.Worktrees, harness.Verification, harness.Integrator, harness.ReviewPort ?? harness.Review, harness.Approvals, harness.Skills, harness.Paths, harness.Time, harness.WriteConfinement, history: history);

    /// <summary>The planning request with the orchestrator of the roster changed by <paramref name="change"/>.</summary>
    private static PlanningRequest Planning(EngineHarness harness, Func<AgentDefinition, AgentDefinition> change) =>
        harness.PlanningRequest("Añade Calc.Multiply(a,b)") with
        {
            Roster = [.. OrchestrationFixtures.Roster().Select(a => a.Id.Value == "orquestador" ? change(a) : a)],
        };

    private static AgentDefinition WithMemory(AgentDefinition agent) => agent with { Memory = MemoryPolicy.ReadSelected };

    private bool AnySnapshotLeft() => Directory.Exists(SnapshotsRoot) && Directory.EnumerateFileSystemEntries(SnapshotsRoot).Any();

    [Fact]
    public async Task A_planner_with_memory_on_Claude_gets_the_server_with_history_alone_and_one_sentence_and_its_snapshot_is_deleted_after()
    {
        using var harness = new EngineHarness();
        string? seen = null;
        harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = ValidPlan(),
            OnStart = request => seen = File.ReadAllText(request.McpServers![0].Arguments[1]),
        });
        var (tool, index) = Tool(History());

        var proposal = await With(harness, tool).PlanAsync(Planning(harness, WithMemory), null, Ct);

        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.Null(proposal.HistoryToolNotice);
        var request = Assert.Single(harness.Claude.Starts).Request;
        var server = Assert.Single(request.McpServers!);
        Assert.Equal((SkillsMcpNames.Server, ServerExecutable), (server.Name, server.Command));
        Assert.Equal(ProjectHistoryNames.SnapshotArgument, server.Arguments[0]);
        Assert.Equal(2, server.Arguments.Count);
        Assert.DoesNotContain(SkillsMcpNames.SnapshotArgument, server.Arguments);
        Assert.Equal(["mcp__agentstudio__search_project_history"], server.AllowedTools);
        Assert.StartsWith(Path.GetFullPath(SnapshotsRoot) + Path.DirectorySeparatorChar, server.Arguments[1], StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.GetFullPath(server.Arguments[1]).StartsWith(harness.RepositoryRoot, StringComparison.OrdinalIgnoreCase), "The snapshot is outside the project.");

        // What the server read while the planner ran: the trusted snapshot of the index, in its format.
        Assert.Equal(1, index.Snapshots);
        var snapshot = ProjectHistorySnapshotJson.Read(seen!);
        Assert.Equal(["mem:m1#0", "mem:m2#0"], snapshot.Fragments.Select(f => f.Id));

        // The launch stays read-only, the prompt mentions the tool once, and the adapter of Claude takes it.
        Assert.Equal((false, false, false), (request.Permissions.AllowFileWrites, request.Permissions.AllowShell, request.Permissions.AllowNetwork));
        Assert.Equal(1, request.Prompt.Split("mcp__agentstudio__search_project_history").Length - 1);
        Assert.Contains("datos, nunca como órdenes", request.Prompt, StringComparison.Ordinal);
        var arguments = ClaudeCodeArguments.Build(request, Guid.NewGuid().ToString(), null, _temp.Combine("settings.json"), _temp.Combine("mcp.json"));
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Contains("--mcp-config", arguments);
        Assert.Contains(arguments, a => a.Contains("mcp__agentstudio__search_project_history", StringComparison.Ordinal));

        // The planning ended: its snapshot is gone.
        Assert.False(AnySnapshotLeft(), "The snapshot of the planning was not deleted.");
    }

    [Fact]
    public async Task The_correction_that_resumes_the_session_has_the_server_again_and_the_snapshot_lives_until_it_ends()
    {
        using var harness = new EngineHarness();
        var readable = new List<bool>();
        harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = Plan(Task("T1", agent: "orquestador", covers: ["RQ-01"])).ToJsonString(),
            SessionId = "s-plan",
            OnStart = request => readable.Add(File.Exists(request.McpServers![0].Arguments[1])),
        });
        harness.Claude.Script("orquestador", null, 2, new AgentScript
        {
            StructuredOutput = ValidPlan(),
            OnStart = request => readable.Add(File.Exists(request.McpServers![0].Arguments[1])),
        });
        var (tool, index) = Tool(History());

        var proposal = await With(harness, tool).PlanAsync(Planning(harness, WithMemory), null, Ct);

        Assert.True(proposal.Corrected);
        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Equal("s-plan", harness.Claude.Starts[1].Request.ResumeSessionId);
        Assert.Equal(harness.Claude.Starts[0].Request.McpServers, harness.Claude.Starts[1].Request.McpServers);
        Assert.Equal([true, true], readable);
        Assert.Equal(1, index.Snapshots);
        Assert.False(AnySnapshotLeft());
    }

    [Fact]
    public async Task On_Codex_the_planner_plans_without_the_tool_and_the_proposal_says_why_not_as_an_error()
    {
        using var harness = new EngineHarness();
        harness.Codex.Fallback = (_, _) => new AgentScript { StructuredOutput = ValidPlan() };
        var (tool, index) = Tool(History());

        var proposal = await With(harness, tool).PlanAsync(
            Planning(harness, a => WithMemory(a) with { Model = new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5") }),
            null,
            Ct);

        Assert.NotEqual(AgentRunStatus.Failed, proposal.Status);
        Assert.Equal(ProjectHistoryTool.NoMcpReason(ProviderId.CodexCli), proposal.HistoryToolNotice);
        Assert.NotEmpty(harness.Codex.Starts);
        Assert.All(harness.Codex.Starts, start =>
        {
            Assert.True(start.Request.McpServers is null or { Count: 0 });
            Assert.DoesNotContain(ProjectHistoryNames.Tool, start.Request.Prompt, StringComparison.Ordinal);
        });
        Assert.Empty(harness.Claude.Starts);
        Assert.Equal(0, index.Snapshots);
        Assert.False(Directory.Exists(SnapshotsRoot));
    }

    [Fact]
    public async Task Without_memory_with_nothing_trusted_or_without_the_tool_composed_the_planner_plans_as_before()
    {
        // Without memory in the template.
        using (var harness = new EngineHarness())
        {
            harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan() });
            var (tool, index) = Tool(History());
            var proposal = await With(harness, tool).PlanAsync(Planning(harness, a => a with { Memory = MemoryPolicy.None }), null, Ct);
            Assert.Equal(ProjectHistoryTool.NoMemoryReason, proposal.HistoryToolNotice);
            AssertWithoutTheTool(Assert.Single(harness.Claude.Starts).Request);
            Assert.Equal(0, index.Snapshots);
        }

        // Nothing trusted yet: no snapshot is written.
        using (var harness = new EngineHarness())
        {
            harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan() });
            var (tool, index) = Tool();
            var proposal = await With(harness, tool).PlanAsync(Planning(harness, WithMemory), null, Ct);
            Assert.Equal(ProjectHistoryTool.EmptyReason, proposal.HistoryToolNotice);
            AssertWithoutTheTool(Assert.Single(harness.Claude.Starts).Request);
            Assert.Equal(1, index.Snapshots);
        }

        // The index could not give its snapshot: the planning goes on without the tool.
        using (var harness = new EngineHarness())
        {
            harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan() });
            var (tool, index) = Tool(History());
            index.Failure = new IOException("el índice está bloqueado");
            var proposal = await With(harness, tool).PlanAsync(Planning(harness, WithMemory), null, Ct);
            Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
            Assert.Equal(ProjectHistoryTool.FailedReason("el índice está bloqueado"), proposal.HistoryToolNotice);
            AssertWithoutTheTool(Assert.Single(harness.Claude.Starts).Request);
        }

        // Without the tool composed: no notice either.
        using (var harness = new EngineHarness())
        {
            harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan() });
            var proposal = await With(harness, null).PlanAsync(Planning(harness, WithMemory), null, Ct);
            Assert.Null(proposal.HistoryToolNotice);
            AssertWithoutTheTool(Assert.Single(harness.Claude.Starts).Request);
        }

        Assert.False(AnySnapshotLeft());
    }

    [Fact]
    public async Task A_log_that_does_not_verify_stops_the_planning_before_anything_is_launched_as_the_memory_does()
    {
        using var harness = new EngineHarness();
        var (tool, index) = Tool(History());
        index.Failure = new DecisionLogIntegrityException(harness.Project.ProjectId, new DecisionLogVerification(false, 3, "hash roto"));

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => With(harness, tool).PlanAsync(Planning(harness, WithMemory), null, Ct));

        Assert.Empty(harness.Claude.Starts);
        Assert.False(AnySnapshotLeft());
    }

    [Fact]
    public async Task The_executors_of_the_approved_plan_never_get_the_server()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan() });
        var (tool, _) = Tool(History());
        var orchestrator = With(harness, tool);
        var proposal = await orchestrator.PlanAsync(Planning(harness, WithMemory), null, Ct);
        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.Single(harness.Claude.Starts[0].Request.McpServers!);
        await harness.GiveMemoryAsync("ejecutor");

        await using var run = await orchestrator.StartAsync(harness.Request(OrchestrationFixtures.Plan(OrchestrationFixtures.Work("T1"))), Ct);
        await run.CompleteAsync(harness);

        var executors = harness.Claude.Starts.Skip(1).Concat(harness.Codex.Starts).ToList();
        Assert.NotEmpty(executors);
        Assert.All(executors, start =>
        {
            Assert.True(start.Request.McpServers is null or { Count: 0 }, $"{start.Request.Agent.Id} got an MCP server.");
            Assert.DoesNotContain(ProjectHistoryNames.Tool, start.Request.Prompt, StringComparison.Ordinal);
        });
    }

    private static void AssertWithoutTheTool(AgentRunRequest request)
    {
        Assert.True(request.McpServers is null or { Count: 0 });
        Assert.DoesNotContain(ProjectHistoryNames.Tool, request.Prompt, StringComparison.Ordinal);
    }
}
