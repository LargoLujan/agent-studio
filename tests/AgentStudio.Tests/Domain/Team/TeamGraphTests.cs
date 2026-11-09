using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Team;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Team;

/// <summary>
/// The Team layer as a graph (docs/17 D-08 to D-11): its nodes are the template, its cables are typed and checked, the limits
/// of a team are seen while it is assembled, and the node of the repository never keeps a URL with credentials (F13-3).
/// </summary>
public sealed class TeamGraphTests
{
    private static AgentNode Node(string id, AgentRole role, VendorId? vendor = null) => new(new AgentDefinition(
        Aid(id),
        id,
        role,
        new ModelBinding(vendor == VendorId.OpenAI ? ProviderId.CodexCli : ProviderId.ClaudeCode, vendor ?? VendorId.Anthropic, null),
        string.Empty,
        [],
        ["Read"],
        [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false)));

    private static readonly RepositoryNode Repository = new(
        "C:/src/calc", "main", "a1b2c3d", [RepositoryRemote.Cleaned("origin", "https://github.com/acme/calc.git")], false, "dotnet", ["src", "tests", "docs"]);

    private static TeamGraph Team(IReadOnlyList<TeamEdge> edges, params AgentNode[] extra) => new(
        [Node("orquestador", AgentRole.Orchestrator), Node("ejecutor", AgentRole.Executor), Node("juez", AgentRole.Judge, VendorId.OpenAI), .. extra],
        Repository,
        edges);

    [Fact]
    public void A_team_with_the_cables_of_the_design_is_consistent()
    {
        var team = Team(
        [
            new TeamEdge(TeamEdgeKind.Distributes, Aid("orquestador"), Aid("ejecutor")),
            new TeamEdge(TeamEdgeKind.Reviews, Aid("ejecutor"), Aid("juez")),
            new TeamEdge(TeamEdgeKind.Reviews, Aid("orquestador"), Aid("juez")),
            new TeamEdge(TeamEdgeKind.WorksIn, Aid("ejecutor"), ToFolder: "src"),
            new TeamEdge(TeamEdgeKind.WorksIn, Aid("ejecutor"), ToFolder: "tests"),
        ]);

        Assert.Empty(team.Validate());
        Assert.Equal(["orquestador", "ejecutor", "juez"], team.Roster.Select(a => a.Id.Value));
        Assert.False(team.SharesVendor(team.Edges[1]));
    }

    [Fact]
    public void A_judge_of_the_vendor_of_a_producer_draws_its_cable_as_an_error()
    {
        var team = Team([new TeamEdge(TeamEdgeKind.Reviews, Aid("ejecutor"), Aid("juez-claude"))], Node("juez-claude", AgentRole.Judge));

        Assert.Empty(team.Validate());
        Assert.True(team.SharesVendor(team.Edges[0]));
    }

    [Fact]
    public void Each_cable_joins_what_its_kind_says()
    {
        Assert.Equal(["team.distributes-not-orchestrator-to-executor"], Team([new TeamEdge(TeamEdgeKind.Distributes, Aid("ejecutor"), Aid("ejecutor"))]).Validate());
        Assert.Equal(["team.reviews-not-producer-to-judge"], Team([new TeamEdge(TeamEdgeKind.Reviews, Aid("ejecutor"), Aid("orquestador"))]).Validate());
        Assert.Equal(["team.works-in-not-writer"], Team([new TeamEdge(TeamEdgeKind.WorksIn, Aid("juez"), ToFolder: "src")]).Validate());
        Assert.Equal(["team.works-in-unknown-folder"], Team([new TeamEdge(TeamEdgeKind.WorksIn, Aid("ejecutor"), ToFolder: "bin")]).Validate());
        Assert.Equal(["team.edge-unknown-agent"], Team([new TeamEdge(TeamEdgeKind.Reviews, Aid("nadie"), Aid("juez"))]).Validate());
        Assert.Empty(Team([new TeamEdge(TeamEdgeKind.WorksIn, Aid("ejecutor"))]).Validate());
    }

    [Fact]
    public void The_limits_of_a_team_are_one_orchestrator_eight_executors_and_a_judge()
    {
        var nine = Enumerable.Range(2, 8).Select(i => Node("ejecutor-" + i, AgentRole.Executor)).ToArray();

        Assert.Contains("team.too-many-orchestrators", Team([], Node("otro", AgentRole.Orchestrator)).Validate());
        Assert.Contains("team.too-many-executors", Team([], nine).Validate());
        Assert.Empty(Team([], nine[..7]).Validate());
        Assert.Contains("team.judge-required", new TeamGraph([Node("ejecutor", AgentRole.Executor)], null, []).Validate());
        Assert.Contains("team.duplicate-agent", Team([], Node("ejecutor", AgentRole.Executor)).Validate());
        Assert.Equal((1, 8, 1), (TeamLimits.MaxOrchestrators, TeamLimits.MaxExecutors, TeamLimits.MinJudges));
    }

    /// <summary>F13-3: a URL with credentials is never shown nor kept with them.</summary>
    [Theory]
    [InlineData("https://usuario:token@github.com/acme/calc.git", "https://github.com/acme/calc.git")]
    [InlineData("https://token@github.com/acme/calc.git", "https://github.com/acme/calc.git")]
    [InlineData("ssh://git:secreto@host:22/repo.git", "ssh://host:22/repo.git")]
    [InlineData("https://github.com/acme/calc.git", "https://github.com/acme/calc.git")]
    [InlineData("git@github.com:acme/calc.git", "github.com:acme/calc.git")]
    [InlineData("usuario@host:acme/calc.git", "host:acme/calc.git")]
    [InlineData("https://user:pa?ss@host/acme/calc.git", "https://host/acme/calc.git")]
    [InlineData("https://user:pa#ss@host/acme/calc.git", "https://host/acme/calc.git")]
    [InlineData("https://github.com/acme/calc.git?token=SECRETO#parte", "https://github.com/acme/calc.git")]
    [InlineData("https://usuario:token@github.com/acme/calc.git?x=1", "https://github.com/acme/calc.git")]
    [InlineData("C:/src/calc", "C:/src/calc")]
    public void A_remote_is_kept_without_the_credentials_of_its_url(string url, string kept)
    {
        Assert.Equal(kept, RepositoryRemote.Cleaned("origin", url).Url);
        Assert.False(RepositoryRemote.HasCredentials(kept));
    }

    [Fact]
    public void A_remote_with_credentials_cannot_even_be_built()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryRemote("origin", "https://usuario:token@github.com/acme/calc.git"));
        Assert.True(RepositoryRemote.HasCredentials("http://a:b@host/x"));
    }

    [Theory]
    [InlineData("src", true)]
    [InlineData("src/", true)]
    [InlineData("src/Calc", true)]
    [InlineData("../fuera", false)]
    [InlineData("/raiz", false)]
    [InlineData("C:/src", false)]
    [InlineData("src\\Calc", false)]
    [InlineData(" ", false)]
    public void A_main_folder_is_relative_and_never_climbs(string folder, bool valid)
    {
        Assert.Equal(valid, RepositoryNode.IsMainFolder(folder));
    }

    [Fact]
    public void The_roles_say_who_writes_who_judges_and_who_only_debates()
    {
        Assert.True(TeamRoles.Writes(AgentRole.Executor));
        Assert.False(TeamRoles.Writes(AgentRole.Judge));
        Assert.True(TeamRoles.Judges(AgentRole.Judge));
        Assert.True(TeamRoles.IsDebateOnly(AgentRole.Critic));
        Assert.False(TeamRoles.IsDebateOnly(AgentRole.Executor));
    }
}
