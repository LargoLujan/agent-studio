using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Channels;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Semantic;
using Microsoft.Data.Sqlite;
using static AgentStudio.Infrastructure.Persistence.SqliteValues;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Where the semantic indexes live (docs/17 D-04): the folder <c>%LOCALAPPDATA%</c> resolves to. The app uses
/// <see cref="ForCurrentUser"/>; the tests, a folder of their own.
/// </summary>
public sealed record SemanticIndexOptions(string LocalAppData)
{
    public static SemanticIndexOptions ForCurrentUser() => new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}

/// <summary>
/// The semantic index of each open project in a SQLite database of its own (docs/17 D-04 and D-05; ADR-0047; WP2), at
/// <see cref="SemanticIndexLocation.IndexFile"/> for the final path of the project root, the key of the anchor of its log:
/// outside the project, so git never sees it, a repository cannot bring it and the check of the user's repository never
/// trips on it. It is a cache: it stores and ranks, and decides nothing about trust.
/// <list type="bullet">
/// <item><b>Tables:</b> <c>fragments</c> (id, source with its hash, part, time, fingerprint and text), its FTS5 index
/// <c>fragments_fts</c> with the tokenizer of the memory (<c>unicode61</c>, external content kept by triggers) and
/// <c>fragment_vectors</c> (the vector in float32, little-endian, with the fingerprint of its model and its dimensions,
/// L2-normalised when it is written). No sqlite-vec and no native extension.</item>
/// <item><b>Words:</b> each term a quoted phrase, its quotes doubled, joined by <c>OR</c>, so no FTS5 operator gets through;
/// ranked by <c>bm25</c>, ties by time (newest first) and then id.</item>
/// <item><b>Meaning:</b> the dot product of the normalised query with every vector of the model, in memory with
/// <see cref="Vector{T}"/>. The vectors of a project are loaded once and kept until the index changes (a generation
/// counter that the triggers raise), so a search reads no blob. Best first, ties by time (newest first) and id.</item>
/// <item><b>Rebuilt the same</b> (F12-3): nothing depends on the order the fragments came in, on chance or on the order of a
/// dictionary, so deleting the index and filling it again gives the same fingerprints and, with the same vectors, the same
/// order.</item>
/// <item><b>Self-healing:</b> a file that is not an index of this version (another program's, damaged, or written by a
/// later version) is deleted and the index starts empty; the next refresh rebuilds it. A row the store could not have
/// written (an empty text or one longer than <see cref="SemanticFragment.MaxCharacters"/>, an id or a fingerprint that is
/// not the one its content gives, a time that does not parse) never comes out of a read or a search, which fail for none;
/// <see cref="ReadStateAsync"/> marks it with an empty fingerprint, so the next refresh replaces it or removes it.</item>
/// </list>
/// <b>One writer:</b> every operation of one index (a read, a search, a write or the delete) goes to its queue, and a
/// single consumer runs them one after another, in the order they came, on the thread pool. So the refresh and a search
/// that adds or repairs fragments can write at the same time without ever meeting in the database, the vectors kept in
/// memory always match it, and no lock is held. Each operation opens and closes its own connection, so deleting the index
/// never finds a handle open. A caller that cancels stops waiting, and its operation is skipped when its turn comes. The
/// project must be open in this instance (<see cref="ProjectDatabases"/>).
/// <para>
/// Compose it once, as a singleton: the single writer belongs to the instance. Two stores over one index would each have
/// their own queue (SQLite would still keep their writes apart, and the vectors in memory follow the generation of the
/// index), so the refresh and the searches would no longer take turns.
/// </para>
/// </summary>
public sealed class SemanticIndexStore : ISemanticIndexStore
{
    private const int SchemaVersion = 1;

    // SQLITE_CORRUPT and SQLITE_NOTADB: the file is not a database SQLite can read.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    // How many more candidates a search takes than it gives, so that a row it has to leave out still leaves a full list.
    private const int Spare = 16;

    // Well under the limit of SQLite on the parameters of one statement.
    private const int IdsPerQuery = 500;

    private const string Schema = """
        CREATE TABLE fragments (
            seq           INTEGER PRIMARY KEY,
            id            TEXT NOT NULL UNIQUE,
            source_kind   TEXT NOT NULL,
            source_ref    TEXT NOT NULL,
            source_sha256 TEXT NOT NULL,
            part          INTEGER NOT NULL,
            at            TEXT NOT NULL,
            sha256        TEXT NOT NULL,
            text          TEXT NOT NULL
        );

        CREATE TABLE fragment_vectors (
            fragment_id TEXT PRIMARY KEY REFERENCES fragments(id) ON DELETE CASCADE,
            model       TEXT NOT NULL,
            dimensions  INTEGER NOT NULL,
            vector      BLOB NOT NULL
        );

        CREATE INDEX fragment_vectors_by_model ON fragment_vectors(model);

        CREATE VIRTUAL TABLE fragments_fts USING fts5(text, content = 'fragments', content_rowid = 'seq', tokenize = 'unicode61');

        CREATE TABLE index_generation (id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL);
        INSERT INTO index_generation(id, value) VALUES (1, 0);

        CREATE TRIGGER fragments_ai AFTER INSERT ON fragments BEGIN
            INSERT INTO fragments_fts(rowid, text) VALUES (new.seq, new.text);
            UPDATE index_generation SET value = value + 1;
        END;

        CREATE TRIGGER fragments_ad AFTER DELETE ON fragments BEGIN
            INSERT INTO fragments_fts(fragments_fts, rowid, text) VALUES ('delete', old.seq, old.text);
            UPDATE index_generation SET value = value + 1;
        END;

        CREATE TRIGGER fragments_au AFTER UPDATE ON fragments BEGIN
            INSERT INTO fragments_fts(fragments_fts, rowid, text) VALUES ('delete', old.seq, old.text);
            INSERT INTO fragments_fts(rowid, text) VALUES (new.seq, new.text);
            UPDATE index_generation SET value = value + 1;
        END;

        CREATE TRIGGER fragment_vectors_ai AFTER INSERT ON fragment_vectors BEGIN
            UPDATE index_generation SET value = value + 1;
        END;

        CREATE TRIGGER fragment_vectors_ad AFTER DELETE ON fragment_vectors BEGIN
            UPDATE index_generation SET value = value + 1;
        END;

        CREATE TRIGGER fragment_vectors_au AFTER UPDATE ON fragment_vectors BEGIN
            UPDATE index_generation SET value = value + 1;
        END;

        PRAGMA user_version = 1;
        """;

    private const string FragmentColumns = "f.id, f.source_kind, f.source_ref, f.source_sha256, f.part, f.at, f.sha256, f.text";

    private readonly ProjectDatabases _databases;
    private readonly string _localAppData;
    private readonly ConcurrentDictionary<string, IndexQueue> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public SemanticIndexStore(ProjectDatabases databases, SemanticIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LocalAppData);
        _databases = databases;
        _localAppData = options.LocalAppData;
    }

    /// <summary>The database of the index of <paramref name="project"/>, which must be open in this instance.</summary>
    public string IndexFileOf(ProjectContext project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return SemanticIndexLocation.IndexFile(_localAppData, _databases.Get(project.ProjectId).FinalRootPath);
    }

    public async Task<IReadOnlyList<IndexedFragmentState>> ReadStateAsync(ProjectContext project, CancellationToken ct) =>
        await UseAsync(project, (connection, _) => ReadState(connection), ct).ConfigureAwait(false);

    public async Task UpsertAsync(ProjectContext project, IReadOnlyList<IndexedFragment> fragments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var rows = Prepare(fragments);
        if (rows.Count > 0)
        {
            await UseAsync(project, (connection, _) => Upsert(connection, rows), ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var ids = Ids(fragmentIds);
        if (ids.Count > 0)
        {
            await UseAsync(project, (connection, _) => Remove(connection, ids), ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SemanticFragment>> ReadAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var ids = Ids(fragmentIds);
        return ids.Count == 0 ? [] : await UseAsync(project, (connection, _) => Read(connection, ids), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexedFragment>> ReadIndexedAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var ids = Ids(fragmentIds);
        return ids.Count == 0 ? [] : await UseAsync(project, (connection, _) => ReadIndexed(connection, ids), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RankedCandidate>> SearchWordsAsync(ProjectContext project, IReadOnlyList<string> terms, int top, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(terms);
        return MatchExpression(terms) is not { } match || top <= 0
            ? []
            : await UseAsync(project, (connection, _) => SearchWords(connection, match, top), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SimilarCandidate>> SearchVectorsAsync(ProjectContext project, ReadOnlyMemory<float> query, string modelFingerprint, int top, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!ContentHash.IsSha256Hex(modelFingerprint))
        {
            throw new ArgumentException("The fingerprint of a model is 64 lowercase hex characters.", nameof(modelFingerprint));
        }

        var normalised = Normalised(query.Span) ?? throw new ArgumentException("The query vector is empty, zero or not finite.", nameof(query));
        return top <= 0
            ? []
            : await UseAsync(project, (connection, index) => SearchVectors(connection, index, normalised, modelFingerprint, top), ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(ProjectContext project, CancellationToken ct)
    {
        var file = IndexFileOf(project);
        await QueueOf(file).Enqueue(
            index =>
            {
                Wipe(file, index);
                return true;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="work"/> on its own connection to the index of <paramref name="project"/>, in its turn in the queue of that index.</summary>
    private Task<T> UseAsync<T>(ProjectContext project, Func<SqliteConnection, IndexQueue, T> work, CancellationToken ct)
    {
        var file = IndexFileOf(project);
        return QueueOf(file).Enqueue(index => Run(file, index, work, ct), ct);
    }

    private IndexQueue QueueOf(string file) => _indexes.GetOrAdd(file, static _ => new IndexQueue());

    /// <summary>Runs <paramref name="work"/>; an index that cannot be read is deleted and the work runs once more, on an empty one.</summary>
    private static T Run<T>(string file, IndexQueue index, Func<SqliteConnection, IndexQueue, T> work, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            SqliteConnection? connection = null;
            try
            {
                connection = Open(file);
                if (connection is not null)
                {
                    return work(connection, index);
                }
            }
            catch (SqliteException ex) when (attempt == 0 && ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                // A cache SQLite cannot read: it is rebuilt.
            }
            finally
            {
                connection?.Dispose();
            }

            if (attempt > 0)
            {
                throw new InvalidDataException($"The semantic index '{file}' was deleted because it was not one of this version, and it still is not one.");
            }

            Wipe(file, index);
        }
    }

    /// <summary>The connection to the index at <paramref name="file"/>, created if needed; null when the file holds something else.</summary>
    private static SqliteConnection? Open(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var connection = SqliteSchema.Open(ProjectDatabases.ConnectionString(file, create: true));
        try
        {
            var version = Convert.ToInt64(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version == SchemaVersion)
            {
                return connection;
            }

            if (version != 0 || Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master;"), CultureInfo.InvariantCulture) != 0)
            {
                connection.Dispose();
                return null;
            }

            using var transaction = connection.BeginTransaction();
            using (var create = Command(connection, Schema, transaction))
            {
                create.ExecuteNonQuery();
            }

            transaction.Commit();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Deletes the folder of the index (the database and the files SQLite keeps next to it) and forgets its vectors.</summary>
    private static void Wipe(string file, IndexQueue index)
    {
        index.Vectors = null;
        var folder = Path.GetDirectoryName(file)!;
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, sql, transaction);
        return command.ExecuteScalar();
    }

    private static List<string> Ids(IReadOnlyList<string> fragmentIds)
    {
        ArgumentNullException.ThrowIfNull(fragmentIds);
        var ids = new List<string>(fragmentIds.Count);
        for (var i = 0; i < fragmentIds.Count; i++)
        {
            ids.Add(fragmentIds[i] ?? throw new ArgumentException($"Fragment id {i} is null.", nameof(fragmentIds)));
        }

        return ids;
    }

    /// <summary>The rows to write, checked before anything is: no repeated id, and each vector with its model, finite and with a direction.</summary>
    private static List<FragmentRow> Prepare(IReadOnlyList<IndexedFragment> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        var rows = new List<FragmentRow>(fragments.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var dimensions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < fragments.Count; i++)
        {
            var item = fragments[i] ?? throw new ArgumentException($"Indexed fragment {i} is null.", nameof(fragments));
            var fragment = item.Fragment ?? throw new ArgumentException($"Indexed fragment {i} has no fragment.", nameof(fragments));
            if (!ids.Add(fragment.Id))
            {
                throw new ArgumentException($"Fragment '{fragment.Id}' comes twice.", nameof(fragments));
            }

            if (item.Vector is null != item.ModelFingerprint is null)
            {
                throw new ArgumentException($"Fragment '{fragment.Id}' has a vector without the fingerprint of its model, or a fingerprint without a vector.", nameof(fragments));
            }

            if (item.Vector is not { } vector)
            {
                rows.Add(new FragmentRow(fragment, null, 0, null));
                continue;
            }

            var model = item.ModelFingerprint!;
            if (!ContentHash.IsSha256Hex(model))
            {
                throw new ArgumentException($"The fingerprint of the model of fragment '{fragment.Id}' is not 64 lowercase hex characters.", nameof(fragments));
            }

            if (dimensions.TryGetValue(model, out var expected) && expected != vector.Length)
            {
                throw new ArgumentException($"The vectors of model '{model}' have {expected} dimensions, but the one of fragment '{fragment.Id}' has {vector.Length}.", nameof(fragments));
            }

            dimensions[model] = vector.Length;
            var normalised = Normalised(vector) ?? throw new ArgumentException($"The vector of fragment '{fragment.Id}' is empty, zero or not finite.", nameof(fragments));
            rows.Add(new FragmentRow(fragment, model, vector.Length, Encode(normalised)));
        }

        return rows;
    }

    private static int Upsert(SqliteConnection connection, List<FragmentRow> rows)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var model in rows.Where(r => r.Model is not null).GroupBy(r => r.Model!, StringComparer.Ordinal))
        {
            var dimensions = model.First().Dimensions;
            using var other = Command(connection, "SELECT dimensions FROM fragment_vectors WHERE model = $model AND dimensions <> $dimensions LIMIT 1;", transaction);
            other.Parameters.AddWithValue("$model", model.Key);
            other.Parameters.AddWithValue("$dimensions", dimensions);
            if (other.ExecuteScalar() is { } held)
            {
                throw new ArgumentException($"The index holds vectors of model '{model.Key}' with {held} dimensions; these have {dimensions}.", "fragments");
            }
        }

        using var previous = Command(connection, $"SELECT {FragmentColumns}, v.model, v.dimensions, v.vector FROM fragments f LEFT JOIN fragment_vectors v ON v.fragment_id = f.id WHERE f.id = $id;", transaction);
        var previousId = previous.Parameters.Add("$id", SqliteType.Text);
        using var write = Command(connection, """
            INSERT INTO fragments(id, source_kind, source_ref, source_sha256, part, at, sha256, text)
            VALUES ($id, $kind, $ref, $source, $part, $at, $sha, $text)
            ON CONFLICT(id) DO UPDATE SET
                source_kind = excluded.source_kind,
                source_ref = excluded.source_ref,
                source_sha256 = excluded.source_sha256,
                part = excluded.part,
                at = excluded.at,
                sha256 = excluded.sha256,
                text = excluded.text;
            """, transaction);
        var id = write.Parameters.Add("$id", SqliteType.Text);
        var kind = write.Parameters.Add("$kind", SqliteType.Text);
        var reference = write.Parameters.Add("$ref", SqliteType.Text);
        var source = write.Parameters.Add("$source", SqliteType.Text);
        var part = write.Parameters.Add("$part", SqliteType.Integer);
        var at = write.Parameters.Add("$at", SqliteType.Text);
        var sha = write.Parameters.Add("$sha", SqliteType.Text);
        var text = write.Parameters.Add("$text", SqliteType.Text);
        using var put = Command(connection, """
            INSERT INTO fragment_vectors(fragment_id, model, dimensions, vector) VALUES ($id, $model, $dimensions, $vector)
            ON CONFLICT(fragment_id) DO UPDATE SET model = excluded.model, dimensions = excluded.dimensions, vector = excluded.vector;
            """, transaction);
        var putId = put.Parameters.Add("$id", SqliteType.Text);
        var putModel = put.Parameters.Add("$model", SqliteType.Text);
        var putDimensions = put.Parameters.Add("$dimensions", SqliteType.Integer);
        var putVector = put.Parameters.Add("$vector", SqliteType.Blob);
        using var drop = Command(connection, "DELETE FROM fragment_vectors WHERE fragment_id = $id;", transaction);
        var dropId = drop.Parameters.Add("$id", SqliteType.Text);

        foreach (var row in rows)
        {
            var fragment = row.Fragment;
            previousId.Value = fragment.Id;
            var keepsVector = false;
            var hadVector = false;
            using (var held = previous.ExecuteReader())
            {
                if (held.Read())
                {
                    // The vector it has stays only when it is of the same content (by the fingerprint the store wrote, or by the
                    // one its content gives, should either have been changed behind its back) and a search can still read it.
                    hadVector = !held.IsDBNull(8);
                    var sameContent = string.Equals(held.GetValue(6) as string, fragment.Sha256, StringComparison.Ordinal)
                        || (TryRebuildContent(held, out var stored) && string.Equals(stored.Sha256, fragment.Sha256, StringComparison.Ordinal));
                    keepsVector = sameContent && ReadableVectorModel(held, 8) is not null;
                }
            }

            id.Value = fragment.Id;
            kind.Value = fragment.Source.Kind.ToString();
            reference.Value = fragment.Source.Reference;
            source.Value = fragment.Source.Sha256;
            part.Value = fragment.Part;
            at.Value = Time(fragment.At);
            sha.Value = fragment.Sha256;
            text.Value = fragment.Text;
            write.ExecuteNonQuery();

            if (row.Vector is not null)
            {
                putId.Value = fragment.Id;
                putModel.Value = row.Model;
                putDimensions.Value = row.Dimensions;
                putVector.Value = row.Vector;
                put.ExecuteNonQuery();
            }
            else if (hadVector && !keepsVector)
            {
                // Another text, whose vector was of the old one, or a vector a search could not read: the refresh embeds it again.
                dropId.Value = fragment.Id;
                drop.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return rows.Count;
    }

    private static int Remove(SqliteConnection connection, List<string> ids)
    {
        using var transaction = connection.BeginTransaction();
        using var delete = Command(connection, "DELETE FROM fragments WHERE id = $id;", transaction);
        var id = delete.Parameters.Add("$id", SqliteType.Text);
        foreach (var fragmentId in ids)
        {
            id.Value = fragmentId;
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return ids.Count;
    }

    private static List<IndexedFragmentState> ReadState(SqliteConnection connection)
    {
        using var select = Command(connection, $"SELECT {FragmentColumns}, v.model, v.dimensions, v.vector FROM fragments f LEFT JOIN fragment_vectors v ON v.fragment_id = f.id;");
        using var reader = select.ExecuteReader();
        var states = new List<IndexedFragmentState>();
        while (reader.Read())
        {
            // A row the store could not have written gets no fingerprint, so the refresh replaces it or removes it; a vector
            // a search could not read gives no model, so the refresh embeds the fragment again.
            var sha = TryRebuild(reader, out var fragment) ? fragment.Sha256 : string.Empty;
            states.Add(new IndexedFragmentState(reader.GetString(0), sha, ReadableVectorModel(reader, 8)));
        }

        states.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return states;
    }

    /// <summary>
    /// The model of the vector in the columns from <paramref name="ordinal"/> (model, dimensions, vector) when a search can
    /// read it, as <see cref="LoadVectors"/> does: a model fingerprint and as many finite float32 values as its dimensions.
    /// Null otherwise.
    /// </summary>
    private static string? ReadableVectorModel(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not string model || !ContentHash.IsSha256Hex(model)
            || reader.GetValue(ordinal + 1) is not long dimensions || dimensions <= 0
            || reader.GetValue(ordinal + 2) is not byte[] blob || blob.Length != dimensions * sizeof(float))
        {
            return null;
        }

        for (var offset = 0; offset < blob.Length; offset += sizeof(float))
        {
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(offset))))
            {
                return null;
            }
        }

        return model;
    }

    private static List<SemanticFragment> Read(SqliteConnection connection, List<string> ids) =>
        [.. InOrder(ReadById(connection, ids, withVectors: false), ids).Select(indexed => indexed.Fragment)];

    private static List<IndexedFragment> ReadIndexed(SqliteConnection connection, List<string> ids) =>
        InOrder(ReadById(connection, ids, withVectors: true), ids);

    /// <summary>What <paramref name="found"/> holds of <paramref name="ids"/>, in their order; an id it does not hold is skipped.</summary>
    private static List<IndexedFragment> InOrder(Dictionary<string, IndexedFragment> found, List<string> ids)
    {
        var fragments = new List<IndexedFragment>(ids.Count);
        foreach (var fragmentId in ids)
        {
            if (found.TryGetValue(fragmentId, out var indexed))
            {
                fragments.Add(indexed);
            }
        }

        return fragments;
    }

    /// <summary>
    /// The fragments with these ids that the store wrote and, <paramref name="withVectors"/>, each with its vector and the
    /// fingerprint of its model when it has one that decodes (a vector that does not is left out, the fragment is not).
    /// </summary>
    private static Dictionary<string, IndexedFragment> ReadById(SqliteConnection connection, List<string> ids, bool withVectors)
    {
        var found = new Dictionary<string, IndexedFragment>(StringComparer.Ordinal);
        foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(IdsPerQuery))
        {
            using var select = connection.CreateCommand();
            var names = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = "$i" + i.ToString(CultureInfo.InvariantCulture);
                names.Append(i == 0 ? string.Empty : ", ").Append(name);
                select.Parameters.AddWithValue(name, chunk[i]);
            }

            select.CommandText = withVectors
                ? $"SELECT {FragmentColumns}, v.model, v.dimensions, v.vector FROM fragments f LEFT JOIN fragment_vectors v ON v.fragment_id = f.id WHERE f.id IN ({names});"
                : $"SELECT {FragmentColumns} FROM fragments f WHERE f.id IN ({names});";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                if (!TryRebuild(reader, out var fragment))
                {
                    continue;
                }

                float[]? vector = null;
                string? model = null;
                if (withVectors && !reader.IsDBNull(8) && !reader.IsDBNull(10) && reader.GetValue(10) is byte[] blob && reader.GetInt64(9) * sizeof(float) == blob.Length && blob.Length > 0)
                {
                    var decoded = new float[blob.Length / sizeof(float)];
                    if (TryDecode(blob, decoded) && ContentHash.IsSha256Hex(reader.GetString(8)))
                    {
                        (vector, model) = (decoded, reader.GetString(8));
                    }
                }

                found[fragment.Id] = new IndexedFragment(fragment, vector, model);
            }
        }

        return found;
    }

    /// <summary>
    /// The fragment of the row at the reader (the columns of <see cref="FragmentColumns"/>), built again from what it says:
    /// false when it is not one the store wrote (a value that does not parse, or an id or fingerprint that is not the one
    /// its content gives).
    /// </summary>
    private static bool TryRebuild(SqliteDataReader reader, [NotNullWhen(true)] out SemanticFragment? fragment)
    {
        fragment = null;
        if (!TryRebuildContent(reader, out var rebuilt)
            || !string.Equals(rebuilt.Id, reader.GetValue(0) as string, StringComparison.Ordinal)
            || !string.Equals(rebuilt.Sha256, reader.GetValue(6) as string, StringComparison.Ordinal))
        {
            return false;
        }

        fragment = rebuilt;
        return true;
    }

    /// <summary>
    /// The fragment that the content of the row at the reader gives (source, part, time and text), whatever its id and
    /// fingerprint columns say; false when that content is not one of a fragment.
    /// </summary>
    private static bool TryRebuildContent(SqliteDataReader reader, [NotNullWhen(true)] out SemanticFragment? fragment)
    {
        fragment = null;
        if (reader.GetValue(1) is not string kindText || !TryParseEnum<FragmentSourceKind>(kindText, out var kind)
            || reader.GetValue(5) is not string atText || !TryParseTime(atText, out var at)
            || reader.GetValue(2) is not string reference || reader.GetValue(3) is not string sourceSha256
            || reader.GetValue(4) is not long part || part is < 0 or > int.MaxValue
            || reader.GetValue(7) is not string text)
        {
            return false;
        }

        try
        {
            fragment = new SemanticFragment(new FragmentSource(kind, reference, sourceSha256), (int)part, text, at);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The FTS5 query for <paramref name="terms"/>: each a quoted phrase, its quotes doubled, joined by <c>OR</c>; null when none is left.</summary>
    private static string? MatchExpression(IReadOnlyList<string> terms)
    {
        var phrases = terms
            .Select(WithoutControls)
            .Where(term => !string.IsNullOrWhiteSpace(term) && term.Any(char.IsLetterOrDigit))
            .Take(LexicalTerms.MaxTerms)
            .Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToList();
        return phrases.Count == 0 ? null : string.Join(" OR ", phrases);
    }

    /// <summary>
    /// <paramref name="term"/> without its control characters: FTS5 reads a query as a C string, so a NUL would end it in
    /// the middle of a quoted phrase ("unterminated string"). A search never fails for what a person or an agent wrote.
    /// </summary>
    private static string WithoutControls(string? term) =>
        term is null ? string.Empty : term.Any(char.IsControl) ? new string([.. term.Where(c => !char.IsControl(c))]) : term;

    /// <summary>How many candidates a search takes to give <paramref name="top"/>: <see cref="Spare"/> more, without overflowing.</summary>
    private static int WithSpare(int top) => top > int.MaxValue - Spare ? int.MaxValue : top + Spare;

    private static List<RankedCandidate> SearchWords(SqliteConnection connection, string match, int top)
    {
        var candidates = new List<RankedCandidate>();
        using (var select = Command(connection, """
            SELECT f.id, f.at
            FROM fragments_fts JOIN fragments f ON f.seq = fragments_fts.rowid
            WHERE fragments_fts MATCH $match
            ORDER BY bm25(fragments_fts), f.at DESC, f.id
            LIMIT $limit;
            """))
        {
            select.Parameters.AddWithValue("$match", match);
            select.Parameters.AddWithValue("$limit", WithSpare(top));
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                if (TryParseTime(reader.GetString(1), out var at))
                {
                    candidates.Add(new RankedCandidate(reader.GetString(0), at));
                }
            }
        }

        var whole = ReadById(connection, [.. candidates.Select(candidate => candidate.Id)], withVectors: false);
        return [.. candidates.Where(candidate => whole.ContainsKey(candidate.Id)).Take(top)];
    }

    private static List<SimilarCandidate> SearchVectors(SqliteConnection connection, IndexQueue index, float[] query, string model, int top)
    {
        var generation = Convert.ToInt64(Scalar(connection, "SELECT value FROM index_generation WHERE id = 1;"), CultureInfo.InvariantCulture);
        var set = index.Vectors;
        if (set is null || set.Generation != generation || set.Dimensions != query.Length || !string.Equals(set.Model, model, StringComparison.Ordinal))
        {
            set = LoadVectors(connection, model, query.Length, generation);
            index.Vectors = set;
        }

        var dimensions = set.Dimensions;
        var wanted = WithSpare(top);
        var best = new PriorityQueue<int, Rank>(Math.Min(wanted, set.Ids.Length) + 1, WorstFirst.Instance);
        for (var i = 0; i < set.Ids.Length; i++)
        {
            var rank = new Rank(Dot(query, set.Values.AsSpan(i * dimensions, dimensions)), set.Times[i].UtcTicks, set.Ids[i]);
            if (best.Count < wanted)
            {
                best.Enqueue(i, rank);
            }
            else if (best.TryPeek(out _, out var worst) && WorstFirst.Instance.Compare(rank, worst) > 0)
            {
                best.DequeueEnqueue(i, rank);
            }
        }

        var ranked = new SimilarCandidate[best.Count];
        for (var k = ranked.Length - 1; k >= 0; k--)
        {
            best.TryDequeue(out var i, out var rank);
            ranked[k] = new SimilarCandidate(set.Ids[i], set.Times[i], rank.Score);
        }

        var whole = ReadById(connection, [.. ranked.Select(candidate => candidate.Id)], withVectors: false);
        return [.. ranked.Where(candidate => whole.ContainsKey(candidate.Id)).Take(top)];
    }

    /// <summary>Every vector of <paramref name="model"/> with <paramref name="dimensions"/> dimensions, with the id and time of its fragment; a row that does not decode is left out.</summary>
    private static VectorSet LoadVectors(SqliteConnection connection, string model, int dimensions, long generation)
    {
        using var select = Command(connection, """
            SELECT v.fragment_id, f.at, v.vector
            FROM fragment_vectors v JOIN fragments f ON f.id = v.fragment_id
            WHERE v.model = $model AND v.dimensions = $dimensions
            ORDER BY v.fragment_id;
            """);
        select.Parameters.AddWithValue("$model", model);
        select.Parameters.AddWithValue("$dimensions", dimensions);
        using var reader = select.ExecuteReader();
        var ids = new List<string>();
        var times = new List<DateTimeOffset>();
        var values = new List<float>();
        var vector = new float[dimensions];
        while (reader.Read())
        {
            if (!TryParseTime(reader.GetString(1), out var at) || reader.GetValue(2) is not byte[] blob || !TryDecode(blob, vector))
            {
                continue;
            }

            ids.Add(reader.GetString(0));
            times.Add(at);
            values.AddRange(vector);
        }

        return new VectorSet(model, dimensions, generation, [.. ids], [.. times], [.. values]);
    }

    /// <summary><paramref name="vector"/> scaled to length one; null when it is empty, zero or not finite.</summary>
    private static float[]? Normalised(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            return null;
        }

        var sum = 0.0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                return null;
            }

            sum += (double)value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm == 0 || !double.IsFinite(norm))
        {
            return null;
        }

        var normalised = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            normalised[i] = (float)(vector[i] / norm);
        }

        return normalised;
    }

    private static byte[] Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    private static bool TryDecode(byte[] bytes, float[] vector)
    {
        if (bytes.Length != vector.Length * sizeof(float))
        {
            return false;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
            if (!float.IsFinite(value))
            {
                return false;
            }

            vector[i] = value;
        }

        return true;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        }

        var total = Vector.Sum(sum);
        for (; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    /// <summary>A fragment to write, with its vector already normalised and encoded (null without one).</summary>
    private sealed record FragmentRow(SemanticFragment Fragment, string? Model, int Dimensions, byte[]? Vector);

    /// <summary>The place of a candidate of the semantic list: its score, the time of its source and its id.</summary>
    private readonly record struct Rank(float Score, long UtcTicks, string Id);

    /// <summary>The worse candidate first: the lower score, then the older source, then the greater id (ordinal).</summary>
    private sealed class WorstFirst : IComparer<Rank>
    {
        public static readonly WorstFirst Instance = new();

        public int Compare(Rank x, Rank y)
        {
            var byScore = x.Score.CompareTo(y.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byTime = x.UtcTicks.CompareTo(y.UtcTicks);
            return byTime != 0 ? byTime : string.CompareOrdinal(y.Id, x.Id);
        }
    }

    /// <summary>The vectors of one model of an index, kept in memory while its generation does not change.</summary>
    private sealed record VectorSet(string Model, int Dimensions, long Generation, string[] Ids, DateTimeOffset[] Times, float[] Values);

    /// <summary>
    /// The queue of the operations of one index and its single consumer, which runs them one after another, in the order
    /// they came, on the thread pool; it starts with the first operation and waits, without a thread, while there is none.
    /// Only the consumer touches <see cref="Vectors"/>, the vectors of the last semantic search.
    /// </summary>
    private sealed class IndexQueue
    {
        private readonly Channel<IndexJob> _jobs = Channel.CreateUnbounded<IndexJob>(new UnboundedChannelOptions { SingleReader = true });
        private int _started;

        public VectorSet? Vectors { get; set; }

        public Task<T> Enqueue<T>(Func<IndexQueue, T> work, CancellationToken ct)
        {
            var job = new IndexJob<T>(work, ct);
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = Task.Run(ConsumeAsync, CancellationToken.None);
            }

            // Unbounded and never completed: the write always succeeds.
            _jobs.Writer.TryWrite(job);
            return job.Done;
        }

        private async Task ConsumeAsync()
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                job.Run(this);
            }
        }
    }

    /// <summary>One operation in the queue of an index.</summary>
    private abstract class IndexJob
    {
        /// <summary>Runs the operation, unless its caller cancelled it, and gives its result, its exception or its cancellation to the caller.</summary>
        public abstract void Run(IndexQueue index);
    }

    private sealed class IndexJob<T> : IndexJob
    {
        private readonly Func<IndexQueue, T> _work;
        private readonly CancellationToken _ct;
        private readonly TaskCompletionSource<T> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellation;

        public IndexJob(Func<IndexQueue, T> work, CancellationToken ct)
        {
            _work = work;
            _ct = ct;

            // The caller stops waiting at once; the operation itself is skipped when its turn comes.
            _cancellation = ct.Register(static state => ((IndexJob<T>)state!).Cancel(), this);
        }

        public Task<T> Done => _done.Task;

        public override void Run(IndexQueue index)
        {
            _cancellation.Dispose();
            if (_ct.IsCancellationRequested)
            {
                Cancel();
                return;
            }

            try
            {
                _done.TrySetResult(_work(index));
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                Cancel();
            }
            catch (Exception ex)
            {
                // Whatever the operation throws is its caller's: the consumer goes on with the next one.
                _done.TrySetException(ex);
            }
        }

        private void Cancel() => _done.TrySetCanceled(_ct);
    }
}
