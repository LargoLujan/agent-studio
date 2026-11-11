using System.IO;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// docs/16 D-18 and ADR-0038: the versions of the backlog in <c>backlog_items</c>. Each is found after reopening as it was written,
/// with the SHA-256 that <c>BacklogItemRecorded</c> announces, written once, listed in the order they were written, and a row changed
/// behind the store's back is never read.
/// </summary>
public sealed class SqliteBacklogStoreTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SqliteBacklogStore Store(ProjectInstance instance) => new(instance.Databases);

    private static BacklogItem Item(string id = "bk-20261111-a1b2c3", int version = 1, string statement = "Que el informe se exporte a \"PDF\"\ncon cabecera.") => new(
        new BacklogItemId(id),
        version,
        statement,
        RequirementPriority.Mandatory,
        [new AcceptanceCriterion("C2", "Se abre sin avisos", VerificationMethod.HumanReview), new AcceptanceCriterion("C1", "La prueba pasa", VerificationMethod.AutomatedTest)],
        T0.AddTicks(1_234_567 * version));

    [Fact]
    public async Task A_version_is_found_after_reopening_exactly_as_it_was_written()
    {
        var item = Item();
        using (var first = await ProjectInstance.OpenAsync(_folder.Root))
        {
            await Store(first).AppendAsync(first.ProjectId, item, Ct);
        }

        using var second = await ProjectInstance.OpenAsync(_folder.Root);
        var found = await Store(second).FindAsync(second.ProjectId, item.Id, 1, Ct);

        Assert.NotNull(found);
        Assert.Equal(item.Sha256(), found.Sha256());
        Assert.Equal(item.Statement, found.Statement);
        Assert.Equal(item.CreatedAt, found.CreatedAt);
        Assert.Equal(["C1", "C2"], found.Criteria.Select(c => c.Id));
        using var connection = second.Connect();
        Assert.Equal(item.Sha256(), Text(connection, "SELECT content_sha256 FROM backlog_items"));
    }

    [Fact]
    public async Task Versions_are_listed_in_the_order_they_were_written_and_each_stays()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var other = Item("bk-20261111-000000");
        await Store(instance).AppendAsync(instance.ProjectId, Item(), Ct);
        await Store(instance).AppendAsync(instance.ProjectId, other, Ct);
        await Store(instance).AppendAsync(instance.ProjectId, Item(version: 2, statement: "Que el informe se exporte a PDF o a HTML."), Ct);

        var items = await Store(instance).ListAsync(instance.ProjectId, Ct);

        Assert.Equal([("bk-20261111-a1b2c3", 1), ("bk-20261111-000000", 1), ("bk-20261111-a1b2c3", 2)], items.Select(i => (i.Id.Value, i.Version)));
        Assert.Null(await Store(instance).FindAsync(instance.ProjectId, other.Id, 2, Ct));
    }

    [Fact]
    public async Task Appending_the_same_row_again_writes_nothing()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var item = Item();

        await Store(instance).AppendAsync(instance.ProjectId, item, Ct);
        await Store(instance).AppendAsync(instance.ProjectId, item with { Criteria = [.. item.Criteria.Reverse()], CreatedAt = item.CreatedAt.ToOffset(TimeSpan.FromHours(3)) }, Ct);

        Assert.Single(await Store(instance).ListAsync(instance.ProjectId, Ct));
    }

    [Fact]
    public async Task Another_row_with_the_same_item_and_version_is_refused_and_the_first_stays()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var item = Item();
        await Store(instance).AppendAsync(instance.ProjectId, item, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(instance).AppendAsync(instance.ProjectId, item with { Statement = "Otra cosa" }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(instance).AppendAsync(instance.ProjectId, item with { Priority = RequirementPriority.Desirable }, Ct));

        var found = await Store(instance).FindAsync(instance.ProjectId, item.Id, 1, Ct);
        Assert.Equal(item.Sha256(), found!.Sha256());
    }

    [Fact]
    public async Task A_version_the_domain_does_not_allow_is_refused_before_it_is_written()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        await Assert.ThrowsAsync<ArgumentException>(() => Store(instance).AppendAsync(instance.ProjectId, Item(statement: " "), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Store(instance).AppendAsync(instance.ProjectId, Item() with { Version = 0 }, Ct));

        Assert.Empty(await Store(instance).ListAsync(instance.ProjectId, Ct));
    }

    public static TheoryData<string> Tamperings => new()
    {
        "UPDATE backlog_items SET statement = 'Haz otra cosa'",
        "UPDATE backlog_items SET priority = 'Desirable'",
        """UPDATE backlog_items SET criteria_json = '[]'""",
        "UPDATE backlog_items SET content_sha256 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'",
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task A_row_changed_behind_the_stores_back_is_never_read(string tampering)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var item = Item();
        await Store(instance).AppendAsync(instance.ProjectId, item, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER backlog_items_no_update");
            Exec(connection, tampering);
        }

        var find = await Assert.ThrowsAsync<InvalidDataException>(() => Store(instance).FindAsync(instance.ProjectId, item.Id, 1, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => Store(instance).ListAsync(instance.ProjectId, Ct));
        Assert.Contains(item.Id.Value, find.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> ForeignRows => new()
    {
        "UPDATE backlog_items SET criteria_json = 'no es json'",
        "UPDATE backlog_items SET created_at = 'ayer'",
        """UPDATE backlog_items SET criteria_json = '[{"id":"C1","description":"x","method":"Magia"}]'""",
    };

    [Theory]
    [MemberData(nameof(ForeignRows))]
    public async Task A_row_this_store_did_not_write_is_invalid_data(string tampering)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var item = Item();
        await Store(instance).AppendAsync(instance.ProjectId, item, Ct);
        using (var connection = instance.Connect())
        {
            Exec(connection, "DROP TRIGGER backlog_items_no_update");
            Exec(connection, tampering);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Store(instance).FindAsync(instance.ProjectId, item.Id, 1, Ct));
    }

    [Fact]
    public async Task The_table_refuses_update_delete_and_replace()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        var item = Item();
        await Store(instance).AppendAsync(instance.ProjectId, item, Ct);

        using var connection = instance.Connect();
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => Exec(connection, "UPDATE backlog_items SET statement = 'x'")).Message, StringComparison.Ordinal);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => Exec(connection, "DELETE FROM backlog_items")).Message, StringComparison.Ordinal);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => Exec(connection, $"""
            INSERT OR REPLACE INTO backlog_items(item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256)
            VALUES ('{item.Id.Value}', 1, '{instance.ProjectId}', '2026-11-11T00:00:00.0000000+00:00', 'x', 'Mandatory', '[]', 'h')
            """)).Message, StringComparison.Ordinal);
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM backlog_items"));
    }
}
