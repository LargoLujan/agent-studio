using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills;
using Microsoft.Data.Sqlite;
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
    public async Task Taking_an_agent_out_turns_off_its_skills_so_one_added_later_with_the_same_id_does_not_inherit_them()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var roster = FullRoster();
        foreach (var agent in roster.Take(4))
        {
            await instance.Roster.SaveAsync(instance.ProjectId, agent, Ct);
        }

        var gone = roster[1];
        var other = roster[2];
        var skills = new SkillActivationStore(instance.Databases, instance.Clock, new SkillActivationStoreOptions(Path.Combine(Path.GetTempPath(), "as perfil " + Guid.NewGuid().ToString("N")[..12], "skill-activations.jsonl")));
        var tdd = new SkillId("tdd");
        var review = new SkillId("review");
        var docs = new SkillId("docs");
        var lint = new SkillId("lint");
        await skills.AppendAsync(instance.ProjectId, new SkillActivation(tdd, SkillScopeKind.Agent, gone.Id.Value, true), "manuel", Ct);
        await skills.AppendAsync(instance.ProjectId, new SkillActivation(review, SkillScopeKind.Agent, gone.Id.Value, true), "manuel", Ct);
        await skills.AppendAsync(instance.ProjectId, new SkillActivation(review, SkillScopeKind.Agent, gone.Id.Value, false), "manuel", Ct);
        await skills.AppendAsync(instance.ProjectId, new SkillActivation(docs, SkillScopeKind.Agent, gone.Id.Value, true), "manuel", Ct);
        await skills.AppendAsync(instance.ProjectId, new SkillActivation(tdd, SkillScopeKind.Project, instance.ProjectId, true), "manuel", Ct);
        await skills.AppendAsync(instance.ProjectId, new SkillActivation(lint, SkillScopeKind.Agent, other.Id.Value, true), "manuel", Ct);
        var before = await skills.ListAsync(instance.ProjectId, Ct);
        Assert.True(SkillActivationResolver.IsEnabled(docs, instance.ProjectId, gone.Id.Value, before));

        await instance.Roster.DeleteAsync(instance.ProjectId, gone.Id, Ct);
        await instance.Roster.SaveAsync(instance.ProjectId, gone, Ct);

        // The agent added later with the same id has none of what the one before had on; the other agent keeps its own.
        var after = await skills.ListAsync(instance.ProjectId, Ct);
        Assert.False(SkillActivationResolver.IsEnabled(docs, instance.ProjectId, gone.Id.Value, after));
        Assert.False(SkillActivationResolver.IsEnabled(review, instance.ProjectId, gone.Id.Value, after));
        Assert.True(SkillActivationResolver.IsEnabled(lint, instance.ProjectId, other.Id.Value, after));

        // What the project has on, an agent with an off of its own does not have: the side of less power, and the screen shows the off.
        Assert.False(SkillActivationResolver.IsEnabled(tdd, instance.ProjectId, gone.Id.Value, after));
        Assert.True(SkillActivationResolver.IsEnabled(tdd, instance.ProjectId, other.Id.Value, after));

        // The history is only added to: the six decisions are where they were, and the two skills that were on got one more.
        Assert.Equal(before.Count + 2, after.Count);
        Assert.Equal(before, after.Take(before.Count));
        using var connection = instance.Connect();
        Assert.Equal(6, Scalar(connection, "SELECT COUNT(*) FROM skill_activations WHERE decided_by = 'manuel'"));
        Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM skill_activations WHERE decided_by = 'roster:agent-removed' AND enabled = 0 AND scope = 'Agent' AND scope_id = '" + gone.Id.Value + "'"));

        // Once closed there is nothing left to close: taking it out again writes nothing.
        await instance.Roster.DeleteAsync(instance.ProjectId, gone.Id, Ct);
        await instance.Roster.DeleteAsync(instance.ProjectId, gone.Id, Ct);
        Assert.Equal(8, Scalar(connection, "SELECT COUNT(*) FROM skill_activations"));
    }

    [Fact]
    public async Task Deleting_by_id_leaves_alone_the_rows_and_the_activations_of_another_project()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        using var connection = instance.Connect();
        Run(connection, "INSERT INTO projects(id, name, root_path, created_at) VALUES ('otro', 'otro', 'C:/otro', '2026-01-01T10:00:00.0000000+00:00')");
        Run(connection, "INSERT INTO agents(id, project_id, name, role, provider_id, vendor_id, definition_json, created_at) VALUES ('foraneo', 'otro', 'n', 'Executor', 'p', 'v', '{}', '2026-01-01T10:00:00.0000000+00:00')");
        Run(connection, "INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('otro', 'tdd', 'Agent', 'foraneo', 1, 'manuel', '2026-01-01T10:00:00.0000000+00:00')");

        // The id is the primary key of the whole table, so only the project filter keeps this call from taking the other project's row.
        await instance.Roster.DeleteAsync(instance.ProjectId, new AgentId("foraneo"), Ct);

        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM agents WHERE id = 'foraneo' AND project_id = 'otro'"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM skill_activations"));
    }

    [Fact]
    public async Task An_agent_that_a_row_of_tasks_points_at_is_not_deleted_and_nothing_of_the_removal_is_kept()
    {
        // tasks.assigned_agent_id is the only column that references agents(id). No store writes tasks (0002 leaves them unused), so
        // in the app this never happens; a row put there from outside does stop the delete, and the foreign keys are on.
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var agent = FullRoster()[1];
        await instance.Roster.SaveAsync(instance.ProjectId, agent, Ct);
        using var connection = instance.Connect();
        Run(connection, $"INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at) VALUES ('{instance.ProjectId}', 'tdd', 'Agent', '{agent.Id.Value}', 1, 'manuel', '2026-01-01T10:00:00.0000000+00:00')");
        Run(connection, $"INSERT INTO tasks(id, project_id, title, kind, status, assigned_agent_id, max_retries, retry_count, requires_evidence, created_at, last_activity_at) VALUES ('t1', '{instance.ProjectId}', 't', 'k', 'Pending', '{agent.Id.Value}', 3, 0, 0, '2026-01-01T10:00:00.0000000+00:00', '2026-01-01T10:00:00.0000000+00:00')");

        var ex = await Assert.ThrowsAsync<SqliteException>(() => instance.Roster.DeleteAsync(instance.ProjectId, agent.Id, Ct));

        // The agent stays and, because it is one transaction, the closing of its skills that was written before the delete is not kept.
        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([agent.Id], (await instance.Roster.ListAsync(instance.ProjectId, Ct)).Select(a => a.Id));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM skill_activations"));

        // With the row gone it is taken out, and its skills are closed.
        Run(connection, "DELETE FROM tasks WHERE id = 't1'");
        await instance.Roster.DeleteAsync(instance.ProjectId, agent.Id, Ct);
        Assert.Empty(await instance.Roster.ListAsync(instance.ProjectId, Ct));
        Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM skill_activations"));
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
