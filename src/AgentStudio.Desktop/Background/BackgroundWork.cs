using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Background;

/// <summary>What a piece of work with agents of this process is (docs/15 D-05): the activities a project can hold.</summary>
public enum BackgroundWorkKind
{
    /// <summary>A run of the orchestrator.</summary>
    Run,

    Debate,

    /// <summary>«Auditar proyecto completo».</summary>
    Audit,

    /// <summary>The orchestrator agent planning a run.</summary>
    Planning,
}

/// <summary>
/// One piece of work with agents going on in this process, as the tray, its panel, the managed exit and «mantener
/// despierto» follow it (docs/16 D-14 to D-16 and D-40). Everything is read from what already exists: the monitor of a run,
/// the snapshot of a debate, or <see cref="IProjectActivity"/> for what has no screen to follow here.
/// <see cref="Changed"/> is raised on the UI thread.
/// </summary>
public interface IBackgroundWork
{
    BackgroundWorkKind Kind { get; }

    /// <summary>The folder of the project, as the shell opened it.</summary>
    string ProjectRoot { get; }

    /// <summary>The project once its storage was opened (its id is what the log and <see cref="IProjectActivity"/> use); null before.</summary>
    ProjectContext? Project { get; }

    /// <summary>The id of the run, the debate or the audit, which is where the managed exit writes its entry; null while it is not known here.</summary>
    string? WorkId { get; }

    /// <summary>The work is going on in this process (it holds its project), paused or waiting included.</summary>
    bool IsActive { get; }

    /// <summary>How many attempts of agents are running now; null when this process cannot tell (an audit, a planning).</summary>
    int? AttemptsInFlight { get; }

    /// <summary>Whether this kind of work can be paused, so that nothing new starts (only a run of the orchestrator can).</summary>
    bool IsPausable { get; }

    /// <summary>«Ejecución 20261021-ab12cd», «Debate deb-…», «Auditoría del proyecto».</summary>
    string Title { get; }

    /// <summary>Its state in words: «En ejecución · 2 intentos en curso».</summary>
    string StateText { get; }

    Tone Tone { get; }

    /// <summary>Stops dispatching new work (a run); what is running goes on. Does nothing for work that cannot be paused.</summary>
    Task PauseAsync();

    event EventHandler? Changed;
}

/// <summary>Texts shared by the work items.</summary>
internal static class WorkTexts
{
    public static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    public static string ProjectName(string projectRoot) => RecentProjectItem.From(projectRoot).Name;
}

/// <summary>
/// The run an «Orquestador» document follows, read from its monitor (<see cref="RunMonitorViewModel"/>): the document lives
/// for the session and its monitor follows one run after another, so one of these follows the project's runs. It is
/// active while the monitor follows a run that has not finished.
/// </summary>
public sealed class RunWork : IBackgroundWork, IDisposable
{
    private static readonly HashSet<string> MonitorProperties = new(StringComparer.Ordinal)
    {
        nameof(RunMonitorViewModel.IsActive),
        nameof(RunMonitorViewModel.IsFinished),
        nameof(RunMonitorViewModel.RunId),
        nameof(RunMonitorViewModel.Phase),
        nameof(RunMonitorViewModel.PhaseDetail),
        nameof(RunMonitorViewModel.ErrorMessage),
        nameof(RunMonitorViewModel.AgentRunsUsed),
        nameof(RunMonitorViewModel.Health),
    };

    private static readonly HashSet<string> TaskProperties = new(StringComparer.Ordinal)
    {
        nameof(MonitorTaskViewModel.Status),
        nameof(MonitorTaskViewModel.QueuedOn),
        nameof(MonitorTaskViewModel.IsQueued),
        nameof(MonitorTaskViewModel.BlockReason),
        nameof(MonitorTaskViewModel.PendingApproval),
        nameof(MonitorTaskViewModel.Agent),
        nameof(MonitorTaskViewModel.Context),
    };

    private readonly Func<ProjectContext?> _project;
    private readonly HashSet<MonitorTaskViewModel> _watched = [];

    /// <param name="projectRoot">The folder of the project of the document.</param>
    /// <param name="monitor">The monitor of the document, on the UI thread.</param>
    /// <param name="project">The project of the document once it was opened (<c>OrchestratorDocumentViewModel.Context?.Project</c>).</param>
    public RunWork(string projectRoot, RunMonitorViewModel monitor, Func<ProjectContext?> project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(project);
        ProjectRoot = projectRoot;
        Monitor = monitor;
        _project = project;
        monitor.PropertyChanged += OnMonitorChanged;
        monitor.Tasks.CollectionChanged += OnTasksChanged;
        foreach (var task in monitor.Tasks)
        {
            Watch(task);
        }
    }

    public BackgroundWorkKind Kind => BackgroundWorkKind.Run;

    public string ProjectRoot { get; }

    /// <summary>The monitor of the run: phase, tasks, context, block reasons, pending approvals and commands.</summary>
    public RunMonitorViewModel Monitor { get; }

    public ProjectContext? Project => _project();

    public string? WorkId => Monitor.RunId?.Value;

    public bool IsActive => Monitor.IsActive;

    /// <summary>
    /// The attempts launched (<see cref="WorkTaskStatus.Running"/>) and those dispatched but not launched yet: the engine
    /// says a task is queued (<see cref="TaskQueuedEvent"/>) as soon as it dispatches it, and from then on its worker may be
    /// preparing the worktree or already handing the prompt to the CLI while the task still shows as pending.
    /// </summary>
    public int? AttemptsInFlight => IsActive ? Monitor.Tasks.Count(t => t.Status == WorkTaskStatus.Running || t.IsQueued) : 0;

    public bool IsPausable => true;

    public string Title => Monitor.RunId is { } run ? $"Ejecución {run}" : "Ejecución";

    public string StateText => AttemptsInFlight is > 0 and var running
        ? $"{Monitor.PhaseLabel} · {WorkTexts.Count(running, "intento en curso", "intentos en curso")}"
        : Monitor.PhaseLabel;

    public Tone Tone => Monitor.PhaseTone;

    /// <summary>Whether «Pausar» can be asked now (the run is going or waiting for quota).</summary>
    public bool CanPause => Monitor.PauseCommand.CanExecute(null);

    /// <summary>Whether «Reanudar» can be asked now (the run is paused).</summary>
    public bool CanResume => Monitor.ResumeCommand.CanExecute(null);

    /// <summary>Whether «Detener» can be asked now (the run has not finished).</summary>
    public bool CanCancel => Monitor.CancelCommand.CanExecute(null);

    public event EventHandler? Changed;

    /// <summary>
    /// «Pausar», as the monitor asks it: nothing new is dispatched, and what is running goes on. When the run did not take it (it
    /// had ended, or stopped before the pause was in its log), it throws <see cref="InvalidOperationException"/>, so whoever asked
    /// (the tray, the managed exit) knows; the monitor says why in Spanish (<see cref="RunMonitorViewModel.ErrorMessage"/>), and
    /// the tray says it with a notice of its own.
    /// </summary>
    public async Task PauseAsync()
    {
        if (CanPause && !await Monitor.TryPauseAsync().ConfigureAwait(true))
        {
            throw new InvalidOperationException($"Run {Monitor.RunId} did not take the pause: it had ended, or stopped before the pause was in its log.");
        }
    }

    /// <summary>«Reanudar», as the monitor asks it; throws, as <see cref="PauseAsync"/>, when the run did not take it.</summary>
    public async Task ResumeAsync()
    {
        if (CanResume && !await Monitor.TryResumeAsync().ConfigureAwait(true))
        {
            throw new InvalidOperationException($"Run {Monitor.RunId} did not take the resume: it had ended, or stopped before the resume was in its log.");
        }
    }

    /// <summary>«Detener»: the monitor's «Cancelar ejecución»; throws, as <see cref="PauseAsync"/>, when the run did not take it.</summary>
    public async Task CancelAsync()
    {
        if (CanCancel && !await Monitor.TryCancelAsync().ConfigureAwait(true))
        {
            throw new InvalidOperationException($"Run {Monitor.RunId} did not take the cancellation: it had ended, or stopped before the cancellation was in its log.");
        }
    }

    public void Dispose()
    {
        Monitor.PropertyChanged -= OnMonitorChanged;
        Monitor.Tasks.CollectionChanged -= OnTasksChanged;
        foreach (var task in _watched)
        {
            task.PropertyChanged -= OnTaskChanged;
        }

        _watched.Clear();
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || MonitorProperties.Contains(e.PropertyName))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var task in _watched)
            {
                task.PropertyChanged -= OnTaskChanged;
            }

            _watched.Clear();
            foreach (var task in Monitor.Tasks)
            {
                Watch(task);
            }
        }
        else
        {
            foreach (var task in e.OldItems?.OfType<MonitorTaskViewModel>() ?? [])
            {
                task.PropertyChanged -= OnTaskChanged;
                _watched.Remove(task);
            }

            foreach (var task in e.NewItems?.OfType<MonitorTaskViewModel>() ?? [])
            {
                Watch(task);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Watch(MonitorTaskViewModel task)
    {
        if (_watched.Add(task))
        {
            task.PropertyChanged += OnTaskChanged;
        }
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || TaskProperties.Contains(e.PropertyName))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// A debate going on, read from its run (<see cref="IDebateRun"/>): its snapshot says the phase, the round, the turns in
/// course, the tokens and the agent runs; its completion says it ended. Its events have a single reader (the room), so the
/// registry asks the snapshot again when it polls. A debate cannot be paused.
/// </summary>
public sealed class DebateWork : IBackgroundWork
{
    private readonly IDebateRun _run;
    private readonly SynchronizationContext? _ui;

    /// <param name="projectRoot">The folder of the project of the debate.</param>
    /// <param name="project">The project of the debate.</param>
    /// <param name="run">The debate, as the coordinator opened or resumed it.</param>
    /// <param name="uiContext">Where <see cref="Changed"/> is raised; defaults to the creating thread's context.</param>
    public DebateWork(string projectRoot, ProjectContext project, IDebateRun run, SynchronizationContext? uiContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(run);
        ProjectRoot = projectRoot;
        Project = project;
        _run = run;
        _ui = uiContext ?? SynchronizationContext.Current;
        _ = run.Completion.ContinueWith(_ => Post(() => Changed?.Invoke(this, EventArgs.Empty)), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public BackgroundWorkKind Kind => BackgroundWorkKind.Debate;

    public string ProjectRoot { get; }

    public ProjectContext? Project { get; }

    public string? WorkId => _run.Id.Value;

    public bool IsActive => !_run.Completion.IsCompleted;

    public int? AttemptsInFlight => IsActive ? Snapshot()?.TurnsInFlight : 0;

    public bool IsPausable => false;

    public string Title => $"Debate {_run.Id}";

    public string StateText => Snapshot() is { } snapshot
        ? snapshot.TurnsInFlight > 0
            ? $"{DebateTexts.Phase(snapshot.Phase)} · ronda {snapshot.Round.ToString(CultureInfo.InvariantCulture)} · {WorkTexts.Count(snapshot.TurnsInFlight, "turno en curso", "turnos en curso")}"
            : $"{DebateTexts.Phase(snapshot.Phase)} · ronda {snapshot.Round.ToString(CultureInfo.InvariantCulture)}"
        : IsActive ? "En curso" : "Terminado";

    public Tone Tone => Snapshot() is { } snapshot ? DebateTexts.ToneOf(snapshot.Phase, snapshot.Outcome) : Tone.Neutral;

    public event EventHandler? Changed;

    /// <summary>The debate as it is now; null when it cannot be read (it was released).</summary>
    public DebateSnapshot? Snapshot()
    {
        try
        {
            return _run.Snapshot();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    public Task PauseAsync() => Task.CompletedTask;

    /// <summary>What the registry compares when it polls: a change raises <see cref="Changed"/>.</summary>
    internal string Signature() => Snapshot() is { } s
        ? string.Create(CultureInfo.InvariantCulture, $"{s.Phase}|{s.Outcome}|{s.Round}|{s.TurnsInFlight}|{s.Awaiting?.Reason}|{IsActive}")
        : IsActive.ToString();

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}

/// <summary>
/// A run the scheduler resumed by the consent of its header (docs/16 D-39), followed from the moment it is announced, on the
/// scheduler's thread, until the monitor of its «Orquestador» takes it on the UI thread, when the shell forgets this one: the
/// managed exit lists it, pauses it and writes its choice in it, and so does the end of the session, whatever the dispatcher
/// runs first and without waiting for the UI thread. It reads the run itself, whose snapshot any thread may ask; it has no
/// events of its own (the registry's polls see it change).
/// </summary>
public sealed class ResumedRunWork : IBackgroundWork
{
    /// <param name="projectRoot">The folder of the project of the run.</param>
    /// <param name="project">The project of the run.</param>
    /// <param name="run">The run, going: its events wait for the monitor that will read them.</param>
    public ResumedRunWork(string projectRoot, ProjectContext project, IOrchestrationRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(run);
        ProjectRoot = projectRoot;
        Project = project;
        Run = run;
    }

    public BackgroundWorkKind Kind => BackgroundWorkKind.Run;

    public string ProjectRoot { get; }

    public ProjectContext? Project { get; }

    public IOrchestrationRun Run { get; }

    public string? WorkId => Run.Id.Value;

    public bool IsActive => !Run.Completion.IsCompleted;

    public int? AttemptsInFlight => IsActive ? Snapshot()?.Tasks.Count(t => t.Status == WorkTaskStatus.Running) : 0;

    public bool IsPausable => true;

    public string Title => $"Ejecución {Run.Id}";

    public string StateText => Snapshot() is { } snapshot ? RunPhaseText.Label(snapshot.Phase) : "Reanudada sola";

    public Tone Tone => Snapshot() is { } snapshot ? RunPhaseText.ToneOf(snapshot.Phase) : Tone.Active;

    /// <summary>Never raised: the registry's polls see what changed.</summary>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <summary>«Pausar», straight to the run: it fails (<see cref="InvalidOperationException"/>) when the run had ended.</summary>
    public Task PauseAsync() => Run.PauseAsync();

    private RunSnapshot? Snapshot()
    {
        try
        {
            return Run.Snapshot();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }
}

/// <summary>
/// What holds a project in this process when no screen here follows it (an audit, a planning, or a run or a debate that is
/// not tracked), from <see cref="IProjectActivity"/>: it says what kind of activity it is and nothing more, so its id and
/// its attempts are not known here, and it cannot be paused.
/// </summary>
public sealed class ProjectActivityWork : IBackgroundWork
{
    private readonly Func<ProjectContext?> _project;
    private readonly IProjectActivity _activity;

    public ProjectActivityWork(string projectRoot, Func<ProjectContext?> project, IProjectActivity activity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(activity);
        ProjectRoot = projectRoot;
        _project = project;
        _activity = activity;
    }

    /// <summary>What holds the project now; null when nothing does or the project was not opened yet.</summary>
    public ProjectActivityKind? Current => _project() is { } project ? _activity.Current(project.ProjectId) : null;

    public BackgroundWorkKind Kind => Current switch
    {
        ProjectActivityKind.Run => BackgroundWorkKind.Run,
        ProjectActivityKind.Debate => BackgroundWorkKind.Debate,
        ProjectActivityKind.Planning => BackgroundWorkKind.Planning,
        _ => BackgroundWorkKind.Audit,
    };

    public string ProjectRoot { get; }

    public ProjectContext? Project => _project();

    public string? WorkId => null;

    public bool IsActive => Current is not null;

    public int? AttemptsInFlight => IsActive ? null : 0;

    public bool IsPausable => false;

    public string Title => Current switch
    {
        ProjectActivityKind.Run => "Ejecución del orquestador",
        ProjectActivityKind.Debate => "Debate",
        ProjectActivityKind.Planning => "Planificación del orquestador",
        ProjectActivityKind.Audit => "Auditoría del proyecto",
        _ => "Sin actividad",
    };

    public string StateText => IsActive ? "En curso (sin más detalle aquí)" : "Sin actividad";

    public Tone Tone => IsActive ? Tone.Active : Tone.Neutral;

    /// <summary>Never raised: <see cref="IProjectActivity"/> has no events, so the registry's polls see the changes.</summary>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task PauseAsync() => Task.CompletedTask;

    internal string Signature() => Current?.ToString() ?? "-";
}

/// <summary>
/// The work with agents of this process that the tray, its panel, the managed exit and «mantener despierto» follow
/// (docs/16 D-14 to D-16 and D-40). The shell tracks what it knows: the monitor of each «Orquestador» document
/// (<see cref="TrackRun"/>), each debate it opens or resumes (<see cref="TrackDebate"/>), and the projects of its other
/// documents (<see cref="TrackProject"/>), whose activity <see cref="IProjectActivity"/> says. Nothing else is invented.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Changed"/> is raised on the UI thread: when a tracked run or debate says it changed, and when a poll
/// (<see cref="Poll"/>, every few seconds from the clock) finds that a debate or the activity of a project changed, since
/// those have no events of their own here.</item>
/// <item>A project held by work the registry already follows is not listed twice: its activity only counts when nothing
/// tracked of that project is going on.</item>
/// <item>Disposing the token a tracking returned forgets that work.</item>
/// </list>
/// </remarks>
public sealed class BackgroundWorkRegistry : IDisposable
{
    /// <summary>How often a debate and the activity of the projects are read again.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);

    private readonly List<IBackgroundWork> _items = [];

    /// <summary>Work announced on another thread (<see cref="TrackFromAnyThread"/>); the UI thread is its single reader.</summary>
    private readonly ConcurrentQueue<IBackgroundWork> _incoming = new();
    private readonly IProjectActivity? _activity;
    private readonly SynchronizationContext? _ui;
    private readonly ITimer? _timer;
    private string _signature = string.Empty;
    private bool _disposed;

    /// <param name="time">The clock of the polls.</param>
    /// <param name="activity">What holds each project; without it only tracked runs and debates are known.</param>
    /// <param name="uiContext">Where <see cref="Changed"/> is raised; defaults to the creating thread's context.</param>
    /// <param name="pollInterval">How often to poll; <see cref="DefaultPollInterval"/> by default, and <see cref="Timeout.InfiniteTimeSpan"/> for never.</param>
    public BackgroundWorkRegistry(TimeProvider time, IProjectActivity? activity = null, SynchronizationContext? uiContext = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _activity = activity;
        _ui = uiContext ?? SynchronizationContext.Current;
        var interval = pollInterval ?? DefaultPollInterval;
        if (interval != Timeout.InfiniteTimeSpan)
        {
            _timer = time.CreateTimer(_ => Post(Poll), null, interval, interval);
        }
    }

    /// <summary>Raised on the UI thread when what goes on changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Everything tracked, going on or not.</summary>
    public IReadOnlyList<IBackgroundWork> All
    {
        get
        {
            Drain();
            return [.. _items];
        }
    }

    /// <summary>What is going on now, a project counted once (see the remarks of the class).</summary>
    public IReadOnlyList<IBackgroundWork> Active
    {
        get
        {
            Drain();
            var tracked = _items.Where(w => w is not ProjectActivityWork && w.IsActive).ToList();
            var busy = tracked.Select(w => Key(w.ProjectRoot)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return
            [
                .. tracked,
                .. _items.OfType<ProjectActivityWork>().Where(w => w.IsActive && !busy.Contains(Key(w.ProjectRoot))),
            ];
        }
    }

    /// <summary>Whether anything is going on: what closing the window and «mantener despierto» ask.</summary>
    public bool HasActiveWork => Active.Count > 0;

    /// <summary>Follows a piece of work until the returned token is disposed.</summary>
    public IDisposable Track(IBackgroundWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Drain();
        if (_items.Contains(work))
        {
            return new Token(this, work);
        }

        _items.Add(work);
        work.Changed += OnWorkChanged;
        Refresh();
        return new Token(this, work);
    }

    /// <summary>
    /// Follows a piece of work announced on another thread: a run the scheduler resumed, on its own thread, before the suspension
    /// the managed exit asks for is handed over (docs/16 D-39). It is queued, and it is in the registry from the next read on the
    /// UI thread, the single reader of the queue, whatever the dispatcher runs first; nothing here waits for the UI thread, which
    /// may be blocked (the end of the session). <see cref="Changed"/> follows on the UI thread.
    /// </summary>
    public void TrackFromAnyThread(IBackgroundWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _incoming.Enqueue(work);
        Post(() =>
        {
            if (!_disposed)
            {
                Drain();
                Refresh();
            }
        });
    }

    /// <summary>Forgets a piece of work tracked without a token (<see cref="TrackFromAnyThread"/>); on the UI thread.</summary>
    public void Untrack(IBackgroundWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Drain();
        Forget(work);
    }

    /// <summary>
    /// Follows the runs of an «Orquestador» document through its monitor, and the activity of its project (an audit or a
    /// debate of the same project, say). The token forgets the run; the activity of the project stays followed.
    /// </summary>
    public IDisposable TrackRun(string projectRoot, RunMonitorViewModel monitor, Func<ProjectContext?> project)
    {
        var tracked = Track(new RunWork(projectRoot, monitor, project));
        TrackProject(projectRoot, project);
        return tracked;
    }

    /// <summary>Follows a debate the shell opened or resumed, until it is forgotten.</summary>
    public IDisposable TrackDebate(string projectRoot, ProjectContext project, IDebateRun run) =>
        Track(new DebateWork(projectRoot, project, run, _ui));

    /// <summary>
    /// Follows what holds a project (<see cref="IProjectActivity"/>), once per folder and for the rest of the session (the
    /// documents of a project live as long): its audits and plannings, and runs or debates no screen here follows. Without
    /// an activity it follows nothing.
    /// </summary>
    public void TrackProject(string projectRoot, Func<ProjectContext?> project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(project);
        Drain();
        if (_activity is null || _items.OfType<ProjectActivityWork>().Any(w => string.Equals(Key(w.ProjectRoot), Key(projectRoot), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Track(new ProjectActivityWork(projectRoot, project, _activity));
    }

    /// <summary>Reads the debates and the activity of the projects again, and raises <see cref="Changed"/> if anything changed.</summary>
    public void Poll()
    {
        if (_disposed)
        {
            return;
        }

        Drain();
        var signature = Signature();
        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            _signature = signature;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        foreach (var work in _items)
        {
            work.Changed -= OnWorkChanged;
            (work as IDisposable)?.Dispose();
        }

        _items.Clear();
    }

    private void OnWorkChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// Takes in what was announced on another thread, on the UI thread and without raising anything (a read may be in course):
    /// the post of <see cref="TrackFromAnyThread"/> says it changed.
    /// </summary>
    private void Drain()
    {
        while (_incoming.TryDequeue(out var work))
        {
            if (!_disposed && !_items.Contains(work))
            {
                _items.Add(work);
                work.Changed += OnWorkChanged;
            }
        }
    }

    private void Refresh()
    {
        _signature = Signature();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Forget(IBackgroundWork work)
    {
        if (_items.Remove(work))
        {
            work.Changed -= OnWorkChanged;
            (work as IDisposable)?.Dispose();
            Refresh();
        }
    }

    private string Signature() => string.Join(
        ";",
        _items.Select(w => w switch
        {
            DebateWork debate => $"{debate.WorkId}:{debate.Signature()}",
            ProjectActivityWork activity => $"{activity.ProjectRoot}:{activity.Signature()}",
            _ => $"{w.Kind}:{w.WorkId}:{w.IsActive}:{w.AttemptsInFlight}",
        }));

    private static string Key(string projectRoot) => projectRoot.TrimEnd('\\', '/');

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    /// <summary>Forgets one piece of work, once.</summary>
    private sealed class Token(BackgroundWorkRegistry owner, IBackgroundWork work) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Forget(work);
            }
        }
    }
}
