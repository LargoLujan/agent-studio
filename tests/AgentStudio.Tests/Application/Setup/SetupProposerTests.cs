using AgentStudio.Application.Setup;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Team;

namespace AgentStudio.Tests.Application.Setup;

/// <summary>F13-12: the deterministic proposals of the configurator, from the names of the files and the template of the project alone.</summary>
public sealed class SetupProposerTests
{
    [Fact]
    public void The_stack_is_dotnet_then_node_then_python_and_nothing_when_none_is_there()
    {
        Assert.Equal("dotnet", SetupProposer.ProposeProject(SetupFakes.Facts(["a/App.csproj", "package.json", "pyproject.toml"])).Stack);
        Assert.Equal("node", SetupProposer.ProposeProject(SetupFakes.Facts(["web/package.json", "pyproject.toml"])).Stack);
        Assert.Equal("python", SetupProposer.ProposeProject(SetupFakes.Facts(["pyproject.toml"])).Stack);
        Assert.Equal("python", SetupProposer.ProposeProject(SetupFakes.Facts(["requirements.txt"])).Stack);
        Assert.Null(SetupProposer.ProposeProject(SetupFakes.Facts(["README.md", "script.sql"])).Stack);
        Assert.Contains("sql", SetupProposer.ProposeProject(SetupFakes.Facts(["script.sql"])).StackTags);
    }

    [Theory]
    [InlineData(0, SetupSize.Small)]
    [InlineData(299, SetupSize.Small)]
    [InlineData(300, SetupSize.Medium)]
    [InlineData(2000, SetupSize.Medium)]
    [InlineData(2001, SetupSize.Large)]
    public void The_size_of_the_project_is_read_from_the_count_of_files(int files, SetupSize expected)
    {
        Assert.Equal(expected, SetupSizes.Of(files));
        Assert.Equal(expected, SetupProposer.ProposeProject(SetupFakes.Facts(Enumerable.Range(0, files).Select(i => $"f{i}.txt"))).Size);
    }

    [Fact]
    public void The_main_folders_are_the_top_level_ones_with_most_files_and_a_remote_never_carries_credentials()
    {
        var files = Enumerable.Range(0, 5).Select(i => $"src/f{i}.cs")
            .Concat(Enumerable.Range(0, 3).Select(i => $"docs\\d{i}.md"))
            .Concat(["tests/t.cs", "README.md"]);
        var remote = RepositoryRemote.Cleaned("origin", "https://usuario:token@github.com/org/repo.git");

        var proposal = SetupProposer.ProposeProject(SetupFakes.Facts(files, [remote]));

        Assert.Equal(["src/", "docs/", "tests/"], proposal.MainFolders);
        Assert.Equal("https://github.com/org/repo.git", Assert.Single(proposal.Remotes).Url);
        Assert.Equal(10, proposal.Files);
    }

    [Fact]
    public void Each_size_has_its_template_and_a_project_that_has_an_agent_keeps_its_own_definition()
    {
        Assert.Equal(["orquestador", "ejecutor", "juez"], SetupProposer.Template(SetupSize.Small).Select(a => a.Id.Value));
        Assert.Equal(["orquestador", "ejecutor", "ejecutor-ligero", "juez"], SetupProposer.Template(SetupSize.Medium).Select(a => a.Id.Value));
        Assert.Equal(["orquestador", "ejecutor", "ejecutor-ligero", "juez", "revisor"], SetupProposer.Template(SetupSize.Large).Select(a => a.Id.Value));
        Assert.All(Enum.GetValues<SetupSize>().SelectMany(SetupProposer.Template), a => Assert.Empty(a.Validate()));

        var custom = SetupTeams.Default().Single(a => a.Id.Value == "ejecutor") with { Name = "Mi ejecutor" };
        var proposal = SetupProposer.ProposeTeam(SetupSize.Small, [custom]);

        var executor = proposal.Changes.Single(c => c.After.Id.Value == "ejecutor");
        Assert.Equal("Mi ejecutor", executor.After.Name);
        Assert.False(executor.IsChange);
        Assert.All(proposal.Changes.Where(c => c.IsAdded), c => Assert.True(c.IsChange));
    }

    [Fact]
    public void A_template_never_removes_an_agent_the_project_has_and_the_size_does_not_name()
    {
        var current = SetupTeams.Default().Append(SetupTeams.Agent("arquitecto", AgentRole.Architect, ProviderId.ClaudeCode, VendorId.Anthropic)).ToList();

        var proposal = SetupProposer.ProposeTeam(SetupSize.Small, current);

        Assert.Contains(proposal.Changes, c => c.After.Id.Value == "arquitecto" && !c.IsChange);
        Assert.Equal(current.Count, proposal.Changes.Count);
    }

    [Fact]
    public void The_reviewer_and_the_default_template_hold_no_capability_of_the_barrier()
    {
        // The proposals never need the barrier by themselves: the person grants a shell or the network from the team layer.
        Assert.All(Enum.GetValues<SetupSize>().SelectMany(SetupProposer.Template), a => Assert.Empty(SetupCapabilities.Of(a)));
    }
}
