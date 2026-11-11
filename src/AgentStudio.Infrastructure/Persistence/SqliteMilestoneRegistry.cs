using AgentStudio.Application.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Infrastructure.Persistence.Json;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The immutable header of each milestone in <c>milestones</c> (append-only, docs/16 D-17, ADR-0038): name, objective, the first set
/// of frozen requirements and the budget. The state of a milestone is never stored here; it comes from replaying the decision log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The row keeps the canonical hash of the header (<see cref="MilestoneHeaderHash.Of"/>, the one <c>MilestoneOpened</c> anchors).
/// A header read back is rebuilt and hashed again: one that no longer gives the hash of its row was changed behind the store's back
/// and is an <see cref="InvalidDataException"/>, as a debate header is, so it is never used. The service still compares that hash
/// with the anchored one.</item>
/// <item>Creating a milestone that already exists with the same header does nothing, so a retry after a lost reply (or the
/// completion of an opening whose announcement was never recorded) is harmless; the same id with another header is refused with an
/// <see cref="InvalidOperationException"/>.</item>
/// <item>The first set of requirements must already be stored: the foreign key refuses a header that points at nothing.</item>
/// </list>
/// </remarks>
public sealed class SqliteMilestoneRegistry(ProjectDatabases databases) : IMilestoneRegistry
{
    private const string Columns = "id, project_id, created_at, name, objective, requirement_set_hash, budget_json, header_hash";

    public async Task CreateAsync(string projectId, MilestoneHeader header, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(header);
        var row = Row.From(projectId, header);

        await using var connection = databases.Connect(projectId);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var select = Command(connection, $"SELECT {Columns} FROM milestones WHERE id = $id;", transaction))
        {
            select.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Row.Read(reader) != row)
                {
                    throw new InvalidOperationException($"Milestone '{row.Id}' already exists with a different header; a milestone header never changes.");
                }

                return;
            }
        }

        await using (var insert = Command(connection, $"""
            INSERT INTO milestones({Columns})
            VALUES ($id, $project, $at, $name, $objective, $requirements, $budget, $hash);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$project", row.ProjectId);
            insert.Parameters.AddWithValue("$at", row.CreatedAt);
            insert.Parameters.AddWithValue("$name", row.Name);
            insert.Parameters.AddWithValue("$objective", row.Objective);
            insert.Parameters.AddWithValue("$requirements", row.RequirementSetHash);
            insert.Parameters.AddWithValue("$budget", OrNull(row.Budget));
            insert.Parameters.AddWithValue("$hash", row.HeaderHash);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MilestoneHeader?> FindAsync(string projectId, MilestoneId id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value, nameof(id));
        var headers = await ReadAsync(projectId, "AND id = $id", id.Value, cancellationToken).ConfigureAwait(false);
        return headers.Count == 0 ? null : headers[0];
    }

    public Task<IReadOnlyList<MilestoneHeader>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        ReadAsync(projectId, string.Empty, null, cancellationToken);

    private async Task<IReadOnlyList<MilestoneHeader>> ReadAsync(string projectId, string filter, string? id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, $"SELECT {Columns} FROM milestones WHERE project_id = $project {filter} ORDER BY rowid;");
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$id", OrNull(id));

        var headers = new List<MilestoneHeader>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            headers.Add(Row.Read(reader).ToDomain());
        }

        return headers;
    }

    /// <summary>A row of <c>milestones</c> as text, which is also how two headers are compared.</summary>
    private sealed record Row(string Id, string ProjectId, string CreatedAt, string Name, string Objective, string RequirementSetHash, string? Budget, string HeaderHash)
    {
        public static Row From(string projectId, MilestoneHeader header)
        {
            var id = header.Id.Value ?? throw new ArgumentException("The milestone has no id.", nameof(header));
            if (string.IsNullOrWhiteSpace(header.Name) || string.IsNullOrWhiteSpace(header.Objective) || string.IsNullOrWhiteSpace(header.RequirementSetHash))
            {
                throw new ArgumentException($"Milestone '{id}' has no name, no objective or no requirements.", nameof(header));
            }

            header.Budget?.EnsureValid();
            return new Row(
                id,
                projectId,
                Time(header.CreatedAt),
                header.Name,
                header.Objective,
                header.RequirementSetHash,
                header.Budget is { } budget ? PersistenceJson.Serialize(MilestoneBudgetJson.From(budget)) : null,
                MilestoneHeaderHash.Of(header));
        }

        public static Row Read(SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            NullableString(reader, 6),
            reader.GetString(7));

        public MilestoneHeader ToDomain()
        {
            var what = $"Milestone '{Id}'";
            if (!TryParseTime(CreatedAt, out var createdAt))
            {
                throw new InvalidDataException($"{what} has created_at '{CreatedAt}', which is not a round-trip time.");
            }

            var budget = Budget is null ? null : PersistenceJson.Deserialize<MilestoneBudgetJson>(Budget, $"{what}: budget");
            var header = PersistenceJson.Rebuild(
                () =>
                {
                    var rebuilt = new MilestoneHeader(new MilestoneId(Id), Name, Objective, RequirementSetHash, budget?.ToDomain(), createdAt);
                    rebuilt.Budget?.EnsureValid();
                    return rebuilt;
                },
                what);

            var hash = MilestoneHeaderHash.Of(header);
            if (!string.Equals(hash, HeaderHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{what} no longer matches the hash of its row (its header hashes to {hash}): it was modified.");
            }

            return header;
        }
    }
}
