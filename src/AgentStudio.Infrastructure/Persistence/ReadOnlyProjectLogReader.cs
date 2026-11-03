using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The decision log of a project that this instance does not have open, read only (docs/14 D-05): for the usage of the
/// recent projects. In this order:
/// <list type="number">
/// <item>the root is taken at its final path, and <c>.agentstudio</c> and everything the opening of a project checks
/// there are checked the same way (<see cref="ProjectStorageGuard.CheckStorage"/>): a link, or a database file with a
/// second name, is a <see cref="ProjectStorageIntegrityException"/> before anything is opened;</item>
/// <item>the database is opened read only, with no pool and <c>query_only</c>: no migration, no row, no lock file, and
/// the journal mode is never switched (SQLite may create the <c>-wal</c> and <c>-shm</c> files of a database in
/// write-ahead logging, already excluded from git and from the comparison of the user's repository);</item>
/// <item>the log is verified against the anchor of its location by <see cref="ReadOnlyLogVerifier"/>, which never
/// confirms or moves an anchor, not even one kept under the old name of the project id (ADR-0010, addendum P-04).</item>
/// </list>
/// </summary>
/// <remarks>
/// A process of the same user running at the same time can still change a file between the check and the opening: that
/// race is a residual of ADR-0018 (point 2), as for the opening of a project.
/// </remarks>
public sealed partial class ReadOnlyProjectLogReader(DecisionLogAnchorOptions anchors) : IProjectLogReader
{
    public async Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The project folder '{root}' does not exist.");
        }

        var layout = ProjectStorage.For(string.Empty, root, ProjectStorageGuard.FinalRoot(root));
        ProjectStorageGuard.CheckFolder(layout, layout.DataDirectory);
        ProjectStorageGuard.CheckStorage(layout);
        if (!File.Exists(layout.DatabasePath))
        {
            throw new FileNotFoundException($"'{root}' has no Agent Studio database, so it has no decision log.", layout.DatabasePath);
        }

        var (projectId, entries) = await ReadLogAsync(layout.DatabasePath, ct).ConfigureAwait(false);

        // The anchor store finds a project by the registry of open projects: a registry of this read alone, with no lock.
        using var registry = new ProjectDatabases();
        registry.Register(layout with { ProjectId = projectId }, NoLock.Instance);
        var verified = await new ReadOnlyLogVerifier(new FileDecisionLogAnchorStore(anchors, registry))
            .VerifyAsync(projectId, entries, ct)
            .ConfigureAwait(false);
        return new ProjectLogSnapshot(projectId, root, entries, verified.Verification, verified.Anchor);
    }

    private static async Task<(string ProjectId, IReadOnlyList<DecisionEntry> Entries)> ReadLogAsync(string databasePath, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var timeout = SqliteSchema.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
            await using (var pragma = Command(connection, $"PRAGMA busy_timeout = {timeout}; PRAGMA query_only = ON;"))
            {
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            try
            {
                var projectId = await ReadProjectIdAsync(connection, databasePath, ct).ConfigureAwait(false);
                return (projectId, await ReadEntriesAsync(connection, projectId, ct).ConfigureAwait(false));
            }
            catch (SqliteException ex)
            {
                throw new InvalidDataException($"'{databasePath}' cannot be read as the database of an Agent Studio project: {ex.Message}", ex);
            }
        }
    }

    private static async Task<string> ReadProjectIdAsync(SqliteConnection connection, string databasePath, CancellationToken ct)
    {
        await using var select = Command(connection, "SELECT id FROM projects ORDER BY rowid;");
        var ids = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }

        if (ids.Count != 1)
        {
            throw new InvalidDataException($"The database '{databasePath}' holds {ids.Count} projects; a project database holds exactly one.");
        }

        // The id names the old anchor file: anything but a plain name would point elsewhere.
        return PlainName().IsMatch(ids[0])
            ? ids[0]
            : throw new InvalidDataException($"The database '{databasePath}' gives its project the id '{ids[0]}', which Agent Studio never writes.");
    }

    private static async Task<IReadOnlyList<DecisionEntry>> ReadEntriesAsync(SqliteConnection connection, string projectId, CancellationToken ct)
    {
        await using var select = Command(connection, """
            SELECT sequence, recorded_at, actor_kind, actor_id, kind, summary, payload_json, corrects_sequence, previous_hash, hash
            FROM decision_log
            WHERE project_id = $project
            ORDER BY sequence;
            """);
        select.Parameters.AddWithValue("$project", projectId);
        var entries = new List<DecisionEntry>();
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader, projectId));
        }

        return entries;
    }

    /// <summary>An entry as <see cref="SqliteDecisionLogStore"/> reads it: a row that is not even a valid entry is reported with its sequence.</summary>
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

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex PlainName();

    /// <summary>A read takes no lock: the registry it builds only needs one to give back on disposal.</summary>
    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();

        public void Dispose()
        {
        }
    }
}
