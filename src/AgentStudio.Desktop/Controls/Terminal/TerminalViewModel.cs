using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Controls.Terminal;

public enum TerminalState
{
    NotStarted,
    Running,
    Exited,
    Failed,
}

/// <summary>
/// State and commands of one terminal: shell selection, start/restart/stop and the visible exit code. The view
/// (<see cref="TerminalControl"/>) feeds it the size reported by xterm.js and forwards <see cref="OutputReceived"/>.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject, IAsyncDisposable
{
    private const short DefaultColumns = 120;
    private const short DefaultRows = 30;

    private readonly ITerminalLauncher _launcher;
    private readonly SynchronizationContext? _ui;
    private ITerminalSession? _session;
    private string? _sessionDirectory;
    private int _generation;
    private bool _stopRequested;
    private volatile bool _disposed;
    private short _columns = DefaultColumns;
    private short _rows = DefaultRows;

    /// <param name="uiContext">Where state changes are published; defaults to the creating thread's context.</param>
    public TerminalViewModel(ITerminalLauncher launcher, IShellLocator shellLocator, SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(shellLocator);
        _launcher = launcher;
        _ui = uiContext ?? SynchronizationContext.Current;
        foreach (var shell in shellLocator.GetAvailableShells())
        {
            Shells.Add(shell);
        }

        _selectedShell = Shells.FirstOrDefault();
        if (_selectedShell is null)
        {
            _statusText = "No se ha encontrado ninguna shell instalada";
        }
    }

    public ObservableCollection<ShellInfo> Shells { get; } = [];

    /// <summary>Working directory for new sessions.</summary>
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Makes <paramref name="directory"/> the working directory. A running shell that was started somewhere else is
    /// restarted there (the terminal belongs to the open project); a shell that is not running starts there next time.
    /// </summary>
    public async Task ChangeWorkingDirectoryAsync(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        WorkingDirectory = directory;
        if (State == TerminalState.Running && _sessionDirectory is not null && !SameDirectory(_sessionDirectory, directory)
            && StartCommand.CanExecute(null))
        {
            await StartCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private ShellInfo? _selectedShell;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private TerminalState _state;

    [ObservableProperty]
    private int? _exitCode;

    [ObservableProperty]
    private string _statusText = Texts.Get("Terminal_NotStarted");

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsRunning => State == TerminalState.Running;

    public string StartButtonText => State == TerminalState.NotStarted ? Texts.Get("Terminal_Start") : Texts.Get("Terminal_Restart");

    /// <summary>Raised from a background thread with text to show. Subscribers must be thread-safe.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised on the UI context when the screen must be wiped (a new session is about to start).</summary>
    public event Action? Cleared;

    /// <summary>Called by the view once xterm.js is loaded and sized; starts the first session.</summary>
    public void ViewReady(int columns, int rows)
    {
        SetSize(columns, rows);
        if (State == TerminalState.NotStarted && SelectedShell is not null && StartCommand.CanExecute(null))
        {
            StartCommand.Execute(null);
        }
    }

    /// <summary>Called by the view when xterm.js fits to a new size.</summary>
    public void ViewResized(int columns, int rows)
    {
        SetSize(columns, rows);
        try
        {
            _session?.Resize(_columns, _rows);
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException)
        {
            // The shell ended between the check and the call.
        }
    }

    /// <summary>Forwards keystrokes from xterm.js to the shell. Input is dropped when no session is running.</summary>
    public async Task SendInputAsync(string input)
    {
        var session = _session;
        if (session is null || State != TerminalState.Running)
        {
            return;
        }

        try
        {
            await session.WriteAsync(input, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException)
        {
            // The shell ended; the exit handler reports it.
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var shell = SelectedShell;
        if (shell is null || _disposed)
        {
            return;
        }

        await ReleaseSessionAsync().ConfigureAwait(true);
        if (_disposed)
        {
            return; // Disposed while the previous session was being released: do not start a shell nobody owns.
        }

        var generation = Interlocked.Increment(ref _generation);
        _stopRequested = false;
        ErrorMessage = null;
        ExitCode = null;
        Cleared?.Invoke();

        ITerminalSession session;
        try
        {
            session = _launcher.Start(new TerminalStartInfo(shell.Path, shell.DefaultArguments, WorkingDirectory, _columns, _rows));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            State = TerminalState.Failed;
            ErrorMessage = Texts.Format("Terminal_StartFailed", shell.DisplayName, ex.Message);
            StatusText = ErrorMessage;
            return;
        }

        if (_disposed)
        {
            // Disposed between the check above and now: nobody would release this shell.
            await session.DisposeAsync().ConfigureAwait(true);
            return;
        }

        _session = session;
        _sessionDirectory = WorkingDirectory;
        State = TerminalState.Running;
        StatusText = Texts.Format("Terminal_Running", shell.DisplayName, session.ProcessId);
        _ = Task.Run(() => PumpAsync(session, shell, generation));
    }

    private bool CanStart() => SelectedShell is not null;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _stopRequested = true;
        await session.KillAsync().ConfigureAwait(true);
    }

    private async Task PumpAsync(ITerminalSession session, ShellInfo shell, int generation)
    {
        try
        {
            await foreach (var chunk in session.Output.ConfigureAwait(false))
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    OutputReceived?.Invoke(chunk);
                }
            }

            var code = await session.Exited.ConfigureAwait(false);
            Post(() =>
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                ExitCode = _stopRequested ? null : code;
                State = TerminalState.Exited;
                StatusText = _stopRequested
                    ? Texts.Format("Terminal_Stopped", shell.DisplayName)
                    : Texts.Format("Terminal_ExitedWithCode", shell.DisplayName, code);
            });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Post(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    State = TerminalState.Failed;
                    ErrorMessage = Texts.Format("Terminal_Interrupted", ex.Message);
                    StatusText = ErrorMessage;
                }
            });
        }
    }

    private async Task ReleaseSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            // Make a stale pump ignore its exit notification.
            Interlocked.Increment(ref _generation);
            await session.DisposeAsync().ConfigureAwait(true);
        }
    }

    private void SetSize(int columns, int rows)
    {
        _columns = (short)Math.Clamp(columns, 1, short.MaxValue);
        _rows = (short)Math.Clamp(rows, 1, short.MaxValue);
    }

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

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await ReleaseSessionAsync().ConfigureAwait(false);
    }
}
