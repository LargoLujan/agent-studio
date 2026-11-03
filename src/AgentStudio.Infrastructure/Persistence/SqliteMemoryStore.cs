using System.Globalization;
using System.Text;
using AgentStudio.Application.Knowledge;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The rows of the memory in <c>memory_entries</c> and its full-text index <c>memory_entries_fts</c> (migrations 0001 and
/// 0004; docs/14 D-19 and D-21). It appends and searches, and keeps no state: what a row is worth (anchored, reviewed,
/// superseded, in quarantine) is derived from the decision log by the memory module.
/// <list type="bullet">
/// <item>A row is immutable: the triggers of the 0004 abort an update, a delete and an insert that would replace one, so
/// a second row with the same id is refused by the database.</item>
/// <item>A row must carry the canonical SHA-256 of its category, topic and content (<see cref="MemoryContent.Sha256"/>)
/// and at most <see cref="MemoryContent.MaxCharacters"/> of content: the store refuses any other, so a row it writes is
/// always one an announcement can match.</item>
/// <item>With text, rows are ranked by <c>bm25</c> over topic and content; without it, newest first. Ties go newest first
/// and then by id.</item>
/// <item>The text is cut at white space and each term goes to FTS5 as a phrase between quotes, with its quotes doubled,
/// joined by <c>OR</c>: no operator (<c>NEAR</c>, <c>*</c>, <c>^</c>, <c>column:</c>, <c>AND</c>, <c>NOT</c>) ever gets
/// through from what a person or an agent wrote, and a row matches when it has any of the terms (more of them, or rarer
/// ones, rank it higher). A term with no letter or digit is dropped; a text left with none finds nothing. The
/// <c>unicode61</c> tokenizer folds case and accents, but does not stem Spanish.</item>
/// <item>A row that Agent Studio could not have written (an unknown category or origin, a missing hash, a time that is
/// not round-trip) is reported as <see cref="InvalidDataException"/> with its id.</item>
/// </list>
/// </summary>
public sealed class SqliteMemoryStore(ProjectDatabases databases) : IMemoryStore
{
    /// <summary>The most terms a text sends to FTS5; the rest are dropped.</summary>
    private const int MaxTerms = 32;

    private const string Columns = "m.id, m.kind, m.topic, m.content, m.origin, m.source, m.created_at, m.content_sha256, m.supersedes_entry_id";

    public async Task AppendAsync(string projectId, MemoryRow row, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Id);
        ArgumentNullException.ThrowIfNull(row.Topic);
        ArgumentNullException.ThrowIfNull(row.Content);
        ArgumentNullException.ThrowIfNull(row.Source);
        if (row.Content.Length > MemoryContent.MaxCharacters)
        {
            throw new ArgumentException($"The content of memory row '{row.Id}' has {row.Content.Length} characters; an entry keeps at most {MemoryContent.MaxCharacters}.", nameof(row));
        }

        if (!string.Equals(row.ContentSha256, MemoryContent.Sha256(row.Kind, row.Topic, row.Content), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Memory row '{row.Id}' does not carry the canonical SHA-256 of its category, topic and content.", nameof(row));
        }

        await using var connection = databases.Connect(projectId);
        await using var insert = Command(connection, """
            INSERT INTO memory_entries(id, project_id, kind, topic, content, source, created_at, origin, content_sha256, supersedes_entry_id)
            VALUES ($id, $project, $kind, $topic, $content, $source, $at, $origin, $sha, $supersedes);
            """);
        insert.Parameters.AddWithValue("$id", row.Id);
        insert.Parameters.AddWithValue("$project", projectId);
        insert.Parameters.AddWithValue("$kind", row.Kind.ToString());
        insert.Parameters.AddWithValue("$topic", row.Topic);
        insert.Parameters.AddWithValue("$content", row.Content);
        insert.Parameters.AddWithValue("$source", row.Source);
        insert.Parameters.AddWithValue("$at", Time(row.CreatedAt));
        insert.Parameters.AddWithValue("$origin", row.Origin.ToString());
        insert.Parameters.AddWithValue("$sha", row.ContentSha256);
        insert.Parameters.AddWithValue("$supersedes", OrNull(row.Supersedes));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryRow>> SearchAsync(string projectId, MemorySearch search, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(search);
        string? match = null;
        if (!string.IsNullOrWhiteSpace(search.Text) && (match = MatchExpression(search.Text)) is null)
        {
            return [];
        }

        // An empty list of categories allows none; null allows all.
        if (search.Kinds is { Count: 0 } || search.Limit is <= 0)
        {
            return [];
        }

        await using var connection = databases.Connect(projectId);
        await using var select = connection.CreateCommand();
        var sql = new StringBuilder("SELECT ").Append(Columns);
        sql.Append(match is null
            ? " FROM memory_entries m WHERE m.project_id = $project"
            : " FROM memory_entries_fts JOIN memory_entries m ON m.rowid = memory_entries_fts.rowid WHERE memory_entries_fts MATCH $match AND m.project_id = $project");
        select.Parameters.AddWithValue("$project", projectId);
        if (match is not null)
        {
            select.Parameters.AddWithValue("$match", match);
        }

        if (search.Kinds is { } kinds)
        {
            var names = kinds.Distinct().Select((kind, i) => (Name: "$k" + i.ToString(CultureInfo.InvariantCulture), Kind: kind)).ToList();
            sql.Append(" AND m.kind IN (").Append(string.Join(", ", names.Select(n => n.Name))).Append(')');
            foreach (var (name, kind) in names)
            {
                select.Parameters.AddWithValue(name, kind.ToString());
            }
        }

        if (search.Topic is { } topic)
        {
            sql.Append(" AND m.topic = $topic COLLATE NOCASE");
            select.Parameters.AddWithValue("$topic", topic);
        }

        sql.Append(match is null ? " ORDER BY m.created_at DESC, m.id" : " ORDER BY bm25(memory_entries_fts), m.created_at DESC, m.id");
        if (search.Limit is { } limit)
        {
            sql.Append(" LIMIT $limit");
            select.Parameters.AddWithValue("$limit", limit);
        }

        select.CommandText = sql.Append(';').ToString();
        var rows = new List<MemoryRow>();
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadRow(reader, projectId));
        }

        return rows;
    }

    /// <summary>The FTS5 query for <paramref name="text"/>: each term a quoted phrase, joined by <c>OR</c>; null when no term is left.</summary>
    private static string? MatchExpression(string text)
    {
        var terms = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Any(char.IsLetterOrDigit))
            .Take(MaxTerms)
            .Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToList();
        return terms.Count == 0 ? null : string.Join(" OR ", terms);
    }

    private static MemoryRow ReadRow(SqliteDataReader reader, string projectId)
    {
        var id = reader.GetString(0);
        string Problem(string what) => $"memory_entries row '{id}' of project '{projectId}' is not a row Agent Studio writes: {what}.";

        var kindText = reader.GetString(1);
        if (!TryParseEnum<MemoryKind>(kindText, out var kind))
        {
            throw new InvalidDataException(Problem($"unknown category '{kindText}'"));
        }

        var originText = NullableString(reader, 4);
        if (originText is null || !TryParseEnum<MemoryOrigin>(originText, out var origin))
        {
            throw new InvalidDataException(Problem($"unknown origin '{originText}'"));
        }

        var createdAt = reader.GetString(6);
        if (!TryParseTime(createdAt, out var at))
        {
            throw new InvalidDataException(Problem($"created_at '{createdAt}' is not a round-trip time"));
        }

        var sha = NullableString(reader, 7) ?? throw new InvalidDataException(Problem("it has no content hash"));
        return new MemoryRow(id, kind, reader.GetString(2), reader.GetString(3), origin, reader.GetString(5), at, sha, NullableString(reader, 8));
    }
}
