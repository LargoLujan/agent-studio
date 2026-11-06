using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Memory.MemorySearchSamples;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The panel «Búsqueda por significado» of «Memoria» (docs/17 D-03 and D-06, F12-6; ADR-0047): the state of the model and
/// of the index in plain Spanish, and the download of the model, which nothing starts without the person's OK. The button
/// only opens a confirmation that says what is downloaded, from where, its size and where it is kept; only «Descargar»
/// there calls the installer, with the consent of the person and the time. The download shows its progress and can be
/// cancelled.
/// </summary>
public sealed class EmbeddingModelViewModelTests
{
    private static readonly EmbeddingModelDescriptor E5 = EmbeddingModels.MultilingualE5Small;

    private static (EmbeddingModelViewModel Model, FakeModelInstaller Installer, Func<int> Reads) Create(SemanticIndexStatus? status, bool withInstaller = true) =>
        Create(status, new List<EmbeddingModelStatus>(), withInstaller);

    /// <param name="installed">Where the installations the model tells of (<c>installed</c>) are kept.</param>
    private static (EmbeddingModelViewModel Model, FakeModelInstaller Installer, Func<int> Reads) Create(SemanticIndexStatus? status, List<EmbeddingModelStatus> installed, bool withInstaller = true)
    {
        var installer = new FakeModelInstaller();
        var reads = 0;
        var model = new EmbeddingModelViewModel(
            true,
            withInstaller ? installer : null,
            E5,
            new FakeClock(At),
            null,
            () =>
            {
                reads++;
                return Task.CompletedTask;
            },
            installed.Add);
        model.Show(true, status, null);
        return (model, installer, () => reads);
    }

    [Fact]
    public async Task A_download_that_ends_with_the_model_ready_tells_once_that_it_was_installed_before_reading_the_state()
    {
        var reads = 0;
        var told = new List<(EmbeddingModelStatus Status, int ReadsBefore)>();
        var model = new EmbeddingModelViewModel(
            true,
            new FakeModelInstaller(),
            E5,
            new FakeClock(At),
            null,
            () =>
            {
                reads++;
                return Task.CompletedTask;
            },
            status => told.Add((status, reads)));
        model.Show(true, Missing(), null);
        model.AskDownloadCommand.Execute(null);

        await model.ConfirmDownloadCommand.ExecuteAsync(null);

        var only = Assert.Single(told);
        Assert.Equal(EmbeddingModelState.Ready, only.Status.State);
        Assert.Equal(0, only.ReadsBefore); // told first, so the refresh of the index starts before the state is read
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_download_that_fails_does_not_verify_is_cancelled_or_is_declined_never_tells_it_was_installed()
    {
        var installed = new List<EmbeddingModelStatus>();
        var (model, installer, _) = Create(Missing(), installed);

        model.AskDownloadCommand.Execute(null);
        model.DeclineDownloadCommand.Execute(null);
        installer.Install = (_, _) => Task.FromException<EmbeddingModelStatus>(new InvalidOperationException("No hay conexión."));
        model.AskDownloadCommand.Execute(null);
        await model.ConfirmDownloadCommand.ExecuteAsync(null);
        foreach (var state in new[] { EmbeddingModelState.Mismatch, EmbeddingModelState.Missing, EmbeddingModelState.Unloadable })
        {
            installer.Install = (_, _) => Task.FromResult(new EmbeddingModelStatus(state, ModelFolder, "Algo no cuadra."));
            model.AskDownloadCommand.Execute(null);
            await model.ConfirmDownloadCommand.ExecuteAsync(null);
        }

        installer.Install = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new EmbeddingModelStatus(EmbeddingModelState.Ready, ModelFolder);
        };
        model.AskDownloadCommand.Execute(null);
        var cancelled = model.ConfirmDownloadCommand.ExecuteAsync(null);
        model.CancelDownloadCommand.Execute(null);
        await cancelled;

        Assert.Equal(5, installer.Consents.Count);
        Assert.Empty(installed);
    }

    [Fact]
    public void The_label_of_the_download_says_the_size_of_every_file_together_from_the_pinned_sizes()
    {
        var (model, _, _) = Create(Missing());

        // 118 346 824 bytes of the network and 5 069 051 of the tokenizer: 123,4 MB, not the 113 of the network alone.
        Assert.Equal(123_415_875, model.TotalBytes);
        Assert.Equal("Descargar el modelo de búsqueda (123 MB)", model.DownloadLabel);
        Assert.Equal("Descargar 123,4 MB", model.ConfirmLabel);
    }

    [Fact]
    public void Without_the_model_the_panel_says_the_search_goes_by_words_and_offers_the_download()
    {
        var (model, _, _) = Create(Missing());

        Assert.StartsWith("Falta el modelo de búsqueda por significado. Sin él, la memoria se busca solo por palabras", model.StatusText, StringComparison.Ordinal);
        Assert.Equal(Tone.Warning, model.StatusTone);
        Assert.True(model.CanDownload);
        Assert.True(model.AskDownloadCommand.CanExecute(null));
        Assert.False(model.IsHybridReady);
    }

    [Fact]
    public void A_model_that_does_not_give_its_fingerprint_is_not_loaded_and_can_be_downloaded_again()
    {
        var (model, _, _) = Create(new SemanticIndexStatus(120, 0, new EmbeddingModelStatus(EmbeddingModelState.Mismatch, ModelFolder, "sentencepiece.bpe.model no da su SHA-256.")));

        Assert.Equal(
            "El modelo descargado no coincide con su huella fijada, así que no se carga y la búsqueda va por palabras. Vuelve a descargarlo. sentencepiece.bpe.model no da su SHA-256.",
            model.StatusText);
        Assert.Equal("Volver a descargar el modelo de búsqueda (123 MB)", model.DownloadLabel);
        Assert.True(model.CanDownload);
    }

    [Fact]
    public void A_model_that_verifies_but_does_not_load_offers_no_download()
    {
        var (model, _, _) = Create(new SemanticIndexStatus(120, 0, new EmbeddingModelStatus(EmbeddingModelState.Unloadable, ModelFolder, "Falta memoria.")));

        Assert.Equal("El modelo está descargado y verifica, pero no se ha podido cargar: la búsqueda va por palabras. Falta memoria.", model.StatusText);
        Assert.False(model.CanDownload);
    }

    [Theory]
    [InlineData(120, 120, "Activa: el modelo está descargado y comprobado, y los 120 fragmentos del índice tienen su significado calculado.")]
    [InlineData(1500, 300, "Activa a medias: 300 de 1.500 fragmentos tienen su significado calculado. El resto se calcula en segundo plano y, mientras tanto, solo se encuentra por palabras.")]
    [InlineData(0, 0, "Activa: el modelo está descargado y comprobado. El índice de este proyecto aún no tiene fragmentos: se construye en segundo plano.")]
    public void With_the_model_the_panel_says_how_much_of_the_index_has_its_meaning(int fragments, int withVectors, string text)
    {
        var (model, _, _) = Create(Ready(fragments, withVectors));

        Assert.Equal(text, model.StatusText);
        Assert.False(model.CanDownload);
        Assert.Equal(fragments > 0 && withVectors == fragments ? Tone.Success : Tone.Neutral, model.StatusTone);
    }

    [Fact]
    public void Without_an_installer_no_download_is_offered()
    {
        var (model, _, _) = Create(Missing(), withInstaller: false);

        Assert.False(model.CanDownload);
        Assert.False(model.AskDownloadCommand.CanExecute(null));
    }

    [Fact]
    public void The_button_only_opens_the_confirmation_with_what_from_where_its_size_and_where_it_is_kept()
    {
        var (model, installer, _) = Create(Missing());

        model.AskDownloadCommand.Execute(null);

        Assert.True(model.IsConfirming);
        Assert.Empty(installer.Consents);
        Assert.False(model.CanDownload);
        Assert.Equal(
            [
                ("model_qint8_avx512_vnni.onnx", "118,3 MB", E5.Model.Source.ToString()),
                ("sentencepiece.bpe.model", "5,1 MB", E5.Tokenizer.Source.ToString()),
            ],
            model.Files.Select(f => (f.Name, f.Size, f.Source)));
        Assert.All(model.Files, f => Assert.StartsWith("https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/onnx/", f.Source, StringComparison.Ordinal));
        Assert.Contains("intfloat/multilingual-e5-small (licencia MIT)", model.WhatText, StringComparison.Ordinal);
        Assert.Equal("En total, 123,4 MB, una sola vez.", model.TotalText);
        Assert.Equal("De dónde: huggingface.co, solo de esas direcciones.", model.FromText);
        Assert.StartsWith($"Dónde se guarda: {ModelFolder}.", model.WhereText, StringComparison.Ordinal);
        Assert.Contains("huella SHA-256", model.CheckText, StringComparison.Ordinal);
        Assert.Contains("no gasta cuota", model.CheckText, StringComparison.Ordinal);
    }

    [Fact]
    public void Declining_downloads_nothing_and_says_so()
    {
        var (model, installer, reads) = Create(Missing());
        model.AskDownloadCommand.Execute(null);

        model.DeclineDownloadCommand.Execute(null);

        Assert.False(model.IsConfirming);
        Assert.Empty(installer.Consents);
        Assert.Equal("No se ha descargado nada.", model.Outcome);
        Assert.False(model.ConfirmDownloadCommand.CanExecute(null));
        Assert.Equal(0, reads());
    }

    [Fact]
    public async Task Without_the_confirmation_the_installer_is_never_called()
    {
        var (model, installer, _) = Create(Missing());

        await model.ConfirmDownloadCommand.ExecuteAsync(null);

        Assert.Empty(installer.Consents);
        Assert.False(model.IsDownloading);
    }

    [Fact]
    public async Task Confirming_calls_the_installer_once_with_the_persons_consent_and_reads_the_state_again()
    {
        var (model, installer, reads) = Create(Missing());
        model.AskDownloadCommand.Execute(null);

        await model.ConfirmDownloadCommand.ExecuteAsync(null);

        var consent = Assert.Single(installer.Consents);
        Assert.Equal(new EmbeddingModelDownloadConsent("user", At), consent);
        Assert.False(model.IsDownloading);
        Assert.False(model.IsConfirming);
        Assert.Equal(Tone.Success, model.OutcomeTone);
        Assert.StartsWith("Modelo descargado y comprobado.", model.Outcome, StringComparison.Ordinal);
        Assert.Equal(1, reads());
    }

    [Fact]
    public async Task The_progress_says_which_file_arrives_and_how_much_of_the_whole_download()
    {
        var (model, installer, _) = Create(Missing());
        var seen = new List<(double Percent, string? Text)>();
        installer.Install = (progress, _) =>
        {
            progress!.Report(new EmbeddingModelDownloadProgress("model_qint8_avx512_vnni.onnx", 59_173_412, 118_346_824));
            seen.Add((model.ProgressPercent, model.ProgressText));
            progress.Report(new EmbeddingModelDownloadProgress("model_qint8_avx512_vnni.onnx", 118_346_824, 118_346_824));
            progress.Report(new EmbeddingModelDownloadProgress("sentencepiece.bpe.model", 5_069_051, 5_069_051));
            seen.Add((model.ProgressPercent, model.ProgressText));
            return Task.FromResult(new EmbeddingModelStatus(EmbeddingModelState.Ready, ModelFolder));
        };
        model.AskDownloadCommand.Execute(null);

        await model.ConfirmDownloadCommand.ExecuteAsync(null);

        Assert.Equal(59_173_412 * 100d / 123_415_875, seen[0].Percent, 6);
        Assert.Equal("Descargando model_qint8_avx512_vnni.onnx: 59,2 de 118,3 MB · 48 % del total", seen[0].Text);
        Assert.Equal(100, seen[1].Percent, 6);
        Assert.Equal("Descargando sentencepiece.bpe.model: 5,1 de 5,1 MB · 100 % del total", seen[1].Text);
        Assert.Null(model.ProgressText);
    }

    [Fact]
    public async Task A_download_can_be_cancelled_and_leaves_nothing_half_done()
    {
        var (model, installer, reads) = Create(Missing());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        installer.Install = async (progress, ct) =>
        {
            progress!.Report(new EmbeddingModelDownloadProgress("model_qint8_avx512_vnni.onnx", 1_000_000, 118_346_824));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new EmbeddingModelStatus(EmbeddingModelState.Ready, ModelFolder);
        };
        model.AskDownloadCommand.Execute(null);

        var download = model.ConfirmDownloadCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(model.IsDownloading);
        Assert.True(model.CancelDownloadCommand.CanExecute(null));
        model.CancelDownloadCommand.Execute(null);
        await download;

        Assert.False(model.IsDownloading);
        Assert.Equal("Descarga cancelada: no se ha guardado nada a medias.", model.Outcome);
        Assert.Equal(Tone.Neutral, model.OutcomeTone);
        Assert.Equal(1, reads());
    }

    [Fact]
    public async Task A_download_that_fails_says_why_and_is_not_thrown()
    {
        var (model, installer, _) = Create(Missing());
        installer.Install = (_, _) => Task.FromException<EmbeddingModelStatus>(new InvalidOperationException("No hay conexión."));
        model.AskDownloadCommand.Execute(null);

        await model.ConfirmDownloadCommand.ExecuteAsync(null);

        Assert.Equal("No se pudo descargar el modelo (No hay conexión). No se ha guardado nada a medias.", model.Outcome);
        Assert.Equal(Tone.Error, model.OutcomeTone);
        Assert.True(model.CanDownload);
    }

    [Fact]
    public async Task A_download_that_does_not_verify_says_it_was_deleted_and_not_loaded()
    {
        var (model, installer, _) = Create(Missing());
        installer.Install = (_, _) => Task.FromResult(new EmbeddingModelStatus(EmbeddingModelState.Mismatch, ModelFolder, "model_qint8_avx512_vnni.onnx no da su SHA-256."));
        model.AskDownloadCommand.Execute(null);

        await model.ConfirmDownloadCommand.ExecuteAsync(null);

        Assert.Equal("Lo descargado no coincide con su huella fijada: se ha borrado y no se carga. model_qint8_avx512_vnni.onnx no da su SHA-256.", model.Outcome);
        Assert.Equal(Tone.Error, model.OutcomeTone);
    }

    /// <summary>
    /// V42: the note of a download that ended well says that until the index has the meaning of its fragments the search goes
    /// by words. It stays while that is true, and goes once the index has the meaning of some fragment (the state above then
    /// says «Activa…»).
    /// </summary>
    [Fact]
    public async Task The_note_of_the_download_goes_once_the_index_has_the_meaning_of_its_fragments()
    {
        var (model, _, _) = Create(Missing());
        model.AskDownloadCommand.Execute(null);
        await model.ConfirmDownloadCommand.ExecuteAsync(null);
        Assert.Equal(EmbeddingModelViewModel.DownloadedText, model.Outcome);

        model.Show(true, Ready(fragments: 58, withVectors: 0), null);
        Assert.Equal(EmbeddingModelViewModel.DownloadedText, model.Outcome);
        Assert.False(model.IsHybridReady);

        model.Show(true, Ready(fragments: 58, withVectors: 58), null);

        Assert.Null(model.Outcome);
        Assert.StartsWith("Activa: el modelo está descargado y comprobado, y los 58 fragmentos", model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_or_declined_download_keeps_its_note_whatever_the_index_says()
    {
        var (model, installer, _) = Create(Missing());
        installer.Install = (_, _) => Task.FromException<EmbeddingModelStatus>(new InvalidOperationException("No hay conexión."));
        model.AskDownloadCommand.Execute(null);
        await model.ConfirmDownloadCommand.ExecuteAsync(null);

        model.Show(true, Ready(), null);

        Assert.Equal("No se pudo descargar el modelo (No hay conexión). No se ha guardado nada a medias.", model.Outcome);

        var (declined, _, _) = Create(Missing());
        declined.AskDownloadCommand.Execute(null);
        declined.DeclineDownloadCommand.Execute(null);
        declined.Show(true, Ready(), null);

        Assert.Equal("No se ha descargado nada.", declined.Outcome);
    }

    [Fact]
    public void The_confirmation_closes_when_the_model_is_there_after_all()
    {
        var (model, installer, _) = Create(Missing());
        model.AskDownloadCommand.Execute(null);

        model.Show(true, Ready(), null);

        Assert.False(model.IsConfirming);
        Assert.False(model.CanDownload);
        Assert.Empty(installer.Consents);
    }
}
