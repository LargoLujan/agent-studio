using AgentStudio.Application.Orchestration;
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

    [Fact]
    public async Task Deleting_an_agent_lists_the_template_without_it_and_changes_its_hash_and_keeps_the_order_of_the_others()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var roster = FullRoster();
        foreach (var agent in roster)
        {
            await instance.Roster.SaveAsync(instance.ProjectId, agent, Ct);
        }

        var hashBefore = RosterHash.Of(await instance.Roster.ListAsync(instance.ProjectId, Ct));
        var gone = roster[2].Id;

        await instance.Roster.DeleteAsync(instance.ProjectId, gone, Ct);

        var listed = await instance.Roster.ListAsync(instance.ProjectId, Ct);
        Assert.DoesNotContain(listed, a => a.Id == gone);
        Assert.Equal(roster.Where(a => a.Id != gone).Select(a => a.Id), listed.Select(a => a.Id));
        Assert.NotEqual(hashBefore, RosterHash.Of(listed));
        using var connection = instance.Connect();
        Assert.Equal(roster.Count - 1, Scalar(connection, "SELECT COUNT(*) FROM agents"));
    }

    [Fact]
    public async Task Deleting_an_agent_that_is_not_in_the_template_or_twice_is_no_error_and_changes_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var roster = FullRoster();
        foreach (var agent in roster.Take(3))
        {
            await instance.Roster.SaveAsync(instance.ProjectId, agent, Ct);
        }

        var hash = RosterHash.Of(await instance.Roster.ListAsync(instance.ProjectId, Ct));
        await instance.Roster.DeleteAsync(instance.ProjectId, new AgentId("nadie"), Ct);
        Assert.Equal(hash, RosterHash.Of(await instance.Roster.ListAsync(instance.ProjectId, Ct)));

        await instance.Roster.DeleteAsync(instance.ProjectId, roster[1].Id, Ct);
        await instance.Roster.DeleteAsync(instance.ProjectId, roster[1].Id, Ct);

        Assert.Equal([roster[0].Id, roster[2].Id], (await instance.Roster.ListAsync(instance.ProjectId, Ct)).Select(a => a.Id));
    }

    [Fact]
    public async Task A_deleted_agent_can_be_saved_again_and_the_deletion_survives_reopening()
    {
        var roster = FullRoster();
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            foreach (var agent in roster.Take(3))
            {
                await first.Roster.SaveAsync(first.ProjectId, agent, Ct);
            }

            await first.Roster.DeleteAsync(first.ProjectId, roster[0].Id, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        Assert.Equal([roster[1].Id, roster[2].Id], (await second.Roster.ListAsync(second.ProjectId, Ct)).Select(a => a.Id));

        await second.Roster.SaveAsync(second.ProjectId, roster[0], Ct);

        Assert.Equal([roster[1].Id, roster[2].Id, roster[0].Id], (await second.Roster.ListAsync(second.ProjectId, Ct)).Select(a => a.Id));
    }

    [Fact]
    public async Task Deleting_an_agent_of_the_template_leaves_the_header_of_a_past_run_and_its_hash_as_they_were()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var run = await SeedRunAsync(instance);
        await instance.Runs.CreateAsync(run, Ct);
        foreach (var agent in run.Roster)
        {
            await instance.Roster.SaveAsync(instance.ProjectId, agent, Ct);
        }

        var frozen = run.Roster.First(a => a.Id == new AgentId("ejecutor-completo"));
        await instance.Roster.DeleteAsync(instance.ProjectId, frozen.Id, Ct);

        // The template no longer has it, and the run that started with it still reads it, whole.
        Assert.DoesNotContain(await instance.Roster.ListAsync(instance.ProjectId, Ct), a => a.Id == frozen.Id);
        var found = await instance.Runs.FindAsync(instance.ProjectId, run.Id, Ct);
        Assert.NotNull(found);
        Assert.Equal(Shape(frozen), Shape(found.Roster.Single(a => a.Id == frozen.Id)));
        Assert.Equal(RunHeaderHash.Of(run), RunHeaderHash.Of(found));
    }

    [Fact]
    public async Task The_agent_table_can_be_deleted_from_even_with_foreign_keys_on_because_nothing_that_is_written_points_at_it()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var agent = FullRoster()[0];
        await instance.Roster.SaveAsync(instance.ProjectId, agent, Ct);
        using var connection = instance.Connect();

        // The only reference to agents(id) is tasks.assigned_agent_id, which no store writes: the delete cannot be blocked by a row.
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM tasks WHERE assigned_agent_id IS NOT NULL"));
        await instance.Roster.DeleteAsync(instance.ProjectId, agent.Id, Ct);

        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM agents"));
    }
}
