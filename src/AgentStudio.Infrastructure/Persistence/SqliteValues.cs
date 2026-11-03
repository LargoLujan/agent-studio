using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// How values are written to and read from the database. Times are ISO-8601 round-trip text in UTC, which is also the
/// form the decision log hashes; enums are stored by name and read strictly (no numbers, no unknown names), because a
/// value that does not parse means the row was written by someone else.
/// </summary>
internal static class SqliteValues
{
    public static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static bool TryParseTime(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);

    public static bool TryParseEnum<T>(string text, out T value)
        where T : struct, Enum
    {
        value = default;
        return text.Length > 0
            && !char.IsAsciiDigit(text[0]) && text[0] is not ('-' or '+')
            && Enum.TryParse(text, ignoreCase: false, out value)
            && Enum.IsDefined(value);
    }

    public static object OrNull(object? value) => value ?? DBNull.Value;

    public static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? NullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? NullableInt32(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }
}
