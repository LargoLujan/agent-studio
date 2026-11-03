using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Decisions;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The decision log in the <c>decision_log</c> table, append-only by contract and by trigger. It stores each entry
/// exactly as the domain produced it: the sequence and the hashes come from <see cref="DecisionLog"/> and the time is
/// stored as the same UTC round-trip text the hash covers, so a log read back verifies with the same head. Reading
/// does not verify (that is <see cref="DecisionLog.Verify"/>'s job, called by the recorder); a row that is not even a
/// valid entry is reported with its sequence.
/// </summary>
public sealed class SqliteDecisionLogStore(ProjectDatabases databases) : IDecisionLogStore
{
    public async Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Actor);

        await using var connection = databases.Connect(projectId);
        await using var insert = Command(connection, """
            INSERT INTO decision_log(project_id, sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json,
                                     corrects_sequence, previous_hash, hash)
            VALUES ($project, $sequence, $at, $actorKind, $actorId, $kind, $summary, $payload, $corrects, $previous, $hash);
            """);
        insert.Parameters.AddWithValue("$project", projectId);
        insert.Parameters.AddWithValue("$sequence", entry.Sequence);
        insert.Parameters.AddWithValue("$at", Time(entry.RecordedAt));
        insert.Parameters.AddWithValue("$actorKind", entry.Actor.Kind.ToString());
        insert.Parameters.AddWithValue("$actorId", entry.Actor.Id);
        insert.Parameters.AddWithValue("$kind", entry.Kind.ToString());
        insert.Parameters.AddWithValue("$summary", entry.Summary);
        insert.Parameters.AddWithValue("$payload", entry.PayloadJson);
        insert.Parameters.AddWithValue("$corrects", OrNull(entry.Corrects));
        insert.Parameters.AddWithValue("$previous", entry.PreviousHash);
        insert.Parameters.AddWithValue("$hash", entry.Hash);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = databases.Connect(projectId);
        await using var select = Command(connection, """
            SELECT sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, corrects_sequence, previous_hash, hash
            FROM decision_log
            WHERE project_id = $project
            ORDER BY sequence;
            """);
        select.Parameters.AddWithValue("$project", projectId);

        var entries = new List<DecisionEntry>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader, projectId));
        }

        return entries;
    }

    private static DecisionEntry ReadEntry(SqliteDataReader reader, string projectId)
    {
        var sequence = reader.GetInt64(0);
        string Problem(string what) => $"decision_log entry {sequence} of project '{projectId}' is not a valid entry: {what}.";

        var recordedAt = reader.GetString(1);
        if (!TryParseTime(recordedAt, out var at))
        {
            throw new InvalidDataException(Problem($"recorded_at '{recordedAt}' is not a round-trip time"));
        }

        var actorKind = reader.GetString(2);
        if (!TryParseEnum<ActorKind>(actorKind, out var actor))
        {
            throw new InvalidDataException(Problem($"unknown actor kind '{actorKind}'"));
        }

        var kindText = reader.GetString(4);
        if (!TryParseEnum<DecisionKind>(kindText, out var kind))
        {
            throw new InvalidDataException(Problem($"unknown kind '{kindText}'"));
        }

        return new DecisionEntry(
            sequence,
            at,
            new Actor(actor, reader.GetString(3)),
            kind,
            reader.GetString(5),
            reader.GetString(6),
            NullableInt64(reader, 7),
            reader.GetString(8),
            reader.GetString(9));
    }
}
