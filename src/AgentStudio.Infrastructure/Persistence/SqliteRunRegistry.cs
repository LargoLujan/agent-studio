using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Persistence.Json;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The immutable header of each run in <c>orchestration_runs</c> (append-only): plan, requirements, request, base
/// commit, frozen verification profile, roster and options. The state of a run is never stored here; it comes from
/// replaying the decision log (ADR-0010). Creating a run that already exists with the same header does nothing, so a
/// retry after a lost reply is harmless; the same id with another header is refused.
/// </summary>
public sealed class SqliteRunRegistry(ProjectDatabases databases) : IRunRegistry
{
    private const string Columns = """
        id, project_id, created_at, plan_hash, requirement_set_hash, original_request_evidence_id, base_commit, base_ref,
        verification_profile_json, roster_json, options_json, follows_run_id
        """;

    public async Task CreateAsync(RunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.ProjectId, nameof(run));
        var row = Row.From(run);

        await using var connection = databases.Connect(run.ProjectId);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var select = Command(connection, $"SELECT {Columns} FROM orchestration_runs WHERE id = $id;", transaction))
        {
            select.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Row.Read(reader) != row)
                {
                    throw new InvalidOperationException($"Run '{row.Id}' already exists with a different header; a run header never changes.");
                }

                return;
            }
        }

        await using (var insert = Command(connection, $"""
            INSERT INTO orchestration_runs({Columns})
            VALUES ($id, $project, $at, $plan, $requirements, $request, $baseCommit, $baseRef, $profile, $roster, $options, $follows);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$project", row.ProjectId);
            insert.Parameters.AddWithValue("$at", row.CreatedAt);
            insert.Parameters.AddWithValue("$plan", row.PlanHash);
            insert.Parameters.AddWithValue("$requirements", row.RequirementSetHash);
            insert.Parameters.AddWithValue("$request", row.OriginalRequest);
            insert.Parameters.AddWithValue("$baseCommit", row.BaseCommit);
            insert.Parameters.AddWithValue("$baseRef", OrNull(row.BaseRef));
            insert.Parameters.AddWithValue("$profile", row.Profile);
            insert.Parameters.AddWithValue("$roster", row.Roster);
            insert.Parameters.AddWithValue("$options", row.Options);
            insert.Parameters.AddWithValue("$follows", OrNull(row.Follows));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value, nameof(id));
        var runs = await ReadAsync(projectId, "AND id = $id", id.Value, cancellationToken).ConfigureAwait(false);
        return runs.Count == 0 ? null : runs[0];
    }

    public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        ReadAsync(projectId, string.Empty, null, cancellationToken);

    private async Task<IReadOnlyList<RunRecord>> ReadAsync(string projectId, string filter, string? id, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, $"SELECT {Columns} FROM orchestration_runs WHERE project_id = $project {filter} ORDER BY rowid;");
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$id", OrNull(id));

        var runs = new List<RunRecord>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(Row.Read(reader).ToDomain());
        }

        return runs;
    }

    /// <summary>A row of <c>orchestration_runs</c> as text, which is also how two headers are compared.</summary>
    private sealed record Row(
        string Id,
        string ProjectId,
        string CreatedAt,
        string PlanHash,
        string RequirementSetHash,
        string OriginalRequest,
        string BaseCommit,
        string? BaseRef,
        string Profile,
        string Roster,
        string Options,
        string? Follows)
    {
        public static Row From(RunRecord run) => new(
            run.Id.Value ?? throw new ArgumentException("The run has no id.", nameof(run)),
            run.ProjectId,
            Time(run.CreatedAt),
            run.PlanHash,
            run.RequirementSetHash,
            run.OriginalRequest.Value ?? throw new ArgumentException("The run has no original request evidence.", nameof(run)),
            run.BaseCommit,
            run.BaseRef,
            PersistenceJson.Serialize(VerificationProfileJson.From(run.Verification)),
            PersistenceJson.Serialize(run.Roster.Select(AgentJson.From).ToList()),
            PersistenceJson.Serialize(RunOptionsJson.From(run.Options)),
            run.Follows?.Value);

        public static Row Read(SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            NullableString(reader, 7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            NullableString(reader, 11));

        public RunRecord ToDomain()
        {
            var what = $"Run '{Id}'";
            if (!TryParseTime(CreatedAt, out var createdAt))
            {
                throw new InvalidDataException($"{what} has created_at '{CreatedAt}', which is not a round-trip time.");
            }

            var profile = PersistenceJson.Deserialize<VerificationProfileJson>(Profile, $"{what}: verification profile");
            var roster = PersistenceJson.Deserialize<List<AgentJson>>(Roster, $"{what}: roster");
            var options = PersistenceJson.Deserialize<RunOptionsJson>(Options, $"{what}: options");
            return PersistenceJson.Rebuild(
                () => new RunRecord(
                    new RunId(Id),
                    ProjectId,
                    createdAt,
                    PlanHash,
                    RequirementSetHash,
                    new EvidenceId(OriginalRequest),
                    BaseCommit,
                    BaseRef,
                    profile.ToDomain(),
                    [.. roster.Select(a => a.ToDomain())],
                    options.ToDomain(),
                    Follows is null ? null : new RunId(Follows)),
                what);
        }
    }
}
