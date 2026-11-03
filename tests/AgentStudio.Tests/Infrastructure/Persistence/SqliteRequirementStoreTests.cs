using System.IO;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Domain.Orchestration;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

public sealed class SqliteRequirementStoreTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FrozenRequirementSet Other() =>
        FrozenRequirementSet.Freeze([Req("RQ-09", RequirementPriority.Mandatory, "Otra cosa: «comillas» y ñ", Crit("c9"))], T0.AddDays(1));

    [Fact]
    public async Task A_saved_set_is_found_by_its_hash_with_the_same_content()
    {
        var set = PlanFixtures.Requirements();
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            await first.Requirements.SaveAsync(first.ProjectId, set, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await second.Requirements.FindAsync(second.ProjectId, set.ContentHash, Ct);

        Assert.NotNull(found);
        Assert.Equal(set.ContentHash, found.ContentHash);
        Assert.Equal(set.FrozenAt, found.FrozenAt);
        Assert.Equal(Shape(set.Requirements), Shape(found.Requirements));
    }

    [Fact]
    public async Task Saving_a_set_that_is_already_stored_writes_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var set = PlanFixtures.Requirements();

        await instance.Requirements.SaveAsync(instance.ProjectId, set, Ct);
        await instance.Requirements.SaveAsync(instance.ProjectId, FrozenRequirementSet.Freeze(set.Requirements, T0.AddHours(5)), Ct);

        using var connection = instance.Connect();
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM requirement_sets"));
    }

    [Fact]
    public async Task The_latest_set_is_the_last_one_saved()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        Assert.Null(await instance.Requirements.LatestAsync(instance.ProjectId, Ct));

        await instance.Requirements.SaveAsync(instance.ProjectId, PlanFixtures.Requirements(), Ct);
        await instance.Requirements.SaveAsync(instance.ProjectId, Other(), Ct);

        Assert.Equal(Other().ContentHash, (await instance.Requirements.LatestAsync(instance.ProjectId, Ct))?.ContentHash);
    }

    [Fact]
    public async Task An_unknown_hash_finds_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Null(await instance.Requirements.FindAsync(instance.ProjectId, HashA, Ct));
    }

    [Fact]
    public async Task A_set_whose_stored_content_was_changed_is_refused()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var set = PlanFixtures.Requirements();
        await instance.Requirements.SaveAsync(instance.ProjectId, set, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER requirement_sets_no_update");
            Exec(connection, "UPDATE requirement_sets SET payload_json = replace(payload_json, 'Add Calc.Multiply', 'Borrar todo')");
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => instance.Requirements.FindAsync(instance.ProjectId, set.ContentHash, Ct));

        Assert.Contains(set.ContentHash, ex.Message, StringComparison.Ordinal);
    }
}
