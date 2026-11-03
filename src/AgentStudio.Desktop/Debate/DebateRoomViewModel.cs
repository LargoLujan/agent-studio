using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Debate;

/// <summary>
/// What the room knows of a debate besides what its run reports (the snapshot carries neither the question nor the
/// disposition of the judgment): the literal question, the frozen requirements, the disposition with its waivers and the
/// judges, from the proposal that opened it or from the debate read back.
/// </summary>
public sealed record DebateRoomContext(
    string? Question = null,
    FrozenRequirementSet? Requirements = null,
    DebateJudgingKind? Judging = null,
    IReadOnlyList<VendorId>? WaivedVendors = null,
    IReadOnlyList<JudgeIdentity>? Judges = null);

/// <summary>A participant as the room lists it: its label and role, and the agent, CLI, vendor and model behind them.</summary>
public sealed record DebateParticipantRow(string Label, string Role, AgentId Agent, string Where)
{
    /// <summary>What a screen reader says of a participant of the room (V34): its label and its role.</summary>
    public override string ToString() => $"{Label} · {Role}";

    public string AgentName => Agent.Value;
}

/// <summary>An evidence a contribution cites, as a button that opens it.</summary>
public sealed record CitedEvidence(string Id)
{
    /// <summary>What a screen reader says of an evidence a contribution cites (V34): the evidence.</summary>
    public override string ToString() => $"Evidencia {Id}";
}

/// <summary>An evidence opened from a contribution: what it is, whether its bytes still give the SHA-256 of its record, and its text when they do.</summary>
public sealed record EvidenceViewer(string Id, string Title, string Description, string HashText, Tone HashTone, string? Text);

/// <summary>One published contribution as the room shows it (D-41): label, kind, author, reply, text, quotes and what could not be resolved.</summary>
public sealed class DebateContributionViewModel
{
    public DebateContributionViewModel(DebateContributionView view, IReadOnlyDictionary<string, DebateParticipantView> participants, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(zone);
        View = view;
        var author = participants.TryGetValue(view.Author, out var who) ? $"{view.Author} · {DebateTexts.Role(who.Role)}" : view.Author;
        Header = $"{view.Label} · {DebateTexts.Kind(view.Kind)} de {author}";
        RepliesText = view.RepliesTo is { Length: > 0 } replies ? $"Responde a {replies}" : null;
        AnsweredText = "Respondió a las " + TimeZoneInfo.ConvertTime(view.AnsweredAt, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " (se publicó al cerrar la ronda)";
        HashText = "SHA-256 " + DebateTexts.Short(view.Sha256);
        Cites = [.. view.Cites.Select(id => new CitedEvidence(id.Value))];
        Unresolved = [.. view.UnresolvedCitations.Select(c => $"Cita que no se resolvió en el commit: {c}")];
        RedactionsText = view.Redactions switch
        {
            0 => null,
            1 => "Se redactó 1 secreto antes de publicarla.",
            var count => string.Create(CultureInfo.InvariantCulture, $"Se redactaron {count} secretos antes de publicarla."),
        };
        FinishedText = view.Finished ? "Marcó su posición como terminada." : null;
        if (view.Text is not { } text)
        {
            TextProblem = "El texto no se pudo leer o no cuadra con el hash que ancló el registro: no se muestra.";
            Claims = [];
            Quotes = [];
            return;
        }

        var content = ContributionText.Read(text);
        Summary = content.Summary;
        Argument = content.Argument ?? content.Raw;
        Claims = [.. content.Claims.Select(c => c.Support.Count == 0 ? c.Claim : $"{c.Claim} (apoyo: {string.Join(", ", c.Support)})")];
        Quotes = [.. content.Quotes.Select(q => $"{q.Id} · {q.Where}{(string.IsNullOrWhiteSpace(q.Why) ? string.Empty : " · " + q.Why)}")];
        ConcedesText = content.Concedes.Count == 0 ? null : "Concede: " + string.Join(", ", content.Concedes);
    }

    public DebateContributionView View { get; }

    public string Label => View.Label;

    public int Round => View.Round;

    public string Header { get; }

    public string? RepliesText { get; }

    public string? Summary { get; }

    public string? Argument { get; }

    public IReadOnlyList<string> Claims { get; }

    public IReadOnlyList<string> Quotes { get; }

    public bool HasClaims => Claims.Count > 0;

    public bool HasQuotes => Quotes.Count > 0;

    public string? ConcedesText { get; }

    public string? FinishedText { get; }

    public IReadOnlyList<CitedEvidence> Cites { get; }

    public bool HasCites => Cites.Count > 0;

    /// <summary>Quotes the system could not resolve in the commit, in the warning colour: facts for the judge, not integrity problems.</summary>
    public IReadOnlyList<string> Unresolved { get; }

    public bool HasUnresolved => Unresolved.Count > 0;

    public string? RedactionsText { get; }

    public string AnsweredText { get; }

    public string HashText { get; }

    /// <summary>Why the text is not shown; null when it is.</summary>
    public string? TextProblem { get; }

    /// <summary>What a screen reader says of a contribution of the room (V34): its label, its kind and its author.</summary>
    public override string ToString() => Header;
}

/// <summary>The published contributions of one round.</summary>
public sealed record DebateRoundViewModel(int Round, string Title, IReadOnlyList<DebateContributionViewModel> Contributions)
{
    /// <summary>What a screen reader says of a round of the room (V34): its title.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// The room of a debate (docs/15 D-41, docs/03 §4): the header (mode, phase, round, time and tokens with their origin, «~»
/// when an estimate is among them), the participants with the agents behind their labels, the published contributions by
/// round with their replies and quotes (which open their evidence; the quotes that could not be resolved in the warning
/// colour), a round in course shown sealed and without text, and the actions «Pedir juicio ahora», «Ejecutar pruebas»,
/// «Añadir evidencia», «Reintentar turno» and «Abortar». At the end, the card of the verdict.
/// </summary>
/// <remarks>
/// It follows one <see cref="IDebateRun"/> like the run monitor follows a run: its snapshot at once, then its events read on
/// a background task and applied on the UI context, in order; every event reads the snapshot again, which is the truth.
/// Nothing is shown that the run did not say, and the actions only ask the run. A debate read back
/// (<see cref="ShowTranscript"/>) is the same room without actions.
/// </remarks>
public sealed partial class DebateRoomViewModel : ObservableObject
{
    public const string AbortReason = "Abortado por el usuario desde la sala.";

    /// <summary>The evidence opened from a contribution is shown up to this size.</summary>
    public const int MaxEvidenceBytes = 1024 * 1024;

    private const int MaxActivity = 200;

    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly IEvidenceStore? _evidence;
    private readonly Func<string, Task>? _challenge;
    private readonly Func<string, Task>? _correctiveTasks;
    private readonly List<JudgedVerdict> _verdicts = [];
    private IDebateRun? _run;
    private ProjectContext? _project;
    private DebateRoomContext _context = new();
    private DebateSnapshot? _snapshot;
    private IReadOnlyList<DebateParticipantView> _participants = [];
    private int _generation;
    private string? _cardKey;

    /// <param name="time">Its local time zone formats the times shown.</param>
    /// <param name="uiContext">Where the events are applied; defaults to the creating thread's context.</param>
    /// <param name="evidence">Opens the evidence a contribution cites; without it the quotes are listed, not opened.</param>
    /// <param name="challenge">«Impugnar» on a verdict of the card, by its id.</param>
    /// <param name="correctiveTasks">«Crear tareas correctivas» from the card.</param>
    public DebateRoomViewModel(TimeProvider time, SynchronizationContext? uiContext = null, IEvidenceStore? evidence = null, Func<string, Task>? challenge = null, Func<string, Task>? correctiveTasks = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        _evidence = evidence;
        _challenge = challenge;
        _correctiveTasks = correctiveTasks;
    }

    /// <summary>Raised on the UI context once the debate followed has ended (or its stream failed).</summary>
    public event EventHandler? Finished;

    public ObservableCollection<DebateParticipantRow> Participants { get; } = [];

    public ObservableCollection<DebateRoundViewModel> Rounds { get; } = [];

    /// <summary>What the turns and the judge did, newest last.</summary>
    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    /// <summary>What the replay or the integrity checks found.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Completes once every event of the debate has been read and its completion observed.</summary>
    public Task Listening { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowing))]
    private RunId? _debateId;

    /// <summary>A debate read back, not followed: no action is offered.</summary>
    [ObservableProperty]
    private bool _isTranscript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    [NotifyCanExecuteChangedFor(nameof(RequestJudgmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTestsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddEvidenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryTurnCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    private bool _isFinished;

    [ObservableProperty]
    private string _title = "Sin debate";

    [ObservableProperty]
    private string? _subjectText;

    [ObservableProperty]
    private string? _question;

    [ObservableProperty]
    private string? _requirementsText;

    [ObservableProperty]
    private string? _judgesText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestJudgmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTestsCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddEvidenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    private DebatePhase? _phase;

    [ObservableProperty]
    private string _phaseText = "Sin debate";

    [ObservableProperty]
    private Tone _phaseTone = Tone.Neutral;

    [ObservableProperty]
    private string? _roundText;

    [ObservableProperty]
    private string? _timeText;

    [ObservableProperty]
    private string? _tokensText;

    [ObservableProperty]
    private string? _runsText;

    /// <summary>
    /// The tokens of the debate against its maximum (docs/18 P3), with their origin and «parcial» when turns did not report
    /// them; without a maximum, the tokens alone on an empty track. Null without a debate.
    /// </summary>
    [ObservableProperty]
    private MetricBarReading? _tokensBar;

    /// <summary>The agent runs of the debate against its budget (docs/18 P3), measured by Agent Studio; null without a debate.</summary>
    [ObservableProperty]
    private MetricBarReading? _runsBar;

    /// <summary>«Ronda 2: 2 turnos en curso, sellados hasta que terminen todos»; null when no turn runs.</summary>
    [ObservableProperty]
    private string? _sealedText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryTurnCommand))]
    [NotifyCanExecuteChangedFor(nameof(RequestJudgmentCommand))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingFailedTurn))]
    private DebateAwaiting? _awaiting;

    /// <summary>A turn failed twice: «Reintentar turno» is offered next to its participant.</summary>
    public bool IsAwaitingFailedTurn => IsLive && Awaiting is { Reason: DebateAwaitReason.FailedTurn };

    [ObservableProperty]
    private string? _awaitingText;

    /// <summary>Why the debate went to judgment, and the limit that cut it; null before.</summary>
    [ObservableProperty]
    private string? _judgmentText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunTestsCommand))]
    private bool _canRunTests;

    [ObservableProperty]
    private VerdictCardViewModel? _card;

    [ObservableProperty]
    private EvidenceViewer? _openedEvidence;

    [ObservableProperty]
    private string? _resultSummary;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The title of the evidence «Añadir evidencia» captures as a note of yours.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEvidenceCommand))]
    private string _noteTitle = string.Empty;

    /// <summary>The text of that note.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEvidenceCommand))]
    private string _noteText = string.Empty;

    /// <summary>A debate is being followed or shown.</summary>
    public bool IsShowing => DebateId is not null;

    /// <summary>A debate is being followed and has not ended: its actions are offered.</summary>
    public bool IsLive => _run is not null && !IsFinished;

    /// <summary>The project of the debate on screen.</summary>
    public ProjectContext? Project => _project;

    /// <summary>Starts following <paramref name="run"/>: its snapshot at once, then its events as they come.</summary>
    public void Attach(IDebateRun run, ProjectContext project, DebateRoomContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(project);
        if (_run is not null || IsTranscript)
        {
            throw new InvalidOperationException("A room follows one debate: create another room for another debate.");
        }

        Reset();
        _run = run;
        _project = project;
        _context = context ?? new DebateRoomContext();
        var generation = ++_generation;
        ApplyContext();
        DebateId = run.Id;
        OnPropertyChanged(nameof(IsLive));
        Refresh(SafeSnapshot(run));
        Log(_time.GetUtcNow(), $"Siguiendo el debate {run.Id}");
        Listening = ListenAsync(run, generation);
        NotifyCommands();
    }

    /// <summary>Shows a debate read back from its log, without actions (its card, if it was judged, offers «Impugnar»).</summary>
    public void ShowTranscript(DebateTranscript transcript, ProjectContext project, FrozenRequirementSet? requirements = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(project);
        if (_run is not null)
        {
            throw new InvalidOperationException("A room follows one debate: create another room to show one read back.");
        }

        Reset();
        _generation++;
        _project = project;
        _context = new DebateRoomContext(transcript.Summary.Question, requirements, transcript.Judging, transcript.WaivedParticipantVendors);
        IsTranscript = true;
        var summary = transcript.Summary;
        ApplyContext();
        DebateId = summary.Debate;
        Title = $"Debate · {DebateTexts.Mode(summary.Mode)}";
        SubjectText = DebateTexts.Subject(summary.Subject);
        Phase = summary.Phase;
        PhaseText = PhaseLine(summary.Phase, summary.Outcome, summary.Interrupted);
        PhaseTone = DebateTexts.ToneOf(summary.Phase, summary.Outcome, summary.Interrupted);
        RoundText = null;
        SetParticipants(transcript.Participants);
        SetContributions(transcript.Contributions);
        JudgmentText = JudgmentLine(transcript.JudgmentReason, transcript.LimitReached);
        Replace(Problems, [.. summary.Problems, .. transcript.Problems]);
        _verdicts.AddRange(transcript.Verdicts);
        if (transcript.Verdicts.Count > 0 || transcript.Outcome is not null)
        {
            Card = BuildCard(transcript.Outcome, summary.Mode, summary.Subject, awaitingHuman: false);
        }

        IsFinished = summary.Phase == DebatePhase.Closed;
        StatusMessage = summary.Interrupted ? "Interrumpido: el debate quedó sin cerrar y sin proceso detrás. «Reanudar» lo continúa (gasta cuota y te pide aprobación)." : null;
    }

    /// <summary>Releases the debate followed once it ended; one in course is never released from here.</summary>
    public async Task ReleaseAsync()
    {
        if (IsLive)
        {
            throw new InvalidOperationException("The debate is still in course.");
        }

        if (_run is { } run)
        {
            _generation++;
            await run.DisposeAsync().ConfigureAwait(true);
        }
    }

    // ---- actions (D-20, D-22 and D-27) ----

    /// <summary>«Pedir juicio ahora», or «Pedir juicio con lo que hay» when a turn failed twice.</summary>
    [RelayCommand(CanExecute = nameof(CanRequestJudgment))]
    private Task RequestJudgmentAsync() => SendAsync("pedir el juicio", run => run.RequestJudgmentAsync());

    private bool CanRequestJudgment() => IsLive && Phase is DebatePhase.Proposals or DebatePhase.Deliberation;

    /// <summary>«Ejecutar pruebas»: the trusted profile on the commit of the subject, isolated at Low integrity; it spends no quota.</summary>
    [RelayCommand(CanExecute = nameof(CanRunTestsNow))]
    private Task RunTestsAsync() => SendAsync("ejecutar las pruebas", run => run.RunTestsAsync());

    private bool CanRunTestsNow() => IsLive && CanRunTests && Phase is not DebatePhase.Closed;

    /// <summary>«Añadir evidencia»: a note of yours, captured as <c>HumanNote</c>/<c>ProvidedByHuman</c>; the next round and the judge see it.</summary>
    [RelayCommand(CanExecute = nameof(CanAddEvidence))]
    private async Task AddEvidenceAsync()
    {
        var title = NoteTitle.Trim();
        var text = NoteText.Trim();
        if (await SendAsync("añadir la evidencia", run => run.AddHumanEvidenceAsync(title, text)).ConfigureAwait(true))
        {
            NoteTitle = string.Empty;
            NoteText = string.Empty;
            StatusMessage = $"Evidencia «{title}» añadida: la verán la ronda siguiente y el juez.";
        }
    }

    private bool CanAddEvidence() => IsLive && Phase is not DebatePhase.Closed && !string.IsNullOrWhiteSpace(NoteTitle) && !string.IsNullOrWhiteSpace(NoteText);

    /// <summary>«Reintentar turno» (D-22): a new attempt of the participant whose turn failed twice.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryTurn))]
    private Task RetryTurnAsync(DebateParticipantRow? participant) =>
        participant is null ? Task.CompletedTask : SendAsync($"reintentar el turno de {participant.Label}", run => run.RetryTurnAsync(participant.Agent));

    private bool CanRetryTurn(DebateParticipantRow? participant) =>
        IsLive && participant is not null && Awaiting is { Reason: DebateAwaitReason.FailedTurn } waiting
        && (waiting.Participant is null || string.Equals(waiting.Participant, participant.Label, StringComparison.Ordinal));

    /// <summary>«Abortar»: the turns in course are cancelled and the debate is closed as aborted.</summary>
    [RelayCommand(CanExecute = nameof(CanAbort))]
    private Task AbortAsync() => SendAsync("abortar el debate", run => run.AbortAsync(AbortReason));

    private bool CanAbort() => IsLive && Phase is not DebatePhase.Closed;

    /// <summary>Opens an evidence a contribution cites, if its bytes still give the SHA-256 of its record.</summary>
    [RelayCommand]
    private async Task OpenEvidenceAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || _project is not { } project)
        {
            return;
        }

        if (_evidence is not { } store)
        {
            OpenedEvidence = new EvidenceViewer(id, id, "Falta el almacén de evidencias en esta composición: no se puede abrir.", string.Empty, Tone.Warning, null);
            return;
        }

        try
        {
            OpenedEvidence = await ReadEvidenceAsync(store, project, new EvidenceId(id)).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The store is behind a port: a failure is shown in the room, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            OpenedEvidence = new EvidenceViewer(id, id, $"No se pudo abrir: {ex.Message}", string.Empty, Tone.Error, null);
        }
    }

    [RelayCommand]
    private void CloseEvidence() => OpenedEvidence = null;

    private static async Task<EvidenceViewer> ReadEvidenceAsync(IEvidenceStore store, ProjectContext project, EvidenceId id)
    {
        var records = await store.ListAsync(project.ProjectId, CancellationToken.None).ConfigureAwait(false);
        if (records.FirstOrDefault(r => r.Id == id) is not { } record)
        {
            return new EvidenceViewer(id.Value, id.Value, "No está en el almacén de evidencias del proyecto.", string.Empty, Tone.Warning, null);
        }

        var description = $"{EvidenceText.Kind(record.Kind)} · {EvidenceText.Origin(record.Origin)}{(record.SubjectCommit is { } commit ? $" · commit {DebateTexts.Short(commit)}" : string.Empty)}";
        byte[] bytes;
        await using (var stream = await store.OpenContentAsync(project.ProjectId, id, CancellationToken.None).ConfigureAwait(false))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy).ConfigureAwait(false);
            bytes = copy.ToArray();
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, record.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new EvidenceViewer(id.Value, record.Title, description, $"SHA-256 {DebateTexts.Short(actual)} · no cuadra con su registro ({DebateTexts.Short(record.ContentSha256)}): no se muestra", Tone.Error, null);
        }

        var hash = $"SHA-256 {DebateTexts.Short(actual)} · cuadra con su registro";
        return bytes.Length > MaxEvidenceBytes
            ? new EvidenceViewer(id.Value, record.Title, description, hash, Tone.Success, "Ocupa más de 1 MiB: no se muestra aquí.")
            : new EvidenceViewer(id.Value, record.Title, description, hash, Tone.Success, Encoding.UTF8.GetString(bytes));
    }

    private async Task<bool> SendAsync(string what, Func<IDebateRun, Task> action)
    {
        if (_run is not { } run || !IsLive)
        {
            return false;
        }

        try
        {
            ErrorMessage = null;
            await action(run).ConfigureAwait(true);
            return true;
        }
#pragma warning disable CA1031 // The debate is behind a port: whatever it refuses is shown, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = $"No se pudo {what}: {ex.Message}";
            return false;
        }
    }

    // ---- following the run ----

    private async Task ListenAsync(IDebateRun run, int generation)
    {
        try
        {
            await foreach (var progress in run.Events.ConfigureAwait(false))
            {
                Post(generation, () => Apply(run, progress));
            }

            var result = await run.Completion.ConfigureAwait(false);
            Post(generation, () => Finish(run, result));
        }
        catch (OperationCanceledException)
        {
            Post(generation, () => Fail("El debate se dejó de seguir antes de terminar."));
        }
#pragma warning disable CA1031 // A failure of the debate must be shown in the room, never thrown on a background task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Post(generation, () => Fail($"El debate terminó con un error: {ex.Message}"));
        }
    }

    private void Post(int generation, Action action)
    {
        void Run()
        {
            if (generation == _generation)
            {
                action();
            }
        }

        if (_ui is null)
        {
            Run();
        }
        else
        {
            _ui.Post(_ => Run(), null);
        }
    }

    private void Apply(IDebateRun run, DebateProgress progress)
    {
        switch (progress)
        {
            case DebateTurnStarted started:
                Log(started.At, $"{started.Participant} · ronda {started.Round} · intento {started.Attempt}: turno lanzado");
                break;
            case DebateTurnFinished finished:
                Log(finished.At, $"{finished.Participant} · ronda {finished.Round} · intento {finished.Attempt}: {DebateTexts.Turn(finished.Outcome)} (su respuesta sigue sellada hasta que cierre la ronda)");
                break;
            case DebateRoundSealed @sealed:
                Log(@sealed.At, $"Ronda {@sealed.Round}: {DebateTexts.Count(@sealed.TurnsInFlight, "turno en curso", "turnos en curso")}, sellada");
                break;
            case DebateContributionPublished published:
                Log(published.At, $"{published.Contribution.Label} publicada: {DebateTexts.Kind(published.Contribution.Kind)} de {published.Contribution.Author}");
                break;
            case DebateRoundEnded ended:
                Log(ended.At, $"Ronda {ended.Round} cerrada: {DebateTexts.Phase(ended.Next)}{(ended.Judgment is { } reason ? " · " + DebateTexts.Judgment(reason, ended.Limit) : string.Empty)}");
                break;
            case DebateJudgeLaunched judge:
                Log(judge.At, judge.Attempt > 1 ? $"Juez {judge.Judge} lanzado para su corrección" : $"Juez {judge.Judge} lanzado");
                break;
            case DebateVerdictReceived received:
                Keep(received.Verdict);
                Log(received.At, $"Veredicto {received.Verdict.Id} de {received.Verdict.Verdict.Judge.Agent}: {VerdictTexts.Decision(received.Verdict.Verdict.Decision)}{(received.Verdict.Violations.Count == 0 ? string.Empty : " · no válido")}");
                break;
            case DebateAwaitingUser waiting:
                Log(waiting.At, $"Espera: {waiting.Awaiting.Message}");
                break;
            case DebateFinished done:
                Finish(run, done.Result);
                return;
        }

        Refresh(SafeSnapshot(run));
    }

    private void Finish(IDebateRun run, DebateResult result)
    {
        if (IsFinished)
        {
            return;
        }

        Refresh(SafeSnapshot(run));
        foreach (var verdict in result.Verdicts)
        {
            Keep(verdict);
        }

        Phase = result.Phase;
        PhaseText = PhaseLine(result.Phase, result.Outcome, interrupted: false);
        PhaseTone = DebateTexts.ToneOf(result.Phase, result.Outcome);
        JudgmentText = JudgmentLine(result.JudgmentReason, result.LimitReached) ?? JudgmentText;
        ResultSummary = result.Summary;
        SealedText = null;
        Awaiting = null;
        AwaitingText = null;
        if (_verdicts.Count > 0 || result.Panel is not null)
        {
            _cardKey = "final";
            Card = BuildCard(result.Panel, _snapshot?.Mode, _snapshot?.Subject, awaitingHuman: false);
        }

        IsFinished = true;
        OnPropertyChanged(nameof(IsLive));
        Log(_time.GetUtcNow(), $"Debate terminado: {DebateTexts.Outcome(result.Outcome)}");
        NotifyCommands();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        if (!IsFinished)
        {
            IsFinished = true;
            OnPropertyChanged(nameof(IsLive));
            NotifyCommands();
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Keep(JudgedVerdict verdict)
    {
        // A correction replaces the verdict of the same judge: the card shows the last one of each judge.
        _verdicts.RemoveAll(v => v.Id == verdict.Id || JudgingPanel.SameJudge(v.Verdict.Judge, verdict.Verdict.Judge));
        _verdicts.Add(verdict);
    }

    private DebateSnapshot? SafeSnapshot(IDebateRun run)
    {
        try
        {
            return run.Snapshot();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void Refresh(DebateSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        _snapshot = snapshot;
        Title = $"Debate · {DebateTexts.Mode(snapshot.Mode)}";
        SubjectText = DebateTexts.Subject(snapshot.Subject);
        Phase = snapshot.Phase;
        PhaseText = PhaseLine(snapshot.Phase, snapshot.Outcome, interrupted: false);
        PhaseTone = DebateTexts.ToneOf(snapshot.Phase, snapshot.Outcome);
        RoundText = string.Create(CultureInfo.InvariantCulture, $"Ronda {snapshot.Round} de {snapshot.Limits.MaxRounds}");
        TimeText = $"Tiempo: {MetricText.DurationText((long)snapshot.Elapsed.TotalMilliseconds)} de {MetricText.DurationText((long)snapshot.Limits.MaxDuration.TotalMilliseconds)} · {MetricText.Origin(MetricOrigin.MeasuredBySystem)} (reloj del sistema)";
        TokensText = TokensLine(snapshot);
        RunsText = $"Ejecuciones: {MetricText.Integer(snapshot.AgentRuns)} de {MetricText.Integer(snapshot.MaxAgentRuns)} · {MetricText.Origin(MetricOrigin.MeasuredBySystem)}";
        var partial = snapshot.Tokens.Count > 0 && snapshot.ReportedTurns < snapshot.Tokens.Count
            ? string.Create(CultureInfo.InvariantCulture, $"parcial ({snapshot.ReportedTurns} de {snapshot.Tokens.Count} turnos informados)")
            : null;
        TokensBar = MetricBars.Tokens("Tokens del debate", snapshot.Tokens, snapshot.Limits.MaxTotalTokens, partial);
        RunsBar = MetricBars.Budget("Ejecuciones", snapshot.AgentRuns, snapshot.MaxAgentRuns, "ejecuciones de agente del debate frente a su máximo");
        SetParticipants(snapshot.Participants);
        SetContributions(snapshot.Contributions);
        SealedText = snapshot.TurnsInFlight switch
        {
            <= 0 => null,
            1 => string.Create(CultureInfo.InvariantCulture, $"Ronda {snapshot.Round}: 1 turno en curso, sellado hasta que termine"),
            var turns => string.Create(CultureInfo.InvariantCulture, $"Ronda {snapshot.Round}: {turns} turnos en curso, sellados hasta que terminen todos"),
        };
        Awaiting = snapshot.Awaiting;
        AwaitingText = snapshot.Awaiting is { } waiting ? AwaitingLine(waiting) : null;
        JudgmentText = JudgmentLine(snapshot.JudgmentReason, snapshot.LimitReached);
        CanRunTests = snapshot.CanRunTests;
        Replace(Problems, snapshot.Problems);
        if (snapshot.Awaiting is { Reason: DebateAwaitReason.HumanResolution } && !IsFinished)
        {
            // Rebuilt only when the verdicts change, so what you are typing in «Decidir yo» is not lost on the next event.
            var key = "awaiting|" + string.Join(",", _verdicts.Select(v => v.Id));
            if (!string.Equals(key, _cardKey, StringComparison.Ordinal))
            {
                _cardKey = key;
                Card = BuildCard(null, snapshot.Mode, snapshot.Subject, awaitingHuman: true, pending: DebateTexts.Outcome(snapshot.Outcome));
            }
        }

        NotifyCommands();
    }

    private void ApplyContext()
    {
        Question = _context.Question;
        RequirementsText = _context.Requirements is { } requirements
            ? $"Requisitos congelados: {DebateTexts.Short(requirements.ContentHash)} ({DebateTexts.Count(requirements.Requirements.Count, "requisito", "requisitos")})"
            : null;
        var judges = (_context.Judges ?? []).Select(VerdictPanelViewModel.JudgeLine).ToList();
        JudgesText = judges.Count == 0
            ? _context.Judging is { } kind ? $"{DebateTexts.Judging(kind)} ({(kind == DebateJudgingKind.CrossVendorPanel ? "los jueces no participan" : "el juez no participa")})" : null
            : $"{(judges.Count == 1 ? "Juez" : "Jurado")}: {string.Join(" | ", judges)} (no participa{(judges.Count == 1 ? string.Empty : "n")})";
    }

    private void SetParticipants(IReadOnlyList<DebateParticipantView> participants)
    {
        _participants = participants;
        var rows = participants.Select(p => new DebateParticipantRow(
            p.Label,
            DebateTexts.Role(p.Role),
            p.Agent,
            $"{p.Agent} · {DebateTexts.Provider(p.Provider)} · {DebateTexts.Vendor(p.Vendor)} · {(string.IsNullOrWhiteSpace(p.Model) ? "modelo predeterminado del CLI" : p.Model)}")).ToList();
        if (!rows.SequenceEqual(Participants))
        {
            Replace(Participants, rows);
            RetryTurnCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetContributions(IReadOnlyList<DebateContributionView> contributions)
    {
        static (string, string, bool) Key(DebateContributionView c) => (c.Label, c.Sha256, c.Text is not null);
        var shown = Rounds.SelectMany(r => r.Contributions).Select(c => Key(c.View)).ToList();
        if (shown.SequenceEqual(contributions.Select(Key)))
        {
            return;
        }

        var byLabel = _participants.ToDictionary(p => p.Label, StringComparer.Ordinal);
        var zone = _time.LocalTimeZone;
        Replace(Rounds, contributions
            .OrderBy(c => c.Index)
            .GroupBy(c => c.Round)
            .OrderBy(g => g.Key)
            .Select(g => new DebateRoundViewModel(
                g.Key,
                g.Key == 1 ? "Ronda 1 · propuestas independientes, selladas hasta el final de la ronda" : string.Create(CultureInfo.InvariantCulture, $"Ronda {g.Key}"),
                [.. g.Select(c => new DebateContributionViewModel(c, byLabel, zone))])));
    }

    private VerdictCardViewModel BuildCard(PanelOutcome? outcome, DebateMode? mode, DebateSubjectKind? subject, bool awaitingHuman, string? pending = null)
    {
        var decide = awaitingHuman && _run is { } run && !IsFinished
            ? (Func<VerdictDecision?, string?, string, Task>)((decision, proposal, comment) => run.DecideAsync(decision, proposal, comment))
            : null;
        var context = new VerdictCardContext(
            DebateId is { } id ? $"Veredicto del debate {id}" : "Veredicto del debate",
            _context.Judging,
            _context.WaivedVendors,
            _participants,
            _context.Requirements,
            mode is { } m && subject is { } s && DebateJudgingRules.IsChoice(m, s),
            Proposals: [.. _participants.Select(p => p.Label)],
            AwaitingHuman: awaitingHuman,
            PendingOutcome: pending);
        return new VerdictCardViewModel([.. _verdicts], outcome, context, _time.LocalTimeZone, new VerdictCardActions(_challenge, decide, _correctiveTasks));
    }

    private string AwaitingLine(DebateAwaiting waiting)
    {
        var who = waiting.Participant is { Length: > 0 } participant ? $" · {participant}" : string.Empty;
        var until = waiting.Until is { } at ? " · hasta las " + TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        return $"{waiting.Message}{who}{until}. {DebateTexts.Await(waiting.Reason)}";
    }

    private static string TokensLine(DebateSnapshot snapshot)
    {
        var figure = MetricText.Tokens(snapshot.Tokens);
        var limit = snapshot.Limits.MaxTotalTokens is { } max ? " de " + MetricText.Integer(max) : " (sin límite de tokens)";
        var counted = snapshot.Tokens.Count;
        var partial = counted > 0 && snapshot.ReportedTurns < counted
            ? string.Create(CultureInfo.InvariantCulture, $" · parcial ({snapshot.ReportedTurns} de {counted} informados)")
            : string.Empty;
        return $"Tokens: {figure.Shown}{limit} · {figure.Origin}{partial}";
    }

    private static string PhaseLine(DebatePhase phase, DebateOutcome outcome, bool interrupted) =>
        interrupted ? $"Interrumpido · {DebateTexts.Phase(phase)}"
        : phase == DebatePhase.Closed || outcome != DebateOutcome.Open ? $"{DebateTexts.Phase(phase)} · {DebateTexts.Outcome(outcome)}"
        : DebateTexts.Phase(phase);

    private static string? JudgmentLine(DebateJudgmentReason? reason, DebateLimitReached limit) =>
        reason is { } why ? "A juicio porque " + DebateTexts.Judgment(why, limit) + "." : null;

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsAwaitingFailedTurn));
        RequestJudgmentCommand.NotifyCanExecuteChanged();
        RunTestsCommand.NotifyCanExecuteChanged();
        AddEvidenceCommand.NotifyCanExecuteChanged();
        RetryTurnCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
    }

    private void Log(DateTimeOffset at, string text)
    {
        var local = TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);
        Activity.Add(new ActivityEntry(at, local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), null, text));
        while (Activity.Count > MaxActivity)
        {
            Activity.RemoveAt(0);
        }
    }

    private void Reset()
    {
        Participants.Clear();
        Rounds.Clear();
        Activity.Clear();
        Problems.Clear();
        _verdicts.Clear();
        _participants = [];
        _snapshot = null;
        _project = null;
        _context = new DebateRoomContext();
        DebateId = null;
        IsTranscript = false;
        IsFinished = false;
        Title = "Sin debate";
        SubjectText = null;
        Question = null;
        RequirementsText = null;
        JudgesText = null;
        Phase = null;
        PhaseText = "Sin debate";
        PhaseTone = Tone.Neutral;
        RoundText = null;
        TimeText = null;
        TokensText = null;
        RunsText = null;
        TokensBar = null;
        RunsBar = null;
        SealedText = null;
        Awaiting = null;
        AwaitingText = null;
        JudgmentText = null;
        CanRunTests = false;
        Card = null;
        _cardKey = null;
        OpenedEvidence = null;
        ResultSummary = null;
        StatusMessage = null;
        ErrorMessage = null;
        Listening = Task.CompletedTask;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
