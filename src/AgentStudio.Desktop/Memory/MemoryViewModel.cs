using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Markdown;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Semantic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Memory;

/// <summary>Spanish names of the categories, origins and trust of the memory (docs/14 D-18 and D-20).</summary>
public static class MemoryTexts
{
    /// <summary>The trust of a journal entry in quarantine because who wrote it cannot be checked (V40).</summary>
    public const string UncheckedJournalEntry =
        "En cuarentena: no se puede comprobar quién la escribió. Acéptala, o confía en su día o en toda la bitácora";

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
        MemoryKind.ProjectBrief => Texts.Get("Memory_Kind_ProjectBrief"),
        _ => kind.ToString(),
    };

    public static string Origin(MemoryOrigin origin) => origin switch
    {
        MemoryOrigin.System => "Sistema",
        MemoryOrigin.User => "Tú",
        MemoryOrigin.Agent => "Agente",
        _ => origin.ToString(),
    };

    /// <summary>
    /// Who wrote an entry of <paramref name="kind"/>. For a journal entry (docs/17 D-02) <see cref="MemoryOrigin.System"/>
    /// is Agent Studio, matched with the receipt of the run that wrote it, and <see cref="MemoryOrigin.Agent"/> is an entry
    /// whose author cannot be checked: the development hook, a third party, or Agent Studio itself before it left a receipt
    /// (V40). It is never an agent of the run, and it is never said to be someone else.
    /// </summary>
    public static string Origin(MemoryKind kind, MemoryOrigin origin) => kind == MemoryKind.JournalEntry
        ? origin switch
        {
            MemoryOrigin.System => "Agent Studio",
            MemoryOrigin.Agent => "Autor sin comprobar",
            _ => Origin(origin),
        }
        : Origin(origin);

    public static string Trust(MemoryTrust trust) => trust switch
    {
        MemoryTrust.Trusted => "De confianza",
        MemoryTrust.Proposed => "Propuesta de un agente: sin revisar",
        MemoryTrust.Quarantined => "En cuarentena: heredada de un historial aceptado como nuevo origen; revísala",
        MemoryTrust.Rejected => "Rechazada",
        MemoryTrust.Retracted => "Retirada",
        _ => trust.ToString(),
    };

    /// <summary>
    /// The trust of an entry, said for its category: a journal entry whose author cannot be checked waits in quarantine
    /// because of that (docs/17 D-02), not because it was inherited. It says only what is known (V40): without a receipt, an
    /// entry of the hook and one Agent Studio wrote before it left receipts look the same, and the format of a line can be
    /// copied, so it does not say who wrote it.
    /// </summary>
    public static string Trust(MemoryKind kind, MemoryOrigin origin, MemoryTrust trust) =>
        kind == MemoryKind.JournalEntry && origin == MemoryOrigin.Agent && trust == MemoryTrust.Quarantined
            ? UncheckedJournalEntry
            : Trust(trust);

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
    /// <param name="entry">The entry, as recall or the search gives it now.</param>
    /// <param name="zone">The local time zone of its date.</param>
    /// <param name="match">Why it came out of a search (its words, its meaning or both); null when it was listed, not searched.</param>
    public MemoryEntryViewModel(MemoryEntry entry, TimeZoneInfo zone, string? match = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(zone);
        Entry = entry;
        Match = match;
        Created = TimeZoneInfo.ConvertTime(entry.CreatedAt, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        Content = entry.Kind == MemoryKind.JournalEntry ? MarkdownLite.ToPlainText(entry.Content) : entry.Content;
    }

    public MemoryEntry Entry { get; }

    public string Id => Entry.Id;

    public string KindLabel => MemoryTexts.Kind(Entry.Kind);

    public string OriginLabel => MemoryTexts.Origin(Entry.Kind, Entry.Origin);

    public string TrustLabel => MemoryTexts.Trust(Entry.Kind, Entry.Origin, Entry.Trust);

    /// <summary>Why it came out of a search by meaning and by words; null when it was not searched that way.</summary>
    public string? Match { get; }

    public Tone TrustTone => MemoryTexts.Tone(Entry.Trust);

    public string Topic => Entry.Topic;

    /// <summary>
    /// The content as it reads. An entry of the journal is Markdown (<c>- **04:22:31** · P-0001 · … \(intento 1\)</c>): it
    /// reads without its marks, as «Bitácora de hoy» does (V31, V41). Only what is shown changes: the anchored text is
    /// <see cref="MemoryEntry.Content"/>, and any other category is shown as it is.
    /// </summary>
    public string Content { get; }

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
/// <para>
/// Phase 12 (docs/17 D-02 to D-06, F12-6; ADR-0047), with a <see cref="MemorySearchBackend"/>: «Buscar» with words goes to
/// the semantic index (<see cref="ISemanticIndex.SearchAsync"/>, every trust), by meaning and by words when the model is
/// there and by words alone otherwise, and says which; each result keeps its origin and its trust, and the texts the
/// system wrote from the log come with them, read-only. Without words, or without the index, it lists the memory as
/// before. The search by meaning has its own panel (<see cref="Model"/>: the state of the model and its download, only with
/// the person's OK), and so does the journal (<see cref="Journal"/>: «Confiar en la bitácora de este proyecto» and by days).
/// </para>
/// </summary>
public sealed partial class MemoryViewModel : DocumentViewModel
{
    public const string DocumentId = "memory:project";

    /// <summary>Recall asks for at most this many entries.</summary>
    public const int MaxEntries = 500;

    /// <summary>A search of the index asks for at most this many results.</summary>
    public const int MaxHits = 50;

    private readonly IProjectWorkspace? _workspace;
    private readonly IProjectMemory? _memory;
    private readonly IMemoryExporter? _exporter;
    private readonly IRunActivity? _activity;
    private readonly ISemanticIndex? _index;
    private readonly TimeProvider _time;
    private readonly RefreshQueue _refreshes;
    private ProjectContext? _project;

    /// <param name="workspace">Opens the project to know its id.</param>
    /// <param name="memory">The project memory; null when the shell was composed without it (the screen says so).</param>
    /// <param name="exporter">The Markdown export; null when it was not composed.</param>
    /// <param name="activity">Whether a run or a planning goes on; without it the exporter alone decides.</param>
    /// <param name="summary">The daily summary of the journal.</param>
    /// <param name="time">The local time of the dates shown.</param>
    /// <param name="search">
    /// The semantic index, the trust of the journal and the installer of the model (Phase 12); null, or any of them null,
    /// when they were not composed: the search goes by words, and the panels say what is not available.
    /// </param>
    public MemoryViewModel(
        IProjectWorkspace? workspace,
        IProjectMemory? memory,
        IMemoryExporter? exporter,
        IRunActivity? activity,
        DailySummaryViewModel summary,
        TimeProvider time,
        MemorySearchBackend? search = null)
        : base(DocumentId, "Memoria")
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(time);
        search ??= new MemorySearchBackend();
        _workspace = workspace;
        _memory = memory;
        _exporter = exporter;
        _activity = activity;
        _index = search.Index;
        _time = time;
        _refreshes = new RefreshQueue(RunRefreshAsync, search.UiContext);
        Summary = summary;
        Model = new EmbeddingModelViewModel(
            search.Index is not null,
            search.Installer,
            search.Model,
            time,
            search.UiContext,
            () => _refreshes.RequestAsync(MemoryRefresh.Status),
            status => ModelInstalled?.Invoke(this, new ModelInstalledEventArgs(_project, status)));
        Journal = new JournalTrustViewModel(search.JournalTrust, memory, activity, () => _refreshes.RequestAsync(MemoryRefresh.Journal | MemoryRefresh.List));
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EmbeddingModelViewModel.IsHybridReady))
            {
                OnPropertyChanged(nameof(SearchLabel));
                OnPropertyChanged(nameof(SearchAutomationName));
            }
        };
        _selectedKind = Kinds[0];
    }

    /// <summary>
    /// The model was downloaded and verifies, after the person's OK: once per download that ended
    /// <see cref="EmbeddingModelState.Ready"/>, never after one that failed, did not verify or was cancelled. The
    /// composition starts the refresh of the index from here (<see cref="ISemanticIndex.RefreshAsync"/>, in the background),
    /// and calls <see cref="IndexRefreshedAsync"/> when it ends.
    /// </summary>
    public event EventHandler<ModelInstalledEventArgs>? ModelInstalled;

    public DailySummaryViewModel Summary { get; }

    /// <summary>The search by meaning: the state of the model and of the index, and the download of the model.</summary>
    public EmbeddingModelViewModel Model { get; }

    /// <summary>The journal: how far it is trusted, and the decision to trust it, for the project or by days.</summary>
    public JournalTrustViewModel Journal { get; }

    /// <summary>The categories to filter by; the first one is «Todas».</summary>
    public IReadOnlyList<Option<MemoryKind?>> Kinds { get; } =
        [new(null, "Todas"), .. Enum.GetValues<MemoryKind>().Select(k => new Option<MemoryKind?>(k, MemoryTexts.Kind(k)))];

    /// <summary>The memory entries on screen, in their order: every entry listed, or those a search found.</summary>
    public ObservableCollection<MemoryEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Everything on screen, in the order of the list or of the search: the memory entries (<see cref="MemoryEntryViewModel"/>)
    /// and, from a search with «Todas», the texts the system wrote from the log (<see cref="LogFragmentViewModel"/>).
    /// </summary>
    public ObservableCollection<object> Results { get; } = [];

    /// <summary>The label of the search box: by meaning and by words when the model can, by words otherwise.</summary>
    public string SearchLabel => Model.IsHybridReady ? MemorySearchTexts.HybridLabel : MemorySearchTexts.WordsLabel;

    /// <summary>The accessible name of the search box.</summary>
    public string SearchAutomationName => Model.IsHybridReady ? MemorySearchTexts.HybridName : MemorySearchTexts.WordsName;

    /// <summary>How the last search went (by meaning and by words, or by words only and why); null for a plain list.</summary>
    [ObservableProperty]
    private string? _searchNotice;

    /// <summary>Words to search: by meaning and by words in the index, or by words in the topic and the content.</summary>
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
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isBusy;

    /// <summary>Why «Exportar a Markdown» is not offered now (a run or a planning goes on); null when it is.</summary>
    public string? ExportBlocked => RunIsActive ? "Hay una ejecución, una planificación, un debate o una auditoría en curso: la exportación espera a que termine." : null;

    private bool RunIsActive => _project is { } project && _activity?.IsBusy(project.ProjectId) == true;

    /// <summary>
    /// The project open in the shell changed (null: none): it is opened, and everything is read for it once: the state of
    /// the model and of its index, the trust of its journal and its memory.
    /// </summary>
    public async Task SetProjectAsync(string? projectRoot)
    {
        _project = null;
        ErrorMessage = null;
        ExportStatus = null;
        SearchNotice = null;
        Entries.Clear();
        Results.Clear();
        if (projectRoot is null)
        {
            Summary.SetProject(null);
            StatusMessage = "Abre una carpeta para ver su memoria.";
            await _refreshes.RequestAsync(MemoryRefresh.All).ConfigureAwait(true);
            return;
        }

        if (_memory is null || _workspace is null)
        {
            Summary.SetProject(null);
            StatusMessage = "La memoria del proyecto no está disponible en esta composición.";
            await _refreshes.RequestAsync(MemoryRefresh.All).ConfigureAwait(true);
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
            await _refreshes.RequestAsync(MemoryRefresh.All).ConfigureAwait(true);
            return;
        }

        Summary.SetProject(_project);
        await _refreshes.RequestAsync(MemoryRefresh.All).ConfigureAwait(true);
    }

    /// <summary>
    /// The index of the open project was refreshed or rebuilt (the composition calls it when
    /// <see cref="ISemanticIndex.RefreshAsync"/> ends): the state of the model and of the index is read again, after any
    /// refresh of the screen that is going on. It is the only way, besides opening the project and a download, that state
    /// is read: a search or a review never reads it, because it checks the files of the model.
    /// </summary>
    public Task IndexRefreshedAsync() => _refreshes.RequestAsync(MemoryRefresh.Status);

    /// <summary>
    /// Says again whether a run or a planning goes on (the export waits for it). The screen asks on every refresh; the
    /// composition may call it when a run starts or ends.
    /// </summary>
    public void RefreshRunActivity()
    {
        OnPropertyChanged(nameof(ExportBlocked));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// «Buscar» (and Intro in the box). With words and an index that has fragments, a search of the index by meaning and by
    /// words (or by words only, and why), with the results of every trust; without words, or without the index, the entries
    /// of every trust that match the words and the category, as before Phase 12, and the search says it went by words only.
    /// It reads only the list: the state of the index is the one read when the project opened, after a download or after a
    /// refresh of the index, and the journal panel is read again only when the journal may have changed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task SearchAsync() => _refreshes.RequestAsync(MemoryRefresh.List);

    /// <summary>
    /// The memory changed (a run ended and it was derived): the trust of the journal and the list or the search on screen
    /// are read again, after any refresh that is going on.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => _refreshes.RequestAsync(MemoryRefresh.Journal | MemoryRefresh.List);

    /// <summary>
    /// One refresh, the only consumer of <see cref="RefreshQueue"/>: two never interleave. The journal first (it recalls its
    /// entries on its own, and the list stays the last thing recalled), then the state of the index, then the list, each
    /// only when <paramref name="scope"/> asks for it, and all for the project open when it starts.
    /// </summary>
    private async Task RunRefreshAsync(MemoryRefresh scope)
    {
        var project = _project;
        IsBusy = true;
        try
        {
            if (scope.HasFlag(MemoryRefresh.Journal))
            {
                await Journal.RefreshAsync(project).ConfigureAwait(true);
            }

            if (scope.HasFlag(MemoryRefresh.Status))
            {
                await ReadIndexStatusAsync(project).ConfigureAwait(true);
            }

            if (scope.HasFlag(MemoryRefresh.List))
            {
                await ListOrSearchAsync(project).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
            RefreshEntryCommands();
            RefreshRunActivity();
        }
    }

    /// <summary>The list or the search of <paramref name="project"/>, with the state of the index last read; nothing without a project.</summary>
    private async Task ListOrSearchAsync(ProjectContext? project)
    {
        if (project is null || _memory is not { } memory)
        {
            Entries.Clear();
            Results.Clear();
            return;
        }

        ErrorMessage = null;
        try
        {
            var text = SearchText.Trim();
            var status = Model.Status;
            if (text.Length > 0 && _index is { } index && status is { Fragments: > 0 })
            {
                await SearchIndexAsync(index, memory, project, text).ConfigureAwait(true);
            }
            else
            {
                await ListAsync(memory, project, text).ConfigureAwait(true);
                SearchNotice = text.Length == 0 ? null
                    : _index is null ? MemorySearchTexts.NoIndex
                    : status is null ? MemorySearchTexts.UnreadableIndex
                    : MemorySearchTexts.EmptyIndex;
            }
        }
#pragma warning disable CA1031 // The memory is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo leer la memoria: {ex.Message}";
        }
    }

    /// <summary>The entries of every trust that match the words (full-text) and the category, in the order of recall.</summary>
    private async Task ListAsync(IProjectMemory memory, ProjectContext project, string text)
    {
        var query = new MemoryQuery(
            Text: text.Length == 0 ? null : text,
            Kinds: SelectedKind.Value is { } kind ? [kind] : null,
            MaxEntries: MaxEntries,
            TrustedOnly: false);
        var entries = await memory.RecallAsync(project, query, CancellationToken.None).ConfigureAwait(true);
        if (!Equals(project, _project))
        {
            return; // another project opened meanwhile: its own refresh follows this one.
        }

        Entries.Clear();
        Results.Clear();
        foreach (var entry in entries)
        {
            var item = new MemoryEntryViewModel(entry, _time.LocalTimeZone);
            Entries.Add(item);
            Results.Add(item);
        }

        StatusMessage = entries.Count == 0 ? "No hay entradas que coincidan." : null;
    }

    /// <summary>
    /// The search of the index (docs/17 D-05 and D-06): every trust, so the person sees what waits for a review too; the
    /// category of the filter, and the texts of the log only with «Todas». One card per entry (its best fragment ranks it),
    /// in the order of the fusion. A search that fails falls back to the words of the memory, and says so.
    /// </summary>
    private async Task SearchIndexAsync(ISemanticIndex index, IProjectMemory memory, ProjectContext project, string text)
    {
        var kind = SelectedKind.Value;
        var query = new SemanticQuery(text, MaxHits)
        {
            Kinds = kind is { } only ? [only] : null,
            IncludeLog = kind is null,
            TrustedOnly = false,
        };
        SemanticSearchResult result;
        try
        {
            result = await index.SearchAsync(project, query, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The index is behind a port: its failure leaves the search by words, and is said.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await ListAsync(memory, project, text).ConfigureAwait(true);
            SearchNotice = $"Solo por palabras: la búsqueda en el índice falló ({ex.Message.TrimEnd('.')}).";
            return;
        }

        if (!Equals(project, _project))
        {
            return; // another project opened meanwhile: its own refresh follows this one.
        }

        Entries.Clear();
        Results.Clear();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in result.Hits)
        {
            if (hit.Entry is { } entry)
            {
                if (shown.Add(SemanticFragment.MemoryPrefix + entry.Id))
                {
                    var item = new MemoryEntryViewModel(entry, _time.LocalTimeZone, MemorySearchTexts.Match(hit.Rank));
                    Entries.Add(item);
                    Results.Add(item);
                }
            }
            else if (hit.Fragment.Source.Kind == FragmentSourceKind.LogEntry && shown.Add(SemanticFragment.LogPrefix + hit.Fragment.Source.Reference))
            {
                Results.Add(new LogFragmentViewModel(hit, _time.LocalTimeZone));
            }
        }

        SearchNotice = result.Notice ?? MemorySearchTexts.Hybrid(Results.Count);
        StatusMessage = Results.Count == 0 ? "No hay entradas que coincidan." : null;
    }

    /// <summary>
    /// The state of the index of <paramref name="project"/> (it checks the files of the model), shown in the panel of the
    /// model and kept there (<see cref="EmbeddingModelViewModel.Status"/>) for the searches that follow; nothing without a
    /// project or without an index.
    /// </summary>
    private async Task ReadIndexStatusAsync(ProjectContext? project)
    {
        if (project is null)
        {
            Model.Show(false, null, null);
            return;
        }

        if (_index is not { } index)
        {
            Model.Show(true, null, null);
            return;
        }

        SemanticIndexStatus? status = null;
        string? problem = null;
        try
        {
            status = await index.StatusAsync(project, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The index is behind a port: a failure is shown in the panel, and the search goes by words.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problem = ex.Message;
        }

        if (Equals(project, _project))
        {
            Model.Show(true, status, problem);
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

        // The trust comes from the log: read it again, after any refresh that is going on (a review that waited in the queue
        // may end in the middle of a search). An entry of the journal also changes what the journal panel counts.
        var scope = entry.Entry.Kind == MemoryKind.JournalEntry ? MemoryRefresh.Journal | MemoryRefresh.List : MemoryRefresh.List;
        await _refreshes.RequestAsync(scope).ConfigureAwait(true);
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
