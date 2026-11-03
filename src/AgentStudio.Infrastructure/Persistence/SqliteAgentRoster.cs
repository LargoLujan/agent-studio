using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Agents;
using AgentStudio.Infrastructure.Persistence.Json;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The agent template of a project in <c>agents</c>. Unlike the history tables it can change: saving an agent again
/// replaces its definition in place (each run freezes the roster it started with, so changing the template never
/// rewrites a past run). Only valid definitions are saved, so a judge with write access never reaches the template.
/// </summary>
public sealed class SqliteAgentRoster(ProjectDatabases databases, TimeProvider timeProvider) : IAgentRoster
{
    public async Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, "SELECT id, definition_json FROM agents WHERE project_id = $project ORDER BY rowid;");
        select.Parameters.AddWithValue("$project", projectId);

        var agents = new List<AgentDefinition>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var what = $"Agent '{reader.GetString(0)}'";
            var json = PersistenceJson.Deserialize<AgentJson>(reader.GetString(1), what);
            agents.Add(PersistenceJson.Rebuild(json.ToDomain, what));
        }

        return agents;
    }

    public async Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(agent.Model);
        agent.EnsureValid();

        await using var connection = databases.Connect(projectId);
        await using var upsert = Command(connection, """
            INSERT INTO agents(id, project_id, name, role, provider_id, vendor_id, model, definition_json, created_at)
            VALUES ($id, $project, $name, $role, $provider, $vendor, $model, $definition, $at)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, role = excluded.role, provider_id = excluded.provider_id, vendor_id = excluded.vendor_id,
                model = excluded.model, definition_json = excluded.definition_json
            WHERE agents.project_id = excluded.project_id;
            """);
        upsert.Parameters.AddWithValue("$id", agent.Id.Value);
        upsert.Parameters.AddWithValue("$project", projectId);
        upsert.Parameters.AddWithValue("$name", agent.Name);
        upsert.Parameters.AddWithValue("$role", agent.Role.ToString());
        upsert.Parameters.AddWithValue("$provider", agent.Model.Provider.Value);
        upsert.Parameters.AddWithValue("$vendor", agent.Model.Vendor.Value);
        upsert.Parameters.AddWithValue("$model", OrNull(agent.Model.Model));
        upsert.Parameters.AddWithValue("$definition", PersistenceJson.Serialize(AgentJson.From(agent)));
        upsert.Parameters.AddWithValue("$at", Time(timeProvider.GetUtcNow()));
        if (await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException($"Agent id '{agent.Id}' belongs to another project.");
        }
    }
}
