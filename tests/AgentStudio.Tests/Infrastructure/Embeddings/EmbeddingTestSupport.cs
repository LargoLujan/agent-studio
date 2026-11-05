using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Application.Semantic;
using AgentStudio.Infrastructure.Embeddings;

namespace AgentStudio.Tests.Infrastructure.Embeddings;

/// <summary>
/// A model of two small files of known bytes, pinned like the real one (sizes and SHA-256) but at addresses that can
/// never resolve (<c>.invalid</c>, RFC 2606). Its bytes are not a model: a source that verifies them still cannot load them.
/// </summary>
internal sealed record FakeModel(EmbeddingModelDescriptor Descriptor, byte[] ModelBytes, byte[] TokenizerBytes)
{
    public EmbeddingModelFile Model => Descriptor.Model;

    public EmbeddingModelFile Tokenizer => Descriptor.Tokenizer;

    public static FakeModel Create(int modelBytes = 40_000, int tokenizerBytes = 3_000, int seed = 1)
    {
        var random = new Random(seed);
        var model = new byte[modelBytes];
        var tokenizer = new byte[tokenizerBytes];
        random.NextBytes(model);
        random.NextBytes(tokenizer);
        return new FakeModel(
            EmbeddingModels.MultilingualE5Small with
            {
                Name = "pruebas/modelo-falso",
                Revision = "0123456789abcdef0123456789abcdef01234567",
                Model = new EmbeddingModelFile("model.onnx", new Uri("https://modelos.invalid/pruebas/onnx/model.onnx"), model.Length, Sha256(model)),
                Tokenizer = new EmbeddingModelFile("sentencepiece.bpe.model", new Uri("https://modelos.invalid/pruebas/onnx/sentencepiece.bpe.model"), tokenizer.Length, Sha256(tokenizer)),
            },
            model,
            tokenizer);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Puts both files, as they are, in <paramref name="folder"/>.</summary>
    public void WriteTo(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, Model.FileName), ModelBytes);
        File.WriteAllBytes(Path.Combine(folder, Tokenizer.FileName), TokenizerBytes);
    }
}

/// <summary>
/// A server that never touches the network: it answers each address it was given, 404 to the rest, and keeps every
/// address it was asked for, in order.
/// </summary>
internal sealed class FakeModelServer : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Uri> _requests = new();

    public IReadOnlyList<Uri> Requests => [.. _requests];

    public void Answer(Uri address, Func<HttpResponseMessage> answer) => _answers[address.AbsoluteUri] = answer;

    /// <summary>Serves <paramref name="bytes"/> at the address of <paramref name="file"/>, with its length announced or not.</summary>
    public void Serve(EmbeddingModelFile file, byte[] bytes, bool announceLength = true) =>
        Answer(file.Source, () => Ok(new ServedStream(bytes), announceLength ? bytes.Length : null));

    public static HttpResponseMessage Ok(Stream body, long? length = null)
    {
        var content = new StreamContent(body);
        if (length is { } announced)
        {
            content.Headers.ContentLength = announced;
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request.RequestUri!);
        return Task.FromResult(_answers.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });
    }
}

/// <summary>
/// The body of an answer, as a network would give it: it cannot seek (so its length is unknown unless announced) and
/// gives at most 8 KiB per read. It may repeat its bytes for ever, fail after some bytes, or call back after each read.
/// </summary>
internal sealed class ServedStream(byte[] bytes, bool endless = false, long failAfter = long.MaxValue, Action<long>? afterRead = null) : Stream
{
    private long _served;

    /// <summary>How many bytes it has given.</summary>
    public long Served => Interlocked.Read(ref _served);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_served >= failAfter)
        {
            throw new IOException("La conexión se cerró a mitad de la descarga.");
        }

        var count = 0;
        while (count < buffer.Length && count < 8192 && (endless || _served + count < bytes.Length))
        {
            buffer[count] = bytes[(int)((_served + count) % bytes.Length)];
            count++;
        }

        Interlocked.Add(ref _served, count);
        afterRead?.Invoke(_served);
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Keeps every report, in order and at once (a <see cref="Progress{T}"/> would post them later).</summary>
internal sealed class ProgressRecorder<T> : IProgress<T>
{
    private readonly ConcurrentQueue<T> _items = new();

    public IReadOnlyList<T> Items => [.. _items];

    public void Report(T value) => _items.Enqueue(value);
}

/// <summary>
/// Where the tests find the real multilingual-e5-small without ever downloading it: the folder named by
/// <see cref="DirectoryVariable"/> (for instance the copy the spike downloaded), or the one Agent Studio installs it in,
/// under the <c>%LOCALAPPDATA%</c> of the user, which they only read. A test that needs it is skipped when neither holds
/// both files with their pinned size and SHA-256.
/// </summary>
internal static class LocalE5Model
{
    public const string DirectoryVariable = "AGENTSTUDIO_E5_MODEL_DIR";

    private static readonly Lazy<string?> Folder = new(FindVerified);

    public static EmbeddingModelDescriptor Descriptor => EmbeddingModels.MultilingualE5Small;

    /// <summary>The folder of the verified model, or null when this machine has none.</summary>
    public static string? Find() => Folder.Value;

    /// <summary>The folder of the verified model; the test is skipped when there is none.</summary>
    public static string Require()
    {
        var folder = Find();
        Assert.SkipWhen(folder is null, $"multilingual-e5-small is not on this machine: set {DirectoryVariable} to a folder with its two files, or install it from Agent Studio. The tests never download it.");
        return folder!;
    }

    private static string? FindVerified()
    {
        foreach (var folder in new[] { Environment.GetEnvironmentVariable(DirectoryVariable), EmbeddingModelLocation.ForCurrentUser().ModelDirectory })
        {
            if (!string.IsNullOrWhiteSpace(folder) && Verifies(folder, Descriptor.Tokenizer) && Verifies(folder, Descriptor.Model))
            {
                return folder;
            }
        }

        return null;
    }

    private static bool Verifies(string folder, EmbeddingModelFile file)
    {
        var path = Path.Combine(folder, file.FileName);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), file.Sha256, StringComparison.Ordinal);
    }
}

/// <summary>One sentence of the reference of the embedder: its text, the ids of <c>sentencepiece</c> and, for some, the int8 vector of onnxruntime in Python.</summary>
internal sealed record ReferenceSentence(string Text, long[] Ids, float[]? Int8);

/// <summary>The reference of the embedder (Fixtures/e5-reference.json; see its <c>about</c>).</summary>
internal static class E5Reference
{
    public static IReadOnlyList<ReferenceSentence> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Embeddings", "Fixtures", "e5-reference.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return
        [
            .. json.RootElement.GetProperty("sentences").EnumerateArray().Select(sentence => new ReferenceSentence(
                sentence.GetProperty("text").GetString()!,
                [.. sentence.GetProperty("ids").EnumerateArray().Select(id => id.GetInt64())],
                sentence.TryGetProperty("int8", out var vector) ? [.. vector.EnumerateArray().Select(value => value.GetSingle())] : null)),
        ];
    }
}

internal static class VectorMath
{
    public static double Dot(float[] a, float[] b)
    {
        Assert.Equal(a.Length, b.Length);
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    public static double Norm(float[] a) => Math.Sqrt(Dot(a, a));
}
