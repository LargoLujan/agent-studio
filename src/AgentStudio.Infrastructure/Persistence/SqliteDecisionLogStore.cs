using System.Globalization;
using System.Text.RegularExpressions;
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
/// <para>
/// It also reads only the tail of the log (<see cref="IDecisionLogTailReader"/>), in one read transaction, so the recorder
/// verifies only what was appended since it last verified the log. The stamp is this opening of the project, the schema
/// version of the database and the creation time of its file. The triggers keep <c>decision_log</c> append-only (no update,
/// no delete, no replace), so changing an entry that is already there through SQL means dropping one of them first,
/// which changes the schema version: left as it is, the stamp differs, and the recorder reads and verifies the whole log
/// again, as it does after the project is opened again. And the log continues only while those three triggers are exactly
/// the ones of the schema (<see cref="AppendOnlyTriggers"/>): with one missing or rewritten, an entry could be changed later
/// with no schema change at all, so every load reads and verifies the whole log until they are back. So do the three of
/// <c>memory_entries</c>, in the same database: the memory serves from memory the rows it verified against the log, and
/// checks them again at each whole verification of the log (<c>ProjectMemory</c>), so a row it could change in place must
/// make every load whole too. The marked entries must still be there, as many of them and the last with its hash; anything
/// after them is the tail.
/// </para>
/// <para>
/// The stamp is no proof that nothing changed. Whoever can write to the database can set its schema version back
/// (<c>PRAGMA schema_version</c>) or replace the file with a crafted copy that keeps its creation time, and bytes can be
/// written in place: the stamp stays as it was. So can a connection without recursive triggers (another program's: the
/// app turns them on), with <c>INSERT OR REPLACE</c> through the rowid of a row: the row it conflicts with is deleted
/// without its DELETE trigger, and a key no row has (a sequence, an id) passes the trigger of the insert, in
/// <c>decision_log</c> as in <c>memory_entries</c>, with the triggers intact and the same schema version. Those are found
/// by the recorder's whole verifications (at the critical moments, after a time and a number of loads, and on opening
/// the project again: ADR-0010, addenda of 2026-11-04 and 2026-11-05).
/// </para>
/// </summary>
public sealed partial class SqliteDecisionLogStore(ProjectDatabases databases) : IDecisionLogStore, IDecisionLogTailReader
{
    /// <summary>
    /// The triggers that keep <c>decision_log</c> append-only (<c>0001_initial.sql</c>) and <c>memory_entries</c> too
    /// (<c>0004_memory.sql</c>), as the schema creates them (the replace ones as <c>0007_replace_by_rowid.sql</c> recreates
    /// them, with the rowid closed), with their blanks collapsed: what <c>sqlite_master</c> must hold
    /// for the log to be read only from its tail.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> AppendOnlyTriggers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["decision_log_no_update"] = "CREATE TRIGGER decision_log_no_update BEFORE UPDATE ON decision_log BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END",
        ["decision_log_no_delete"] = "CREATE TRIGGER decision_log_no_delete BEFORE DELETE ON decision_log BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END",
        ["decision_log_no_replace"] = "CREATE TRIGGER decision_log_no_replace BEFORE INSERT ON decision_log WHEN EXISTS (SELECT 1 FROM decision_log WHERE project_id = NEW.project_id AND sequence = NEW.sequence) OR EXISTS (SELECT 1 FROM decision_log WHERE rowid = NEW.rowid) BEGIN SELECT RAISE(ABORT, 'decision_log is append-only'); END",
        ["memory_entries_no_update"] = "CREATE TRIGGER memory_entries_no_update BEFORE UPDATE ON memory_entries BEGIN SELECT RAISE(ABORT, 'memory_entries is append-only'); END",
        ["memory_entries_no_delete"] = "CREATE TRIGGER memory_entries_no_delete BEFORE DELETE ON memory_entries BEGIN SELECT RAISE(ABORT, 'memory_entries is append-only'); END",
        ["memory_entries_no_replace"] = "CREATE TRIGGER memory_entries_no_replace BEFORE INSERT ON memory_entries WHEN EXISTS (SELECT 1 FROM memory_entries WHERE id = NEW.id) OR EXISTS (SELECT 1 FROM memory_entries WHERE rowid = NEW.rowid) BEGIN SELECT RAISE(ABORT, 'memory_entries is append-only'); END",
    };

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
        return await ReadAfterAsync(connection, null, projectId, 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DecisionLogTail> ReadTailAsync(string projectId, DecisionLogMark? after, CancellationToken cancellationToken)
    {
        var storage = databases.Get(projectId);
        var opening = databases.OpeningOf(projectId);
        await using var connection = databases.Connect(projectId);

        // One snapshot for every read below: what the stamp says, the marked entries and the tail are of the same moment.
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using (var version = Command(connection, "PRAGMA schema_version;", transaction))
        {
            var schema = Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var stamp = string.Create(CultureInfo.InvariantCulture, $"{opening}:{schema}:{File.GetCreationTimeUtc(storage.DatabasePath).Ticks}");
            if (after is not null
                && (!string.Equals(stamp, after.Stamp, StringComparison.Ordinal)
                    || !await AppendOnlyAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
                    || !await ContinuesAsync(connection, transaction, projectId, after, cancellationToken).ConfigureAwait(false)))
            {
                return new DecisionLogTail(stamp, false, []);
            }

            var entries = await ReadAfterAsync(connection, transaction, projectId, after?.EntryCount ?? 0, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DecisionLogTail(stamp, true, entries);
        }
    }

    /// <summary>Whether the triggers that keep the log and the memory append-only are in the database exactly as the schema creates them.</summary>
    private static async Task<bool> AppendOnlyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var select = Command(connection, """
            SELECT name, sql FROM sqlite_master
            WHERE type = 'trigger' AND name IN ('decision_log_no_update', 'decision_log_no_delete', 'decision_log_no_replace',
                                                'memory_entries_no_update', 'memory_entries_no_delete', 'memory_entries_no_replace');
            """, transaction);
        var genuine = 0;
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1)
                && AppendOnlyTriggers.TryGetValue(reader.GetString(0), out var expected)
                && string.Equals(Blanks().Replace(reader.GetString(1), " ").Trim().TrimEnd(';').TrimEnd(), expected, StringComparison.Ordinal))
            {
                genuine++;
            }
        }

        return genuine == AppendOnlyTriggers.Count;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Blanks();

    /// <summary>Whether the first entries of the log are the marked ones: as many, and the last with the marked hash.</summary>
    private static async Task<bool> ContinuesAsync(SqliteConnection connection, SqliteTransaction transaction, string projectId, DecisionLogMark mark, CancellationToken cancellationToken)
    {
        await using var select = Command(connection, """
            SELECT COUNT(*), MAX(CASE WHEN sequence = $mark THEN hash END)
            FROM decision_log
            WHERE project_id = $project AND sequence <= $mark;
            """, transaction);
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$mark", mark.EntryCount);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) != mark.EntryCount)
        {
            return false;
        }

        return mark.EntryCount == 0
            ? string.Equals(mark.HeadHash, DecisionLog.GenesisHash, StringComparison.Ordinal)
            : !reader.IsDBNull(1) && string.Equals(reader.GetString(1), mark.HeadHash, StringComparison.Ordinal);
    }

    /// <summary>The entries of the log after <paramref name="after"/> (every entry from 0), in order.</summary>
    private static async Task<IReadOnlyList<DecisionEntry>> ReadAfterAsync(SqliteConnection connection, SqliteTransaction? transaction, string projectId, long after, CancellationToken cancellationToken)
    {
        await using var select = Command(connection, """
            SELECT sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, corrects_sequence, previous_hash, hash
            FROM decision_log
            WHERE project_id = $project AND sequence > $after
            ORDER BY sequence;
            """, transaction);
        select.Parameters.AddWithValue("$project", projectId);
        select.Parameters.AddWithValue("$after", after);

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
