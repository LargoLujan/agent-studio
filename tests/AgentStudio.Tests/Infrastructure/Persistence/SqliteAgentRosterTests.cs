using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

public sealed class SqliteAgentRosterTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saved_agents_are_listed_after_reopening_in_the_order_they_were_added()
    {
        var roster = FullRoster();
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            Assert.Empty(await first.Roster.ListAsync(first.ProjectId, Ct));
            foreach (var agent in roster)
            {
                await first.Roster.SaveAsync(first.ProjectId, agent, Ct);
            }
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Equal(Shape(roster), Shape(await second.Roster.ListAsync(second.ProjectId, Ct)));
    }

    [Fact]
    public async Task Saving_an_agent_again_replaces_its_definition_in_place()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var roster = FullRoster();
        foreach (var agent in roster.Take(3))
        {
            await instance.Roster.SaveAsync(instance.ProjectId, agent, Ct);
        }

        var renamed = roster[1] with { Name = "ejecutor renombrado", MaxRounds = 9 };
        instance.Clock.Advance(TimeSpan.FromDays(1));
        await instance.Roster.SaveAsync(instance.ProjectId, renamed, Ct);

        var listed = await instance.Roster.ListAsync(instance.ProjectId, Ct);
        Assert.Equal(roster.Take(3).Select(a => a.Id), listed.Select(a => a.Id));
        Assert.Equal(Shape(renamed), Shape(listed[1]));
        using var connection = instance.Connect();
        Assert.Equal(3, Scalar(connection, "SELECT COUNT(*) FROM agents"));
        Assert.Equal("ejecutor renombrado", Text(connection, $"SELECT name FROM agents WHERE id = '{renamed.Id.Value}'"));
        Assert.Equal("2026-01-01T10:00:00.0000000+00:00", Text(connection, $"SELECT created_at FROM agents WHERE id = '{renamed.Id.Value}'"));
    }

    [Fact]
    public async Task A_judge_with_write_access_never_reaches_the_template()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var judge = FullRoster().First(a => a.Role == AgentRole.Judge) with { PathGrants = [new PathGrant("src/", FileAccessLevel.ReadWrite)] };

        await Assert.ThrowsAsync<DomainRuleViolationException>(() => instance.Roster.SaveAsync(instance.ProjectId, judge, Ct));

        Assert.Empty(await instance.Roster.ListAsync(instance.ProjectId, Ct));
    }
}
