using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Orchestration.Planning;

namespace AgentStudio.Tests.Application;

/// <summary>
/// How the history tool goes into a launch (ADR-0024, addendum of 2026-11-03; docs/14 D-30): only into a read-only one, as
/// one server of Agent Studio with <c>--history</c>, next to <c>--snapshot</c> when the launch already had the skills
/// catalog; and the tool is offered only to the planner, never to an executor, a judge or an auditor, nor to a launch that
/// writes, has a shell or has network.
/// </summary>
public sealed class ProjectHistoryLaunchTests
{
    private const string Server = @"C:\Agent Studio\AgentStudio.SkillsMcp.exe";

    private static readonly ProjectContext Project = new("p-1", @"C:\proyectos\calc");

    private static readonly PermissionProfile ReadOnly = new(false, false, false, ["Read", "Grep", "Glob"], [], []);

    private static AgentDefinition Agent(AgentRole role, ProviderId? provider = null) => new(
        new AgentId("agente"), "Agente", role, new ModelBinding(provider ?? ProviderId.ClaudeCode, VendorId.Anthropic), "i", [], ["Read"], [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.ReadSelected, new ReviewPolicy(false, false));

    private static AgentRunRequest Request(PermissionProfile permissions, IReadOnlyList<McpServerSpec>? servers = null) =>
        new(Agent(AgentRole.Orchestrator), "Planifica.", @"C:\wt\_base", permissions, McpServers: servers);

    [Fact]
    public void A_launch_without_servers_gets_the_server_of_Agent_Studio_with_the_history_alone()
    {
        var history = ProjectHistoryLaunch.Server(Server, @"C:\datos\his-1\history.json");

        var request = ProjectHistoryLaunch.With(Request(ReadOnly), history);

        var server = Assert.Single(request.McpServers!);
        Assert.Equal(SkillsMcpNames.Server, server.Name);
        Assert.Equal(["--history", @"C:\datos\his-1\history.json"], server.Arguments);
        Assert.Equal(["mcp__agentstudio__search_project_history"], server.AllowedTools);
    }

    [Fact]
    public void A_launch_that_already_had_the_skills_catalog_keeps_one_server_with_both_snapshots_and_the_three_rules()
    {
        var skills = new McpServerSpec(SkillsMcpNames.Server, Server, [SkillsMcpNames.SnapshotArgument, @"C:\datos\rec-1\snapshot.json"], SkillsMcpNames.AllowRules);
        var other = new McpServerSpec("otro", @"C:\otro.exe", [], ["mcp__otro__leer"]);

        var request = ProjectHistoryLaunch.With(Request(ReadOnly, [skills, other]), ProjectHistoryLaunch.Server(Server, @"C:\datos\his-1\history.json"));

        Assert.Equal(2, request.McpServers!.Count);
        var server = request.McpServers[0];
        Assert.Equal(["--snapshot", @"C:\datos\rec-1\snapshot.json", "--history", @"C:\datos\his-1\history.json"], server.Arguments);
        Assert.Equal(["mcp__agentstudio__search_skills", "mcp__agentstudio__recommend_skills", "mcp__agentstudio__search_project_history"], server.AllowedTools);
        Assert.Same(other, request.McpServers[1]);
        Assert.Throws<ArgumentException>(() => ProjectHistoryLaunch.With(request, ProjectHistoryLaunch.Server(Server, @"C:\datos\his-2\history.json")));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void A_launch_that_writes_has_a_shell_or_has_network_never_gets_it(bool writes, bool shell, bool network)
    {
        var permissions = new PermissionProfile(writes, shell, network, ["Read"], [], []);

        Assert.Throws<ArgumentException>(() => ProjectHistoryLaunch.With(Request(permissions), ProjectHistoryLaunch.Server(Server, @"C:\datos\his-1\history.json")));
    }

    [Theory]
    [InlineData(AgentRole.Executor)]
    [InlineData(AgentRole.Judge)]
    [InlineData(AgentRole.Auditor)]
    [InlineData(AgentRole.Architect)]
    public async Task Only_the_planner_is_offered_the_tool_and_nothing_is_read_for_anyone_else(AgentRole role)
    {
        var index = new SnapshotIndex(new HistoryFragment("mem:m1#0", "decisión", DateTimeOffset.UnixEpoch, "Usar decimal."));
        var folders = new CountingFolders();
        var tool = new ProjectHistoryTool(index, folders, new ProjectHistoryToolOptions(Server));

        using var offer = await tool.OfferAsync(Project, Agent(role), ReadOnly, TestContext.Current.CancellationToken);
        using var writing = await tool.OfferAsync(Project, Agent(AgentRole.Orchestrator), ReadOnly with { AllowFileWrites = true }, TestContext.Current.CancellationToken);
        using var gemini = await tool.OfferAsync(Project, Agent(AgentRole.Orchestrator, ProviderId.GeminiCli), ReadOnly, TestContext.Current.CancellationToken);

        Assert.Equal((false, ProjectHistoryTool.NotThePlannerReason), (offer.Offered, offer.Withheld));
        Assert.Equal((false, ProjectHistoryTool.NotReadOnlyReason), (writing.Offered, writing.Withheld));
        Assert.Equal((false, ProjectHistoryTool.NoMcpReason(ProviderId.GeminiCli)), (gemini.Offered, gemini.Withheld));
        Assert.Equal(0, index.Snapshots);
        Assert.Equal(0, folders.Created);
    }

    [Fact]
    public async Task Turned_off_in_the_settings_no_planner_gets_it_and_nothing_is_read_and_the_reasons_are_plain_spanish()
    {
        var index = new SnapshotIndex(new HistoryFragment("mem:m1#0", "decisión", DateTimeOffset.UnixEpoch, "Usar decimal."));
        var folders = new CountingFolders();
        var on = true;
        var tool = new ProjectHistoryTool(index, folders, new ProjectHistoryToolOptions(Server) { Enabled = () => on });

        on = false;
        using var off = await tool.OfferAsync(Project, Agent(AgentRole.Orchestrator), ReadOnly, TestContext.Current.CancellationToken);
        on = true;
        using var again = await tool.OfferAsync(Project, Agent(AgentRole.Orchestrator), ReadOnly, TestContext.Current.CancellationToken);

        Assert.Equal((false, ProjectHistoryTool.DisabledReason), (off.Offered, off.Withheld));
        Assert.True(again.Offered);
        Assert.Equal((1, 1), (index.Snapshots, folders.Created));
        Assert.Equal("El orquestador no tuvo la búsqueda en el historial: Codex no admite herramientas externas.", ProjectHistoryTool.NoMcpReason(ProviderId.CodexCli));
        Assert.Equal("El orquestador no tuvo la búsqueda en el historial: Gemini no admite herramientas externas.", ProjectHistoryTool.NoMcpReason(ProviderId.GeminiCli));
        Assert.All(
            new[] { ProjectHistoryTool.DisabledReason, ProjectHistoryTool.NoMemoryReason, ProjectHistoryTool.NotReadOnlyReason, ProjectHistoryTool.EmptyReason, ProjectHistoryTool.FailedReason("disco lleno") },
            reason =>
            {
                Assert.StartsWith("El orquestador no tuvo la búsqueda en el historial: ", reason, StringComparison.Ordinal);
                Assert.DoesNotContain("MCP", reason, StringComparison.Ordinal);
                Assert.DoesNotContain("servidor", reason, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Disposing_the_offer_deletes_its_snapshot_once()
    {
        var folders = new CountingFolders();
        var tool = new ProjectHistoryTool(new SnapshotIndex(new HistoryFragment("mem:m1#0", "decisión", DateTimeOffset.UnixEpoch, "Usar decimal.")), folders, new ProjectHistoryToolOptions(Server));

        var offer = await tool.OfferAsync(Project, Agent(AgentRole.Orchestrator), ReadOnly, TestContext.Current.CancellationToken);
        offer.Dispose();
        offer.Dispose();

        Assert.True(offer.Offered);
        Assert.Equal((1, 1), (folders.Created, folders.Deleted));
    }

    private sealed class CountingFolders : IProjectHistoryFolders
    {
        public int Created { get; private set; }

        public int Deleted { get; private set; }

        public Task<ProjectHistoryFolder> CreateAsync(string snapshotJson, CancellationToken ct)
        {
            Created++;
            return Task.FromResult(new ProjectHistoryFolder(@"C:\datos\his-1\history.json"));
        }

        public void Delete(ProjectHistoryFolder folder) => Deleted++;
    }
}
