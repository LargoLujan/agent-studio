using System.IO;
using AgentStudio.Application.Semantic;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Infrastructure.Embeddings.VectorMath;

namespace AgentStudio.Tests.Infrastructure.Embeddings;

/// <summary>
/// The embedder with the real multilingual-e5-small (docs/17 D-03; ADR-0047), only where it is already on disk
/// (<see cref="LocalE5Model"/>): the tests never download it, and are skipped without it. They pin what the spike
/// measured: the ids of <c>sentencepiece</c>, the vectors of onnxruntime in Python, the prefixes, the cut at 512 tokens,
/// the same vector for the same text from any thread, and a clean end when the source is disposed.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class OnnxEmbedderLocalModelTests(LocalE5Fixture fixture) : IClassFixture<LocalE5Fixture>
{
    private static readonly EmbeddingModelDescriptor E5 = EmbeddingModels.MultilingualE5Small;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEmbedder Embedder
    {
        get
        {
            LocalE5Model.Require();
            return fixture.Embedder!;
        }
    }

    [Fact]
    public async Task The_tokenizer_gives_the_ids_of_the_sentencepiece_reference_on_every_sentence()
    {
        var folder = LocalE5Model.Require();
        var tokenizer = XlmRobertaTokenizer.Create(await File.ReadAllBytesAsync(Path.Combine(folder, E5.Tokenizer.FileName), Ct), E5);
        var reference = E5Reference.Load();

        Assert.Equal(56, reference.Count);
        Assert.All(reference, sentence => Assert.Equal(sentence.Ids, tokenizer.Encode(sentence.Text)));
        Assert.Equal(512, tokenizer.Encode(reference[^1].Text).Length);
        Assert.Equal([0L, 2L], tokenizer.Encode(string.Empty));
    }

    [Fact]
    public async Task The_vectors_are_those_of_onnxruntime_in_Python_for_the_same_text()
    {
        var folder = LocalE5Model.Require();

        // Without prefixes, as the reference was computed; the files, and so their check, are the same.
        using var source = new OnnxEmbedderSource(new EmbeddingModelLocation(folder, E5 with { QueryPrefix = string.Empty, PassagePrefix = string.Empty }));
        var embedder = (await source.OpenAsync(Ct)).Embedder!;
        var reference = E5Reference.Load().Where(sentence => sentence.Int8 is not null).ToList();

        var vectors = await embedder.EmbedAsync([.. reference.Select(sentence => sentence.Text)], EmbeddingRole.Query, Ct);

        Assert.Equal(8, vectors.Count);
        for (var i = 0; i < vectors.Count; i++)
        {
            Assert.Equal(384, vectors[i].Length);
            Assert.InRange(Norm(vectors[i]), 0.99999, 1.00001);
            Assert.InRange(Dot(vectors[i], reference[i].Int8!), 0.9999, 1.0001);
        }
    }

    [Fact]
    public async Task The_role_gives_the_prefix_and_the_same_text_always_gives_the_same_vector()
    {
        var query = (await Embedder.EmbedAsync(["test en rojo"], EmbeddingRole.Query, Ct))[0];
        var trimmed = (await Embedder.EmbedAsync(["  test en rojo \n"], EmbeddingRole.Query, Ct))[0];
        var passage = (await Embedder.EmbedAsync(["test en rojo"], EmbeddingRole.Passage, Ct))[0];

        Assert.Equal(query, trimmed);
        Assert.NotEqual(query, passage);
        Assert.InRange(Dot(query, passage), 0.8, 0.99999);
        Assert.Same(E5, Embedder.Model);
    }

    [Fact]
    public async Task The_meaning_brings_together_what_the_words_do_not()
    {
        var query = (await Embedder.EmbedAsync(["pruebas que fallan"], EmbeddingRole.Query, Ct))[0];
        var passages = await Embedder.EmbedAsync(
            [
                "Hecho: el test está en rojo desde el cambio del validador.",
                "Decisión: el tema oscuro usa el contraste AA en todos los paneles.",
            ],
            EmbeddingRole.Passage,
            Ct);

        var related = Dot(query, passages[0]);
        var unrelated = Dot(query, passages[1]);
        TestContext.Current.TestOutputHelper?.WriteLine($"coseno con «test en rojo»: {related:F4}; con «tema oscuro»: {unrelated:F4}");
        // Measured on 2026-11-03: 0.8510 against 0.8248 (int8, this CPU).
        Assert.True(related > unrelated + 0.01, $"{related} is not clearly above {unrelated}.");
    }

    [Fact]
    public async Task Several_threads_embed_at_once_and_get_exactly_the_vectors_of_one_thread()
    {
        var texts = Enumerable.Range(0, 12).Select(i => $"Fragmento {i}: la verificación aislada pasa tras el arreglo número {i}.").ToList();
        var alone = new List<float[]>();
        foreach (var text in texts)
        {
            alone.Add((await Embedder.EmbedAsync([text], EmbeddingRole.Passage, Ct))[0]);
        }

        var together = await Task.WhenAll(texts.Select(text => Task.Run(() => Embedder.EmbedAsync([text], EmbeddingRole.Passage, Ct), Ct)));
        var batch = await Embedder.EmbedAsync(texts, EmbeddingRole.Passage, Ct);

        for (var i = 0; i < texts.Count; i++)
        {
            Assert.Equal(alone[i], together[i][0]);
            Assert.Equal(alone[i], batch[i]);
        }
    }

    [Fact]
    public async Task A_text_longer_than_the_model_takes_is_cut_at_512_tokens()
    {
        var text = string.Join(' ', Enumerable.Repeat("La bitácora del proyecto guarda cada petición con su resultado.", 120));

        var vectors = await Embedder.EmbedAsync([text, text + " Y esta frase ya no cabe en la entrada del modelo."], EmbeddingRole.Passage, Ct);

        Assert.Equal(vectors[0], vectors[1]);
        Assert.InRange(Norm(vectors[0]), 0.99999, 1.00001);
    }

    [Fact]
    public async Task A_blank_text_is_refused_and_no_text_gives_no_vector()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Embedder.EmbedAsync(["bien", " \t"], EmbeddingRole.Query, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Embedder.EmbedAsync(["bien", null!], EmbeddingRole.Query, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Embedder.EmbedAsync(["bien"], (EmbeddingRole)7, Ct));
        Assert.Empty(await Embedder.EmbedAsync([], EmbeddingRole.Query, Ct));
    }

    [Fact]
    public async Task A_model_file_that_gives_its_SHA256_but_is_not_a_model_cannot_be_loaded()
    {
        var folder = LocalE5Model.Require();
        var junkFolder = Path.Combine(Path.GetTempPath(), "as modelo roto " + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(junkFolder);
            File.Copy(Path.Combine(folder, E5.Tokenizer.FileName), Path.Combine(junkFolder, E5.Tokenizer.FileName));
            var junk = new byte[50_000];
            new Random(7).NextBytes(junk);
            await File.WriteAllBytesAsync(Path.Combine(junkFolder, "model.onnx"), junk, Ct);
            var descriptor = E5 with { Model = new EmbeddingModelFile("model.onnx", new Uri("https://modelos.invalid/model.onnx"), junk.Length, FakeModel.Sha256(junk)) };
            using var source = new OnnxEmbedderSource(new EmbeddingModelLocation(junkFolder, descriptor));

            var opening = await source.OpenAsync(Ct);

            Assert.Null(opening.Embedder);
            Assert.Equal(EmbeddingModelState.Unloadable, opening.Status.State);
            Assert.NotNull(opening.Status.Problem);
            TestContext.Current.TestOutputHelper?.WriteLine(opening.Status.Problem);
        }
        finally
        {
            TempFolders.Delete(junkFolder);
        }
    }

    [Fact]
    public async Task A_cancelled_batch_stops_between_texts()
    {
        var texts = Enumerable.Range(0, 60).Select(i => $"Pasaje {i}: " + string.Join(' ', Enumerable.Repeat("contenido de prueba para indexar", 30))).ToList();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var running = Embedder.EmbedAsync(texts, EmbeddingRole.Passage, cancel.Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Disposing_the_source_while_texts_are_embedded_ends_cleanly()
    {
        var folder = LocalE5Model.Require();
        var source = new OnnxEmbedderSource(new EmbeddingModelLocation(folder, E5));
        var embedder = (await source.OpenAsync(Ct)).Embedder!;
        var texts = Enumerable.Range(0, 40).Select(i => $"Pasaje {i}: " + string.Join(' ', Enumerable.Repeat("contenido de prueba para indexar", 30))).ToList();

        var running = embedder.EmbedAsync(texts, EmbeddingRole.Passage, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        source.Dispose();

        // It ends either way: the batch had finished, or its next text finds the session released.
        var failure = await Record.ExceptionAsync(() => running);
        Assert.True(failure is null or ObjectDisposedException, failure?.ToString());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => embedder.EmbedAsync(["otra"], EmbeddingRole.Query, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.OpenAsync(Ct));
    }
}

/// <summary>One opening of the real model for the tests of a class, when it is on disk (and none otherwise: the tests skip).</summary>
public sealed class LocalE5Fixture : IAsyncLifetime
{
    private OnnxEmbedderSource? _source;

    public IEmbedder? Embedder { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (LocalE5Model.Find() is not { } folder)
        {
            return;
        }

        _source = new OnnxEmbedderSource(new EmbeddingModelLocation(folder, LocalE5Model.Descriptor));
        var opening = await _source.OpenAsync(TestContext.Current.CancellationToken);
        Embedder = opening.Embedder ?? throw new InvalidOperationException($"The local model is on disk but did not open: {opening.Status.Problem}");
    }

    public ValueTask DisposeAsync()
    {
        _source?.Dispose();
        return ValueTask.CompletedTask;
    }
}
