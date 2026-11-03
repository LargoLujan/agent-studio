using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Memory;

/// <summary>Spanish names of the categories, origins and trust of the memory (docs/14 D-18 and D-20).</summary>
public static class MemoryTexts
{
    public static string Kind(MemoryKind kind) => kind switch
    {
        MemoryKind.Architecture => "Arquitectura",
        MemoryKind.Convention => "Convención",
        MemoryKind.Decision => "Decisión",
        MemoryKind.KnownError => "Error conocido",
        MemoryKind.Solution => "Solución",
        MemoryKind.PendingTask => "Tarea pendiente",
        MemoryKind.Change => "Cambio",
        MemoryKind.DebateSummary => "Resumen de debate",
        MemoryKind.Verdict => "Veredicto",
        MemoryKind.DailySummary => "Resumen diario",
        MemoryKind.JournalEntry => "Bitácora",
        _ => kind.ToString(),
    };

    public static string Origin(MemoryOrigin origin) => origin switch
    {
        MemoryOrigin.System => "Sistema",
        MemoryOrigin.User => "Tú",
        MemoryOrigin.Agent => "Agente",
        _ => origin.ToString(),
    };

    public static string Trust(MemoryTrust trust) => trust switch
    {
        MemoryTrust.Trusted => "De confianza",
        MemoryTrust.Proposed => "Propuesta de un agente: sin revisar",
        MemoryTrust.Quarantined => "En cuarentena: heredada de un historial aceptado como nuevo origen; revísala",
        MemoryTrust.Rejected => "Rechazada",
        MemoryTrust.Retracted => "Retirada",
        _ => trust.ToString(),
    };

    /// <summary>The tone of the dot of the trust: what waits for a person is a warning; what is out of use, neutral.</summary>
    public static Tone Tone(MemoryTrust trust) => trust switch
    {
        MemoryTrust.Trusted => Orchestrator.Tone.Success,
        MemoryTrust.Proposed or MemoryTrust.Quarantined => Orchestrator.Tone.Warning,
        _ => Orchestrator.Tone.Neutral,
    };
}

/// <summary>One entry of the memory as the screen shows it, and whether a decision about it waits to be recorded.</summary>
public sealed partial class MemoryEntryViewModel : ObservableObject
{
    public MemoryEntryViewModel(MemoryEntry entry, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(zone);
        Entry = entry;
        Created = TimeZoneInfo.ConvertTime(entry.CreatedAt, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    public MemoryEntry Entry { get; }

    public string Id => Entry.Id;

    public string KindLabel => MemoryTexts.Kind(Entry.Kind);

    public string OriginLabel => MemoryTexts.Origin(Entry.Origin);

    public string TrustLabel => MemoryTexts.Trust(Entry.Trust);

    public Tone TrustTone => MemoryTexts.Tone(Entry.Trust);

    public string Topic => Entry.Topic;

    public string Content => Entry.Content;

    /// <summary>When it was recorded, in local time.</summary>
    public string Created { get; }

    /// <summary>What it cites: a run and an entry of the log, or an evidence and its SHA-256.</summary>
    public string Source => Entry.Source;

    /// <summary>Which entry replaces it, or which one it replaces; null when neither.</summary>
    public string? Superseded => Entry.SupersededBy is { } by ? $"Sustituida por {by}"
        : Entry.Supersedes is { } old ? $"Sustituye a {old}"
        : null;

    /// <summary>A decision about the entry that is being recorded or waits in the queue; null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeAccepted))]
    [NotifyPropertyChangedFor(nameof(CanBeRetracted))]
    private string? _pending;

    /// <summary>A proposal of an agent, or an entry in quarantine, waits for a person: accept or reject.</summary>
    public bool CanBeAccepted => Pending is null && Entry.Trust is MemoryTrust.Proposed or MemoryTrust.Quarantined;

    /// <summary>A trusted entry can be withdrawn (it is hidden; purging is Phase 7).</summary>
    public bool CanBeRetracted => Pending is null && Entry.Trust == MemoryTrust.Trusted;

    /// <summary>What a screen reader says of an entry of the memory (V34): its category, its topic and its trust.</summary>
    public override string ToString() => $"{KindLabel} · {Topic} · {TrustLabel}";
}

/// <summary>
/// The «Memoria» document (docs/14 F4-5, D-18 to D-22): every entry of the project's memory with its category, origin
/// and trust, as <see cref="IProjectMemory.RecallAsync"/> gives the anchored rows (every trust, so a person can review
/// them). A person accepts or rejects a proposal of an agent or an entry in quarantine, and withdraws a trusted entry:
/// each decision goes to the log through <see cref="IProjectMemory.ReviewAsync"/>, which waits while a run or a planning
/// goes on (the entry says it is queued). «Exportar a Markdown» writes the trusted and accepted memory to
/// <c>.agentstudio/memory/</c> (<see cref="IMemoryExporter"/>), never while a run or a planning goes on. It also holds
/// the daily summary of the journal (<see cref="Summary"/>).
/// </summary>
public sealed partial class MemoryViewModel : DocumentViewModel
{
    public const string DocumentId = "memory:project";

    /// <summary>Recall asks for at most this many entries.</summary>
    public const int MaxEntries = 500;

    private readonly IProjectWorkspace? _workspace;
    private readonly IProjectMemory? _memory;
    private readonly IMemoryExporter? _exporter;
    private readonly IRunActivity? _activity;
    private readonly TimeProvider _time;
    private ProjectContext? _project;

    /// <param name="workspace">Opens the project to know its id.</param>
    /// <param name="memory">The project memory; null when the shell was composed without it (the screen says so).</param>
    /// <param name="exporter">The Markdown export; null when it was not composed.</param>
    /// <param name="activity">Whether a run or a planning goes on; without it the exporter alone decides.</param>
    /// <param name="summary">The daily summary of the journal.</param>
    /// <param name="time">The local time of the dates shown.</param>
    public MemoryViewModel(IProjectWorkspace? workspace, IProjectMemory? memory, IMemoryExporter? exporter, IRunActivity? activity, DailySummaryViewModel summary, TimeProvider time)
        : base(DocumentId, "Memoria")
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(time);
        _workspace = workspace;
        _memory = memory;
        _exporter = exporter;
        _activity = activity;
        _time = time;
        Summary = summary;
        _selectedKind = Kinds[0];
    }

    public DailySummaryViewModel Summary { get; }

    /// <summary>The categories to filter by; the first one is «Todas».</summary>
    public IReadOnlyList<Option<MemoryKind?>> Kinds { get; } =
        [new(null, "Todas"), .. Enum.GetValues<MemoryKind>().Select(k => new Option<MemoryKind?>(k, MemoryTexts.Kind(k)))];

    public ObservableCollection<MemoryEntryViewModel> Entries { get; } = [];

    /// <summary>Words to search in the topic and the content (full-text, each word as a phrase).</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private Option<MemoryKind?> _selectedKind;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _exportStatus;

    /// <summary>
    /// An export that did not finish: refused before the first file, or stopped before the next one because a run started
    /// meanwhile (the files already rewritten are named; the rest stays as it was until the next export). Null otherwise.
    /// </summary>
    [ObservableProperty]
    private string? _exportWarning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isBusy;

    /// <summary>Why «Exportar a Markdown» is not offered now (a run or a planning goes on); null when it is.</summary>
    public string? ExportBlocked => RunIsActive ? "Hay una ejecución, una planificación, un debate o una auditoría en curso: la exportación espera a que termine." : null;

    private bool RunIsActive => _project is { } project && _activity?.IsBusy(project.ProjectId) == true;

    /// <summary>The project open in the shell changed (null: none): it is opened and its memory recalled.</summary>
    public async Task SetProjectAsync(string? projectRoot)
    {
        _project = null;
        ErrorMessage = null;
        ExportStatus = null;
        Entries.Clear();
        if (projectRoot is null)
        {
            Summary.SetProject(null);
            StatusMessage = "Abre una carpeta para ver su memoria.";
            RefreshRunActivity();
            return;
        }

        if (_memory is null || _workspace is null)
        {
            Summary.SetProject(null);
            StatusMessage = "La memoria del proyecto no está disponible en esta composición.";
            RefreshRunActivity();
            return;
        }

        try
        {
            _project = await _workspace.OpenAsync(projectRoot, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The workspace is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Summary.SetProject(null);
            StatusMessage = null;
            ErrorMessage = $"No se pudo abrir el proyecto: {ex.Message}";
            RefreshRunActivity();
            return;
        }

        Summary.SetProject(_project);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Says again whether a run or a planning goes on (the export waits for it). The screen asks on every refresh; the
    /// composition may call it when a run starts or ends.
    /// </summary>
    public void RefreshRunActivity()
    {
        OnPropertyChanged(nameof(ExportBlocked));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>«Buscar»: recalls the entries of every trust that match the words and the category.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        RefreshRunActivity();
        if (_project is not { } project || _memory is not { } memory)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var text = SearchText.Trim();
            var query = new MemoryQuery(
                Text: text.Length == 0 ? null : text,
                Kinds: SelectedKind.Value is { } kind ? [kind] : null,
                MaxEntries: MaxEntries,
                TrustedOnly: false);
            var entries = await memory.RecallAsync(project, query, CancellationToken.None).ConfigureAwait(true);
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(new MemoryEntryViewModel(entry, _time.LocalTimeZone));
            }

            StatusMessage = entries.Count == 0 ? "No hay entradas que coincidan." : null;
        }
#pragma warning disable CA1031 // The memory is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo leer la memoria: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshEntryCommands();
        }
    }

    private bool IsIdle() => !IsBusy;

    /// <summary>«Aceptar»: a proposal of an agent, or an entry in quarantine, becomes trusted (it may reach a prompt and the export).</summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    private Task AcceptAsync(MemoryEntryViewModel? entry) => ReviewAsync(entry, MemoryReview.Accepted);

    /// <summary>«Rechazar»: the entry never reaches a prompt nor the export.</summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    private Task RejectAsync(MemoryEntryViewModel? entry) => ReviewAsync(entry, MemoryReview.Rejected);

    /// <summary>«Retirar»: a trusted entry is hidden from recall and the export.</summary>
    [RelayCommand(CanExecute = nameof(CanRetract))]
    private Task RetractAsync(MemoryEntryViewModel? entry) => ReviewAsync(entry, MemoryReview.Retracted);

    private bool CanAccept(MemoryEntryViewModel? entry) => CanReview && entry is { CanBeAccepted: true };

    private bool CanRetract(MemoryEntryViewModel? entry) => CanReview && entry is { CanBeRetracted: true };

    private bool CanReview => _project is not null && _memory is not null;

    private async Task ReviewAsync(MemoryEntryViewModel? entry, MemoryReview decision)
    {
        if (entry is null || _project is not { } project || _memory is not { } memory)
        {
            return;
        }

        ErrorMessage = null;
        entry.Pending = RunIsActive
            ? "En cola: se registrará cuando termine la ejecución o la planificación en curso."
            : "Registrando tu decisión…";
        RefreshEntryCommands();
        try
        {
            await memory.ReviewAsync(project, entry.Id, decision, ApprovalPromptService.DecidedBy, null, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The memory is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            entry.Pending = null;
            ErrorMessage = $"No se pudo registrar la revisión: {ex.Message}";
            RefreshEntryCommands();
            return;
        }

        // The trust comes from the log: read it again.
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>«Exportar a Markdown»: the trusted and accepted memory to <c>.agentstudio/memory/</c>; never during a run or a planning.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (_project is not { } project || _exporter is not { } exporter || RunIsActive)
        {
            RefreshRunActivity();
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        ExportStatus = null;
        ExportWarning = null;
        try
        {
            var result = await exporter.ExportAsync(project, CancellationToken.None).ConfigureAwait(true);
            if (result.Exported)
            {
                ExportStatus = string.Create(CultureInfo.InvariantCulture, $"Exportadas {result.Entries} entradas a .agentstudio/memory/ ({result.Files.Count} {(result.Files.Count == 1 ? "fichero" : "ficheros")}). El Markdown nunca se lee de vuelta.");
            }
            else if (result.Files.Count == 0)
            {
                ExportWarning = $"No se exportó: {result.Reason}";
            }
            else
            {
                // A run started while the files were written: the exporter stopped before the next one (docs/14 D-22).
                var rewritten = result.Files.Count == 1
                    ? $"Se reescribió 1 fichero ({result.Files[0]})"
                    : string.Create(CultureInfo.InvariantCulture, $"Se reescribieron {result.Files.Count} ficheros ({string.Join(", ", result.Files)})");
                ExportWarning = $"Exportación incompleta: {result.Reason} {rewritten} antes de parar; el resto sigue como estaba hasta la próxima exportación, que lo reescribe todo.";
            }
        }
#pragma warning disable CA1031 // The exporter is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo exportar la memoria: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshRunActivity();
        }
    }

    private bool CanExport() => _project is not null && _exporter is not null && !IsBusy && !RunIsActive;

    private void RefreshEntryCommands()
    {
        AcceptCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        RetractCommand.NotifyCanExecuteChanged();
    }
}
