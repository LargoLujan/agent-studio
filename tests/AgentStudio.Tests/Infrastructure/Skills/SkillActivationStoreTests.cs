using System.IO;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Tests.Infrastructure.Persistence;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Skills;

/// <summary>
/// Activation decisions are a history in which the last one wins: project and agent decisions in the project's
/// database, global ones in a JSONL file of the user profile (here, a temporary file).
/// </summary>
public sealed class SkillActivationStoreTests : IDisposable
{
    private static readonly SkillId Tdd = new("tdd");

    private readonly TempProjectFolder _folder = new();
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "as perfil " + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_profile);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string GlobalFile => Path.Combine(_profile, "AgentStudio", "skill-activations.jsonl");

    private SkillActivationStore Store(ProjectInstance instance) =>
        new(instance.Databases, instance.Clock, new SkillActivationStoreOptions(GlobalFile));

    private string[] GlobalLines() => File.ReadAllText(GlobalFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Project_and_agent_decisions_go_to_the_project_database()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var store = Store(instance);

        await store.AppendAsync(instance.ProjectId, new SkillActivation(Tdd, SkillScopeKind.Project, instance.ProjectId, true), "manuel", Ct);
        await store.AppendAsync(instance.ProjectId, new SkillActivation(Tdd, SkillScopeKind.Agent, "juez", false), "manuel", Ct);

        Assert.False(File.Exists(GlobalFile));
        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, $"SELECT COUNT(*) FROM skill_activations WHERE skill_id = 'tdd' AND scope = 'Project' AND scope_id = '{instance.ProjectId}' AND enabled = 1 AND decided_by = 'manuel' AND decided_at = '2026-01-01T10:00:00.0000000+00:00'"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM skill_activations WHERE scope = 'Agent' AND scope_id = 'juez' AND enabled = 0"));
    }

    [Fact]
    public async Task Global_decisions_go_to_the_file_of_the_user_profile_and_never_to_the_database()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        await Store(instance).AppendAsync(instance.ProjectId, new SkillActivation(Tdd, SkillScopeKind.Global, null, true), "manuel", Ct);

        var line = Assert.Single(GlobalLines());
        Assert.Contains("\"skill\":\"tdd\"", line, StringComparison.Ordinal);
        Assert.Contains("\"enabled\":true", line, StringComparison.Ordinal);
        Assert.Contains("\"decidedBy\":\"manuel\"", line, StringComparison.Ordinal);
        using var connection = instance.Connect();
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM skill_activations"));
    }

    [Fact]
    public async Task The_history_comes_back_oldest_first_so_the_last_decision_wins()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var store = Store(instance);
        var project = instance.ProjectId;

        await store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Global, null, true), "manuel", Ct);
        await store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Project, project, true), "manuel", Ct);
        await store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Project, project, false), "manuel", Ct);
        await store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Agent, "ejecutor", true), "manuel", Ct);
        var history = await store.ListAsync(project, Ct);

        Assert.Equal(
            [
                new SkillActivation(Tdd, SkillScopeKind.Global, null, true),
                new SkillActivation(Tdd, SkillScopeKind.Project, project, true),
                new SkillActivation(Tdd, SkillScopeKind.Project, project, false),
                new SkillActivation(Tdd, SkillScopeKind.Agent, "ejecutor", true),
            ],
            history);
        Assert.True(SkillActivationResolver.IsEnabled(Tdd, project, "ejecutor", history));
        Assert.False(SkillActivationResolver.IsEnabled(Tdd, project, "juez", history));
    }

    [Fact]
    public async Task Decisions_survive_reopening_and_global_ones_apply_to_every_project()
    {
        using var other = new TempProjectFolder();
        string project;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            project = first.ProjectId;
            var store = Store(first);
            await store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Global, null, true), "manuel", Ct);
            await store.AppendAsync(project, new SkillActivation(new SkillId("docs"), SkillScopeKind.Project, project, true), "manuel", Ct);
        }

        using var reopened = await ProjectInstance.OpenAsync(_folder.Root);
        using var second = await ProjectInstance.OpenAsync(other.Root);

        Assert.Equal(2, (await Store(reopened).ListAsync(project, Ct)).Count);
        Assert.Equal([new SkillActivation(Tdd, SkillScopeKind.Global, null, true)], await Store(second).ListAsync(second.ProjectId, Ct));
    }

    [Fact]
    public async Task Each_scope_names_what_it_applies_to()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var store = Store(instance);
        var project = instance.ProjectId;

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Project, "otro-proyecto", true), "manuel", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Agent, null, true), "manuel", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Global, "algo", true), "manuel", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(project, new SkillActivation(Tdd, SkillScopeKind.Project, project, true), " ", Ct));

        Assert.Empty(await store.ListAsync(project, Ct));
    }

    [Fact]
    public async Task A_line_cut_short_at_the_end_of_the_file_was_never_recorded_and_the_next_append_drops_it()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var store = Store(instance);
        await store.AppendAsync(instance.ProjectId, new SkillActivation(Tdd, SkillScopeKind.Global, null, true), "manuel", Ct);
        File.AppendAllText(GlobalFile, "{\"skill\":\"tdd\",\"enab");

        Assert.Equal([new SkillActivation(Tdd, SkillScopeKind.Global, null, true)], await store.ListAsync(instance.ProjectId, Ct));

        await store.AppendAsync(instance.ProjectId, new SkillActivation(Tdd, SkillScopeKind.Global, null, false), "manuel", Ct);

        Assert.Equal(2, GlobalLines().Length);
        Assert.Equal(
            [new SkillActivation(Tdd, SkillScopeKind.Global, null, true), new SkillActivation(Tdd, SkillScopeKind.Global, null, false)],
            await store.ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task A_damaged_line_inside_the_global_file_is_an_error_not_a_silent_change()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var store = Store(instance);
        await store.AppendAsync(instance.ProjectId, new SkillActivation(Tdd, SkillScopeKind.Global, null, true), "manuel", Ct);
        File.AppendAllText(GlobalFile, "esto no es json\n");
        await store.AppendAsync(instance.ProjectId, new SkillActivation(Tdd, SkillScopeKind.Global, null, false), "manuel", Ct);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync(instance.ProjectId, Ct));

        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_global_decisions_are_all_kept_one_per_line()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var store = Store(instance);

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Task.Run(
            () => store.AppendAsync(instance.ProjectId, new SkillActivation(new SkillId($"skill-{i}"), SkillScopeKind.Global, null, true), "manuel", Ct),
            Ct)));

        Assert.Equal(20, GlobalLines().Length);
        Assert.Equal(20, (await store.ListAsync(instance.ProjectId, Ct)).Select(a => a.Skill).Distinct().Count());
    }
}
