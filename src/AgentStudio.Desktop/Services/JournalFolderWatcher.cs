using System.IO;
using System.Threading.Channels;
using AgentStudio.Application.Projects;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// Says when the journal of a project may have changed (V32): «Bitácora de hoy» reads its day again then, through the
/// checked reader. Nothing is read here.
/// </summary>
public interface IJournalWatcher
{
    /// <summary>
    /// Starts watching the journal folder of <paramref name="projectRoot"/>; <paramref name="changed"/> is called, on any
    /// thread, after each change. Disposing the result stops it.
    /// </summary>
    IJournalWatch Watch(string projectRoot, Action changed);
}

/// <summary>A watch of the journal folder of a project; disposing it stops it.</summary>
public interface IJournalWatch : IDisposable
{
    /// <summary>
    /// Sets the watch again if it is not complete (a folder on the way could not be watched, or went and came back): the panel
    /// asks it with «Actualizar» and when it checks the day.
    /// </summary>
    void Rearm();
}

/// <summary>
/// Watches <c>.agentstudio/bitacora</c> of a project with <see cref="FileSystemWatcher"/>s (V32): the app writes the day when
/// each agent starts and ends, and the logging hook of Claude Code writes it from outside.
/// <list type="bullet">
/// <item>Every folder on the way that exists is watched, each only for the next one (the root for <c>.agentstudio</c>,
/// <c>.agentstudio</c> for <c>bitacora</c>) and the journal for its days. So a folder that is deleted and made again (a
/// checkout of another branch, the demo made again) is seen from the folder above it, and the watch below is set again.</item>
/// <item>A folder that cannot be watched (it is being deleted, it is a link: docs/14 D-31) is not: the watch stops at the one
/// above. While the watch is not complete, or nothing is watched (the root is not there), it is tried again every
/// <see cref="RetryInterval"/>, and when the panel asks (<see cref="IJournalWatch.Rearm"/>).</item>
/// <item>The events only signal; they reach one consumer in order (a queue, no lock), which sets the watch again when a
/// folder on the way changed or a watcher lost events, and then says that the journal changed.</item>
/// </list>
/// </summary>
public sealed class JournalFolderWatcher(TimeProvider? time = null) : IJournalWatcher
{
    /// <summary>How often an incomplete watch is tried again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    public IJournalWatch Watch(string projectRoot, Action changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(changed);
        return new Watching(projectRoot, changed, time ?? TimeProvider.System);
    }

    private sealed class Watching : IJournalWatch
    {
        /// <summary>The index of the journal folder in <see cref="_levels"/>.</summary>
        private const int Journal = 2;

        /// <summary>The project root, its <c>.agentstudio</c> and the journal folder.</summary>
        private readonly string[] _levels;
        private readonly Action _changed;
        private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ITimer _retry;

        /// <summary>The watcher of each level, from the root down; only the consumer (and the constructor before it) touches them.</summary>
        private readonly List<FileSystemWatcher> _watchers = [];
        private volatile bool _disposed;

        public Watching(string root, Action changed, TimeProvider time)
        {
            var data = Path.Combine(root, AgentStudioFiles.DataFolder);
            _levels = [root, data, Path.Combine(data, AgentStudioFiles.JournalFolder)];
            _changed = changed;
            _retry = time.CreateTimer(_ => Send(Signal.Retry), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Rewatch(force: true);
            _ = Task.Run(ConsumeAsync);
        }

        /// <summary>What a signal asks of the consumer.</summary>
        private enum Signal
        {
            /// <summary>A day of the journal changed.</summary>
            Changed,

            /// <summary>A folder on the way appeared, went or was renamed, or a watcher lost events: set the watch again.</summary>
            Moved,

            /// <summary>The watch is incomplete and its time to try again came: set it again if it can be completed now.</summary>
            Retry,

            /// <summary>The panel asks: set the watch again if it is incomplete; the panel is reading anyway.</summary>
            Rearm,
        }

        /// <summary>The deepest level watched now; -1 when nothing is.</summary>
        private int Depth => _watchers.Count - 1;

        public void Rearm() => Send(Signal.Rearm);

        public void Dispose()
        {
            _disposed = true;
            _retry.Dispose();
            _signals.Writer.TryComplete();
        }

        /// <summary>The one consumer: sets the watch again when it must, then says the journal changed when it may have.</summary>
        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var signal in _signals.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (_disposed)
                    {
                        break;
                    }

                    switch (signal)
                    {
                        case Signal.Changed:
                            _changed();
                            break;
                        case Signal.Moved:
                            Rewatch(force: true);
                            _changed();
                            break;
                        default:
                            // Only an incomplete watch, or one whose deepest folder went, is set again; when it gets deeper the
                            // journal may have changed meanwhile, and it is read again (the panel reads anyway when it asks).
                            if (Rewatch(force: false) && signal == Signal.Retry)
                            {
                                _changed();
                            }

                            break;
                    }
                }
            }
            finally
            {
                Close();
            }
        }

        private void Send(Signal signal)
        {
            if (!_disposed)
            {
                _signals.Writer.TryWrite(signal);
            }
        }

        /// <summary>
        /// Watches every plain folder on the way that exists, from the root down, stopping above the first one that cannot be
        /// watched; without <paramref name="force"/>, only when that would watch other folders than now. While the watch is
        /// incomplete it is tried again later. Says whether it watches deeper than before.
        /// </summary>
        private bool Rewatch(bool force)
        {
            var before = Depth;
            var deepest = DeepestFolder();
            var alive = before >= 0 && Directory.Exists(_levels[before]);
            if (force || !alive || before != deepest)
            {
                Close();
                for (var level = 0; level <= deepest; level++)
                {
                    if (Open(level) is not { } watcher)
                    {
                        break;
                    }

                    _watchers.Add(watcher);
                }
            }

            // The journal is not watched yet (a folder on the way is not there, or could not be watched): try again later.
            try
            {
                _retry.Change(Depth < Journal ? RetryInterval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Let go meanwhile: nothing more to try.
            }

            return Depth > before;
        }

        /// <summary>A watcher of one level: the journal for its days, a folder above it only for the next folder on the way.</summary>
        private FileSystemWatcher? Open(int level)
        {
            try
            {
                // Above the journal only the next folder appearing, going or renamed matters (not what is written inside it).
                var watcher = level == Journal
                    ? new FileSystemWatcher(_levels[level], "*.md") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size }
                    : new FileSystemWatcher(_levels[level], Path.GetFileName(_levels[level + 1])) { NotifyFilter = NotifyFilters.DirectoryName };
                watcher.IncludeSubdirectories = false;
                var signal = level == Journal ? Signal.Changed : Signal.Moved;
                watcher.Created += (_, _) => Send(signal);
                watcher.Changed += (_, _) => Send(signal);
                watcher.Deleted += (_, _) => Send(signal);
                watcher.Renamed += (_, _) => Send(signal);

                // Lost events (a full buffer) or a watched folder that went: the watch is set again, and the day read again.
                watcher.Error += (_, _) => Send(Signal.Moved);
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Being deleted, or gone meanwhile: the folder above is watched, and this one is tried again later.
                return null;
            }
        }

        private void Close()
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        /// <summary>The index of the deepest folder on the way that exists and is not a link; -1 when not even the root exists.</summary>
        private int DeepestFolder()
        {
            if (!Directory.Exists(_levels[0]))
            {
                return -1;
            }

            var depth = 0;
            while (depth < Journal && IsPlainFolder(_levels[depth + 1]))
            {
                depth++;
            }

            return depth;
        }

        private static bool IsPlainFolder(string path)
        {
            try
            {
                var folder = new DirectoryInfo(path);
                return folder.Exists && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
