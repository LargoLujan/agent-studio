using System.IO;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

public sealed class SqlitePlanStoreTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The fixture plan plus every predicate type, a timeout and a branch, so every member makes the round trip.</summary>
    private static PlanDefinition RichPlan() => Plan(
    [
        .. ValidTasks(),
        Spec("T9", WorkTaskKind.Condition) with { DependsOn = [Dep("T8")], Condition = new OutputEquals(Tid("T1"), "estado", "listo") },
        Spec("T10", WorkTaskKind.Condition) with { DependsOn = [Dep("T9", "yes")], Condition = new PathsChanged(Tid("T1"), "src/Calc/") },
        Spec("T11", WorkTaskKind.Condition) with { DependsOn = [Dep("T10", "no")], Condition = new TaskSucceeded(Tid("T2")), Timeout = TimeSpan.FromTicks(123_456_789) },
    ]);

    [Fact]
    public async Task A_saved_plan_is_found_by_its_hash_after_reopening()
    {
        var plan = RichPlan();
        string saved;
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            saved = await first.Plans.SaveAsync(first.ProjectId, plan, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Plans.FindAsync(second.ProjectId, saved, Ct);

        Assert.Equal(plan.ContentHash, saved);
        Assert.NotNull(found);
        Assert.Equal(plan.ContentHash, found.ContentHash);
        Assert.Equal(PlanSource.OrchestratorAgent, found.Source);
        Assert.Equal(Aid("orquestador"), found.Author);
        Assert.Equal(plan.Summary, found.Summary);
        Assert.Equal(plan.Risks, found.Risks);
        Assert.Equal(plan.Tasks.Select(t => t.Condition), found.Tasks.Select(t => t.Condition));
        Assert.Equal(plan.Tasks.Select(t => t.Timeout), found.Tasks.Select(t => t.Timeout));
        Assert.Equal(Shape(plan.Tasks.Select(t => t.DependsOn)), Shape(found.Tasks.Select(t => t.DependsOn)));
    }

    [Fact]
    public async Task A_plan_written_by_the_user_keeps_its_source_and_no_author()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var plan = Plan(source: PlanSource.User, author: null);

        var found = await instance.Plans.FindAsync(instance.ProjectId, await instance.Plans.SaveAsync(instance.ProjectId, plan, Ct), Ct);

        Assert.Equal(PlanSource.User, found?.Source);
        Assert.Null(found?.Author);
    }

    [Fact]
    public async Task Saving_a_plan_that_is_already_stored_writes_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var plan = Plan();

        await instance.Plans.SaveAsync(instance.ProjectId, plan, Ct);
        var again = await instance.Plans.SaveAsync(instance.ProjectId, Plan(source: PlanSource.User, author: null), Ct);

        Assert.Equal(plan.ContentHash, again);
        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM plans"));
        Assert.Equal("OrchestratorAgent", Text(connection, "SELECT source FROM plans"));
    }

    [Fact]
    public async Task An_unknown_hash_finds_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Null(await instance.Plans.FindAsync(instance.ProjectId, HashB, Ct));
    }

    [Fact]
    public async Task A_plan_whose_stored_content_was_changed_is_refused()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var hash = await instance.Plans.SaveAsync(instance.ProjectId, Plan(), Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER plans_no_update");
            Exec(connection, "UPDATE plans SET payload_json = replace(payload_json, 'src/Calc/', 'src/')");
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => instance.Plans.FindAsync(instance.ProjectId, hash, Ct));

        Assert.Contains("modified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_stored_with_an_unknown_schema_version_is_refused()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var hash = await instance.Plans.SaveAsync(instance.ProjectId, Plan(), Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER plans_no_update");
            Exec(connection, "UPDATE plans SET schema_version = 99");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => instance.Plans.FindAsync(instance.ProjectId, hash, Ct));
    }
}
