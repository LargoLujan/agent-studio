using System.Globalization;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Persistence.Json;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Immutable plans in <c>plans</c>, keyed by <see cref="PlanDefinition.ContentHash"/>. Saving a plan whose content is
/// already stored writes nothing (the provenance of the first save stays). A plan read back is created again, which
/// recomputes its hash; one that no longer matches its key was modified, and is an <see cref="InvalidDataException"/>.
/// </summary>
public sealed class SqlitePlanStore(ProjectDatabases databases, TimeProvider timeProvider) : IPlanStore
{
    public async Task<string> SaveAsync(string projectId, PlanDefinition plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var connection = databases.Connect(projectId);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var exists = Command(connection, "SELECT COUNT(*) FROM plans WHERE content_hash = $hash;", transaction))
        {
            exists.Parameters.AddWithValue("$hash", plan.ContentHash);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            {
                return plan.ContentHash;
            }
        }

        await using (var insert = Command(connection, """
            INSERT INTO plans(content_hash, project_id, source, author_agent_id, schema_version, created_at, payload_json)
            VALUES ($hash, $project, $source, $author, $version, $at, $payload);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$hash", plan.ContentHash);
            insert.Parameters.AddWithValue("$project", projectId);
            insert.Parameters.AddWithValue("$source", plan.Source.ToString());
            insert.Parameters.AddWithValue("$author", OrNull(plan.Author?.Value));
            insert.Parameters.AddWithValue("$version", PlanJson.Version);
            insert.Parameters.AddWithValue("$at", Time(timeProvider.GetUtcNow()));
            insert.Parameters.AddWithValue("$payload", PersistenceJson.Serialize(PlanJson.From(plan)));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan.ContentHash;
    }

    public async Task<PlanDefinition?> FindAsync(string projectId, string hash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, """
            SELECT source, author_agent_id, schema_version, payload_json FROM plans WHERE project_id = $project AND content_hash = $hash;
            """);
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$hash", hash);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var what = $"The plan {hash}";
        var sourceText = reader.GetString(0);
        if (!TryParseEnum<PlanSource>(sourceText, out var source))
        {
            throw new InvalidDataException($"{what} has an unknown source '{sourceText}'.");
        }

        var version = reader.GetInt32(2);
        if (version != PlanJson.Version)
        {
            throw new InvalidDataException($"{what} has schema version {version}; this version of Agent Studio reads version {PlanJson.Version}.");
        }

        var author = NullableString(reader, 1);
        var json = PersistenceJson.Deserialize<PlanJson>(reader.GetString(3), what);
        var plan = PersistenceJson.Rebuild(() => json.ToDomain(source, author is null ? null : new AgentId(author)), what);
        if (!string.Equals(plan.ContentHash, hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{what} no longer matches its hash (its content hashes to {plan.ContentHash}): it was modified.");
        }

        return plan;
    }
}
