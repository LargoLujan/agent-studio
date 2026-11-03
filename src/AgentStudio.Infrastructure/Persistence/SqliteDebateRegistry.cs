using AgentStudio.Application.Coordination;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Persistence.Json;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The immutable header of each debate in <c>debate_sessions</c> (append-only, docs/15 D-01 and section 6): mode,
/// requirements, question, subject, commit of the copy and challenged verdict in their columns, and the participants and
/// judges with their whole definitions, the disposition, the producers, the waivers, the limits and the launches allowed in
/// <c>header_json</c>. The state of a debate is never stored here; it comes from replaying the decision log (ADR-0026).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The row keeps the canonical hash of the header (<see cref="DebateHeaderHash.Of(DebateRecord)"/>, the one
/// <c>DebateOpened</c> anchors). A header read back is rebuilt and hashed again: one that no longer gives the hash of its row
/// was changed behind the store's back and is an <see cref="InvalidDataException"/>, like a plan that no longer matches its
/// key, so it is never used. The consumer still compares that hash with the anchored one (N-05).</item>
/// <item>Creating a debate that already exists with the same header does nothing, so a retry after a lost reply is
/// harmless; the same id with another header is refused with an <see cref="InvalidOperationException"/>.</item>
/// <item>The question (evidence), the requirements and the challenged verdict must already be stored: their foreign keys
/// refuse a header that points at nothing.</item>
/// </list>
/// </remarks>
public sealed class SqliteDebateRegistry(ProjectDatabases databases) : IDebateRegistry
{
    private const string Columns = """
        id, project_id, created_at, mode, requirement_set_hash, question_evidence_id, subject_kind, subject_ref,
        snapshot_commit, challenges_verdict_id, header_json, header_hash
        """;

    public async Task CreateAsync(DebateRecord debate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(debate);
        ArgumentException.ThrowIfNullOrWhiteSpace(debate.ProjectId, nameof(debate));
        var row = Row.From(debate);

        await using var connection = databases.Connect(debate.ProjectId);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var select = Command(connection, $"SELECT {Columns} FROM debate_sessions WHERE id = $id;", transaction))
        {
            select.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Row.Read(reader) != row)
                {
                    throw new InvalidOperationException($"Debate '{row.Id}' already exists with a different header; a debate header never changes.");
                }

                return;
            }
        }

        await using (var insert = Command(connection, $"""
            INSERT INTO debate_sessions({Columns})
            VALUES ($id, $project, $at, $mode, $requirements, $question, $subjectKind, $subjectRef, $snapshot, $challenges, $header, $hash);
            """, transaction))
        {
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$project", row.ProjectId);
            insert.Parameters.AddWithValue("$at", row.CreatedAt);
            insert.Parameters.AddWithValue("$mode", row.Mode);
            insert.Parameters.AddWithValue("$requirements", row.RequirementSetHash);
            insert.Parameters.AddWithValue("$question", row.Question);
            insert.Parameters.AddWithValue("$subjectKind", row.SubjectKind);
            insert.Parameters.AddWithValue("$subjectRef", OrNull(row.SubjectRef));
            insert.Parameters.AddWithValue("$snapshot", OrNull(row.SnapshotCommit));
            insert.Parameters.AddWithValue("$challenges", OrNull(row.Challenges));
            insert.Parameters.AddWithValue("$header", row.Header);
            insert.Parameters.AddWithValue("$hash", row.HeaderHash);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DebateRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value, nameof(id));
        var debates = await ReadAsync(projectId, "AND id = $id", id.Value, cancellationToken).ConfigureAwait(false);
        return debates.Count == 0 ? null : debates[0];
    }

    public Task<IReadOnlyList<DebateRecord>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        ReadAsync(projectId, string.Empty, null, cancellationToken);

    private async Task<IReadOnlyList<DebateRecord>> ReadAsync(string projectId, string filter, string? id, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, $"SELECT {Columns} FROM debate_sessions WHERE project_id = $project {filter} ORDER BY rowid;");
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$id", OrNull(id));

        var debates = new List<DebateRecord>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            debates.Add(Row.Read(reader).ToDomain());
        }

        return debates;
    }

    /// <summary>A row of <c>debate_sessions</c> as text, which is also how two headers are compared.</summary>
    private sealed record Row(
        string Id,
        string ProjectId,
        string CreatedAt,
        string Mode,
        string RequirementSetHash,
        string Question,
        string SubjectKind,
        string? SubjectRef,
        string? SnapshotCommit,
        string? Challenges,
        string Header,
        string HeaderHash)
    {
        /// <exception cref="ArgumentException">The header holds something the domain could not: a missing member or an enum value that does not exist.</exception>
        public static Row From(DebateRecord debate)
        {
            var id = debate.Id.Value ?? throw new ArgumentException("The debate has no id.", nameof(debate));
            var subject = debate.Subject ?? throw new ArgumentException($"Debate '{id}' has no subject.", nameof(debate));
            if (debate.Limits is null || string.IsNullOrWhiteSpace(debate.RequirementSetHash))
            {
                throw new ArgumentException($"Debate '{id}' has no limits or no requirements.", nameof(debate));
            }

            if (debate.Participants is null || debate.Participants.Any(p => p?.Agent is null || p.Label is null || !Enum.IsDefined(p.Role)))
            {
                throw new ArgumentException($"Debate '{id}' has a missing participant, or one without an agent, a label or a role that exists.", nameof(debate));
            }

            if (debate.Judges is null || debate.Judges.Any(j => j is null))
            {
                throw new ArgumentException($"Debate '{id}' has a missing judge.", nameof(debate));
            }

            if (debate.ProducerVendors is null || debate.WaivedParticipantVendors is null)
            {
                throw new ArgumentException($"Debate '{id}' has no list of producer or waived vendors.", nameof(debate));
            }

            if (!Enum.IsDefined(debate.Mode) || !Enum.IsDefined(subject.Kind) || !Enum.IsDefined(debate.Judging))
            {
                throw new ArgumentException($"Debate '{id}' has a mode, a subject or a disposition that does not exist.", nameof(debate));
            }

            return new Row(
                id,
                debate.ProjectId,
                Time(debate.CreatedAt),
                debate.Mode.ToString(),
                debate.RequirementSetHash,
                debate.Question.Value ?? throw new ArgumentException($"Debate '{id}' has no question evidence.", nameof(debate)),
                subject.Kind.ToString(),
                subject.Reference,
                debate.SnapshotCommit,
                debate.Challenges,
                PersistenceJson.Serialize(DebateHeaderJson.From(debate)),
                DebateHeaderHash.Of(debate));
        }

        public static Row Read(SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            NullableString(reader, 7),
            NullableString(reader, 8),
            NullableString(reader, 9),
            reader.GetString(10),
            reader.GetString(11));

        public DebateRecord ToDomain()
        {
            var what = $"Debate '{Id}'";
            if (!TryParseTime(CreatedAt, out var createdAt))
            {
                throw new InvalidDataException($"{what} has created_at '{CreatedAt}', which is not a round-trip time.");
            }

            if (!TryParseEnum<DebateMode>(Mode, out var mode) || !TryParseEnum<DebateSubjectKind>(SubjectKind, out var subjectKind))
            {
                throw new InvalidDataException($"{what} has the mode '{Mode}' or the subject '{SubjectKind}', which do not exist: it was not stored by this store.");
            }

            var header = PersistenceJson.Deserialize<DebateHeaderJson>(Header, $"{what}: header");
            var debate = PersistenceJson.Rebuild(
                () => new DebateRecord(
                    new RunId(Id),
                    ProjectId,
                    createdAt,
                    mode,
                    RequirementSetHash,
                    new EvidenceId(Question),
                    new DebateSubject(subjectKind, SubjectRef),
                    SnapshotCommit,
                    header.ParticipantsToDomain(),
                    header.JudgesToDomain(),
                    header.JudgingToDomain(),
                    VendorsJson.ToDomain(header.ProducerVendors),
                    VendorsJson.ToDomain(header.WaivedParticipantVendors),
                    header.LimitsToDomain(),
                    header.MaxAgentRuns,
                    Challenges),
                what);

            var hash = DebateHeaderHash.Of(debate);
            if (!string.Equals(hash, HeaderHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{what} no longer matches the hash of its row (its header hashes to {hash}): it was modified.");
            }

            return debate;
        }
    }
}
