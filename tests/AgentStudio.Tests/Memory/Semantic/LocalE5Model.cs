using System.IO;
using System.Security.Cryptography;
using AgentStudio.Application.Semantic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// The local model for the tests that measure with it (F12-4 and the calibration of the floor of what is found only by
/// meaning, ADR-0047, addendum of 2026-11-03). It looks in the folder <see cref="DirectoryVariable"/> names, or else in the
/// pinned one (<see cref="SemanticIndexLocation.ModelDirectory"/>), and opens the model only when its files give the pinned
/// size and SHA-256. It never downloads anything: without the files there is no model, and those tests are skipped.
/// </summary>
internal static class LocalModel
{
    /// <summary>The variable that names another folder with the two files of the model (the spike's download, for instance).</summary>
    public const string DirectoryVariable = "AGENTSTUDIO_E5_MODEL_DIR";

    /// <summary>The variable that names a file where the tests with the model also leave their figures (the output of a test that passes is not shown).</summary>
    public const string ReportVariable = "AGENTSTUDIO_CALIBRATION_REPORT";

    /// <summary>Writes <paramref name="text"/> to the output of the test and, when <see cref="ReportVariable"/> names a file, at its end.</summary>
    public static void Report(string text)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        if (Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } file)
        {
            File.AppendAllText(file, text + Environment.NewLine);
        }
    }

    private static readonly EmbeddingModelDescriptor E5 = EmbeddingModels.MultilingualE5Small;

    /// <summary>
    /// Whether the two files of the model are in its folder with their pinned sizes (<see cref="Open"/> checks their SHA-256):
    /// the folder <see cref="DirectoryVariable"/> names, or its <c>onnx</c> subfolder (the layout of the download of the
    /// spike), or else the pinned one.
    /// </summary>
    public static bool IsOnDisk(string localAppData, out string directory)
    {
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } named)
        {
            directory = HasFiles(named) || !HasFiles(Path.Combine(named, "onnx")) ? named : Path.Combine(named, "onnx");
        }
        else
        {
            directory = SemanticIndexLocation.ModelDirectory(localAppData, E5);
        }

        return HasFiles(directory);
    }

    private static bool HasFiles(string folder) =>
        new[] { E5.Model, E5.Tokenizer }.All(f => new FileInfo(Path.Combine(folder, f.FileName)) is { Exists: true } file && file.Length == f.Bytes);

    /// <summary>The model, when its files are on disk and give their pinned SHA-256; null otherwise. The caller disposes it.</summary>
    public static IEmbedderSource? Open(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        if (!IsOnDisk(localAppData, out var directory))
        {
            return null;
        }

        var source = new SpikeE5Source(directory);
        if (source.Check().IsReady)
        {
            return source;
        }

        source.Dispose();
        return null;
    }
}

/// <summary>
/// multilingual-e5-small in the process with the code of the spike of the P0 (<c>spikes/e5-embeddings</c>): the ids of
/// XLM-RoBERTa from the SentencePiece model, ONNX Runtime with two threads, and the mean of the hidden states over the
/// attention mask, L2-normalised. WP2's source takes its place at the integration. The files are checked once, by size and
/// SHA-256, and a file that does not give them is never loaded.
/// </summary>
internal sealed class SpikeE5Source(string directory) : IEmbedderSource
{
    private static readonly EmbeddingModelDescriptor E5 = EmbeddingModels.MultilingualE5Small;

    private readonly Lazy<EmbeddingModelStatus> _status = new(() => Verify(directory));
    private readonly object _gate = new();
    private SpikeE5Embedder? _embedder;

    public EmbeddingModelStatus Check() => _status.Value;

    public Task<EmbedderOpening> OpenAsync(CancellationToken ct)
    {
        var status = Check();
        if (!status.IsReady)
        {
            return Task.FromResult(new EmbedderOpening(null, status));
        }

        lock (_gate)
        {
            _embedder ??= new SpikeE5Embedder(Path.Combine(directory, E5.Model.FileName), Path.Combine(directory, E5.Tokenizer.FileName));
        }

        return Task.FromResult(new EmbedderOpening(_embedder, status));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _embedder?.Dispose();
            _embedder = null;
        }
    }

    private static EmbeddingModelStatus Verify(string directory)
    {
        foreach (var file in new[] { E5.Model, E5.Tokenizer })
        {
            var path = Path.Combine(directory, file.FileName);
            if (!File.Exists(path))
            {
                return new EmbeddingModelStatus(EmbeddingModelState.Missing, directory, $"Falta {file.FileName}.");
            }

            using var stream = File.OpenRead(path);
            if (stream.Length != file.Bytes || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), file.Sha256, StringComparison.Ordinal))
            {
                return new EmbeddingModelStatus(EmbeddingModelState.Mismatch, directory, $"{file.FileName} no da su tamaño o su SHA-256 fijados.");
            }
        }

        return new EmbeddingModelStatus(EmbeddingModelState.Ready, directory);
    }
}

/// <summary>The embedder of <see cref="SpikeE5Source"/>: the text trimmed, with the prefix of its role, cut at 512 tokens.</summary>
internal sealed class SpikeE5Embedder : IEmbedder, IDisposable
{
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly object _gate = new();

    public SpikeE5Embedder(string modelPath, string tokenizerPath)
    {
        using (var stream = File.OpenRead(tokenizerPath))
        {
            _tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        }

        using var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
    }

    public EmbeddingModelDescriptor Model => EmbeddingModels.MultilingualE5Small;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var prefix = role == EmbeddingRole.Query ? Model.QueryPrefix : Model.PassagePrefix;
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                vectors.Add(Embed(Ids(prefix + text.Trim())));
            }
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    public void Dispose() => _session.Dispose();

    /// <summary>The ids of XLM-RoBERTa: &lt;s&gt; first, &lt;/s&gt; last, the unknown piece as its own id and every other piece shifted by one.</summary>
    private long[] Ids(string text)
    {
        var rule = Model.TokenIds;
        var pieces = _tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var count = Math.Min(pieces.Count, Model.MaxTokens - 2);
        var ids = new long[count + 2];
        ids[0] = rule.BeginOfSentence;
        for (var i = 0; i < count; i++)
        {
            ids[i + 1] = pieces[i] == _tokenizer.UnknownId ? rule.Unknown : pieces[i] + rule.PieceOffset;
        }

        ids[^1] = rule.EndOfSentence;
        return ids;
    }

    private float[] Embed(long[] ids)
    {
        var mask = Enumerable.Repeat(1L, ids.Length).ToArray();
        var types = new long[ids.Length];
        long[] shape = [1, ids.Length];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var typesValue = OrtValue.CreateTensorValueFromMemory(types, shape);
        var inputs = new Dictionary<string, OrtValue>
        {
            [EmbeddingModelDescriptor.InputIds] = idsValue,
            [EmbeddingModelDescriptor.AttentionMask] = maskValue,
            [EmbeddingModelDescriptor.TokenTypeIds] = typesValue,
        };
        using var run = new RunOptions();
        using var outputs = _session.Run(run, inputs, [EmbeddingModelDescriptor.LastHiddenState]);
        var hidden = outputs[0].GetTensorDataAsSpan<float>();
        var dimensions = Model.Dimensions;
        var vector = new float[dimensions];
        for (var token = 0; token < ids.Length; token++)
        {
            for (var d = 0; d < dimensions; d++)
            {
                vector[d] += hidden[(token * dimensions) + d];
            }
        }

        var norm = 0.0;
        for (var d = 0; d < dimensions; d++)
        {
            vector[d] /= ids.Length;
            norm += vector[d] * (double)vector[d];
        }

        var scale = (float)(1.0 / Math.Sqrt(norm));
        for (var d = 0; d < dimensions; d++)
        {
            vector[d] *= scale;
        }

        return vector;
    }
}
