using AgentStudio.Application.Debate;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Persistence.Json;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The immutable header of each whole-project audit in <c>project_audits</c> (append-only, docs/15 D-28 and section 6): the
/// commit audited, the requirements and the request (evidence) in their columns, and whether there were uncommitted
/// changes, the trusted verification profile frozen for it, the judge with its whole definition, the producers according to
/// the log and the declared authors in <c>header_json</c>. The rules are those of <see cref="SqliteDebateRegistry"/>: the row
/// keeps the hash <c>ProjectAuditStarted</c> anchors (<see cref="DebateHeaderHash.Of(ProjectAuditRecord)"/>), a header that no
/// longer gives it is an <see cref="InvalidDataException"/>, the same header twice writes nothing and another one under the
/// same id is refused.
/// </summary>
public sealed class SqliteProjectAuditRegistry(ProjectDatabases databases) : IProjectAuditRegistry
{
    private const string Columns = "id, project_id, created_at, head_commit, requirement_set_hash, request_evidence_id, header_json, header_hash";

    public async Task CreateAsync(ProjectAuditRecord audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.ProjectId, nameof(audit));
        var row = Row.From(audit);

        await using var connection = databases.Connect(audit.ProjectId);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var select = Command(connection, $"SELECT {Columns} FROM project_audits WHERE id = $id;", transaction))
        {
            select.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Row.Read(reader) != row)
                {
                    throw new InvalidOperationException($"Audit '{row.Id}' already exists with a different header; an audit header never changes.");
                }

                return;
            }
        }

        await using (var insert = Command(connection, $"""
            INSERT INTO project_audits({Columns})
            VALUES ($id, $project, $at, $head, $requirements, $request, $header, $hash);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$project", row.ProjectId);
            insert.Parameters.AddWithValue("$at", row.CreatedAt);
            insert.Parameters.AddWithValue("$head", OrNull(row.HeadCommit));
            insert.Parameters.AddWithValue("$requirements", row.RequirementSetHash);
            insert.Parameters.AddWithValue("$request", row.Request);
            insert.Parameters.AddWithValue("$header", row.Header);
            insert.Parameters.AddWithValue("$hash", row.HeaderHash);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectAuditRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value, nameof(id));
        var audits = await ReadAsync(projectId, "AND id = $id", id.Value, cancellationToken).ConfigureAwait(false);
        return audits.Count == 0 ? null : audits[0];
    }

    public Task<IReadOnlyList<ProjectAuditRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        ReadAsync(projectId, string.Empty, null, cancellationToken);

    private async Task<IReadOnlyList<ProjectAuditRecord>> ReadAsync(string projectId, string filter, string? id, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, $"SELECT {Columns} FROM project_audits WHERE project_id = $project {filter} ORDER BY rowid;");
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$id", OrNull(id));

        var audits = new List<ProjectAuditRecord>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            audits.Add(Row.Read(reader).ToDomain());
        }

        return audits;
    }

    /// <summary>A row of <c>project_audits</c> as text, which is also how two headers are compared.</summary>
    private sealed record Row(
        string Id,
        string ProjectId,
        string CreatedAt,
        string? HeadCommit,
        string RequirementSetHash,
        string Request,
        string Header,
        string HeaderHash)
    {
        /// <exception cref="ArgumentException">The header has a missing member.</exception>
        public static Row From(ProjectAuditRecord audit)
        {
            var id = audit.Id.Value ?? throw new ArgumentException("The audit has no id.", nameof(audit));
            if (audit.Verification is null || audit.Judge is null || string.IsNullOrWhiteSpace(audit.RequirementSetHash))
            {
                throw new ArgumentException($"Audit '{id}' has no verification profile, judge or requirements.", nameof(audit));
            }

            if (audit.ProducerVendors is null || audit.DeclaredAuthors is null)
            {
                throw new ArgumentException($"Audit '{id}' has no list of producer vendors or declared authors.", nameof(audit));
            }

            return new Row(
                id,
                audit.ProjectId,
                Time(audit.CreatedAt),
                audit.HeadCommit,
                audit.RequirementSetHash,
                audit.Request.Value ?? throw new ArgumentException($"Audit '{id}' has no request evidence.", nameof(audit)),
                PersistenceJson.Serialize(ProjectAuditHeaderJson.From(audit)),
                DebateHeaderHash.Of(audit));
        }

        public static Row Read(SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            NullableString(reader, 3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));

        public ProjectAuditRecord ToDomain()
        {
            var what = $"Audit '{Id}'";
            if (!TryParseTime(CreatedAt, out var createdAt))
            {
                throw new InvalidDataException($"{what} has created_at '{CreatedAt}', which is not a round-trip time.");
            }

            var header = PersistenceJson.Deserialize<ProjectAuditHeaderJson>(Header, $"{what}: header");
            var audit = PersistenceJson.Rebuild(
                () => new ProjectAuditRecord(
                    new RunId(Id),
                    ProjectId,
                    createdAt,
                    HeadCommit,
                    header.UncommittedChanges,
                    RequirementSetHash,
                    new EvidenceId(Request),
                    (header.Verification ?? throw new InvalidOperationException("The verification profile is missing.")).ToDomain(),
                    (header.Judge ?? throw new InvalidOperationException("The judge is missing.")).ToDomain(),
                    VendorsJson.ToDomain(header.ProducerVendors),
                    VendorsJson.ToDomain(header.DeclaredAuthors)),
                what);

            var hash = DebateHeaderHash.Of(audit);
            if (!string.Equals(hash, HeaderHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{what} no longer matches the hash of its row (its header hashes to {hash}): it was modified.");
            }

            return audit;
        }
    }
}
