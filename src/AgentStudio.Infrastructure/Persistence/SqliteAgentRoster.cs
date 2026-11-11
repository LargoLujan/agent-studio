using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Infrastructure.Persistence.Json;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The agent template of a project in <c>agents</c>. Unlike the history tables it can change: saving an agent again
/// replaces its definition in place and deleting one takes it out (each run and each debate freezes the definitions it
/// started with, so changing the template never rewrites the past). Only valid definitions are saved, so a judge with write
/// access never reaches the template.
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

    /// <summary>
    /// What <c>skill_activations.decided_by</c> says of the rows that close the activations of an agent that was taken out: nobody
    /// chose them one by one, the removal did.
    /// </summary>
    internal const string ActivationsClosedBy = "roster:agent-removed";

    /// <summary>
    /// In one transaction, the row goes or stays together with the closing of the activations of the agent. An id that is not in
    /// the template of this project (never saved, deleted already or belonging to another project, whose rows the
    /// <c>project_id</c> guard leaves alone) deletes nothing and is no error.
    /// <para>
    /// The activations of skills for an agent (<c>skill_activations</c>, scope <c>Agent</c>, <c>scope_id</c> the id of the agent)
    /// are a history that is never rewritten, and the resolver reads the last decision of the scope: an agent added later with the
    /// same id (the editor gives the first free <c>ejecutor-2</c>) would inherit them without the screen of skills showing it. So
    /// every skill whose last decision for this agent is «on» gets one more row, «off», for the agent that goes; no row is changed
    /// or deleted. An append-only history has no way to say «no decision», so the agent that comes later starts with those skills
    /// off at its own level, which wins over what the project has on: the side of less power, and one switch of the screen to turn
    /// them on. Rows are written whether or not the agent was in the template, so what an earlier removal left behind is closed too;
    /// when nothing is on, nothing is written. If deleting fails (a row of <c>tasks</c> still points at the agent), none of it is kept.
    /// </para>
    /// </summary>
    public async Task DeleteAsync(string projectId, AgentId agent, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var close = Command(connection, """
            INSERT INTO skill_activations(project_id, skill_id, scope, scope_id, enabled, decided_by, decided_at)
            SELECT a.project_id, a.skill_id, 'Agent', a.scope_id, 0, $by, $at
            FROM skill_activations a
            WHERE a.project_id = $project AND a.scope = 'Agent' AND a.scope_id = $id AND a.enabled = 1
              AND a.id = (SELECT MAX(b.id) FROM skill_activations b
                          WHERE b.project_id = a.project_id AND b.skill_id = a.skill_id AND b.scope = 'Agent' AND b.scope_id = a.scope_id);
            """, transaction))
        {
            close.Parameters.AddWithValue("$by", ActivationsClosedBy);
            close.Parameters.AddWithValue("$at", Time(timeProvider.GetUtcNow()));
            close.Parameters.AddWithValue("$project", projectId);
            close.Parameters.AddWithValue("$id", agent.Value);
            await close.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = Command(connection, "DELETE FROM agents WHERE id = $id AND project_id = $project;", transaction))
        {
            delete.Parameters.AddWithValue("$id", agent.Value);
            delete.Parameters.AddWithValue("$project", projectId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
