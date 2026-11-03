using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Requirements;
using AgentStudio.Infrastructure.Persistence.Json;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Frozen requirement sets in <c>requirement_sets</c>, keyed by their content hash and append-only. Saving a set that
/// is already stored writes nothing. A set read back is frozen again, which recomputes its hash: one that no longer
/// matches its key was modified, and is an <see cref="InvalidDataException"/>.
/// </summary>
public sealed class SqliteRequirementStore(ProjectDatabases databases) : IRequirementStore
{
    public async Task SaveAsync(string projectId, FrozenRequirementSet requirements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        await using var connection = databases.Connect(projectId);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var exists = Command(connection, "SELECT COUNT(*) FROM requirement_sets WHERE content_hash = $hash;", transaction))
        {
            exists.Parameters.AddWithValue("$hash", requirements.ContentHash);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        await using (var insert = Command(connection, """
            INSERT INTO requirement_sets(content_hash, project_id, frozen_at, payload_json) VALUES ($hash, $project, $at, $payload);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$hash", requirements.ContentHash);
            insert.Parameters.AddWithValue("$project", projectId);
            insert.Parameters.AddWithValue("$at", Time(requirements.FrozenAt));
            insert.Parameters.AddWithValue("$payload", PersistenceJson.Serialize(RequirementSetJson.From(requirements)));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<FrozenRequirementSet?> FindAsync(string projectId, string contentHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        return ReadOneAsync(projectId, "WHERE project_id = $project AND content_hash = $hash", contentHash, cancellationToken);
    }

    public Task<FrozenRequirementSet?> LatestAsync(string projectId, CancellationToken cancellationToken) =>
        ReadOneAsync(projectId, "WHERE project_id = $project ORDER BY rowid DESC LIMIT 1", null, cancellationToken);

    private async Task<FrozenRequirementSet?> ReadOneAsync(string projectId, string where, string? contentHash, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, $"SELECT content_hash, payload_json FROM requirement_sets {where};");
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$hash", OrNull(contentHash));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Read(reader.GetString(0), reader.GetString(1));
    }

    private static FrozenRequirementSet Read(string key, string payload)
    {
        var what = $"The requirement set {key}";
        var json = PersistenceJson.Deserialize<RequirementSetJson>(payload, what);
        if (json.V != RequirementSetJson.Version)
        {
            throw new InvalidDataException($"{what} has version {json.V}; this version of Agent Studio reads version {RequirementSetJson.Version}.");
        }

        var set = PersistenceJson.Rebuild(json.ToDomain, what);
        if (!string.Equals(set.ContentHash, key, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{what} no longer matches its hash (its content hashes to {set.ContentHash}): it was modified.");
        }

        return set;
    }
}
