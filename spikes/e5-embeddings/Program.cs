// Harness of the embeddings spike (docs/investigacion/2026-10-30-spike-embeddings.md). Four measures decide the model of
// the semantic index (docs/17 D-03): (a) the tokenization of Microsoft.ML.Tokenizers against the reference of the model,
// (b) the size and load of the fp32 and int8 variants, (c) the speed on this CPU and (d) the quality in Spanish.
//
//   E5Spike all --model-dir <folder with onnx/> --reference <reference.json> --data <data folder>
//
// The model files are never in the repository: README.md says how to download them, with their SHA-256.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var options = Options.Parse(args);
var onnx = Path.Combine(options.ModelDir, "onnx");
var fp32Path = Path.Combine(onnx, "model.onnx");
var int8Path = Path.Combine(onnx, "model_qint8_avx512_vnni.onnx");
var spmPath = Path.Combine(onnx, "sentencepiece.bpe.model");

Report.Line($"# Spike de embeddings · {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
Report.Line($"CPU lógicas: {Environment.ProcessorCount} · .NET {Environment.Version} · ORT {OrtEnv.Instance().GetVersionString()}");
Report.Line($"Vector<float>.Count = {Vector<float>.Count} · AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported} · AVX-512F {System.Runtime.Intrinsics.X86.Avx512F.IsSupported} · AVX-VNNI {System.Runtime.Intrinsics.X86.AvxVnni.IsSupported}");

var sw = Stopwatch.StartNew();
var tokenizer = new XlmrTokenizer(spmPath);
Report.Line($"Tokenizador cargado en {sw.Elapsed.TotalMilliseconds:F0} ms ({new FileInfo(spmPath).Length / 1048576.0:F1} MB)");

if (options.Command is "all" or "tokens")
{
    Measures.Tokens(tokenizer, options.Reference);
}

if (options.Command is "all" or "parity")
{
    Measures.Parity(tokenizer, options.Reference, fp32Path, int8Path);
}

if (options.Command is "all" or "size")
{
    Measures.Size(fp32Path, int8Path);
}

if (options.Command is "all" or "quality")
{
    Measures.Quality(tokenizer, options.Data, fp32Path, int8Path);
}

if (options.Command is "all" or "speed")
{
    Measures.Speed(tokenizer, options.Data, fp32Path, int8Path);
}

if (options.Command is "all" or "search")
{
    Measures.Search(tokenizer, options.Data, fp32Path, int8Path);
}

internal sealed record Options(string Command, string ModelDir, string Reference, string Data)
{
    public static Options Parse(string[] args)
    {
        string Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
            ?? throw new ArgumentException($"Missing {name}.");
        return new Options(args.Length > 0 ? args[0] : "all", Value("--model-dir"), Value("--reference"), Value("--data"));
    }
}

internal static class Report
{
    public static void Line(string text = "") => Console.WriteLine(text);
}

/// <summary>XLM-RoBERTa ids from the SentencePiece model: &lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3 and every other piece shifted by one (fairseq).</summary>
internal sealed class XlmrTokenizer
{
    public const int MaxTokens = 512;
    public const long Bos = 0;
    public const long Pad = 1;
    public const long Eos = 2;
    public const long Unk = 3;

    private readonly SentencePieceTokenizer _spm;

    public XlmrTokenizer(string modelPath)
    {
        using var stream = File.OpenRead(modelPath);
        _spm = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    public string Describe() =>
        $"{_spm.GetType().Name}: unk={_spm.UnknownId}, bos={_spm.BeginningOfSentenceId}, eos={_spm.EndOfSentenceId}, dummyPrefix={_spm.AddDummyPrefix}, escapeWhiteSpaces={_spm.EscapeWhiteSpaces}, byteFallback={_spm.ByteFallback}, vocab={_spm.Vocabulary.Count}";

    public long[] Encode(string text)
    {
        var ids = _spm.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var count = Math.Min(ids.Count, MaxTokens - 2);
        var result = new long[count + 2];
        result[0] = Bos;
        for (var i = 0; i < count; i++)
        {
            result[i + 1] = ids[i] == _spm.UnknownId ? Unk : ids[i] + 1;
        }

        result[^1] = Eos;
        return result;
    }
}

/// <summary>multilingual-e5-small in ONNX Runtime: mean pooling over the attention mask and L2 normalisation.</summary>
internal sealed class E5Model : IDisposable
{
    public const int Dimensions = 384;
    private readonly InferenceSession _session;

    public E5Model(string path, int threads)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(path, options);
    }

    public float[] Embed(long[] ids) => Embed([ids])[0];

    public float[][] Embed(IReadOnlyList<long[]> batch)
    {
        var rows = batch.Count;
        var cols = batch.Max(b => b.Length);
        var ids = new long[rows * cols];
        var mask = new long[rows * cols];
        var types = new long[rows * cols];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var inside = c < batch[r].Length;
                ids[(r * cols) + c] = inside ? batch[r][c] : XlmrTokenizer.Pad;
                mask[(r * cols) + c] = inside ? 1 : 0;
            }
        }

        long[] shape = [rows, cols];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var typesValue = OrtValue.CreateTensorValueFromMemory(types, shape);
        var inputs = new Dictionary<string, OrtValue>
        {
            ["input_ids"] = idsValue,
            ["attention_mask"] = maskValue,
            ["token_type_ids"] = typesValue,
        };
        using var run = new RunOptions();
        using var outputs = _session.Run(run, inputs, ["last_hidden_state"]);
        var hidden = outputs[0].GetTensorDataAsSpan<float>();
        var result = new float[rows][];
        for (var r = 0; r < rows; r++)
        {
            var vector = new float[Dimensions];
            var n = 0;
            for (var c = 0; c < cols; c++)
            {
                if (mask[(r * cols) + c] == 0)
                {
                    continue;
                }

                n++;
                var offset = ((r * cols) + c) * Dimensions;
                for (var d = 0; d < Dimensions; d++)
                {
                    vector[d] += hidden[offset + d];
                }
            }

            var norm = 0.0;
            for (var d = 0; d < Dimensions; d++)
            {
                vector[d] /= n;
                norm += vector[d] * (double)vector[d];
            }

            var scale = (float)(1.0 / Math.Sqrt(norm));
            for (var d = 0; d < Dimensions; d++)
            {
                vector[d] *= scale;
            }

            result[r] = vector;
        }

        return result;
    }

    public void Dispose() => _session.Dispose();
}

internal static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
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
}

internal static class Stats
{
    public static double Percentile(IReadOnlyList<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        var index = Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1);
        return sorted[index];
    }

    public static string Summary(IReadOnlyList<double> ms) =>
        $"p50 {Percentile(ms, 0.50):F1} ms · p95 {Percentile(ms, 0.95):F1} ms · máx {ms.Max():F1} ms (n={ms.Count})";
}

internal sealed record Passage(string Id, string Text);

internal sealed record Question(string Id, string Text, string Answer);

internal static class Data
{
    public static (List<Passage> Passages, List<Question> Questions) Load(string folder)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "preguntas.json"), Encoding.UTF8));
        var passages = json.RootElement.GetProperty("pasajes").EnumerateArray()
            .Select(p => new Passage(p.GetProperty("id").GetString()!, p.GetProperty("texto").GetString()!)).ToList();
        var questions = json.RootElement.GetProperty("preguntas").EnumerateArray()
            .Select(q => new Question(q.GetProperty("id").GetString()!, q.GetProperty("texto").GetString()!, q.GetProperty("respuesta").GetString()!)).ToList();
        return (passages, questions);
    }
}

/// <summary>FTS5 as the memory store uses it (docs/14 D-21): unicode61, every term a quoted phrase, joined by OR, ranked by bm25.</summary>
internal sealed class Fts : IDisposable
{
    private readonly SqliteConnection _connection;

    public Fts(IEnumerable<Passage> passages)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (var create = _connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE f USING fts5(id UNINDEXED, texto);";
            create.ExecuteNonQuery();
        }

        using var transaction = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO f(id, texto) VALUES ($id, $texto);";
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var text = insert.Parameters.Add("$texto", SqliteType.Text);
        foreach (var passage in passages)
        {
            id.Value = passage.Id;
            text.Value = passage.Text;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public static string? Match(string text)
    {
        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Take(32)
            .Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToList();
        return terms.Count == 0 ? null : string.Join(" OR ", terms);
    }

    public List<string> Search(string text, int limit)
    {
        var match = Match(text);
        if (match is null)
        {
            return [];
        }

        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT id FROM f WHERE f MATCH $match ORDER BY bm25(f), id LIMIT $limit;";
        query.Parameters.AddWithValue("$match", match);
        query.Parameters.AddWithValue("$limit", limit);
        using var reader = query.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>A short list of Spanish function words, folded like unicode61 folds them (case and accents).</summary>
internal static class Stopwords
{
    private static readonly HashSet<string> Words = new(
        """
        a al algo algun alguna algunas alguno algunos ante antes aqui asi aun cada como con contra cual cuales cuando cuanto
        de del desde donde dos el ella ellas ello ellos en entre era es esa esas ese eso esos esta estan estas este esto estos
        fue ha hace hay hasta la las le les lo los mas me mi mis mucho muy nada ni no nos nosotros o otra otras otro otros
        para pero poco por porque puede que quien se sea ser si sin sobre son su sus tambien tan tanto te tiene tienen todo
        todos tu tus un una uno unos y ya yo
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    public static string Remove(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(t => !Words.Contains(Fold(t))));

    private static string Fold(string token)
    {
        var decomposed = token.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string([.. decomposed.Where(char.IsLetterOrDigit)]);
    }
}

internal static class Rrf
{
    public const int K = 60;

    /// <summary>Reciprocal rank fusion with k = 60; ties by id (the spike has no dates).</summary>
    public static List<string> Fuse(params IReadOnlyList<string>[] lists)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            for (var i = 0; i < list.Count; i++)
            {
                scores[list[i]] = scores.GetValueOrDefault(list[i]) + (1.0 / (K + i + 1));
            }
        }

        return [.. scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Key)];
    }
}

internal static class Measures
{
    public static void Tokens(XlmrTokenizer tokenizer, string referencePath)
    {
        Report.Line();
        Report.Line("## (a) Tokenización");
        Report.Line(tokenizer.Describe());
        using var reference = JsonDocument.Parse(File.ReadAllText(referencePath, Encoding.UTF8));
        var sentences = reference.RootElement.GetProperty("sentences").EnumerateArray().ToList();
        int equalHf = 0, equalSpm = 0;
        foreach (var sentence in sentences)
        {
            var text = sentence.GetProperty("text").GetString()!;
            var hf = sentence.GetProperty("hf").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            var spm = sentence.GetProperty("spm").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            var mine = tokenizer.Encode(text);
            equalHf += mine.SequenceEqual(hf) ? 1 : 0;
            equalSpm += mine.SequenceEqual(spm) ? 1 : 0;
            if (!mine.SequenceEqual(hf))
            {
                var first = Enumerable.Range(0, Math.Min(mine.Length, hf.Length)).FirstOrDefault(i => mine[i] != hf[i], Math.Min(mine.Length, hf.Length));
                Report.Line($"  distinta de tokenizers: «{Shorten(text)}» ({mine.Length} frente a {hf.Length} ids; primera diferencia en {first}: {Window(mine, first)} frente a {Window(hf, first)}){(mine.SequenceEqual(spm) ? " · igual que sentencepiece" : string.Empty)}");
            }
        }

        Report.Line($"Iguales a tokenizers (tokenizer.json): {equalHf} de {sentences.Count} · iguales a sentencepiece (+1, unk=3): {equalSpm} de {sentences.Count}");
    }

    public static void Parity(XlmrTokenizer tokenizer, string referencePath, string fp32Path, string int8Path)
    {
        Report.Line();
        Report.Line("## Paridad de los embeddings con la referencia de Python (onnxruntime)");
        using var reference = JsonDocument.Parse(File.ReadAllText(referencePath, Encoding.UTF8));
        var sentences = reference.RootElement.GetProperty("sentences").EnumerateArray().ToList();
        foreach (var (name, path, key) in new[] { ("fp32", fp32Path, "fp32"), ("int8", int8Path, "int8") })
        {
            using var model = new E5Model(path, Environment.ProcessorCount / 2);
            var sameIds = new List<double>();
            var ownIds = new List<double>();
            foreach (var sentence in sentences)
            {
                var hf = sentence.GetProperty("hf").EnumerateArray().Select(e => e.GetInt64()).ToArray();
                var expected = sentence.GetProperty(key).EnumerateArray().Select(e => e.GetSingle()).ToArray();
                sameIds.Add(VectorMath.Dot(model.Embed(hf), expected));
                ownIds.Add(VectorMath.Dot(model.Embed(tokenizer.Encode(sentence.GetProperty("text").GetString()!)), expected));
            }

            Report.Line($"{name}: coseno con los mismos ids mín {sameIds.Min():F6} · media {sameIds.Average():F6}; de punta a punta (tokenizador .NET) mín {ownIds.Min():F6} · media {ownIds.Average():F6}");
        }

        using var a = new E5Model(fp32Path, Environment.ProcessorCount / 2);
        using var b = new E5Model(int8Path, Environment.ProcessorCount / 2);
        var agreement = sentences.Select(s => s.GetProperty("hf").EnumerateArray().Select(e => e.GetInt64()).ToArray())
            .Select(ids => (double)VectorMath.Dot(a.Embed(ids), b.Embed(ids))).ToList();
        Report.Line($"int8 frente a fp32 (mismos ids): coseno mín {agreement.Min():F4} · media {agreement.Average():F4}");
    }

    public static void Size(string fp32Path, string int8Path)
    {
        Report.Line();
        Report.Line("## (b) Tamaño, carga y memoria");
        foreach (var (name, path) in new[] { ("fp32", fp32Path), ("int8", int8Path) })
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var before = process.PrivateMemorySize64;
            var loads = new List<double>();
            E5Model? kept = null;
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                var model = new E5Model(path, Environment.ProcessorCount / 2);
                loads.Add(sw.Elapsed.TotalMilliseconds);
                if (i < 2)
                {
                    model.Dispose();
                }
                else
                {
                    kept = model;
                }
            }

            kept!.Embed([0, 1000, 2]);
            process.Refresh();
            var after = process.PrivateMemorySize64;
            Report.Line($"{name}: {new FileInfo(path).Length / 1048576.0:F1} MB en disco · carga {string.Join(" / ", loads.Select(l => l.ToString("F0", CultureInfo.InvariantCulture)))} ms · memoria privada +{(after - before) / 1048576.0:F0} MB");
            kept.Dispose();
        }
    }

    public static void Quality(XlmrTokenizer tokenizer, string dataFolder, string fp32Path, string int8Path)
    {
        Report.Line();
        Report.Line("## (d) Calidad en castellano (30 preguntas, 40 pasajes)");
        var (passages, questions) = Data.Load(dataFolder);
        using var fts = new Fts(passages);
        var lexical = questions.ToDictionary(q => q.Id, q => fts.Search(q.Text, passages.Count));
        Metrics("FTS5 (bm25, como hoy)", questions, q => lexical[q.Id]);

        foreach (var (name, path) in new[] { ("fp32", fp32Path), ("int8", int8Path) })
        {
            using var model = new E5Model(path, Environment.ProcessorCount / 2);
            foreach (var prefixed in new[] { true, false })
            {
                if (!prefixed && name == "int8")
                {
                    continue;
                }

                var passageVectors = passages.ToDictionary(p => p.Id, p => model.Embed(tokenizer.Encode((prefixed ? "passage: " : string.Empty) + p.Text)));
                var semantic = questions.ToDictionary(q => q.Id, q =>
                {
                    var query = model.Embed(tokenizer.Encode((prefixed ? "query: " : string.Empty) + q.Text));
                    return passageVectors.OrderByDescending(p => VectorMath.Dot(query, p.Value)).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
                });
                var label = prefixed ? name : $"{name} sin prefijos";
                Metrics($"Vectores {label}", questions, q => semantic[q.Id]);
                Metrics($"Híbrida {label} (RRF k=60)", questions, q => Rrf.Fuse(lexical[q.Id], semantic[q.Id]));
                if (prefixed)
                {
                    // Variants of the lexical list, to see why the fusion loses against the vectors alone.
                    var content = questions.ToDictionary(q => q.Id, q => fts.Search(Stopwords.Remove(q.Text), passages.Count));
                    Metrics("  FTS5 sin palabras vacías", questions, q => content[q.Id]);
                    Metrics($"  Híbrida {label}, FTS5 sin palabras vacías", questions, q => Rrf.Fuse(content[q.Id], semantic[q.Id]));
                    Metrics($"  Híbrida {label}, FTS5 solo sus 10 primeros", questions, q => Rrf.Fuse([.. lexical[q.Id].Take(10)], semantic[q.Id]));
                    Metrics($"  Híbrida {label}, FTS5 sin palabras vacías y solo sus 10 primeros", questions, q => Rrf.Fuse([.. content[q.Id].Take(10)], semantic[q.Id]));
                    Metrics($"  Híbrida {label}, FTS5 sin palabras vacías y solo sus 5 primeros", questions, q => Rrf.Fuse([.. content[q.Id].Take(5)], semantic[q.Id]));
                }
            }
        }
    }

    private static void Metrics(string label, List<Question> questions, Func<Question, List<string>> ranking)
    {
        int top1 = 0, top5 = 0;
        var reciprocal = 0.0;
        var misses = new List<string>();
        foreach (var question in questions)
        {
            var rank = ranking(question).IndexOf(question.Answer) + 1;
            top1 += rank == 1 ? 1 : 0;
            top5 += rank is >= 1 and <= 5 ? 1 : 0;
            reciprocal += rank >= 1 ? 1.0 / rank : 0;
            if (rank is < 1 or > 5)
            {
                misses.Add(question.Id);
            }
        }

        Report.Line($"{label}: acierto@1 {top1}/{questions.Count} ({100.0 * top1 / questions.Count:F0} %) · entre los 5 primeros {top5}/{questions.Count} ({100.0 * top5 / questions.Count:F0} %) · MRR {reciprocal / questions.Count:F3}{(misses.Count > 0 ? " · fuera de los 5: " + string.Join(", ", misses) : string.Empty)}");
    }

    public static void Speed(XlmrTokenizer tokenizer, string dataFolder, string fp32Path, string int8Path)
    {
        Report.Line();
        Report.Line("## (c) Velocidad en esta CPU");
        var (passages, questions) = Data.Load(dataFolder);
        var queries = questions.Select(q => "query: " + q.Text).ToList();

        var tokenizeTimes = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var sw = Stopwatch.StartNew();
            tokenizer.Encode(queries[i % queries.Count]);
            tokenizeTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        Report.Line($"Tokenizar una consulta: {Stats.Summary(tokenizeTimes)}");

        int[] threads = [1, 2, 4, 8];
        foreach (var (name, path) in new[] { ("fp32", fp32Path), ("int8", int8Path) })
        {
            foreach (var n in threads)
            {
                using var model = new E5Model(path, n);
                for (var i = 0; i < 10; i++)
                {
                    model.Embed(tokenizer.Encode(queries[i % queries.Count]));
                }

                var times = new List<double>();
                for (var i = 0; i < 300; i++)
                {
                    var sw = Stopwatch.StartNew();
                    model.Embed(tokenizer.Encode(queries[i % queries.Count]));
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                Report.Line($"Consulta {name}, {n} hilos: {Stats.Summary(times)}");
            }
        }

        // Fragments of about 300 tokens (some 1 200 characters), as the index will embed them.
        var random = new Random(20261030);
        var fragments = Enumerable.Range(0, 64).Select(_ =>
        {
            var text = new StringBuilder("passage: ");
            while (text.Length < 1200)
            {
                text.Append(passages[random.Next(passages.Count)].Text).Append(' ');
            }

            return tokenizer.Encode(text.ToString()[..1200]);
        }).ToList();
        Report.Line($"Fragmentos de prueba: 64, de {fragments.Min(f => f.Length)} a {fragments.Max(f => f.Length)} tokens");
        foreach (var (name, path) in new[] { ("fp32", fp32Path), ("int8", int8Path) })
        {
            foreach (var n in new[] { 4, 8 })
            {
                using var model = new E5Model(path, n);
                model.Embed(fragments.Take(2).ToList());
                foreach (var batch in new[] { 1, 8, 16, 32 })
                {
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < fragments.Count; i += batch)
                    {
                        model.Embed(fragments.Skip(i).Take(batch).ToList());
                    }

                    Report.Line($"Indexar {name}, {n} hilos, lotes de {batch}: {sw.Elapsed.TotalMilliseconds / fragments.Count:F1} ms por fragmento");
                }
            }
        }

    }

    /// <summary>F12-5: one query in Spanish against 10 000 fragments, with its embedding, the cosine scan, FTS5 and the fusion.</summary>
    public static void Search(XlmrTokenizer tokenizer, string dataFolder, string fp32Path, string int8Path)
    {
        Report.Line();
        Report.Line("## (c) Consulta completa con 10 000 fragmentos (F12-5)");
        var (passages, questions) = Data.Load(dataFolder);
        var random = new Random(20261030);
        const int count = 10_000;
        var vectors = new float[count * E5Model.Dimensions];
        for (var i = 0; i < count; i++)
        {
            var norm = 0.0;
            for (var d = 0; d < E5Model.Dimensions; d++)
            {
                var value = (float)((random.NextDouble() * 2) - 1);
                vectors[(i * E5Model.Dimensions) + d] = value;
                norm += value * (double)value;
            }

            var scale = (float)(1 / Math.Sqrt(norm));
            for (var d = 0; d < E5Model.Dimensions; d++)
            {
                vectors[(i * E5Model.Dimensions) + d] *= scale;
            }
        }

        var corpus = Enumerable.Range(0, count).Select(i =>
        {
            var text = new StringBuilder();
            var target = 300 + random.Next(1200);
            while (text.Length < target)
            {
                text.Append(passages[random.Next(passages.Count)].Text).Append(' ');
            }

            return new Passage($"f{i:D5}", text.ToString()[..target]);
        }).ToList();
        var buildFts = Stopwatch.StartNew();
        using var fts = new Fts(corpus);
        Report.Line($"FTS5 de 10 000 fragmentos construido en {buildFts.Elapsed.TotalMilliseconds:F0} ms ({corpus.Sum(c => c.Text.Length) / 1048576.0:F1} M caracteres; vectores al azar normalizados, {count * E5Model.Dimensions * 4 / 1048576.0:F1} MB)");

        foreach (var (name, path) in new[] { ("int8", int8Path), ("fp32", fp32Path) })
        {
            foreach (var threads in new[] { 2, 4 })
            {
                foreach (var withoutFunctionWords in new[] { true, false })
                {
                    using var model = new E5Model(path, threads);
                    var embed = new List<double>();
                    var scan = new List<double>();
                    var lexical = new List<double>();
                    var total = new List<double>();
                    for (var i = 0; i < 330; i++)
                    {
                        var query = questions[i % questions.Count].Text;
                        var all = Stopwatch.StartNew();
                        var sw = Stopwatch.StartNew();
                        var vector = model.Embed(tokenizer.Encode("query: " + query));
                        var tEmbed = sw.Elapsed.TotalMilliseconds;
                        sw.Restart();
                        var best = new PriorityQueue<int, float>();
                        for (var f = 0; f < count; f++)
                        {
                            var score = VectorMath.Dot(vector, vectors.AsSpan(f * E5Model.Dimensions, E5Model.Dimensions));
                            if (best.Count < 50)
                            {
                                best.Enqueue(f, score);
                            }
                            else if (best.TryPeek(out _, out var worst) && score > worst)
                            {
                                best.DequeueEnqueue(f, score);
                            }
                        }

                        var semantic = new List<string>();
                        while (best.TryDequeue(out var f, out _))
                        {
                            semantic.Add($"f{f:D5}");
                        }

                        semantic.Reverse();
                        var tScan = sw.Elapsed.TotalMilliseconds;
                        sw.Restart();
                        var words = fts.Search(withoutFunctionWords ? Stopwords.Remove(query) : query, 50);
                        var tFts = sw.Elapsed.TotalMilliseconds;
                        _ = Rrf.Fuse(words, semantic);
                        var tAll = all.Elapsed.TotalMilliseconds;
                        if (i >= 30)
                        {
                            embed.Add(tEmbed);
                            scan.Add(tScan);
                            lexical.Add(tFts);
                            total.Add(tAll);
                        }
                    }

                    Report.Line($"{name}, {threads} hilos, FTS5 {(withoutFunctionWords ? "sin palabras vacías" : "como hoy")}: TOTAL {Stats.Summary(total)} · embedding {Stats.Summary(embed)} · coseno {Stats.Summary(scan)} · FTS5 {Stats.Summary(lexical)}");
                }
            }
        }
    }

    private static string Shorten(string text) => text.Length <= 60 ? text.Replace("\n", "\\n", StringComparison.Ordinal) : text[..57].Replace("\n", "\\n", StringComparison.Ordinal) + "...";

    private static string Window(long[] ids, int at) => "[" + string.Join(",", ids.Skip(Math.Max(0, at - 1)).Take(4)) + "]";
}
