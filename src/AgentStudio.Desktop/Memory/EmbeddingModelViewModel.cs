using System.Globalization;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Memory;

/// <summary>One file of the model as the confirmation of the download shows it: its name, its size and the address it comes from.</summary>
public sealed record ModelFileLine(string Name, string Size, string Source)
{
    /// <summary>How a list and UI Automation name it.</summary>
    public override string ToString() => $"{Name} · {Size}";
}

/// <summary>
/// The search by meaning in «Memoria» (docs/17 D-03 and D-06, F12-6; ADR-0047): the state of the local model and of the
/// index of the project, in plain Spanish, and the download of the model. Nothing reaches the network without the person's
/// explicit OK: «Descargar el modelo de búsqueda» only opens a confirmation that says what is downloaded (each file, its
/// size and its address), where it is kept and how it is checked; only «Descargar» there calls the installer
/// (<see cref="IEmbeddingModelInstaller"/>), with the consent of the person and the time. The download shows its progress
/// and can be cancelled; the installer leaves nothing half done. Without the model, the search goes by words, and says so.
/// </summary>
public sealed partial class EmbeddingModelViewModel : ObservableObject
{
    /// <summary>
    /// What a download that ended with the model in place and verified says, until the index has the meaning of some
    /// fragment: then the state above says so, and this note goes (V42).
    /// </summary>
    public const string DownloadedText =
        "Modelo descargado y comprobado. El índice calculará el significado de los fragmentos en segundo plano; hasta entonces, la búsqueda va por palabras.";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    private readonly IEmbeddingModelInstaller? _installer;
    private readonly EmbeddingModelDescriptor _model;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly Func<Task> _readStatus;
    private readonly Action<EmbeddingModelStatus>? _installed;
    private readonly Dictionary<string, long> _received = new(StringComparer.Ordinal);
    private CancellationTokenSource? _download;

    /// <param name="indexAvailable">Whether the index was composed; without it nothing here is offered.</param>
    /// <param name="installer">Downloads the model; null when it was not composed (no download is offered).</param>
    /// <param name="model">The pinned model the installer downloads.</param>
    /// <param name="time">The time of the consent.</param>
    /// <param name="uiContext">Where the progress is shown; null takes the context of the thread that starts the download.</param>
    /// <param name="readStatus">Reads the state of the index again (the document does it, for its project) once a download ends.</param>
    /// <param name="installed">
    /// Told once per download that ends with the model in place and verified (<see cref="EmbeddingModelState.Ready"/>), with
    /// the state the installer left, before the state is read again; never after one that fails, does not verify or is
    /// cancelled. The document raises <c>ModelInstalled</c> from it, where the composition starts the refresh of the index.
    /// </param>
    public EmbeddingModelViewModel(
        bool indexAvailable,
        IEmbeddingModelInstaller? installer,
        EmbeddingModelDescriptor model,
        TimeProvider time,
        SynchronizationContext? uiContext,
        Func<Task> readStatus,
        Action<EmbeddingModelStatus>? installed = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(readStatus);
        IsIndexAvailable = indexAvailable;
        _installer = installer;
        _model = model;
        _time = time;
        _ui = uiContext;
        _readStatus = readStatus;
        _installed = installed;
        Files = [.. new[] { model.Model, model.Tokenizer }.Select(f => new ModelFileLine(f.FileName, Megabytes(f.Bytes), f.Source.ToString()))];
    }

    /// <summary>Whether the index of the projects was composed.</summary>
    public bool IsIndexAvailable { get; }

    /// <summary>The state of the index of the project and of the model, as the index said it last; null until it says.</summary>
    public SemanticIndexStatus? Status { get; private set; }

    /// <summary>Whether a project is open.</summary>
    public bool HasProject { get; private set; }

    /// <summary>Why the state of the index could not be read; null when it could.</summary>
    public string? StatusProblem { get; private set; }

    /// <summary>The bytes of every file of the model together.</summary>
    public long TotalBytes => _model.Model.Bytes + _model.Tokenizer.Bytes;

    /// <summary>Whether the search may go by meaning: the model is ready and some fragment has its vector.</summary>
    public bool IsHybridReady => Status is { Model.IsReady: true, WithVectors: > 0 };

    /// <summary>The state of the search by meaning, in plain Spanish.</summary>
    public string StatusText => !IsIndexAvailable ? "La búsqueda por significado no está disponible en esta composición: la memoria se busca por palabras."
        : !HasProject ? "Abre una carpeta de proyecto para ver el estado de la búsqueda por significado."
        : StatusProblem is { } problem ? $"No se pudo leer el estado del índice de búsqueda: {problem}"
        : Status is not { } status ? "Leyendo el estado del índice de búsqueda…"
        : status.Model.State switch
        {
            EmbeddingModelState.Missing => "Falta el modelo de búsqueda por significado. Sin él, la memoria se busca solo por palabras: «prueba» no encuentra «pruebas», ni «test en rojo» encuentra «pruebas que fallan».",
            EmbeddingModelState.Mismatch => "El modelo descargado no coincide con su huella fijada, así que no se carga y la búsqueda va por palabras. Vuelve a descargarlo." + Detail(status.Model.Problem),
            EmbeddingModelState.Unloadable => "El modelo está descargado y verifica, pero no se ha podido cargar: la búsqueda va por palabras." + Detail(status.Model.Problem),
            _ => Ready(status),
        };

    /// <summary>The tone of the dot of the state: what needs the person is a warning; what works, a success.</summary>
    public Tone StatusTone => !IsIndexAvailable || !HasProject ? Tone.Neutral
        : StatusProblem is not null ? Tone.Error
        : Status is not { } status ? Tone.Neutral
        : status.Model.State != EmbeddingModelState.Ready ? Tone.Warning
        : status.Fragments > 0 && status.WithVectors >= status.Fragments ? Tone.Success
        : Tone.Neutral;

    /// <summary>The download is offered: there is an installer, and the model is missing or does not give its fingerprint.</summary>
    public bool CanDownload =>
        _installer is not null && HasProject && !IsConfirming && !IsDownloading
        && Status?.Model.State is EmbeddingModelState.Missing or EmbeddingModelState.Mismatch;

    /// <summary>«Descargar el modelo de búsqueda (123 MB)», with the size of every file together, from the pinned sizes.</summary>
    public string DownloadLabel =>
        (Status?.Model.State == EmbeddingModelState.Mismatch ? "Volver a descargar el modelo de búsqueda (" : "Descargar el modelo de búsqueda (")
        + RoundMegabytes(TotalBytes) + ")";

    // ---- the confirmation ----

    /// <summary>The confirmation is open: nothing has been downloaded, and nothing will be until «Descargar».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(AskDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeclineDownloadCommand))]
    private bool _isConfirming;

    public string ConfirmationTitle => "¿Descargar el modelo de búsqueda por significado?";

    /// <summary>What is downloaded: the model, its license and its pinned revision.</summary>
    public string WhatText =>
        $"Qué: el modelo {_model.Name} (licencia {_model.License}) y su tokenizador, en la revisión {_model.Revision[..Math.Min(12, _model.Revision.Length)]} que fija Agent Studio. Son estos ficheros:";

    /// <summary>Each file, with its size and its address.</summary>
    public IReadOnlyList<ModelFileLine> Files { get; }

    public string TotalText => $"En total, {Megabytes(TotalBytes)}, una sola vez.";

    /// <summary>Where it comes from: the host of the addresses, and only those.</summary>
    public string FromText
    {
        get
        {
            var hosts = new[] { _model.Model.Source, _model.Tokenizer.Source }.Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase);
            return $"De dónde: {string.Join(" y ", hosts)}, solo de esas direcciones.";
        }
    }

    /// <summary>Where it is kept: the folder of the model, outside every project.</summary>
    public string WhereText => Status?.Model.Directory is { Length: > 0 } folder
        ? $"Dónde se guarda: {folder}. Está fuera de tus proyectos: git no lo ve y se puede borrar."
        : "Dónde se guarda: en la carpeta de Agent Studio de tu equipo, fuera de tus proyectos.";

    public string CheckText =>
        "Antes de usarlo, Agent Studio comprueba el tamaño y la huella SHA-256 de cada fichero: si no coinciden, lo borra y no lo carga. "
        + "Es lo único de la búsqueda que usa la red; después funciona sin conexión y no gasta cuota.";

    public string ConfirmLabel => $"Descargar {Megabytes(TotalBytes)}";

    public string DeclineLabel => "No descargar";

    // ---- the download ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(AskDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    private bool _isDownloading;

    /// <summary>How much of every file together has arrived, from 0 to 100.</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>The file that is arriving and how much of it, while it downloads.</summary>
    [ObservableProperty]
    private string? _progressText;

    /// <summary>How the last download ended, or that nothing was downloaded; null before any.</summary>
    [ObservableProperty]
    private string? _outcome;

    [ObservableProperty]
    private Tone _outcomeTone;

    /// <summary>
    /// The project (null: none) and the state of its index as the document read it now, or why it could not be read. The
    /// document calls it when the project changes, after a download and after a refresh of the index. Once the index has
    /// the meaning of some fragment, the note of the download (<see cref="DownloadedText"/>: «hasta entonces, la búsqueda va
    /// por palabras») is no longer true, and it goes: the state says how far the index got (V42).
    /// </summary>
    public void Show(bool hasProject, SemanticIndexStatus? status, string? problem)
    {
        HasProject = hasProject;
        Status = hasProject ? status : null;
        StatusProblem = hasProject ? problem : null;
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusProblem));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(IsHybridReady));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadLabel));
        OnPropertyChanged(nameof(WhereText));
        AskDownloadCommand.NotifyCanExecuteChanged();
        if (IsConfirming && Status?.Model.State is not (EmbeddingModelState.Missing or EmbeddingModelState.Mismatch))
        {
            IsConfirming = false; // the model is there now, or there is no project or no state: nothing to confirm.
        }

        if (IsHybridReady && string.Equals(Outcome, DownloadedText, StringComparison.Ordinal))
        {
            Outcome = null; // the search goes by meaning now; any other outcome (a failure, a cancellation) stays.
        }
    }

    /// <summary>«Descargar el modelo de búsqueda»: opens the confirmation. Nothing is downloaded yet.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private void AskDownload()
    {
        if (!CanDownload)
        {
            return;
        }

        Outcome = null;
        IsConfirming = true;
    }

    /// <summary>«No descargar»: the confirmation closes and nothing is downloaded.</summary>
    [RelayCommand(CanExecute = nameof(IsConfirming))]
    private void DeclineDownload()
    {
        IsConfirming = false;
        Outcome = "No se ha descargado nada.";
        OutcomeTone = Tone.Neutral;
    }

    private bool CanConfirm() => IsConfirming && _installer is not null;

    /// <summary>«Descargar» in the confirmation: the person's OK, with the time; only now the installer reaches the network.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmDownloadAsync()
    {
        if (!CanConfirm() || _installer is not { } installer)
        {
            return;
        }

        var consent = new EmbeddingModelDownloadConsent(ApprovalPromptService.DecidedBy, _time.GetUtcNow());
        IsConfirming = false;
        _received.Clear();
        ProgressPercent = 0;
        ProgressText = "Empezando la descarga…";
        Outcome = null;
        IsDownloading = true;
        using var download = new CancellationTokenSource();
        _download = download;
        var progress = new UiProgress<EmbeddingModelDownloadProgress>(_ui ?? SynchronizationContext.Current, Report);
        EmbeddingModelStatus? installed = null;
        try
        {
            var status = await installer.InstallAsync(consent, progress, download.Token).ConfigureAwait(true);
            (Outcome, OutcomeTone) = Ended(status);
            installed = status.State == EmbeddingModelState.Ready ? status : null;
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested)
        {
            (Outcome, OutcomeTone) = ("Descarga cancelada: no se ha guardado nada a medias.", Tone.Neutral);
        }
#pragma warning disable CA1031 // The installer is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            (Outcome, OutcomeTone) = ($"No se pudo descargar el modelo ({ex.Message.TrimEnd('.')}). No se ha guardado nada a medias.", Tone.Error);
        }
        finally
        {
            _download = null;
            IsDownloading = false;
            ProgressText = null;
        }

        // Only a model in place and verified starts the refresh of the index (in the composition); then the state is read.
        if (installed is not null)
        {
            _installed?.Invoke(installed);
        }

        await _readStatus().ConfigureAwait(true);
    }

    /// <summary>«Cancelar la descarga»: the installer stops and removes what it had received.</summary>
    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void CancelDownload()
    {
        if (_download is { IsCancellationRequested: false } download)
        {
            ProgressText = "Cancelando la descarga…";
            download.Cancel();
        }
    }

    /// <summary>A report of the installer, on the UI thread: the file that arrives and the share of the whole download.</summary>
    private void Report(EmbeddingModelDownloadProgress value)
    {
        if (!IsDownloading || value is null)
        {
            return;
        }

        var size = value.Total > 0 ? value.Total : long.MaxValue;
        _received[value.File] = Math.Clamp(value.Received, 0, size);
        var received = Math.Min(_received.Values.Sum(), TotalBytes);
        ProgressPercent = TotalBytes == 0 ? 0 : received * 100d / TotalBytes;
        var share = ProgressPercent.ToString("0", Spanish);
        ProgressText = value.Total > 0
            ? $"Descargando {value.File}: {Number(_received[value.File])} de {Megabytes(value.Total)} · {share} % del total"
            : $"Descargando {value.File}: {Megabytes(_received[value.File])} · {share} % del total";
    }

    /// <summary>Bytes as decimal megabytes with one decimal and no unit: «59,2».</summary>
    private static string Number(long bytes) => (bytes / 1_000_000d).ToString("0.0", Spanish);

    /// <summary>What a download that ended says, by the state the installer left.</summary>
    private static (string Text, Tone Tone) Ended(EmbeddingModelStatus status) => status.State switch
    {
        EmbeddingModelState.Ready => (DownloadedText, Tone.Success),
        EmbeddingModelState.Mismatch => ("Lo descargado no coincide con su huella fijada: se ha borrado y no se carga." + Detail(status.Problem), Tone.Error),
        EmbeddingModelState.Unloadable => ("El modelo se descargó y verifica, pero no se ha podido cargar." + Detail(status.Problem), Tone.Error),
        _ => ("La descarga no dejó el modelo en su sitio: no se ha guardado nada." + Detail(status.Problem), Tone.Error),
    };

    private static string Ready(SemanticIndexStatus status) =>
        status.Fragments == 0 ? "Activa: el modelo está descargado y comprobado. El índice de este proyecto aún no tiene fragmentos: se construye en segundo plano."
        : status.WithVectors >= status.Fragments ? $"Activa: el modelo está descargado y comprobado, y los {Count(status.Fragments)} fragmentos del índice tienen su significado calculado."
        : $"Activa a medias: {Count(status.WithVectors)} de {Count(status.Fragments)} fragmentos tienen su significado calculado. El resto se calcula en segundo plano y, mientras tanto, solo se encuentra por palabras.";

    private static string Detail(string? problem) => string.IsNullOrWhiteSpace(problem) ? string.Empty : " " + problem.Trim();

    private static string Count(int value) => value.ToString("N0", Spanish);

    /// <summary>Bytes as decimal megabytes with one decimal, the Spanish way: «118,3 MB».</summary>
    internal static string Megabytes(long bytes) => Number(bytes) + " MB";

    /// <summary>Bytes as whole decimal megabytes: «123 MB».</summary>
    internal static string RoundMegabytes(long bytes) => Math.Round(bytes / 1_000_000d, MidpointRounding.AwayFromZero).ToString("0", Spanish) + " MB";
}

/// <summary>
/// Progress that reaches the UI thread whatever thread reports it: posted to <paramref name="context"/>, or applied at once
/// when there is none or it is already the current one (the tests, and a report made on the UI thread).
/// </summary>
internal sealed class UiProgress<T>(SynchronizationContext? context, Action<T> apply) : IProgress<T>
{
    public void Report(T value)
    {
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            apply(value);
            return;
        }

        context.Post(static state =>
        {
            var (action, item) = ((Action<T>, T))state!;
            action(item);
        }, (apply, value));
    }
}
