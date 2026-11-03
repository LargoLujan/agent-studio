using System.IO;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.ViewModels;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// What the tray asks of the shell (the integration gives them over <c>MainViewModel</c> and the lock of WP4); any may be
/// missing, and then its entry of the menu is shown disabled.
/// </summary>
/// <param name="OpenProject">Opens a recent project in the window.</param>
/// <param name="OpenFolder">«Abrir carpeta…»: the shell's folder picker.</param>
/// <param name="Lock">«Bloquear»: the lock of the interface (docs/16 D-13), which leaves the work going.</param>
/// <param name="RecentProjects">The recent projects, most recent first (the settings).</param>
public sealed record TrayShellActions(
    Func<string, Task>? OpenProject = null,
    Func<Task>? OpenFolder = null,
    Func<Task>? Lock = null,
    Func<IReadOnlyList<string>>? RecentProjects = null)
{
    public static TrayShellActions None { get; } = new();
}

/// <summary>
/// The tray of the app (docs/16 D-14, F8-7): the icon while the app lives, its tooltip, its menu and its notifications.
/// <list type="bullet">
/// <item>The tooltip (<see cref="TrayToolTip"/>) follows the work going on and the managed exit.</item>
/// <item>A click with the left button shows or hides the status panel; the right button opens the menu: open the window,
/// the panel, a project and the logs; pause, resume or stop each run; lock; and exit (through the managed exit).</item>
/// <item>A notification says that a run ended, asks for an approval or stopped waiting for quota, and that a debate
/// ended: with a title and a text and nothing of the project. The first look at a piece of work says nothing.</item>
/// <item>While the interface is locked (<see cref="IsLocked"/>, D-13) the menu names no project and offers nothing on the
/// work, which goes on; opening the window shows the lock screen.</item>
/// <item>The window is only ever opened through <see cref="ManagedExit.ShowWindow"/>: while the app waits to exit, opening it
/// cancels that exit, and the tray says so.</item>
/// <item>The controller owns the icon: disposing it takes the icon out of the notification area.</item>
/// </list>
/// Everything runs on the UI thread.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly ITrayIcon _tray;
    private readonly BackgroundWorkRegistry _work;
    private readonly ManagedExit _exit;
    private readonly IStatusPanelPresenter _panel;
    private readonly TrayShellActions _actions;
    private readonly IFolderOpener _folders;
    private readonly string _logsFolder;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly Dictionary<IBackgroundWork, WorkStatus> _seen = [];
    private bool _locked;
    private bool _started;
    private bool _disposed;

    /// <param name="logsFolder">The folder of the app's logs (<c>%LOCALAPPDATA%\AgentStudio\logs</c>).</param>
    public TrayController(
        ITrayIcon tray,
        BackgroundWorkRegistry work,
        ManagedExit exit,
        IStatusPanelPresenter panel,
        TrayShellActions actions,
        IFolderOpener folders,
        string logsFolder,
        TimeProvider time,
        ILogger<TrayController>? log = null)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsFolder);
        ArgumentNullException.ThrowIfNull(time);
        _tray = tray;
        _work = work;
        _exit = exit;
        _panel = panel;
        _actions = actions;
        _folders = folders;
        _logsFolder = logsFolder;
        _time = time;
        _log = log;
    }

    /// <summary>What the tray says when a run did not take «Pausar», «Reanudar» or «Detener» of its menu: nothing of the project.</summary>
    public const string RunCommandNotApplied =
        "La ejecución ya había terminado, se paró o cambió de estado antes de aplicarlo, y no se aplicó. Abre Agent Studio para ver en qué quedó.";

    /// <summary>The tooltip as it is now.</summary>
    public string ToolTip { get; private set; } = "Agent Studio";

    /// <summary>The interface is locked (D-13): the work goes on, and the tray hides what it is.</summary>
    public bool IsLocked
    {
        get => _locked;
        set
        {
            if (_locked != value)
            {
                _locked = value;
                Refresh();
            }
        }
    }

    /// <summary>The default folder of the logs of the app, the one the file logger writes to.</summary>
    public static string DefaultLogsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "logs");

    /// <summary>Puts the icon in the notification area, with its tooltip and its menu, and starts following the work.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _tray.SetMenu(Menu);
        _tray.Clicked += OnClicked;
        _tray.NotificationClicked += OnNotificationClicked;
        _work.Changed += OnChanged;
        _exit.StateChanged += OnChanged;
        _exit.PendingExitCancelled += OnPendingExitCancelled;
        Refresh();
        _tray.Show();
    }

    /// <summary>What the menu holds now (it is built again each time it opens).</summary>
    public IReadOnlyList<TrayMenuItem> Menu()
    {
        var items = new List<TrayMenuItem>
        {
            new("Abrir Agent Studio", ShowWindowAsync),
            new(_panel.IsShown ? "Ocultar el estado del trabajo" : "Ver el estado del trabajo", TogglePanelAsync),
            TrayMenuItem.Separator,
            OpenProjectItem(),
            new("Abrir los registros", OpenLogsAsync, IsEnabled: !IsLocked),
            TrayMenuItem.Separator,
        };

        items.AddRange(WorkItems());
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(IsLocked ? "Bloqueado" : "Bloquear", _actions.Lock, IsEnabled: _actions.Lock is not null && !IsLocked));

        // Locked, leaving the work is a decision on it like any other: it waits for the person to unlock.
        if (_exit.State == ExitState.WaitingForIdle)
        {
            items.Add(new TrayMenuItem("Salir ahora y dejarlas interrumpidas", _exit.ExitNowAsync, IsEnabled: !IsLocked));
            items.Add(new TrayMenuItem("No salir", () =>
            {
                _exit.CancelPendingExit();
                return Task.CompletedTask;
            }, IsEnabled: !IsLocked));
        }
        else
        {
            items.Add(new TrayMenuItem("Salir", ExitAsync, IsEnabled: _exit.State == ExitState.None && !IsLocked));
        }

        return items;
    }

    /// <summary>Updates the tooltip and says what changed in the work (the notifications).</summary>
    public void Refresh()
    {
        var statuses = _work.Active.Select(WorkStatus.Of).ToList();
        ToolTip = TrayToolTip.Compose(statuses, _exit.State, _exit.AttemptsLeft, IsLocked, _time.LocalTimeZone);
        _tray.SetToolTip(ToolTip);
        Notify();
    }

    /// <summary>Stops following the work and takes the icon out of the notification area, whatever the shell does afterwards.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _started = false;
            _tray.Clicked -= OnClicked;
            _tray.NotificationClicked -= OnNotificationClicked;
            _work.Changed -= OnChanged;
            _exit.StateChanged -= OnChanged;
            _exit.PendingExitCancelled -= OnPendingExitCancelled;
        }

        _tray.Dispose();
    }

    private TrayMenuItem OpenProjectItem()
    {
        if (IsLocked)
        {
            return new TrayMenuItem("Abrir proyecto", IsEnabled: false);
        }

        var children = new List<TrayMenuItem>();
        if (_actions.OpenProject is { } open)
        {
            foreach (var path in (_actions.RecentProjects?.Invoke() ?? []).Take(10))
            {
                children.Add(new TrayMenuItem(RecentProjectItem.From(path).Name, async () =>
                {
                    _exit.ShowWindow();
                    await open(path).ConfigureAwait(true);
                }));
            }
        }

        if (_actions.OpenFolder is { } folder)
        {
            if (children.Count > 0)
            {
                children.Add(TrayMenuItem.Separator);
            }

            children.Add(new TrayMenuItem("Abrir carpeta…", async () =>
            {
                _exit.ShowWindow();
                await folder().ConfigureAwait(true);
            }));
        }

        return new TrayMenuItem("Abrir proyecto", IsEnabled: children.Count > 0, Children: children);
    }

    /// <summary>One submenu per run going on, with «Pausar», «Reanudar» and «Detener»; the other work only says what it is.</summary>
    private IEnumerable<TrayMenuItem> WorkItems()
    {
        var active = _work.Active;
        if (active.Count == 0)
        {
            yield return new TrayMenuItem("Sin trabajo en marcha", IsEnabled: false);
            yield break;
        }

        if (IsLocked)
        {
            yield return new TrayMenuItem(
                $"Bloqueado: {WorkTexts.Count(active.Count, "trabajo en marcha", "trabajos en marcha")}; desbloquea para {(active.Count == 1 ? "verlo" : "verlos")}",
                IsEnabled: false);
            yield break;
        }

        var exiting = _exit.State != ExitState.None;
        foreach (var work in active)
        {
            var text = $"{WorkTexts.ProjectName(work.ProjectRoot)} · {work.Title} · {work.StateText}";
            if (work is RunWork run)
            {
                yield return new TrayMenuItem(text, Children:
                [
                    new TrayMenuItem("Pausar", OnRun(() => run.CanPause, run.PauseAsync, "No se pudo pausar"), IsEnabled: !exiting && run.CanPause),
                    new TrayMenuItem("Reanudar", OnRun(() => run.CanResume, run.ResumeAsync, "No se pudo reanudar"), IsEnabled: !exiting && run.CanResume),
                    new TrayMenuItem("Detener (cancela la ejecución)", OnRun(() => run.CanCancel, run.CancelAsync, "No se pudo detener"), IsEnabled: !exiting && run.CanCancel),
                ]);
            }
            else
            {
                yield return new TrayMenuItem(text, IsEnabled: false);
            }
        }
    }

    /// <summary>
    /// A command of the menu on a run, said back when the run did not take it: it could no longer take it when the person clicked
    /// (it ended or changed since the menu opened), it had ended or stopped before applying it, or it failed. The window may be
    /// hidden, so the tray says so itself, with nothing of the project; the monitor keeps the detail. A pause that does not reach
    /// the log would let the run resume by itself after a close (docs/16 D-39): the person must know.
    /// </summary>
    private Func<Task> OnRun(Func<bool> canNow, Func<Task> command, string failed) => async () =>
    {
        if (!canNow())
        {
            _tray.Notify(new TrayNotification(failed, RunCommandNotApplied));
            return;
        }

        try
        {
            await command().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A command of the menu must never bring the app down: whatever went wrong is said and logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.LogWarning(ex, "A command of the tray menu was not taken by its run.");
            _tray.Notify(new TrayNotification(failed, RunCommandNotApplied));
        }
    };

    private Task ShowWindowAsync()
    {
        _exit.ShowWindow();
        return Task.CompletedTask;
    }

    private Task TogglePanelAsync()
    {
        _panel.Toggle();
        return Task.CompletedTask;
    }

    private Task OpenLogsAsync()
    {
        try
        {
            _folders.Open(_logsFolder);
        }
#pragma warning disable CA1031 // Opening a folder must never break the tray: the failure goes to the app's log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.LogWarning(ex, "The folder of the logs could not be opened.");
        }

        return Task.CompletedTask;
    }

    private async Task ExitAsync()
    {
        try
        {
            await _exit.ExitAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The exit is asked from a menu: a failure goes to the app's log and the app stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.LogError(ex, "The managed exit failed.");
        }
    }

    private void OnClicked(object? sender, EventArgs e) => _panel.Toggle();

    private void OnNotificationClicked(object? sender, EventArgs e) => _exit.ShowWindow();

    private void OnPendingExitCancelled(object? sender, string notice) => _tray.Notify(new TrayNotification("Agent Studio ya no sale", notice));

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Compares each piece of work with how it was seen last, and says what the person should know.</summary>
    private void Notify()
    {
        var all = _work.All;
        foreach (var gone in _seen.Keys.Where(w => !all.Contains(w)).ToList())
        {
            _seen.Remove(gone);
        }

        foreach (var work in all)
        {
            var now = WorkStatus.Of(work);
            var before = _seen.GetValueOrDefault(work);
            _seen[work] = now;
            if (before is null || _exit.State == ExitState.Exiting || !string.Equals(before.WorkId, now.WorkId, StringComparison.Ordinal))
            {
                continue;
            }

            if (Notification(work, before, now) is { } notification)
            {
                _tray.Notify(notification);
            }
        }
    }

    private static TrayNotification? Notification(IBackgroundWork work, WorkStatus before, WorkStatus now)
    {
        if (before.IsActive && !now.IsActive)
        {
            return work switch
            {
                RunWork run => new TrayNotification("Ejecución terminada", $"Una ejecución terminó: {RunEnd(run)}."),
                DebateWork => new TrayNotification("Debate terminado", "Un debate terminó."),
                _ => null,
            };
        }

        if (!now.IsActive)
        {
            return null;
        }

        if (now.AwaitingPerson > before.AwaitingPerson)
        {
            return work is RunWork
                ? new TrayNotification("Agent Studio espera tu aprobación", "Una ejecución necesita tu aprobación para seguir.")
                : new TrayNotification("Agent Studio espera por ti", "Un debate necesita una decisión tuya.");
        }

        if (before.WaitsForQuota && !now.WaitsForQuota && now.Phase is null or RunPhase.Running)
        {
            return new TrayNotification("La espera de cuota terminó", work is RunWork ? "Una ejecución dejó de esperar cuota y sigue." : "Un debate dejó de esperar cuota y sigue.");
        }

        return null;
    }

    /// <summary>How a run ended, as the notification says it (never anything of the project).</summary>
    private static string RunEnd(RunWork run) => run.Monitor.Phase switch
    {
        RunPhase.Succeeded => "completada",
        RunPhase.Failed => "fallida",
        RunPhase.Cancelled => "cancelada",
        RunPhase.Blocked => "bloqueada",
        RunPhase.Interrupted => "interrumpida",
        null => "sin resultado",
        var phase => RunPhaseText.Label(phase.Value),
    };
}
