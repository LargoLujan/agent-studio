using AgentStudio.Application.Projects;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Where the data of an open project lives: <c>&lt;root&gt;/.agentstudio/</c> with the database, the evidence
/// (content-addressed files) and the temporary files of the stores.
/// </summary>
/// <param name="FinalRootPath">
/// The project root as Windows reaches it, with every link above it resolved (<c>GetFinalPathNameByHandle</c>): each
/// folder and file of the storage must be reached at its own place under it (<see cref="ProjectStorageGuard"/>), and it is
/// where the project is, so the anchor of its decision log is kept by it (<see cref="FileDecisionLogAnchorStore"/>).
/// </param>
public sealed record ProjectStorage(
    string ProjectId,
    string RootPath,
    string DataDirectory,
    string DatabasePath,
    string EvidenceDirectory,
    string TempDirectory,
    string FinalRootPath)
{
    internal const string DataFolderName = ".agentstudio";

    internal const string JournalFolderName = "bitacora";

    internal string LockPath => LockPathFor(DataDirectory);

    /// <summary>The prompt journal, one Markdown file per day, which the memory module appends to through <see cref="ProjectStorageFiles"/>.</summary>
    internal string JournalDirectory => Path.Combine(DataDirectory, JournalFolderName);

    /// <summary>The database and the files SQLite keeps next to it: write-ahead log, shared memory and rollback journal.</summary>
    internal IReadOnlyList<string> DatabaseFiles => [DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal"];

    /// <summary>Where Windows must reach <paramref name="path"/>, a path of this storage: the same place under <see cref="FinalRootPath"/>.</summary>
    internal string FinalPathOf(string path) => Path.Combine(FinalRootPath, Path.GetRelativePath(RootPath, path));

    /// <summary>The storage layout of the project at <paramref name="rootPath"/> (already normalized), whose final path is <paramref name="finalRootPath"/>.</summary>
    internal static ProjectStorage For(string projectId, string rootPath, string finalRootPath)
    {
        var data = Path.Combine(rootPath, DataFolderName);
        return new ProjectStorage(
            projectId,
            rootPath,
            data,
            Path.Combine(data, "agentstudio.db"),
            Path.Combine(data, "evidence"),
            Path.Combine(data, "tmp"),
            finalRootPath);
    }

    // Named so that the ".agentstudio/*.db*" exclusion covers it as well.
    internal static string LockPathFor(string dataDirectory) => Path.Combine(dataDirectory, "agentstudio.db.lock");
}

/// <summary>
/// The projects open in this instance of the app. The stores only receive a project id: this registry turns it into
/// the project's database and folders. <see cref="ProjectWorkspace"/> registers a project when it opens it, together
/// with the lock file that keeps other instances out; closing the project (or disposing the registry, which the host
/// does when it shuts down) releases both.
/// </summary>
/// <remarks>
/// Connections are not pooled: every operation opens its own connection and closes it, so closing a project leaves
/// no handle on its database and nothing outlives the registry.
/// </remarks>
public sealed class ProjectDatabases : IDisposable, IProjectClosedSignal
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, OpenProject> _projects = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Serializes the opening of projects in this instance, so one folder is never locked twice by the same app.</summary>
    internal SemaphoreSlim OpenGate { get; } = new(1, 1);

    /// <summary>
    /// Raised with the id of a project once it is closed: whoever keeps something of it in memory lets it go (the decision
    /// recorder forgets the log it verified, <c>DecisionRecorder.Forget</c>, and the memory its ledger and its snapshot,
    /// <c>ProjectMemory.Forget</c>, through <see cref="IProjectClosedSignal"/>).
    /// </summary>
    public event Action<string>? Closed;

    /// <summary>The storage of an open project. Throws <see cref="InvalidOperationException"/> if this instance has not opened it.</summary>
    public ProjectStorage Get(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_projects.TryGetValue(projectId, out var open))
            {
                return open.Storage;
            }
        }

        throw new InvalidOperationException($"Project '{projectId}' is not open in this instance: open it with IProjectWorkspace.OpenAsync first.");
    }

    /// <summary>
    /// The storage of an open project, or null when this instance has not opened it, has closed it or is shut down. For
    /// whoever only names a project (the screens show its folder, never its id) and must not fail when it is not open.
    /// </summary>
    public ProjectStorage? Find(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        lock (_gate)
        {
            return !_disposed && _projects.TryGetValue(projectId, out var open) ? open.Storage : null;
        }
    }

    /// <summary>Releases the project: its lock file is removed and another instance may open it. Closing a project that is not open does nothing.</summary>
    public void Close(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        OpenProject? open;
        lock (_gate)
        {
            if (!_projects.Remove(projectId, out open))
            {
                return;
            }
        }

        open.Lock.Dispose();
        Closed?.Invoke(projectId);
    }

    public void Dispose()
    {
        List<OpenProject> open;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            open = [.. _projects.Values];
            _projects.Clear();
        }

        foreach (var project in open)
        {
            project.Lock.Dispose();
        }

        OpenGate.Dispose();
    }

    /// <summary>A new connection to the database of an open project. The caller disposes it.</summary>
    internal SqliteConnection Connect(string projectId) => SqliteSchema.Open(ConnectionString(Get(projectId).DatabasePath, create: false));

    /// <summary>
    /// What tells this opening of the project from any other one in this instance: a new value each time the project is
    /// opened, so whatever was verified while it was open before is verified again (<see cref="SqliteDecisionLogStore"/>).
    /// Throws <see cref="InvalidOperationException"/> if this instance has not opened it.
    /// </summary>
    internal string OpeningOf(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_projects.TryGetValue(projectId, out var open))
            {
                return open.Opening;
            }
        }

        throw new InvalidOperationException($"Project '{projectId}' is not open in this instance: open it with IProjectWorkspace.OpenAsync first.");
    }

    internal static string ConnectionString(string databasePath, bool create) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString();

    /// <summary>The project this instance has open at <paramref name="rootPath"/> (normalized), or null.</summary>
    internal ProjectStorage? FindByRoot(string rootPath)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _projects.Values
                .Select(p => p.Storage)
                .FirstOrDefault(s => string.Equals(s.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Takes ownership of <paramref name="projectLock"/>: it is released when the project is closed.</summary>
    internal void Register(ProjectStorage storage, IDisposable projectLock)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_projects.TryGetValue(storage.ProjectId, out var other))
            {
                // A copied project folder carries the id of the original; both cannot be open at once.
                throw new InvalidOperationException(
                    $"Project '{storage.ProjectId}' is already open from '{other.Storage.RootPath}'; '{storage.RootPath}' holds a copy of its database.");
            }

            _projects.Add(storage.ProjectId, new OpenProject(storage, projectLock, Guid.NewGuid().ToString("N")));
        }
    }

    private sealed record OpenProject(ProjectStorage Storage, IDisposable Lock, string Opening);
}
