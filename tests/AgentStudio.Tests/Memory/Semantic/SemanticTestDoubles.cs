using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Semantic;
using AgentStudio.Memory.Semantic;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// Models of the test embedders: the shape of multilingual-e5-small with another name, so their fingerprints are their own.
/// With 2 048 dimensions two different words almost never share one, so texts without a word or concept in common have a
/// similarity of 0. <see cref="Tuning"/> gives it a floor for what is found only by meaning (0.2, which lets one shared
/// concept in a short text through) and no margin, and leaves out the ceiling for common terms, which a test of a few
/// entries would trip on; the tests of that ceiling set it.
/// </summary>
internal static class TestModels
{
    public static EmbeddingModelDescriptor Hashing { get; } = EmbeddingModels.MultilingualE5Small with
    {
        Name = "agentstudio/test-hashing",
        Revision = "1",
        Dimensions = 2048,
    };

    /// <summary>The tuning of the tests: the cut of <see cref="Hashing"/>, no ceiling for common terms, and the weights of D-05.</summary>
    public static SemanticSearchTuning Tuning { get; } = SemanticSearchTuning.Default with
    {
        Cuts = new Dictionary<string, MeaningOnlyCut>(StringComparer.Ordinal) { [Hashing.Fingerprint()] = new MeaningOnlyCut(0.2f) },
        CommonTermFraction = null,
    };

    /// <summary>The tuning of the tests with no cut for any model: everything the meaning finds may enter.</summary>
    public static SemanticSearchTuning Uncut { get; } = Tuning with { Cuts = new Dictionary<string, MeaningOnlyCut>(StringComparer.Ordinal) };

    /// <summary>The tuning of the tests with <paramref name="cut"/> for <see cref="Hashing"/>.</summary>
    public static SemanticSearchTuning CutAt(MeaningOnlyCut cut) =>
        Tuning with { Cuts = new Dictionary<string, MeaningOnlyCut>(StringComparer.Ordinal) { [Hashing.Fingerprint()] = cut } };

    /// <summary>The same embedder in a later revision: another fingerprint, so its vectors are never compared with the first's.</summary>
    public static EmbeddingModelDescriptor HashingRevised { get; } = Hashing with { Revision = "2" };
}

/// <summary>
/// A deterministic embedder for tests (it is not the model, and says nothing of its quality): each word, folded as
/// <c>unicode61</c> folds it and without function words, is hashed into a dimension, so texts that share words point the
/// same way. <paramref name="concepts"/> maps words to a concept they share («rojo» and «fallan» to «fallo»), which is how a
/// test makes two texts close in meaning that share no word. It records every call and can be told to fail.
/// </summary>
internal sealed class TestEmbedder(EmbeddingModelDescriptor model, IReadOnlyDictionary<string, string>? concepts = null) : IEmbedder
{
    private readonly List<(IReadOnlyList<string> Texts, EmbeddingRole Role)> _calls = [];
    private int _failures;

    public EmbeddingModelDescriptor Model => model;

    public IReadOnlyList<(IReadOnlyList<string> Texts, EmbeddingRole Role)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The next <paramref name="calls"/> calls throw, as a broken runtime would.</summary>
    public void FailNext(int calls = 1) => Interlocked.Exchange(ref _failures, calls);

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ct.ThrowIfCancellationRequested();
        lock (_calls)
        {
            _calls.Add(([.. texts], role));
        }

        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            throw new InvalidOperationException("The test model failed, as a broken runtime would.");
        }

        Interlocked.Exchange(ref _failures, 0);
        foreach (var text in texts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        }

        return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Vector)]);
    }

    public float[] Vector(string text)
    {
        var vector = new float[model.Dimensions];
        foreach (var word in Words(text))
        {
            if (LexicalTerms.IsFunctionWord(word))
            {
                continue;
            }

            var key = concepts is not null && concepts.TryGetValue(word, out var concept) ? concept : word;
            vector[(int)(Fnv1a(key) % (uint)model.Dimensions)] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm == 0)
        {
            vector[0] = 1f;
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }

    /// <summary>The words of <paramref name="text"/>, lower case and without accents, cut at anything that is not a letter or a digit.</summary>
    public static IEnumerable<string> Words(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }
}

/// <summary>The local model as a test sets it: its state, the embedder it opens when it is ready, and how often it was opened.</summary>
internal sealed class TestEmbedderSource(IEmbedder? embedder, EmbeddingModelState state = EmbeddingModelState.Ready) : IEmbedderSource
{
    private int _opened;

    public IEmbedder? Embedder { get; set; } = embedder;

    public EmbeddingModelState State { get; set; } = state;

    public int Opened => Volatile.Read(ref _opened);

    public static TestEmbedderSource Without(EmbeddingModelState state) => new(null, state);

    public EmbeddingModelStatus Check() =>
        new(State, @"C:\Users\persona\AppData\Local\AgentStudio\models\test-hashing\1", State == EmbeddingModelState.Ready ? null : "El modelo de prueba no está listo.");

    public Task<EmbedderOpening> OpenAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _opened);
        var status = Check();
        return Task.FromResult(new EmbedderOpening(status.IsReady ? Embedder : null, status));
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// The store of the index as a test double of the contract (WP2 writes the real one): SQLite in memory with FTS5 and its
/// <c>unicode61</c> tokenizer and <c>bm25</c>, as the memory does, and the cosine computed in memory. One database per
/// project, kept open until <see cref="DeleteAsync"/> or <see cref="Dispose"/>. Its helpers let a test manipulate the
/// database as an attacker with access to <c>%LOCALAPPDATA%</c> would.
/// </summary>
internal sealed class TestSemanticStore : ISemanticIndexStore, IDisposable
{
    private const string Schema = """
        CREATE TABLE fragments(
            rowid INTEGER PRIMARY KEY,
            id TEXT NOT NULL UNIQUE,
            source_kind TEXT NOT NULL,
            reference TEXT NOT NULL,
            source_sha TEXT NOT NULL,
            part INTEGER NOT NULL,
            text TEXT NOT NULL,
            at TEXT NOT NULL,
            at_ticks INTEGER NOT NULL,
            sha TEXT NOT NULL,
            model TEXT NULL,
            vector BLOB NULL);
        CREATE VIRTUAL TABLE fragments_fts USING fts5(text, content = 'fragments', content_rowid = 'rowid');
        CREATE TRIGGER fragments_ai AFTER INSERT ON fragments BEGIN
            INSERT INTO fragments_fts(rowid, text) VALUES (new.rowid, new.text);
        END;
        CREATE TRIGGER fragments_ad AFTER DELETE ON fragments BEGIN
            INSERT INTO fragments_fts(fragments_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
        END;
        CREATE TRIGGER fragments_au AFTER UPDATE ON fragments BEGIN
            INSERT INTO fragments_fts(fragments_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
            INSERT INTO fragments_fts(rowid, text) VALUES (new.rowid, new.text);
        END;
        """;

    private readonly Dictionary<string, SqliteConnection> _databases = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private readonly List<string> _written = [];
    private readonly List<(IReadOnlyList<string> Terms, int Top)> _wordSearches = [];
    private int _upserts;

    /// <summary>How many fragments were written (each fragment of each upsert counts).</summary>
    public int Upserted => Volatile.Read(ref _upserts);

    /// <summary>The ids of every fragment written, in order.</summary>
    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_gate)
            {
                return [.. _written];
            }
        }
    }

    /// <summary>
    /// Whether the store does not keep the contract for a row that is not a fragment: its state gives it with the fingerprint
    /// it had, and a read that meets it fails instead of skipping it.
    /// </summary>
    public bool FailOnUnreadableRows { get; set; }

    /// <summary>How many times the state of the index was read.</summary>
    public int StateReads { get; private set; }

    public Task<IReadOnlyList<IndexedFragmentState>> ReadStateAsync(ProjectContext project, CancellationToken ct)
    {
        lock (_gate)
        {
            StateReads++;
            using var select = Command(project, "SELECT id, sha, model, source_kind, reference, source_sha, part, text, at FROM fragments ORDER BY id;");
            using var reader = select.ExecuteReader();
            var states = new List<IndexedFragmentState>();
            while (reader.Read())
            {
                // A row that is not a fragment is given without its fingerprint, so the index writes it again.
                var readable = FailOnUnreadableRows || FragmentOf(reader, 3) is not null;
                states.Add(new IndexedFragmentState(reader.GetString(0), readable ? reader.GetString(1) : string.Empty, reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            return Task.FromResult<IReadOnlyList<IndexedFragmentState>>(states);
        }
    }

    public Task UpsertAsync(ProjectContext project, IReadOnlyList<IndexedFragment> fragments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        lock (_gate)
        {
            foreach (var indexed in fragments)
            {
                if (indexed.Vector is not null && string.IsNullOrWhiteSpace(indexed.ModelFingerprint))
                {
                    throw new ArgumentException("A vector always comes with the fingerprint of its model.", nameof(fragments));
                }

                Upsert(project, indexed);
                _written.Add(indexed.Fragment.Id);
                Interlocked.Increment(ref _upserts);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var id in fragmentIds)
            {
                using var delete = Command(project, "DELETE FROM fragments WHERE id = $id;");
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SemanticFragment>> ReadAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SemanticFragment>>([.. Read(project, fragmentIds).Select(f => f.Fragment)]);
        }
    }

    public Task<IReadOnlyList<IndexedFragment>> ReadIndexedAsync(ProjectContext project, IReadOnlyList<string> fragmentIds, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<IndexedFragment>>(Read(project, fragmentIds));
        }
    }

    /// <summary>Every search by words the store answered, with its terms and how many places it asked for, in order.</summary>
    public IReadOnlyList<(IReadOnlyList<string> Terms, int Top)> WordSearches
    {
        get
        {
            lock (_gate)
            {
                return [.. _wordSearches];
            }
        }
    }

    public Task<IReadOnlyList<RankedCandidate>> SearchWordsAsync(ProjectContext project, IReadOnlyList<string> terms, int top, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0 || top <= 0)
        {
            return Task.FromResult<IReadOnlyList<RankedCandidate>>([]);
        }

        lock (_gate)
        {
            _wordSearches.Add(([.. terms], top));
            using var select = Command(project, """
                SELECT f.id, f.at FROM fragments_fts JOIN fragments f ON f.rowid = fragments_fts.rowid
                WHERE fragments_fts MATCH $match
                ORDER BY bm25(fragments_fts), f.at_ticks DESC, f.id
                LIMIT $top;
                """);
            select.Parameters.AddWithValue("$match", string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")));
            select.Parameters.AddWithValue("$top", top);
            using var reader = select.ExecuteReader();
            var ranked = new List<RankedCandidate>();
            while (reader.Read())
            {
                ranked.Add(new RankedCandidate(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }

            return Task.FromResult<IReadOnlyList<RankedCandidate>>(ranked);
        }
    }

    public Task<IReadOnlyList<SimilarCandidate>> SearchVectorsAsync(ProjectContext project, ReadOnlyMemory<float> query, string modelFingerprint, int top, CancellationToken ct)
    {
        var norm = MathF.Sqrt(query.ToArray().Sum(v => v * v));
        var normalised = query.ToArray().Select(v => norm == 0 ? 0 : v / norm).ToArray();
        lock (_gate)
        {
            using var select = Command(project, "SELECT id, at, at_ticks, vector FROM fragments WHERE model = $model AND vector IS NOT NULL;");
            select.Parameters.AddWithValue("$model", modelFingerprint);
            using var reader = select.ExecuteReader();
            var scored = new List<(string Id, DateTimeOffset At, long Ticks, float Score)>();
            while (reader.Read())
            {
                var vector = Decode((byte[])reader.GetValue(3));
                if (vector.Length != normalised.Length)
                {
                    continue;
                }

                var score = 0f;
                for (var i = 0; i < vector.Length; i++)
                {
                    score += vector[i] * normalised[i];
                }

                scored.Add((reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetInt64(2), score));
            }

            return Task.FromResult<IReadOnlyList<SimilarCandidate>>([.. scored
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Ticks)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .Take(top)
                .Select(s => new SimilarCandidate(s.Id, s.At, s.Score))]);
        }
    }

    public Task DeleteAsync(ProjectContext project, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_databases.Remove(project.ProjectId, out var connection))
            {
                connection.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Changes the text a fragment holds, keeping the fingerprint the index announces for it: what a manipulated database looks like.</summary>
    public void Rewrite(ProjectContext project, string fragmentId, string text)
    {
        lock (_gate)
        {
            using var update = Command(project, "UPDATE fragments SET text = $text WHERE id = $id;");
            update.Parameters.AddWithValue("$text", text);
            update.Parameters.AddWithValue("$id", fragmentId);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
    }

    /// <summary>Writes a fragment straight into the database, as a planted index would hold one.</summary>
    public void Plant(ProjectContext project, IndexedFragment fragment)
    {
        lock (_gate)
        {
            Upsert(project, fragment);
        }
    }

    /// <summary>Leaves the row of a fragment with a text no fragment can have (blank, or longer than a fragment), as a damaged database would.</summary>
    public void Corrupt(ProjectContext project, string fragmentId, string text) => Rewrite(project, fragmentId, text);

    /// <summary>Makes the reads stop finding a fragment its state still lists: what a refresh that removes it in the middle of a search looks like.</summary>
    public void HideFromReads(string fragmentId)
    {
        lock (_gate)
        {
            _hidden.Add(fragmentId);
        }
    }

    /// <summary>The vector the index holds for a fragment, and its model; null when it holds none.</summary>
    public (float[] Vector, string Model)? VectorOf(ProjectContext project, string fragmentId)
    {
        lock (_gate)
        {
            var indexed = Read(project, [fragmentId]).SingleOrDefault();
            return indexed is { Vector: { } vector, ModelFingerprint: { } model } ? (vector, model) : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var connection in _databases.Values)
            {
                connection.Dispose();
            }

            _databases.Clear();
        }
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

    private static float[] Decode(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    private void Upsert(ProjectContext project, IndexedFragment indexed)
    {
        var fragment = indexed.Fragment;
        using (var find = Command(project, "SELECT sha FROM fragments WHERE id = $id;"))
        {
            find.Parameters.AddWithValue("$id", fragment.Id);
            if (indexed.Vector is null && find.ExecuteScalar() is string sha && string.Equals(sha, fragment.Sha256, StringComparison.Ordinal))
            {
                // The same fragment without a vector: its row is written again (which repairs a damaged one) and keeps its vector.
                using var rewrite = Command(project, """
                    UPDATE fragments SET source_kind = $kind, reference = $reference, source_sha = $sourceSha, part = $part, text = $text,
                        at = $at, at_ticks = $ticks WHERE id = $id;
                    """);
                rewrite.Parameters.AddWithValue("$kind", fragment.Source.Kind.ToString());
                rewrite.Parameters.AddWithValue("$reference", fragment.Source.Reference);
                rewrite.Parameters.AddWithValue("$sourceSha", fragment.Source.Sha256);
                rewrite.Parameters.AddWithValue("$part", fragment.Part);
                rewrite.Parameters.AddWithValue("$text", fragment.Text);
                rewrite.Parameters.AddWithValue("$at", fragment.At.ToString("O", CultureInfo.InvariantCulture));
                rewrite.Parameters.AddWithValue("$ticks", fragment.At.UtcTicks);
                rewrite.Parameters.AddWithValue("$id", fragment.Id);
                rewrite.ExecuteNonQuery();
                return;
            }
        }

        using var upsert = Command(project, """
            INSERT INTO fragments(id, source_kind, reference, source_sha, part, text, at, at_ticks, sha, model, vector)
            VALUES ($id, $kind, $reference, $sourceSha, $part, $text, $at, $ticks, $sha, $model, $vector)
            ON CONFLICT(id) DO UPDATE SET source_kind = excluded.source_kind, reference = excluded.reference, source_sha = excluded.source_sha,
                part = excluded.part, text = excluded.text, at = excluded.at, at_ticks = excluded.at_ticks, sha = excluded.sha,
                model = excluded.model, vector = excluded.vector;
            """);
        upsert.Parameters.AddWithValue("$id", fragment.Id);
        upsert.Parameters.AddWithValue("$kind", fragment.Source.Kind.ToString());
        upsert.Parameters.AddWithValue("$reference", fragment.Source.Reference);
        upsert.Parameters.AddWithValue("$sourceSha", fragment.Source.Sha256);
        upsert.Parameters.AddWithValue("$part", fragment.Part);
        upsert.Parameters.AddWithValue("$text", fragment.Text);
        upsert.Parameters.AddWithValue("$at", fragment.At.ToString("O", CultureInfo.InvariantCulture));
        upsert.Parameters.AddWithValue("$ticks", fragment.At.UtcTicks);
        upsert.Parameters.AddWithValue("$sha", fragment.Sha256);
        upsert.Parameters.AddWithValue("$model", (object?)indexed.ModelFingerprint ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$vector", indexed.Vector is { } fresh ? Encode(fresh) : DBNull.Value);
        upsert.ExecuteNonQuery();
    }

    /// <summary>The fragment of the row the reader is on, from its column <paramref name="first"/> on (kind, reference, source hash, part, text, time); null when the row is not one.</summary>
    private static SemanticFragment? FragmentOf(SqliteDataReader reader, int first)
    {
        try
        {
            var source = new FragmentSource(Enum.Parse<FragmentSourceKind>(reader.GetString(first)), reader.GetString(first + 1), reader.GetString(first + 2));
            return new SemanticFragment(source, reader.GetInt32(first + 3), reader.GetString(first + 4), DateTimeOffset.Parse(reader.GetString(first + 5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }

    private List<IndexedFragment> Read(ProjectContext project, IReadOnlyList<string> fragmentIds)
    {
        var found = new List<IndexedFragment>();
        foreach (var id in fragmentIds)
        {
            if (_hidden.Contains(id))
            {
                continue;
            }

            using var select = Command(project, "SELECT source_kind, reference, source_sha, part, text, at, model, vector FROM fragments WHERE id = $id;");
            select.Parameters.AddWithValue("$id", id);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                continue;
            }

            if (FragmentOf(reader, 0) is not { } fragment)
            {
                // The contract: a row that is not a fragment is skipped. A store that does not keep it fails instead.
                if (FailOnUnreadableRows)
                {
                    throw new InvalidDataException($"The row of fragment '{id}' is not a fragment.");
                }

                continue;
            }

            found.Add(new IndexedFragment(fragment, reader.IsDBNull(7) ? null : Decode((byte[])reader.GetValue(7)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return found;
    }

    private SqliteCommand Command(ProjectContext project, string sql)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!_databases.TryGetValue(project.ProjectId, out var connection))
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = Schema;
            create.ExecuteNonQuery();
            _databases.Add(project.ProjectId, connection);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }
}
