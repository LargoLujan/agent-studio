using System.Globalization;
using System.IO;
using System.Threading.Channels;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Markdown;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// Read-only panel with today's journal (<c>.agentstudio/bitacora/YYYY-MM-DD.md</c>) of the open project. The day is read
/// through the checked storage reader (<see cref="IProjectStorageReader"/>, docs/14 D-31): a link planted as a day of the
/// journal, or a folder on the way that is one, is refused and nothing of it is shown. Without that reader the journal is
/// not read at all: no file of the project is ever opened directly.
/// <para>
/// V32: the panel follows the day by itself. The journal folder is watched (<see cref="IJournalWatcher"/>: the app writes the
/// day when each agent starts and ends, and the logging hook writes it from outside), and the day is read again once no change
/// came for <see cref="SettleTime"/> (at most <see cref="LongestSettle"/> after the first); at midnight the panel goes to the
/// new day. Every read, «Actualizar» included, goes through one queue with one consumer: one read at a time, off the UI
/// thread, and the requests that come during a read are one read more when it ends. What is read is shown on the UI thread,
/// and only for the project still open. With «Ver todo» on, a day read again by itself does not reach the plain viewer (it
/// costs about a second for a long day): <see cref="HasNewEntries"/> says so, and «Recargar» or «Actualizar» bring it.
/// </para>
/// </summary>
public sealed partial class JournalViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The most lines the readable view draws (V31): laying out a document blocks the UI thread for a time that grows with
    /// its length, so a longer day shows its latest entries, and all of it as plain text on demand.
    /// </summary>
    public const int ReadableLines = 200;

    /// <summary>The most characters the readable view draws.</summary>
    public const int ReadableCharacters = 40_000;

    /// <summary>How long the journal folder stays without changes before the day is read again: a burst of writes is one read (V32).</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest a burst of changes holds the read back: one that never ends is still read this often.</summary>
    public static readonly TimeSpan LongestSettle = TimeSpan.FromSeconds(3);

    /// <summary>The spells of <see cref="SettleTime"/> that fit in <see cref="LongestSettle"/>.</summary>
    private static readonly int SettleRounds = (int)(LongestSettle / SettleTime);

    /// <summary>The longest wait between two checks of the day: the timer is set again after each, whatever the clock did.</summary>
    private static readonly TimeSpan LongestDayCheck = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly IProjectStorageReader? _reader;
    private readonly IJournalWatcher? _watcher;
    private readonly SynchronizationContext? _ui;
    private readonly Channel<ReadRequest> _requests = Channel.CreateUnbounded<ReadRequest>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ITimer _dayTimer;

    /// <summary>The project open now; a new object for each opening, so a read of another one is told apart.</summary>
    private volatile OpenProject? _project;

    /// <summary>The file of the day read last («2026-10-15.md»), to know when the day changed.</summary>
    private string? _readFile;
    private volatile IJournalWatch? _watching;
    private volatile bool _disposed;

    /// <summary>What the plain viewer holds while «Ver todo» is on: the day as it was when it was asked or reloaded.</summary>
    private string _plainText = string.Empty;

    /// <param name="timeProvider">Its local day names the file; it also spaces the reads after a change.</param>
    /// <param name="reader">The checked reader of <c>.agentstudio</c>; null when the shell was composed without it.</param>
    /// <param name="watcher">Says when the journal folder changed; without it the day is read again only with «Actualizar» and at midnight.</param>
    /// <param name="uiContext">Where what is read is shown; defaults to the creating thread's context.</param>
    public JournalViewModel(TimeProvider timeProvider, IProjectStorageReader? reader = null, IJournalWatcher? watcher = null, SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _time = timeProvider;
        _reader = reader;
        _watcher = watcher;
        _ui = uiContext ?? SynchronizationContext.Current;
        _dayTimer = timeProvider.CreateTimer(_ => OnDayCheck(), null, UntilNextCheck(), Timeout.InfiniteTimeSpan);
        _ = Task.Run(ConsumeAsync);
    }

    /// <summary>The day as read, whole; never changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlainText), nameof(HasNewEntries), nameof(NewEntriesNote))]
    private string _text = string.Empty;

    /// <summary>What the readable view draws of <see cref="Text"/>: all of it, or its title and its latest entries when it is long.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenderedText), nameof(IsTrimmed), nameof(TrimNote))]
    private JournalExcerpt _excerpt = JournalExcerpt.Empty;

    /// <summary>The whole day as plain text (fast, and complete) instead of the readable view of its latest entries.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReadable), nameof(TrimNote), nameof(ToggleText), nameof(PlainText), nameof(HasNewEntries), nameof(NewEntriesNote))]
    private bool _showPlainText;

    public string RenderedText => Excerpt.Text;

    /// <summary>
    /// What the plain viewer holds: the whole day once «Ver todo» is asked, nothing before. A text box takes its text even
    /// while it is not shown, and a long day cost as much as the readable view (about a second for 5,000 lines). So a day read
    /// again by itself does not reach it (<see cref="HasNewEntries"/>): «Recargar» or «Actualizar» bring it.
    /// </summary>
    public string PlainText => ShowPlainText ? _plainText : string.Empty;

    /// <summary>«Ver todo» is on and the day read again by itself has more than the plain viewer shows.</summary>
    public bool HasNewEntries => ShowPlainText && !string.Equals(_plainText, Text, StringComparison.Ordinal);

    /// <summary>What the panel says while <see cref="HasNewEntries"/>; null otherwise.</summary>
    public string? NewEntriesNote => HasNewEntries ? "Hay entradas nuevas: «Recargar» las enseña aquí; la vista legible ya las tiene." : null;

    public bool IsTrimmed => Excerpt.IsTrimmed;

    public bool ShowReadable => !ShowPlainText;

    /// <summary>Why not all of the day is drawn, and how to see it; null when all of it is.</summary>
    public string? TrimNote => !Excerpt.IsTrimmed ? null
        : ShowPlainText ? "Se ve el día completo como texto, sin formato."
        : Excerpt.ShownEntries > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Bitácora larga: se ven las {Excerpt.ShownEntries} entradas más recientes de {Excerpt.TotalEntries}.")
            : string.Create(CultureInfo.InvariantCulture, $"Bitácora larga: se ven las últimas {Excerpt.ShownLines} líneas de {Excerpt.TotalLines}.");

    public string ToggleText => ShowPlainText ? "Ver legible" : "Ver todo";

    partial void OnTextChanged(string value)
    {
        Excerpt = JournalExcerpt.Of(value, ReadableLines, ReadableCharacters);

        // A day short enough is drawn whole: the plain text has nothing more to show, and no way back would be offered.
        if (!Excerpt.IsTrimmed)
        {
            ShowPlainText = false;
        }
    }

    /// <summary>«Ver todo» (the whole day as plain text) and back to the readable view, which always has the day as read last.</summary>
    [RelayCommand]
    private void TogglePlainText() => ShowPlainText = !ShowPlainText;

    /// <summary>«Recargar»: the plain viewer takes the day as read last.</summary>
    [RelayCommand]
    private void ReloadPlainText()
    {
        if (ShowPlainText && !string.Equals(_plainText, Text, StringComparison.Ordinal))
        {
            _plainText = Text;
            OnPropertyChanged(nameof(PlainText));
            OnPropertyChanged(nameof(HasNewEntries));
            OnPropertyChanged(nameof(NewEntriesNote));
        }
    }

    // Runs before the change of ShowPlainText is notified, so the plain viewer gets the day once.
    partial void OnShowPlainTextChanged(bool value) => _plainText = value ? Text : string.Empty;

    /// <summary>Honest empty / error state; null when the journal was shown.</summary>
    [ObservableProperty]
    private string? _statusMessage = "Abre una carpeta para ver su bitácora.";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// What the day shown is of: its project and its file; null with nothing read. The view keeps the place of whoever reads
    /// only while it stays the same (V32): another project or another day starts at the top.
    /// </summary>
    [ObservableProperty]
    private string? _readScope;

    /// <summary>Path of today's journal for the current project, or null without project (shown, never opened directly).</summary>
    public string? CurrentFilePath =>
        _project is not { } project ? null : Path.Combine(project.Root, AgentStudioFiles.DataFolder, AgentStudioFiles.JournalFolder, TodaysFileName());

    /// <summary>The project open in the shell changed (null: none): the watch of the one left goes, and the new day is read.</summary>
    public async Task SetProjectAsync(string? projectPath)
    {
        _watching?.Dispose();
        _watching = null;
        var project = projectPath is null ? null : new OpenProject(projectPath);
        _project = project;
        ShowPlainText = false;
        if (project is not null && _reader is not null && _watcher is not null && !_disposed)
        {
            _watching = _watcher.Watch(project.Root, () => _ = RequestRead(ReadKind.Changed));
        }

        await ReadAsync(ReadKind.Asked);
    }

    /// <summary>Stops watching the journal and the day: the shell closes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watching?.Dispose();
        _watching = null;
        _dayTimer.Dispose();

        // The consumer ends once the requests already queued are answered (without reading).
        _requests.Writer.TryComplete();
    }

    /// <summary>«Actualizar»: the day is read again now, and the plain viewer of «Ver todo» takes it too.</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        // A watch that could not be completed (a folder on the way was being deleted) is set again too.
        _watching?.Rearm();
        return ReadAsync(ReadKind.Asked);
    }

    private Task ReadAsync(ReadKind kind)
    {
        if (_project is null)
        {
            ReadScope = null;
            Text = string.Empty;
            StatusMessage = "Abre una carpeta para ver su bitácora.";
            return Task.CompletedTask;
        }

        if (_reader is null)
        {
            ReadScope = null;
            Text = string.Empty;
            StatusMessage = "La bitácora no se puede leer: falta la lectura comprobada de .agentstudio (sin ella no se abre ningún fichero del proyecto).";
            return Task.CompletedTask;
        }

        return RequestRead(kind);
    }

    /// <summary>Queues a read of the day, answered once a read that started after it was shown.</summary>
    private Task RequestRead(ReadKind kind)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed || !_requests.Writer.TryWrite(new ReadRequest(kind, done)))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    /// <summary>
    /// The one consumer of the reads: it takes every request queued; changes on disk wait until none came for
    /// <see cref="SettleTime"/> (never more than <see cref="LongestSettle"/>, so a burst without end is still read); then it
    /// reads the day once and shows it. The requests that came meanwhile are the next read.
    /// </summary>
    private async Task ConsumeAsync()
    {
        var requests = _requests.Reader;
        while (await requests.WaitToReadAsync().ConfigureAwait(false))
        {
            var waiting = new List<ReadRequest>();
            while (requests.TryRead(out var request))
            {
                waiting.Add(request);
            }

            if (waiting.Count == 0)
            {
                continue;
            }

            try
            {
                for (var round = 0; round < SettleRounds && !_disposed && waiting.TrueForAll(r => r.Kind == ReadKind.Changed); round++)
                {
                    var before = waiting.Count;
                    await Task.Delay(SettleTime, _time).ConfigureAwait(false);
                    while (requests.TryRead(out var more))
                    {
                        waiting.Add(more);
                    }

                    if (waiting.Count == before)
                    {
                        break;
                    }
                }

                await ReadAndShowAsync(reloadPlain: waiting.Exists(r => r.Kind == ReadKind.Asked)).ConfigureAwait(false);
                waiting.ForEach(r => r.Done.TrySetResult());
            }
#pragma warning disable CA1031 // The consumer outlives a failed read: its error goes to whoever asked, and the next read comes.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                waiting.ForEach(r => r.Done.TrySetException(ex));
            }
        }
    }

    /// <summary>
    /// Reads the day of the project open now, off the UI thread, and shows it on the UI thread if it is still open. The plain
    /// viewer of «Ver todo» takes it only when a person asked (<paramref name="reloadPlain"/>).
    /// </summary>
    private async Task ReadAndShowAsync(bool reloadPlain)
    {
        if (_disposed || _project is not { } project || _reader is not { } reader)
        {
            return;
        }

        var file = TodaysFileName();
        await OnUiAsync(() => IsBusy = true).ConfigureAwait(false);
        var (text, status) = await Task.Run(() => Read(reader, project.Root, file)).ConfigureAwait(false);
        await OnUiAsync(() =>
        {
            IsBusy = false;
            if (!ReferenceEquals(_project, project) || _disposed)
            {
                return;
            }

            Volatile.Write(ref _readFile, file);

            // What the day shown is of goes first: the view keeps the place of whoever reads only within one project and day.
            ReadScope = project.Root + "|" + file;

            // The snapshot of the plain viewer goes first, so a changed day reaches it with one notification.
            var reload = reloadPlain && ShowPlainText && !string.Equals(_plainText, text, StringComparison.Ordinal);
            var unchanged = string.Equals(Text, text, StringComparison.Ordinal);
            if (reload)
            {
                _plainText = text;
            }

            Text = text;
            StatusMessage = status;
            if (reload && unchanged)
            {
                OnPropertyChanged(nameof(PlainText));
                OnPropertyChanged(nameof(HasNewEntries));
                OnPropertyChanged(nameof(NewEntriesNote));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>The day through the checked reader: its text, or nothing and why.</summary>
    private static (string Text, string? Status) Read(IProjectStorageReader reader, string project, string file)
    {
        try
        {
            // The reader checks every folder on the way and the file itself, and shares the file for writing (the logging
            // hook may be appending right now).
            using var stream = reader.OpenForRead(project, AgentStudioFiles.JournalFolder + "/" + file);
            using var text = new StreamReader(stream);
            return (text.ReadToEnd(), null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (string.Empty, $"No hay bitácora de hoy ({file}). Se crea con la primera petición del día.");
        }
        catch (IOException ex)
        {
            // Also a refused link or a path that reaches another place: nothing of it is shown.
            return (string.Empty, $"No se pudo leer la bitácora: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (string.Empty, $"No se pudo leer la bitácora: {ex.Message}");
        }
    }

    /// <summary>
    /// The timer of the day (on its own thread): a new day is read when the day of the file read last is gone, and a watch
    /// that could not be completed is set again.
    /// </summary>
    private void OnDayCheck()
    {
        if (_disposed)
        {
            return;
        }

        _watching?.Rearm();
        if (_project is not null && _reader is not null && !string.Equals(Volatile.Read(ref _readFile), TodaysFileName(), StringComparison.Ordinal))
        {
            _ = RequestRead(ReadKind.Day);
        }

        try
        {
            _dayTimer?.Change(UntilNextCheck(), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile: nothing more to check.
        }
    }

    /// <summary>Until a second after the next local midnight, and never more than <see cref="LongestDayCheck"/>.</summary>
    private TimeSpan UntilNextCheck()
    {
        var now = _time.GetLocalNow();
        var wait = now.Date.AddDays(1) - now.DateTime + TimeSpan.FromSeconds(1);
        return wait > LongestDayCheck ? LongestDayCheck : wait;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline without one), and completes once it ran.</summary>
    private Task OnUiAsync(Action action)
    {
        if (_ui is null)
        {
            action();
            return Task.CompletedTask;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(
            _ =>
            {
                try
                {
                    action();
                    ran.TrySetResult();
                }
#pragma warning disable CA1031 // A failure to show goes to whoever asked for the read, never to the dispatcher.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ran.TrySetException(ex);
                }
            },
            null);
        return ran.Task;
    }

    private string TodaysFileName() => AgentStudioFiles.JournalDayFile(DateOnly.FromDateTime(_time.GetLocalNow().DateTime));

    /// <summary>A project as it was opened in the panel.</summary>
    private sealed record OpenProject(string Root);

    /// <summary>A read asked for, and whoever waits for it.</summary>
    private sealed record ReadRequest(ReadKind Kind, TaskCompletionSource Done);

    /// <summary>Why the day is read.</summary>
    private enum ReadKind
    {
        /// <summary>A person asked: the project opened, or «Actualizar».</summary>
        Asked,

        /// <summary>The journal folder changed: the changes settle first.</summary>
        Changed,

        /// <summary>The day changed.</summary>
        Day,
    }
}
