using System.IO;
using System.Net.Http;
using AgentStudio.Application.Semantic;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Embeddings;

/// <summary>
/// The source of the local model without the model (docs/17 decision 9, F12-6; ADR-0047): it reads only its folder, a
/// file that does not give its pinned size or SHA-256 is never loaded, and without a model it says so, in Spanish, so the
/// search goes by words and tells why. The real model is in <see cref="OnnxEmbedderLocalModelTests"/>.
/// </summary>
public sealed class OnnxEmbedderSourceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "as modelo " + Guid.NewGuid().ToString("N")[..12]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TempFolders.Delete(_folder);

    private OnnxEmbedderSource Source(FakeModel model) => new(new EmbeddingModelLocation(_folder, model.Descriptor));

    /// <summary>The file held open by another program that lets no one else open it.</summary>
    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public void The_app_reads_the_model_from_its_folder_in_the_local_app_data_of_the_user()
    {
        var e5 = EmbeddingModels.MultilingualE5Small;
        var location = EmbeddingModelLocation.ForCurrentUser();

        Assert.Equal(SemanticIndexLocation.ModelDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), e5), location.ModelDirectory);
        Assert.Same(e5, location.Model);
        Assert.Equal([e5.Tokenizer, e5.Model], location.Files);
        Assert.Equal(Path.Combine(location.ModelDirectory, "model_qint8_avx512_vnni.onnx"), location.PathOf(e5.Model));
        Assert.Throws<ArgumentException>(() => location.PathOf(e5.Model with { FileName = @"..\fuera.onnx" }));
        Assert.Throws<ArgumentException>(() => location.PathOf(e5.Model with { FileName = "onnx/model.onnx" }));
        Assert.Throws<ArgumentException>(() => location.PathOf(e5.Model with { FileName = ".." }));
    }

    [Fact]
    public async Task Without_its_files_the_model_is_missing_it_says_so_and_nothing_is_created()
    {
        using var source = Source(FakeModel.Create());

        var status = source.Check();
        var opening = await source.OpenAsync(Ct);

        Assert.Equal((EmbeddingModelState.Missing, _folder), (status.State, status.Directory));
        Assert.Equal("El modelo no está descargado: falta el fichero «sentencepiece.bpe.model».", status.Problem);
        Assert.Null(opening.Embedder);
        Assert.Equal(status, opening.Status);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task A_file_of_another_size_is_a_mismatch_and_is_never_loaded()
    {
        var model = FakeModel.Create();
        model.WriteTo(_folder);
        await File.AppendAllTextAsync(Path.Combine(_folder, model.Model.FileName), "x", Ct);
        using var source = Source(model);

        var status = source.Check();
        var opening = await source.OpenAsync(Ct);

        Assert.Equal(EmbeddingModelState.Mismatch, status.State);
        Assert.Equal("El fichero «model.onnx» mide 40001 bytes y el fijado mide 40000: no coincide con su huella y no se carga.", status.Problem);
        Assert.Null(opening.Embedder);
        Assert.Equal(EmbeddingModelState.Mismatch, opening.Status.State);
    }

    [Fact]
    public async Task A_file_of_the_pinned_size_that_does_not_give_its_SHA256_is_rejected_and_never_loaded()
    {
        var model = FakeModel.Create();
        model.WriteTo(_folder);
        var path = Path.Combine(_folder, model.Model.FileName);
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, Ct);
        using var source = Source(model);

        var status = source.Check();
        var opening = await source.OpenAsync(Ct);

        // Had the bytes been loaded, they would be unloadable (they are not a model): a mismatch was never given to ONNX Runtime.
        Assert.Equal(EmbeddingModelState.Mismatch, status.State);
        Assert.Equal("El fichero «model.onnx» no da su SHA-256 fijado: no coincide con su huella y no se carga.", status.Problem);
        Assert.Null(opening.Embedder);
        Assert.Equal(status, opening.Status);
    }

    [Fact]
    public async Task Files_that_give_their_SHA256_but_are_not_a_model_cannot_be_loaded_and_are_not_tried_again_until_they_change()
    {
        var model = FakeModel.Create();
        model.WriteTo(_folder);
        using var source = Source(model);

        Assert.True(source.Check().IsReady);
        var first = await source.OpenAsync(Ct);

        Assert.Null(first.Embedder);
        Assert.Equal(EmbeddingModelState.Unloadable, first.Status.State);
        Assert.StartsWith("El modelo coincide con su huella, pero no se ha podido cargar: ", first.Status.Problem, StringComparison.Ordinal);

        // While the files keep their size and time, the failure is remembered: no hash and no load on every search.
        Assert.Same(first.Status, (await source.OpenAsync(Ct)).Status);

        // When they change (the installer ran, or the user copied them), it is tried again.
        File.SetLastWriteTimeUtc(Path.Combine(_folder, model.Model.FileName), DateTime.UtcNow.AddMinutes(-5));
        var again = await source.OpenAsync(Ct);
        Assert.Equal(EmbeddingModelState.Unloadable, again.Status.State);
        Assert.NotSame(first.Status, again.Status);
    }

    [Fact]
    public async Task A_file_another_program_holds_is_looked_at_again_at_the_next_opening()
    {
        var model = FakeModel.Create();
        model.WriteTo(_folder);
        using var source = Source(model);

        using (new FileStream(Path.Combine(_folder, model.Model.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var held = await source.OpenAsync(Ct);

            Assert.Equal(EmbeddingModelState.Unloadable, held.Status.State);
            Assert.StartsWith("No se ha podido leer el fichero «model.onnx» del modelo: ", held.Status.Problem, StringComparison.Ordinal);
            Assert.Equal(EmbeddingModelState.Unloadable, source.Check().State);
        }

        // Released, with the same size and time: it is read again, not remembered as broken.
        var released = await source.OpenAsync(Ct);
        Assert.StartsWith("El modelo coincide con su huella, pero no se ha podido cargar: ", released.Status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_check_of_files_that_did_not_change_does_not_read_them_and_a_new_size_or_time_reads_them_again()
    {
        var model = FakeModel.Create();
        model.WriteTo(_folder);
        var path = Path.Combine(_folder, model.Model.FileName);
        using var source = Source(model);

        // The first check of a run hashes the files.
        Assert.True(source.Check().IsReady);

        // Nothing changed: it is not read again. Had it been, the lock would have made it unreadable.
        using (Hold(path))
        {
            Assert.True(source.Check().IsReady);
        }

        // Another time of last write: it is read again, and the lock shows that it tried.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-3));
        using (Hold(path))
        {
            var held = source.Check();
            Assert.Equal(EmbeddingModelState.Unloadable, held.State);
            Assert.StartsWith("No se ha podido leer el fichero «model.onnx» del modelo: ", held.Problem, StringComparison.Ordinal);
        }

        Assert.True(source.Check().IsReady);

        // Another size: it is checked again and no longer gives its pinned size; back to its bytes, it is ready again.
        await File.AppendAllTextAsync(path, "x", Ct);
        Assert.Equal(EmbeddingModelState.Mismatch, source.Check().State);
        await File.WriteAllBytesAsync(path, model.ModelBytes, Ct);
        Assert.True(source.Check().IsReady);

        // Another run of the app, another source: its first check hashes the files again.
        using (Hold(path))
        {
            using var restarted = Source(model);
            Assert.Equal(EmbeddingModelState.Unloadable, restarted.Check().State);
        }
    }

    [Fact]
    public async Task What_a_source_remembers_of_a_broken_file_is_forgotten_once_the_model_is_installed_again()
    {
        var model = FakeModel.Create();
        model.WriteTo(_folder);
        var path = Path.Combine(_folder, model.Model.FileName);
        var written = File.GetLastWriteTimeUtc(path);
        var damaged = (byte[])model.ModelBytes.Clone();
        damaged[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, damaged, Ct);
        File.SetLastWriteTimeUtc(path, written);
        using var source = Source(model);

        var broken = await source.OpenAsync(Ct);
        Assert.Equal(EmbeddingModelState.Mismatch, broken.Status.State);
        Assert.Equal(EmbeddingModelState.Mismatch, source.Check().State);

        // Restored by hand with the same size and time: nothing tells the source, which still says what it remembered.
        await File.WriteAllBytesAsync(path, model.ModelBytes, Ct);
        File.SetLastWriteTimeUtc(path, written);
        Assert.Same(broken.Status, (await source.OpenAsync(Ct)).Status);
        Assert.Equal(EmbeddingModelState.Mismatch, source.Check().State);

        // Installed again: the installer finds both files in place and downloads nothing, and the source looks again.
        using var server = new FakeModelServer();
        using var http = new HttpClient(server, disposeHandler: false);
        var installed = await new HttpEmbeddingModelInstaller(http, new EmbeddingModelLocation(_folder, model.Descriptor))
            .InstallAsync(new EmbeddingModelDownloadConsent("manuel", new DateTimeOffset(2026, 11, 3, 22, 0, 0, TimeSpan.FromHours(1))), null, Ct);
        Assert.True(installed.IsReady);
        Assert.Empty(server.Requests);

        Assert.True(source.Check().IsReady);
        var again = await source.OpenAsync(Ct);
        Assert.Equal(EmbeddingModelState.Unloadable, again.Status.State);
    }

    [Fact]
    public async Task Openings_at_once_load_once()
    {
        var model = FakeModel.Create();
        model.WriteTo(_folder);
        using var source = Source(model);

        var openings = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => source.OpenAsync(Ct)));

        Assert.All(openings, opening => Assert.Same(openings[0].Status, opening.Status));
    }

    [Fact]
    public async Task A_disposed_source_opens_nothing()
    {
        var source = Source(FakeModel.Create());
        source.Dispose();
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.Check());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.OpenAsync(Ct));
    }
}
