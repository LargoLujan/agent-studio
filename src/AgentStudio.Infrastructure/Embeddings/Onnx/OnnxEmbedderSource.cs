using AgentStudio.Application.Semantic;
using Microsoft.ML.OnnxRuntime;

namespace AgentStudio.Infrastructure.Embeddings.Onnx;

/// <summary>
/// The local model of the semantic index in ONNX Runtime (docs/17 D-03, F12-6; ADR-0047; WP2). It only ever reads the
/// files of its <see cref="EmbeddingModelLocation"/>, never the network, and it loads only bytes that give the pinned size
/// and SHA-256: each file is read once, hashed, and the same bytes are what ONNX Runtime and the tokenizer receive, so
/// nothing can swap a file between the check and the load.
/// <list type="bullet">
/// <item><see cref="Check"/> loads nothing, and hashes a file only the first time in this run or when its size or time of
/// last write changed (<see cref="EmbeddingModelChecks"/>): the status of the index asks for it on every read.</item>
/// <item><see cref="OpenAsync"/> opens one embedder, once, in the background, and keeps it until the source is disposed.
/// A file that is missing, or that could not be read (another program held it for a moment), is looked for again at the
/// next opening (the user may download it in the meantime); a model that does not verify or does not load is not tried
/// again while its files keep their size and time and the model is not installed again (<see cref="EmbeddingModelFolderChanges"/>),
/// so a broken model does not cost a hash and a load on every search.</item>
/// <item>The session runs with <see cref="IntraOpThreads"/> threads, sequentially and with every graph optimization, as the
/// spike measured it; a short query warms it up, which also proves the graph gives what the embedder reads.</item>
/// </list>
/// </summary>
public sealed class OnnxEmbedderSource : IEmbedderSource
{
    /// <summary>The threads of one run: with 2 a query takes about 8 ms and leaves the processor to the interface (ADR-0047).</summary>
    public const int IntraOpThreads = 2;

    private readonly SemaphoreSlim _opening = new(1, 1);
    private readonly EmbeddingModelChecks _checks = new();
    private readonly Lock _gate = new();
    private OnnxEmbedder? _embedder;
    private (string Stamp, EmbeddingModelStatus Status)? _failure;
    private bool _disposed;

    public OnnxEmbedderSource(EmbeddingModelLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(location.ModelDirectory);
        ArgumentNullException.ThrowIfNull(location.Model);
        Location = location;
    }

    public EmbeddingModelLocation Location { get; }

    public EmbeddingModelStatus Check()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return EmbeddingModelFiles.Check(Location, CancellationToken.None, _checks);
    }

    public async Task<EmbedderOpening> OpenAsync(CancellationToken ct)
    {
        if (Opened() is { } open)
        {
            return open;
        }

        await _opening.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Opened() is { } opened)
            {
                return opened;
            }

            // Taken before the load: if the files change while it runs, the next opening finds another stamp and tries again.
            var stamp = EmbeddingModelFiles.Stamp(Location);
            if (_failure is { } failure && string.Equals(failure.Stamp, stamp, StringComparison.Ordinal))
            {
                return new EmbedderOpening(null, failure.Status);
            }

            var (embedder, status, remember) = await Task.Run(() => Load(ct), ct).ConfigureAwait(false);
            if (embedder is null)
            {
                _failure = remember ? (stamp, status) : null;
                return new EmbedderOpening(null, status);
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _embedder = embedder;
                    _failure = null;
                    return new EmbedderOpening(embedder, status);
                }
            }

            embedder.Dispose();
            throw new ObjectDisposedException(GetType().FullName);
        }
        finally
        {
            _opening.Release();
        }
    }

    /// <summary>Releases the embedder; a run in flight ends first (see <see cref="OnnxEmbedder"/>).</summary>
    public void Dispose()
    {
        OnnxEmbedder? embedder;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            embedder = _embedder;
            _embedder = null;
        }

        embedder?.Dispose();
    }

    private static SessionOptions SessionOptions() => new()
    {
        IntraOpNumThreads = IntraOpThreads,
        InterOpNumThreads = 1,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
    };

    /// <summary>What ONNX Runtime or the tokenizer throw for files that verify but cannot be loaded or run (not a bug of this code).</summary>
    private static bool IsLoadFailure(Exception ex) => ex is OnnxRuntimeException
        or InvalidDataException
        or IOException
        or ArgumentException
        or FormatException
        or InvalidOperationException
        or NotSupportedException
        or DllNotFoundException
        or EntryPointNotFoundException
        or BadImageFormatException
        or TypeInitializationException
        or OutOfMemoryException;

    /// <summary>The graph takes the three inputs and gives the output the embedder reads (ADR-0047).</summary>
    private static void CheckGraph(InferenceSession session)
    {
        foreach (var input in new[] { EmbeddingModelDescriptor.InputIds, EmbeddingModelDescriptor.AttentionMask, EmbeddingModelDescriptor.TokenTypeIds })
        {
            if (!session.InputMetadata.ContainsKey(input))
            {
                throw new InvalidDataException($"The model has no input '{input}'.");
            }
        }

        if (!session.OutputMetadata.ContainsKey(EmbeddingModelDescriptor.LastHiddenState))
        {
            throw new InvalidDataException($"The model has no output '{EmbeddingModelDescriptor.LastHiddenState}'.");
        }
    }

    private EmbedderOpening? Opened()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _embedder is { } embedder ? new EmbedderOpening(embedder, EmbeddingModelFiles.Ready(Location)) : null;
        }
    }

    /// <summary>
    /// The embedder of the verified files, or why there is none and whether that is worth remembering: a file of another
    /// size or hash, or one that does not load, stays so while it does not change; a missing or unreadable one may not.
    /// </summary>
    private (OnnxEmbedder? Embedder, EmbeddingModelStatus Status, bool Remember) Load(CancellationToken ct)
    {
        var model = Location.Model;
        var tokenizerBytes = EmbeddingModelFiles.ReadVerified(Location, model.Tokenizer, ct, out var status);
        if (tokenizerBytes is null)
        {
            return (null, status, status.State == EmbeddingModelState.Mismatch);
        }

        var modelBytes = EmbeddingModelFiles.ReadVerified(Location, model.Model, ct, out status);
        if (modelBytes is null)
        {
            return (null, status, status.State == EmbeddingModelState.Mismatch);
        }

        InferenceSession? session = null;
        OnnxEmbedder? embedder = null;
        try
        {
            var tokenizer = XlmRobertaTokenizer.Create(tokenizerBytes, model);
            using (var options = SessionOptions())
            {
                session = new InferenceSession(modelBytes, options);
            }

            CheckGraph(session);
            embedder = new OnnxEmbedder(model, session, tokenizer);
            session = null;
            embedder.WarmUp();
            return (embedder, EmbeddingModelFiles.Ready(Location), false);
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            embedder?.Dispose();
            session?.Dispose();
            return (null, new EmbeddingModelStatus(
                EmbeddingModelState.Unloadable,
                Location.ModelDirectory,
                $"El modelo coincide con su huella, pero no se ha podido cargar: {ex.Message}"), true);
        }
    }
}
