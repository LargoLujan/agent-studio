using System.Globalization;
using System.IO;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// How the log of a project can be changed so that the stamp the store reads its tail with stays as it was (ADR-0010,
/// addendum of 2026-11-04): only a whole verification of the log can see the change.
/// </summary>
public enum StampBlindChange
{
    /// <summary>The change made with SQL on the database, and its schema version set back with <c>PRAGMA schema_version</c>.</summary>
    SchemaVersionForged,

    /// <summary>The change made on a copy of the database, with its schema version set back, which then replaces the file with the file's creation time.</summary>
    FileReplaced,
}

/// <summary>What someone who can write to the project's files does to its log behind the stores' back, in each <see cref="StampBlindChange"/>.</summary>
internal static class StampBlindTampering
{
    /// <summary>Runs <paramref name="statements"/> on the log of <paramref name="instance"/> as <paramref name="change"/> says.</summary>
    public static void Apply(ProjectInstance instance, StampBlindChange change, params string[] statements)
    {
        var database = instance.Databases.Get(instance.ProjectId).DatabasePath;
        long version;
        if (change == StampBlindChange.SchemaVersionForged)
        {
            using var connection = instance.Connect();
            version = Version(connection);
            Run(connection, statements);
            Run(connection, Forge(version));
            return;
        }

        var copy = database + ".copia";
        using (var connection = instance.Connect())
        {
            version = Version(connection);
            Run(connection, $"VACUUM INTO '{copy}';");
        }

        using (var crafted = SqliteSchema.Open(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            Run(crafted, statements);
            Run(crafted, Forge(version));
        }

        var created = File.GetCreationTimeUtc(database);
        foreach (var file in new[] { database + "-wal", database + "-shm", database })
        {
            File.Delete(file);
        }

        File.Move(copy, database);
        File.SetCreationTimeUtc(database, created);
    }

    private static long Version(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Forge(long version) => string.Create(CultureInfo.InvariantCulture, $"PRAGMA schema_version = {version};");

    private static void Run(SqliteConnection connection, params string[] statements)
    {
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
