using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Debate;

/// <summary>A debate of the history (D-41), from replaying its log.</summary>
public sealed class DebateHistoryItem
{
    public DebateHistoryItem(DebateSummary summary, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(zone);
        Summary = summary;
        When = TimeZoneInfo.ConvertTime(summary.OpenedAt, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        ModeText = DebateTexts.Mode(summary.Mode);
        QuestionText = summary.Question ?? "Pregunta ilegible: no cuadra con el hash que ancló el registro.";
        StateText = summary.Interrupted
            ? $"Interrumpido · {DebateTexts.Phase(summary.Phase)}"
            : summary.Phase == DebatePhase.Closed ? DebateTexts.Outcome(summary.Outcome) : DebateTexts.Phase(summary.Phase);
        Tone = DebateTexts.ToneOf(summary.Phase, summary.Outcome, summary.Interrupted);
        DecisionText = summary.Decision is { } decision
            ? VerdictTexts.Decision(decision) + (summary.ChosenProposal is { Length: > 0 } chosen ? $" · propuesta {chosen}" : string.Empty)
            : null;
        ChallengeText = summary.Challenges is { Length: > 0 } revised
            ? $"Revisión del veredicto {revised}"
            : summary.Challenged ? "Su veredicto se impugnó" : null;
        ProblemsText = summary.Problems.Count == 0 ? null : string.Join(" · ", summary.Problems);
    }

    public DebateSummary Summary { get; }

    public RunId Debate => Summary.Debate;

    public string When { get; }

    public string ModeText { get; }

    public string QuestionText { get; }

    public string StateText { get; }

    public Tone Tone { get; }

    public string? DecisionText { get; }

    public string? ChallengeText { get; }

    public string? ProblemsText { get; }

    /// <summary>«Reanudar»: only an interrupted debate whose replay found nothing wrong; it spends quota and asks your approval.</summary>
    public bool CanResume => Summary.Interrupted && Summary.Problems.Count == 0;

    /// <summary>What a screen reader says of a debate of the history (V34): when it opened, its question and its state.</summary>
    public override string ToString() => $"{When} · {QuestionText} · {StateText}";
}

/// <summary>A debate the «Debates» document started following: opened, opened as a challenge, or resumed.</summary>
public sealed record FollowedDebate(ProjectContext Project, IDebateRun Run);

/// <summary>What the right side of the «Debates» document shows.</summary>
public enum DebatesPart
{
    None,
    NewDebate,
    Challenge,
    Room,
}

/// <summary>
/// The «Debates» document of one project (docs/15 D-41, <c>Ctrl+Shift+D</c>): the history of its debates, from replaying
/// the log, filtered by mode, with «Reanudar» on an interrupted one; «Nuevo debate»; the challenge of a verdict; and the
/// room, which follows the debate in course or shows one read back with its verdict card. One debate at a time per project
/// (D-05): the coordinator refuses another while one is in course, and says why.
/// </summary>
public sealed partial class DebatesDocumentViewModel : DocumentViewModel
{
    public const string IdPrefix = "debates:";

    private readonly DebateScreenBackend _backend;
    private readonly TimeProvider _time;
    private readonly DebateScreens? _screens;
    private readonly SynchronizationContext? _ui;
    private readonly List<DebateHistoryItem> _all = [];
    private DebateRoomViewModel? _liveRoom;
    private bool _opened;

    /// <param name="projectRoot">The project whose debates these are.</param>
    /// <param name="backend">The ports; any may be missing (the document says which).</param>
    /// <param name="time">The clock and the local zone of the dates shown.</param>
    /// <param name="screens">The other review screens and the shell («Crear tareas correctivas»); without them that action is not offered.</param>
    /// <param name="uiContext">Where the rooms apply the events of their debate; defaults to the creating thread's context.</param>
    public DebatesDocumentViewModel(string projectRoot, DebateScreenBackend backend, TimeProvider time, DebateScreens? screens = null, SynchronizationContext? uiContext = null)
        : base(CreateId(projectRoot), $"Debates · {RecentProjectItem.From(projectRoot).Name}")
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        ProjectRoot = projectRoot;
        _backend = backend;
        _time = time;
        _screens = screens;
        _ui = uiContext ?? SynchronizationContext.Current;
        NewDebate = new NewDebateViewModel(backend, time);
        Challenge = new ChallengeFormViewModel(backend, time);
        NewDebate.Opened += (_, opened) => Follow(opened);
        Challenge.Opened += (_, opened) => Follow(opened);
        _selectedFilter = ModeFilters[0];
    }

    public static string CreateId(string projectRoot) => IdPrefix + Path.GetFullPath(projectRoot).ToUpperInvariant();

    public string ProjectRoot { get; }

    /// <summary>The project once opened; null before, or when it could not be opened.</summary>
    public ProjectContext? Project { get; private set; }

    /// <summary>
    /// Raised on the UI thread each time the room starts following a debate going on: one opened with «Nuevo debate», a
    /// challenge, or one resumed with «Reanudar». The shell tracks it for the tray and the managed exit (docs/16 D-14 to D-16).
    /// </summary>
    public event EventHandler<FollowedDebate>? DebateFollowed;

    /// <summary>What is missing in this composition, and anything else you should know.</summary>
    public ObservableCollection<string> Notices { get; } = [];

    public ObservableCollection<DebateHistoryItem> History { get; } = [];

    public IReadOnlyList<Option<DebateMode?>> ModeFilters { get; } =
        [new(null, "Todos los modos"), .. Enum.GetValues<DebateMode>().Select(m => new Option<DebateMode?>(m, DebateTexts.Mode(m)))];

    [ObservableProperty]
    private Option<DebateMode?> _selectedFilter;

    public NewDebateViewModel NewDebate { get; }

    public ChallengeFormViewModel Challenge { get; }

    /// <summary>The room on screen: the debate in course, or one read back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAnotherThanLive))]
    private DebateRoomViewModel? _room;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNewDebate))]
    [NotifyPropertyChangedFor(nameof(ShowsChallenge))]
    [NotifyPropertyChangedFor(nameof(ShowsRoom))]
    [NotifyPropertyChangedFor(nameof(ShowsNothing))]
    private DebatesPart _part;

    public bool ShowsNewDebate => Part == DebatesPart.NewDebate;

    public bool ShowsChallenge => Part == DebatesPart.Challenge;

    public bool ShowsRoom => Part == DebatesPart.Room && Room is not null;

    public bool ShowsNothing => !ShowsNewDebate && !ShowsChallenge && !ShowsRoom;

    /// <summary>A debate is in course while another one is on screen: «Volver al debate en curso».</summary>
    public bool ShowsAnotherThanLive => _liveRoom is { IsLive: true } && (Part != DebatesPart.Room || !ReferenceEquals(Room, _liveRoom));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Opens the project and reads its debates and its template once; called when the tab is opened.</summary>
    public async Task OpenAsync()
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        Notices.Clear();
        if (_backend.Coordinator is null)
        {
            Notices.Add("Falta el coordinador de debates en esta composición: no se puede listar, abrir ni reanudar ningún debate.");
        }

        if (_backend.Challenges is null)
        {
            Notices.Add("Falta el servicio de impugnación: no se puede impugnar un veredicto.");
        }

        if (_backend.Evidence is null)
        {
            Notices.Add("Falta el almacén de evidencias: las citas de las contribuciones se listan, pero no se abren.");
        }

        if (_backend.Roster is null)
        {
            Notices.Add("Falta la plantilla de agentes: no hay participantes ni jueces que elegir.");
        }

        if (_backend.Runs is null)
        {
            Notices.Add("Falta el registro de ejecuciones: no se puede debatir sobre una ejecución.");
        }

        if (_backend.Workspace is null)
        {
            Notices.Add("Falta el almacén del proyecto: no se puede abrir el proyecto.");
            return;
        }

        try
        {
            Project = await _backend.Workspace.OpenAsync(ProjectRoot, CancellationToken.None).ConfigureAwait(true);
            OnPropertyChanged(nameof(Project));
        }
#pragma warning disable CA1031 // The workspace is behind a port: a failure is shown in the document, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo abrir el proyecto: {ex.Message}";
            return;
        }

        await NewDebate.LoadAsync(Project, CancellationToken.None).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>«Actualizar»: the debates of the project, by replaying each one.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (Project is not { } project || _backend.Coordinator is not { } coordinator)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var debates = await coordinator.ListAsync(project, CancellationToken.None).ConfigureAwait(true);
            _all.Clear();
            _all.AddRange(debates.OrderByDescending(d => d.OpenedAt).Select(d => new DebateHistoryItem(d, _time.LocalTimeZone)));
            ApplyFilter();
        }
#pragma warning disable CA1031 // The coordinator is behind a port: a failure is shown in the document, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo leer el historial de debates: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    partial void OnSelectedFilterChanged(Option<DebateMode?> value) => ApplyFilter();

    private void ApplyFilter()
    {
        var mode = SelectedFilter?.Value;
        History.Clear();
        foreach (var item in _all.Where(i => mode is null || i.Summary.Mode == mode))
        {
            History.Add(item);
        }

        StatusMessage = _all.Count == 0
            ? "El proyecto aún no tiene debates."
            : History.Count == 0 ? "Ningún debate de ese modo." : null;
    }

    /// <summary>«Nuevo debate».</summary>
    [RelayCommand]
    private void ShowNewDebate() => Part = DebatesPart.NewDebate;

    /// <summary>«Volver al debate en curso».</summary>
    [RelayCommand]
    private void BackToLive()
    {
        if (_liveRoom is { } live)
        {
            Room = live;
            Part = DebatesPart.Room;
            OnPropertyChanged(nameof(ShowsAnotherThanLive));
        }
    }

    /// <summary>«Abrir»: the debate read back from its log, its text checked against the hashes the log anchored.</summary>
    [RelayCommand]
    private async Task OpenItemAsync(DebateHistoryItem? item)
    {
        if (item is null || Project is not { } project || _backend.Coordinator is not { } coordinator)
        {
            return;
        }

        if (_liveRoom is { IsLive: true } live && live.DebateId == item.Debate)
        {
            BackToLive();
            return;
        }

        ErrorMessage = null;
        try
        {
            var transcript = await coordinator.ReadAsync(project, item.Debate, CancellationToken.None).ConfigureAwait(true);
            var requirements = await RequirementsAsync(project, transcript.RequirementSetHash).ConfigureAwait(true);
            var room = NewRoom();
            room.ShowTranscript(transcript, project, requirements);
            Room = room;
            Part = DebatesPart.Room;
            OnPropertyChanged(nameof(ShowsAnotherThanLive));
        }
#pragma warning disable CA1031 // The coordinator is behind a port: a failure is shown in the document, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo leer el debate {item.Debate}: {ex.Message}";
        }
    }

    /// <summary>«Reanudar» (D-04): the coordinator replays the debate, closes its open turns and asks your approval before spending quota.</summary>
    [RelayCommand]
    private async Task ResumeAsync(DebateHistoryItem? item)
    {
        if (item is not { CanResume: true } || Project is not { } project || _backend.Coordinator is not { } coordinator)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = $"Reanudando {item.Debate}: espera tu aprobación…";
        try
        {
            var run = await coordinator.ResumeAsync(project, item.Debate, CancellationToken.None).ConfigureAwait(true);
            var context = await ContextOfAsync(project, item).ConfigureAwait(true);
            Follow(new OpenedDebate(run, null, context));
            StatusMessage = null;
        }
#pragma warning disable CA1031 // The coordinator is behind a port: a refusal (approval denied, busy project) is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = null;
            ErrorMessage = $"No se reanudó el debate {item.Debate}: {ex.Message}";
        }
    }

    /// <summary>«Impugnar» a verdict: the challenge form, on that verdict.</summary>
    public async Task StartChallengeAsync(string verdictId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verdictId);
        if (Project is null)
        {
            await OpenAsync().ConfigureAwait(true);
        }

        if (Project is not { } project)
        {
            ErrorMessage = "Sin proyecto abierto no se puede impugnar.";
            return;
        }

        Part = DebatesPart.Challenge;
        await Challenge.LoadAsync(project, verdictId, CancellationToken.None).ConfigureAwait(true);
    }

    private void Follow(OpenedDebate opened)
    {
        if (Project is not { } project)
        {
            return;
        }

        _ = ReleaseFinishedAsync(_liveRoom);
        var room = NewRoom();
        room.Finished += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowsAnotherThanLive));
            _ = RefreshAsync();
        };
        room.Attach(opened.Run, project, opened.Context);
        _liveRoom = room;
        Room = room;
        Part = DebatesPart.Room;
        OnPropertyChanged(nameof(ShowsAnotherThanLive));
        DebateFollowed?.Invoke(this, new FollowedDebate(project, opened.Run));
        _ = RefreshAsync();
    }

    private static async Task ReleaseFinishedAsync(DebateRoomViewModel? room)
    {
        if (room is { IsLive: false })
        {
            await room.ReleaseAsync().ConfigureAwait(true);
        }
    }

    private DebateRoomViewModel NewRoom() => new(
        _time,
        _ui,
        _backend.Evidence,
        _backend.Challenges is null ? null : StartChallengeAsync,
        _screens is { CanNavigate: true } screens ? request => screens.CreateCorrectiveTasksAsync(ProjectRoot, request) : null);

    private async Task<DebateRoomContext> ContextOfAsync(ProjectContext project, DebateHistoryItem item)
    {
        if (_backend.Coordinator is not { } coordinator)
        {
            return new DebateRoomContext(item.Summary.Question);
        }

        try
        {
            var transcript = await coordinator.ReadAsync(project, item.Debate, CancellationToken.None).ConfigureAwait(true);
            var requirements = await RequirementsAsync(project, transcript.RequirementSetHash).ConfigureAwait(true);
            return new DebateRoomContext(transcript.Summary.Question, requirements, transcript.Judging, transcript.WaivedParticipantVendors);
        }
#pragma warning disable CA1031 // Best effort: without the transcript the room still follows the debate, without its disposition.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new DebateRoomContext(item.Summary.Question);
        }
    }

    private async Task<FrozenRequirementSet?> RequirementsAsync(ProjectContext project, string hash)
    {
        if (_backend.Requirements is not { } store || string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        try
        {
            return await store.FindAsync(project.ProjectId, hash, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Best effort: without the requirements the card says it shows ids only.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
