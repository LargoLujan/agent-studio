using AgentStudio.Application.Work;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Infrastructure.Persistence.Json;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The versions of the items of the backlog in <c>backlog_items</c> (append-only, docs/16 D-18, ADR-0038): one row per version, with
/// all of its content, never edited or deleted. There is no update or delete operation.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The row keeps the canonical SHA-256 of its content (<see cref="BacklogItem.Sha256"/>, the one <c>BacklogItemRecorded</c>
/// announces). A row read back is rebuilt and hashed again: one that no longer gives the hash of its row was changed behind the
/// store's back and is an <see cref="InvalidDataException"/>. The service still compares that hash with the announced one, and
/// reads only rows the log announced.</item>
/// <item>Appending the same row twice writes nothing, so completing an announcement whose row was never stored is harmless; another
/// row with the same item and version is refused with an <see cref="InvalidOperationException"/>.</item>
/// </list>
/// </remarks>
public sealed class SqliteBacklogStore(ProjectDatabases databases) : IBacklogStore
{
    private const string Columns = "item_id, version, project_id, created_at, statement, priority, criteria_json, content_sha256";

    public async Task AppendAsync(string projectId, BacklogItem item, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(item);
        var row = Row.From(projectId, item);

        await using var connection = databases.Connect(projectId);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var select = Command(connection, $"SELECT {Columns} FROM backlog_items WHERE item_id = $item AND version = $version;", transaction))
        {
            select.Parameters.AddWithValue("$item", row.ItemId);
            select.Parameters.AddWithValue("$version", row.Version);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Row.Read(reader) != row)
                {
                    throw new InvalidOperationException($"Version {row.Version} of backlog item '{row.ItemId}' already exists with other content; a version is written once.");
                }

                return;
            }
        }

        await using (var insert = Command(connection, $"""
            INSERT INTO backlog_items({Columns})
            VALUES ($item, $version, $project, $at, $statement, $priority, $criteria, $hash);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$item", row.ItemId);
            insert.Parameters.AddWithValue("$version", row.Version);
            insert.Parameters.AddWithValue("$project", row.ProjectId);
            insert.Parameters.AddWithValue("$at", row.CreatedAt);
            insert.Parameters.AddWithValue("$statement", row.Statement);
            insert.Parameters.AddWithValue("$priority", row.Priority);
            insert.Parameters.AddWithValue("$criteria", row.Criteria);
            insert.Parameters.AddWithValue("$hash", row.ContentSha256);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BacklogItem>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, $"SELECT {Columns} FROM backlog_items WHERE project_id = $project ORDER BY rowid;");
        select.Parameters.AddWithValue("$project", projectId);

        var items = new List<BacklogItem>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Row.Read(reader).ToDomain());
        }

        return items;
    }

    public async Task<BacklogItem?> FindAsync(string projectId, BacklogItemId id, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value, nameof(id));
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, $"SELECT {Columns} FROM backlog_items WHERE project_id = $project AND item_id = $item AND version = $version;");
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$item", id.Value);
        select.Parameters.AddWithValue("$version", version);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Row.Read(reader).ToDomain() : null;
    }

    /// <summary>A row of <c>backlog_items</c> as text, which is also how two versions are compared.</summary>
    private sealed record Row(string ItemId, int Version, string ProjectId, string CreatedAt, string Statement, string Priority, string Criteria, string ContentSha256)
    {
        public static Row From(string projectId, BacklogItem item)
        {
            var problems = item.Validate();
            if (problems.Count > 0)
            {
                throw new ArgumentException($"The backlog item is not valid: {string.Join(", ", problems)}.", nameof(item));
            }

            return new Row(
                item.Id.Value,
                item.Version,
                projectId,
                Time(item.CreatedAt),
                item.Statement,
                item.Priority.ToString(),
                PersistenceJson.Serialize((item.Criteria ?? []).OrderBy(c => c.Id, StringComparer.Ordinal).Select(CriterionJson.From).ToList()),
                item.Sha256());
        }

        public static Row Read(SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));

        public BacklogItem ToDomain()
        {
            var what = $"Backlog item '{ItemId}' version {Version}";
            if (!TryParseTime(CreatedAt, out var createdAt))
            {
                throw new InvalidDataException($"{what} has created_at '{CreatedAt}', which is not a round-trip time.");
            }

            if (!TryParseEnum<RequirementPriority>(Priority, out var priority))
            {
                throw new InvalidDataException($"{what} has the priority '{Priority}', which does not exist: it was not stored by this store.");
            }

            var criteria = PersistenceJson.Deserialize<List<CriterionJson>>(Criteria, $"{what}: criteria");
            var item = PersistenceJson.Rebuild(
                () => new BacklogItem(new BacklogItemId(ItemId), Version, Statement, priority, [.. criteria.Select(c => c.ToDomain())], createdAt),
                what);
            if (item.Validate().Count > 0)
            {
                throw new InvalidDataException($"{what} is not valid: {string.Join(", ", item.Validate())}.");
            }

            var hash = item.Sha256();
            if (!string.Equals(hash, ContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{what} no longer matches the hash of its row (its content hashes to {hash}): it was modified.");
            }

            return item;
        }
    }
}
