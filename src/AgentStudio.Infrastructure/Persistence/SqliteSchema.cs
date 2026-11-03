using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>Creates and migrates the SQLite database. Migrations are embedded <c>Schema/NNNN_name.sql</c> scripts.</summary>
public static partial class SqliteSchema
{
    /// <summary>How long a connection waits for a lock held by another connection before it gives up.</summary>
    public const int BusyTimeoutMilliseconds = 5000;

    private const string ResourcePrefix = "AgentStudio.Schema.";

    /// <summary>
    /// Opens the database. It waits up to <see cref="BusyTimeoutMilliseconds"/> for a lock held by another connection,
    /// uses write-ahead logging (readers never block the writer and a crash cannot leave a half-written page), and turns
    /// foreign-key enforcement and recursive triggers on (SQLite leaves both off by default).
    /// </summary>
    public static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();

            // First the timeout, so that switching the journal mode already waits instead of failing on a busy file.
            Pragma(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
            UseWriteAheadLog(connection);
            Pragma(connection, "PRAGMA foreign_keys = ON; PRAGMA recursive_triggers = ON;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Pragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void UseWriteAheadLog(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        // An in-memory database has no journal file and keeps the "memory" mode; any other answer means SQLite refused.
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "memory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SQLite did not switch '{connection.DataSource}' to write-ahead logging (journal mode '{mode}').");
        }
    }

    /// <summary>Applies every pending migration inside a transaction. Safe to call repeatedly.</summary>
    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
        var applied = ReadAppliedVersions(connection, transaction);

        foreach (var migration in LoadMigrations().Where(m => !applied.Contains(m.Version)).OrderBy(m => m.Version))
        {
            Execute(connection, transaction, migration.Sql);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $at);";
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            record.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static HashSet<int> ReadAppliedVersions(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations;";
        using var reader = command.ExecuteReader();
        var versions = new HashSet<int>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<(int Version, string Sql)> LoadMigrations()
    {
        var assembly = typeof(SqliteSchema).Assembly;
        var migrations = new List<(int, string)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var match = MigrationName().Match(name[ResourcePrefix.Length..]);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"No se pudo leer el recurso {name}.");
            using var reader = new StreamReader(stream);
            migrations.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), reader.ReadToEnd()));
        }

        return migrations;
    }

    [GeneratedRegex(@"^(\d{4})_.+\.sql$")]
    private static partial Regex MigrationName();
}
