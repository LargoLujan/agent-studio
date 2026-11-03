using AgentStudio.Application.Semantic;
using Microsoft.ML.OnnxRuntime;

namespace AgentStudio.Infrastructure.Embeddings.Onnx;

/// <summary>
/// The embeddings of a verified local model in ONNX Runtime, in the process and without network (docs/17 D-03;
/// ADR-0047; WP2), opened by <see cref="OnnxEmbedderSource"/>, which owns it.
/// <list type="bullet">
/// <item>Each text is trimmed, given the prefix of its role, tokenized (<see cref="XlmRobertaTokenizer"/>) and run alone:
/// the graph takes <c>input_ids</c>, <c>attention_mask</c> (all ones) and <c>token_type_ids</c> (all zeros) and gives
/// <c>last_hidden_state</c>; the vector is its mean over the tokens, L2-normalised, so a dot product is its cosine.</item>
/// <item>One text per run, never a padded batch: the int8 model quantizes its activations over the whole tensor, so a
/// text in a batch could come out a little different depending on its neighbours. Alone, the same text always gives the
/// same vector on the same machine; the spike measured that batches do not make indexing faster.</item>
/// <item>Several threads may embed at once: ONNX Runtime allows concurrent runs of one session, so a query never waits
/// for the indexing in the background, which its caller runs at low priority. The cancellation is checked between
/// texts.</item>
/// <item>Disposing it while a run is in flight is safe: the session is released when the last run ends, and nothing
/// new starts.</item>
/// </list>
/// </summary>
internal sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private static readonly string[] OutputNames = [EmbeddingModelDescriptor.LastHiddenState];

    private readonly InferenceSession _session;
    private readonly XlmRobertaTokenizer _tokenizer;
    private readonly Lock _gate = new();
    private int _running;
    private bool _disposed;

    /// <summary>Takes ownership of <paramref name="session"/>.</summary>
    public OnnxEmbedder(EmbeddingModelDescriptor model, InferenceSession session, XlmRobertaTokenizer tokenizer)
    {
        Model = model;
        _session = session;
        _tokenizer = tokenizer;
    }

    public EmbeddingModelDescriptor Model { get; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var prefix = role switch
        {
            EmbeddingRole.Query => Model.QueryPrefix,
            EmbeddingRole.Passage => Model.PassagePrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role of a text to embed."),
        };

        var inputs = new string[texts.Count];
        for (var i = 0; i < inputs.Length; i++)
        {
            var text = texts[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException($"Text {i} to embed is null or blank.", nameof(texts));
            }

            inputs[i] = prefix + text.Trim();
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return inputs.Length == 0
            ? Task.FromResult<IReadOnlyList<float[]>>([])
            : Task.Run<IReadOnlyList<float[]>>(() => EmbedAll(inputs, ct), ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_running > 0)
            {
                // The last run in flight releases it.
                return;
            }
        }

        _session.Dispose();
    }

    /// <summary>One run with a short query, so the first real one does not pay the first allocations and a broken graph shows now.</summary>
    internal void WarmUp() => Run(_tokenizer.Encode(Model.QueryPrefix + "ok"));

    private float[][] EmbedAll(string[] inputs, CancellationToken ct)
    {
        var vectors = new float[inputs.Length][];
        for (var i = 0; i < inputs.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            vectors[i] = Run(_tokenizer.Encode(inputs[i]));
        }

        return vectors;
    }

    private float[] Run(long[] ids)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _running++;
        }

        try
        {
            long[] shape = [1, ids.Length];
            var mask = new long[ids.Length];
            Array.Fill(mask, 1L);
            var types = new long[ids.Length];
            using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
            using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
            using var typesValue = OrtValue.CreateTensorValueFromMemory(types, shape);
            var inputs = new Dictionary<string, OrtValue>(3, StringComparer.Ordinal)
            {
                [EmbeddingModelDescriptor.InputIds] = idsValue,
                [EmbeddingModelDescriptor.AttentionMask] = maskValue,
                [EmbeddingModelDescriptor.TokenTypeIds] = typesValue,
            };
            using var options = new RunOptions();
            using var outputs = _session.Run(options, inputs, OutputNames);
            return MeanNormalized(outputs[0], ids.Length);
        }
        finally
        {
            bool release;
            lock (_gate)
            {
                _running--;
                release = _disposed && _running == 0;
            }

            if (release)
            {
                _session.Dispose();
            }
        }
    }

    /// <summary>The mean of the hidden states of the <paramref name="tokens"/> tokens of one text (the mask is all ones), L2-normalised.</summary>
    private float[] MeanNormalized(OrtValue hidden, int tokens)
    {
        var dimensions = Model.Dimensions;
        var shape = hidden.GetTensorTypeAndShape().Shape;
        if (shape.Length != 3 || shape[0] != 1 || shape[1] != tokens || shape[2] != dimensions)
        {
            throw new InvalidDataException($"The model gave {EmbeddingModelDescriptor.LastHiddenState} of shape [{string.Join(", ", shape)}], not [1, {tokens}, {dimensions}].");
        }

        var values = hidden.GetTensorDataAsSpan<float>();
        var sums = new double[dimensions];
        for (var token = 0; token < tokens; token++)
        {
            var row = values.Slice(token * dimensions, dimensions);
            for (var d = 0; d < dimensions; d++)
            {
                sums[d] += row[d];
            }
        }

        var norm = 0.0;
        for (var d = 0; d < dimensions; d++)
        {
            sums[d] /= tokens;
            norm += sums[d] * sums[d];
        }

        norm = Math.Sqrt(norm);
        if (!double.IsFinite(norm) || norm == 0)
        {
            throw new InvalidDataException("The model gave hidden states whose mean is zero or not finite, so it has no direction.");
        }

        var vector = new float[dimensions];
        for (var d = 0; d < dimensions; d++)
        {
            vector[d] = (float)(sums[d] / norm);
        }

        return vector;
    }
}
