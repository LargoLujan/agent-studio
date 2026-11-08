using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration.Analysis;

/// <summary>
/// F13-5 (docs/17 D-11): the folders of an agent, the cable «Trabaja en», govern where its tasks may write. With folders, the
/// scope of each task fits in them; without, the agent has the whole project, as before.
/// </summary>
public sealed class AgentWriteAreaTests
{
    private static AgentDefinition Executor(params PathGrant[] grants) =>
        Agent("ejecutor", AgentRole.Executor, AgentStudio.Domain.Providers.VendorId.Anthropic) with { PathGrants = grants };

    private static IReadOnlyList<AgentDefinition> RosterWith(string agent, params PathGrant[] grants) =>
        [.. Roster().Select(a => a.Id == Aid(agent) ? a with { PathGrants = grants } : a)];

    private static PathGrant Writes(string root) => new(root, FileAccessLevel.ReadWrite);

    [Fact]
    public void An_agent_without_folders_has_the_whole_project()
    {
        var agent = Executor();

        Assert.False(AgentWriteArea.IsLimited(agent));
        Assert.Empty(AgentWriteArea.Folders(agent));
        Assert.True(AgentWriteArea.Fits(agent, "anything/at/all.cs"));
        Assert.True(AgentWriteArea.Fits(agent, "README.md"));
    }

    [Fact]
    public void A_grant_to_read_only_is_not_a_folder_to_write()
    {
        var agent = Executor(new PathGrant("docs/", FileAccessLevel.Read));

        Assert.False(AgentWriteArea.IsLimited(agent));
        Assert.True(AgentWriteArea.Fits(agent, "src/Calc/"));
    }

    [Theory]
    [InlineData("src/", true)]
    [InlineData("src/Calc/", true)]
    [InlineData("SRC/Calc/Multiply.cs", true)]
    [InlineData("src", false)] // without the slash it is one file at the root, not the folder
    [InlineData("srcx/", false)]
    [InlineData("tests/Calc.Tests/", false)]
    [InlineData("README.md", false)]
    public void An_entry_fits_when_it_is_one_of_the_folders_or_inside_one(string entry, bool fits)
    {
        var agent = Executor(Writes("src"), Writes("docs/"));

        Assert.Equal(["docs/", "src/"], AgentWriteArea.Folders(agent));
        Assert.Equal(fits, AgentWriteArea.Fits(agent, entry));
    }

    [Fact]
    public void A_folder_that_is_not_valid_covers_nothing_and_fails_closed()
    {
        var agent = Executor(Writes("C:\\repo\\src"), Writes("../outside/"));

        Assert.True(AgentWriteArea.IsLimited(agent));
        Assert.Empty(AgentWriteArea.Folders(agent));
        Assert.False(AgentWriteArea.Fits(agent, "src/Calc/"));
    }

    [Fact]
    public void The_entries_outside_keep_their_order_and_leave_out_the_invalid_ones()
    {
        var agent = Executor(Writes("src/"));

        Assert.Equal(["tests/Calc.Tests/", "README.md"], AgentWriteArea.Outside(agent, ["tests/Calc.Tests/", "src/Calc/", "../x/", "README.md"]));
    }

    [Fact]
    public void The_validator_reports_each_entry_outside_the_folders_on_its_task()
    {
        var plan = Plan(Replace("T1", t => t with { WriteScope = ["src/Calc/", "tests/Calc.Tests/", "tools/"] }));

        var violations = PlanValidator.Validate(plan, Context(roster: RosterWith("ejecutor", Writes("src/"), Writes("docs/"))));

        var outside = violations.Where(v => v.Code == PlanViolationCode.WriteScopeOutsideAgentArea).ToList();
        Assert.Equal(2, outside.Count);
        Assert.All(outside, v => Assert.Equal(Tid("T1"), v.Task));
        Assert.Contains("'tests/Calc.Tests/'", outside[0].Message, StringComparison.Ordinal);
        Assert.Contains("'tools/'", outside[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(violations, v => v.Code != PlanViolationCode.WriteScopeOutsideAgentArea);
    }

    [Fact]
    public void Only_tasks_that_write_are_checked()
    {
        // T6 (ejecutor) writes docs/; made read-only, the folders of ejecutor no longer matter to it.
        var plan = Plan(Replace("T6", t => t with { Workspace = WorkspaceMode.ReadOnly, WriteScope = [] }));

        var violations = PlanValidator.Validate(plan, Context(roster: RosterWith("ejecutor", Writes("src/"), Writes("tests/"))));

        Assert.DoesNotContain(violations, v => v.Code == PlanViolationCode.WriteScopeOutsideAgentArea);
    }

    [Fact]
    public void A_plan_of_before_with_agents_without_folders_is_still_valid()
    {
        Assert.Empty(PlanValidator.Validate(Plan(), Context()));
    }
}
