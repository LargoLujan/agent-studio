using System.IO;
using System.Net.Http;
using AgentStudio.Application.Semantic;
using AgentStudio.Infrastructure.Embeddings;
using AgentStudio.Infrastructure.Embeddings.Onnx;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Embeddings;

/// <summary>
/// The download of the model (docs/17 decision 9, F12-6; ADR-0047 point 4), against a server that never touches the
/// network: only with an OK, only from the pinned addresses, verified before it is usable, all or nothing for each file,
/// with progress and cancellation, and saying why when it could not.
/// </summary>
public sealed class HttpEmbeddingModelInstallerTests : IDisposable
{
    private static readonly EmbeddingModelDownloadConsent Consent = new("manuel", new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(1)));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "as descarga " + Guid.NewGuid().ToString("N")[..12]);
    private readonly FakeModelServer _server = new();
    private readonly HttpClient _http;

    // Over three MiB, so the progress has steps in between.
    private readonly FakeModel _model = FakeModel.Create(modelBytes: (3 * 1024 * 1024) + 12_345, tokenizerBytes: 5_000);

    public HttpEmbeddingModelInstallerTests() => _http = new HttpClient(_server, disposeHandler: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        TempFolders.Delete(_folder);
    }

    private HttpEmbeddingModelInstaller Installer(EmbeddingModelDescriptor? model = null) =>
        new(_http, new EmbeddingModelLocation(_folder, model ?? _model.Descriptor));

    private string PathOf(EmbeddingModelFile file) => Path.Combine(_folder, file.FileName);

    private string[] FilesLeft() => Directory.Exists(_folder) ? [.. Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)] : [];

    private byte[] Altered(byte[] bytes, int at)
    {
        var copy = (byte[])bytes.Clone();
        copy[at] ^= 0x5A;
        return copy;
    }

    [Fact]
    public async Task Nothing_is_downloaded_without_the_OK_of_someone_with_its_time()
    {
        var installer = Installer();

        await Assert.ThrowsAsync<ArgumentNullException>(() => installer.InstallAsync(null!, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(new EmbeddingModelDownloadConsent(" ", Consent.GrantedAt), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(new EmbeddingModelDownloadConsent("manuel", default), null, Ct));

        Assert.Empty(_server.Requests);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task The_files_come_only_from_their_pinned_addresses_and_are_in_place_once_they_verify()
    {
        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Serve(_model.Model, _model.ModelBytes, announceLength: false);
        var progress = new ProgressRecorder<EmbeddingModelDownloadProgress>();

        var status = await Installer().InstallAsync(Consent, progress, Ct);

        Assert.Equal(new EmbeddingModelStatus(EmbeddingModelState.Ready, _folder), status);
        Assert.Equal([_model.Tokenizer.Source, _model.Model.Source], _server.Requests);
        Assert.Equal(_model.TokenizerBytes, await File.ReadAllBytesAsync(PathOf(_model.Tokenizer), Ct));
        Assert.Equal(_model.ModelBytes, await File.ReadAllBytesAsync(PathOf(_model.Model), Ct));
        Assert.Equal(["model.onnx", "sentencepiece.bpe.model"], FilesLeft());
        using (var source = new OnnxEmbedderSource(new EmbeddingModelLocation(_folder, _model.Descriptor)))
        {
            Assert.True(source.Check().IsReady);
        }

        // Each file from nothing to its size, never back, the big one in steps of a MiB.
        foreach (var file in new[] { _model.Tokenizer, _model.Model })
        {
            var reports = progress.Items.Where(p => p.File == file.FileName).ToList();
            Assert.Equal(0, reports[0].Received);
            Assert.Equal(file.Bytes, reports[^1].Received);
            Assert.All(reports, p => Assert.Equal(file.Bytes, p.Total));
            Assert.Equal(reports.Select(p => p.Received).Order(), reports.Select(p => p.Received));
        }

        Assert.InRange(progress.Items.Count(p => p.File == _model.Model.FileName), 5, 6);
    }

    [Fact]
    public async Task A_download_that_does_not_give_the_pinned_SHA256_is_discarded_and_nothing_takes_its_place()
    {
        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Serve(_model.Model, Altered(_model.ModelBytes, 1_000));

        var status = await Installer().InstallAsync(Consent, null, Ct);

        Assert.Equal(EmbeddingModelState.Mismatch, status.State);
        Assert.Equal("El fichero «model.onnx» no da su SHA-256 fijado: no coincide con su huella y no se carga. La descarga se ha descartado.", status.Problem);
        Assert.Equal(["sentencepiece.bpe.model"], FilesLeft());
    }

    [Fact]
    public async Task A_download_longer_than_the_pinned_size_is_cut_at_once_and_discarded()
    {
        var endless = new ServedStream(_model.ModelBytes, endless: true);
        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Answer(_model.Model.Source, () => FakeModelServer.Ok(endless));

        var status = await Installer().InstallAsync(Consent, null, Ct);

        Assert.Equal(EmbeddingModelState.Mismatch, status.State);
        Assert.InRange(endless.Served, _model.Model.Bytes + 1, _model.Model.Bytes + 8192);
        Assert.Equal(["sentencepiece.bpe.model"], FilesLeft());
    }

    [Fact]
    public async Task An_announced_size_that_is_not_the_pinned_one_is_refused_before_reading_the_body()
    {
        var body = new ServedStream(_model.ModelBytes);
        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Answer(_model.Model.Source, () => FakeModelServer.Ok(body, _model.Model.Bytes - 1));

        var status = await Installer().InstallAsync(Consent, null, Ct);

        Assert.Equal(EmbeddingModelState.Mismatch, status.State);
        Assert.Equal(0, body.Served);
        Assert.Equal(["sentencepiece.bpe.model"], FilesLeft());
    }

    [Fact]
    public async Task A_shorter_download_is_discarded()
    {
        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Serve(_model.Model, _model.ModelBytes[..^10], announceLength: false);

        var status = await Installer().InstallAsync(Consent, null, Ct);

        Assert.Equal(EmbeddingModelState.Mismatch, status.State);
        Assert.Equal(["sentencepiece.bpe.model"], FilesLeft());
    }

    [Fact]
    public async Task A_server_error_leaves_the_model_missing_says_why_and_stops_there()
    {
        var status = await Installer().InstallAsync(Consent, null, Ct);

        Assert.Equal(EmbeddingModelState.Missing, status.State);
        Assert.Equal("No se ha podido descargar ni guardar el fichero «sentencepiece.bpe.model» del modelo: el servidor respondió 404 (Not Found).", status.Problem);
        Assert.Equal([_model.Tokenizer.Source], _server.Requests);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public async Task A_network_failure_before_or_during_the_download_leaves_the_model_missing_and_nothing_half_done()
    {
        _server.Answer(_model.Tokenizer.Source, () => throw new HttpRequestException("No se puede conectar con el servidor remoto."));
        var before = await Installer().InstallAsync(Consent, null, Ct);
        Assert.Equal(EmbeddingModelState.Missing, before.State);
        Assert.Equal("No se ha podido descargar ni guardar el fichero «sentencepiece.bpe.model» del modelo: No se puede conectar con el servidor remoto.", before.Problem);

        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Answer(_model.Model.Source, () => FakeModelServer.Ok(new ServedStream(_model.ModelBytes, failAfter: 500_000)));
        var during = await Installer().InstallAsync(Consent, null, Ct);
        Assert.Equal(EmbeddingModelState.Missing, during.State);
        Assert.Contains("La conexión se cerró a mitad de la descarga.", during.Problem, StringComparison.Ordinal);
        Assert.Equal(["sentencepiece.bpe.model"], FilesLeft());
    }

    [Fact]
    public async Task A_folder_that_cannot_be_created_leaves_the_model_missing_and_says_why()
    {
        // A file where the folder of the model should be.
        await File.WriteAllTextAsync(_folder, "no soy una carpeta", Ct);
        try
        {
            var status = await Installer().InstallAsync(Consent, null, Ct);

            Assert.Equal((EmbeddingModelState.Missing, _folder), (status.State, status.Directory));
            Assert.StartsWith("No se ha podido crear la carpeta del modelo: ", status.Problem, StringComparison.Ordinal);
            Assert.Empty(_server.Requests);
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing_behind()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Answer(_model.Model.Source, () => FakeModelServer.Ok(new ServedStream(_model.ModelBytes, afterRead: served =>
        {
            if (served > 1_000_000)
            {
                cancel.Cancel();
            }
        })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer().InstallAsync(Consent, null, cancel.Token));

        Assert.Equal(["sentencepiece.bpe.model"], FilesLeft());
    }

    [Fact]
    public async Task A_file_already_in_place_and_verified_is_not_downloaded_again()
    {
        _model.WriteTo(_folder);
        var progress = new ProgressRecorder<EmbeddingModelDownloadProgress>();

        var status = await Installer().InstallAsync(Consent, progress, Ct);

        Assert.True(status.IsReady);
        Assert.Empty(_server.Requests);
        Assert.Equal(
            [
                new EmbeddingModelDownloadProgress("sentencepiece.bpe.model", _model.Tokenizer.Bytes, _model.Tokenizer.Bytes),
                new EmbeddingModelDownloadProgress("model.onnx", _model.Model.Bytes, _model.Model.Bytes),
            ],
            progress.Items);
    }

    [Fact]
    public async Task A_damaged_file_in_place_is_replaced_only_by_a_download_that_verifies()
    {
        _model.WriteTo(_folder);
        var damaged = Altered(_model.ModelBytes, 0);
        await File.WriteAllBytesAsync(PathOf(_model.Model), damaged, Ct);

        _server.Serve(_model.Model, Altered(_model.ModelBytes, 7));
        Assert.Equal(EmbeddingModelState.Mismatch, (await Installer().InstallAsync(Consent, null, Ct)).State);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(PathOf(_model.Model), Ct));

        _server.Serve(_model.Model, _model.ModelBytes);
        Assert.True((await Installer().InstallAsync(Consent, null, Ct)).IsReady);
        Assert.Equal(_model.ModelBytes, await File.ReadAllBytesAsync(PathOf(_model.Model), Ct));
        Assert.Equal([_model.Model.Source, _model.Model.Source], _server.Requests);
    }

    [Fact]
    public async Task Leftovers_of_an_interrupted_download_are_deleted_and_nothing_else()
    {
        _model.WriteTo(_folder);
        var leftover = Path.Combine(_folder, $"model.onnx.{Guid.NewGuid():N}.part");
        var unrelated = Path.Combine(_folder, "notas.txt");
        await File.WriteAllTextAsync(leftover, "a medias", Ct);
        await File.WriteAllTextAsync(unrelated, "mío", Ct);

        Assert.True((await Installer().InstallAsync(Consent, null, Ct)).IsReady);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Two_installs_at_once_take_turns_and_download_each_file_once()
    {
        _server.Serve(_model.Tokenizer, _model.TokenizerBytes);
        _server.Serve(_model.Model, _model.ModelBytes);
        var installer = Installer();

        var statuses = await Task.WhenAll(installer.InstallAsync(Consent, null, Ct), installer.InstallAsync(Consent, null, Ct));

        Assert.All(statuses, status => Assert.True(status.IsReady));
        Assert.Equal([_model.Tokenizer.Source, _model.Model.Source], _server.Requests);
    }

    [Fact]
    public void Only_a_model_served_over_HTTPS_is_accepted()
    {
        var plain = _model.Descriptor with { Model = _model.Model with { Source = new Uri("http://modelos.invalid/pruebas/onnx/model.onnx") } };

        Assert.Throws<ArgumentException>(() => Installer(plain));
    }

    [Fact]
    public async Task The_real_model_is_asked_for_at_its_pinned_address_and_nowhere_else()
    {
        var status = await Installer(EmbeddingModels.MultilingualE5Small).InstallAsync(Consent, null, Ct);

        Assert.Equal(EmbeddingModelState.Missing, status.State);
        Assert.Equal(
            ["https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/onnx/sentencepiece.bpe.model"],
            _server.Requests.Select(uri => uri.AbsoluteUri));
        Assert.Empty(FilesLeft());
    }
}
