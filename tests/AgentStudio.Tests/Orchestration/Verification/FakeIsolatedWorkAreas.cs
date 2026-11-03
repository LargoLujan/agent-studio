using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// An <see cref="IIsolatedWorkAreaFactory"/> whose areas are plain temporary folders (no labels, no git): it records the
/// commits it was asked for and how many areas were alive at once, and has the writer it is given fill each copy. The real
/// factory has its own tests (LowIntegrityWorkAreaTests).
/// </summary>
internal sealed class FakeIsolatedWorkAreaFactory(string root) : IIsolatedWorkAreaFactory
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<FakeIsolatedWorkArea> _areas = [];
    private int _alive;

    /// <summary>Thrown by <see cref="CreateAsync"/> instead of creating an area.</summary>
    public Exception? Failure { get; init; }

    public int MostAliveAtOnce { get; private set; }

    /// <summary>The commit of every area asked for, in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<FakeIsolatedWorkArea> Areas
    {
        get
        {
            lock (_gate)
            {
                return [.. _areas];
            }
        }
    }

    public async Task<IIsolatedWorkArea> CreateAsync(string commit, Func<string, CancellationToken, Task> writeTree, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _calls.Add(commit);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        var area = new FakeIsolatedWorkArea(Path.Combine(root, Guid.NewGuid().ToString("N")[..8]), commit, this);
        await writeTree(area.SourceDirectory, cancellationToken);
        lock (_gate)
        {
            _areas.Add(area);
            _alive++;
            MostAliveAtOnce = Math.Max(MostAliveAtOnce, _alive);
        }

        return area;
    }

    internal void Released()
    {
        lock (_gate)
        {
            _alive--;
        }
    }
}

/// <summary>An area of <see cref="FakeIsolatedWorkAreaFactory"/>: folders under one root, removed when disposed.</summary>
internal sealed class FakeIsolatedWorkArea : IIsolatedWorkArea
{
    private readonly FakeIsolatedWorkAreaFactory _owner;
    private int _results;
    private int _disposed;

    public FakeIsolatedWorkArea(string root, string commit, FakeIsolatedWorkAreaFactory owner)
    {
        _owner = owner;
        Commit = commit;
        RootDirectory = root;
        SourceDirectory = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        TempDirectory = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
        CliHomeDirectory = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        NuGetPackagesDirectory = Directory.CreateDirectory(Path.Combine(root, "nuget")).FullName;
        NuGetHttpCacheDirectory = Directory.CreateDirectory(Path.Combine(root, "nuget-http")).FullName;
        NuGetPluginsCacheDirectory = Directory.CreateDirectory(Path.Combine(root, "nuget-plugins")).FullName;
    }

    public string Commit { get; }

    public string RootDirectory { get; }

    public string SourceDirectory { get; }

    public string TempDirectory { get; }

    public string CliHomeDirectory { get; }

    public string NuGetPackagesDirectory { get; }

    public string NuGetHttpCacheDirectory { get; }

    public string NuGetPluginsCacheDirectory { get; }

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public string CreateResultsDirectory() =>
        Directory.CreateDirectory(Path.Combine(RootDirectory, "r-" + Interlocked.Increment(ref _results))).FullName;

    public Stream OpenReport(string path, long maxBytes)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"'{full}' is outside the area.");
        }

        var info = new FileInfo(full);
        if (!info.Exists || info.Length > maxBytes)
        {
            throw new IOException($"'{full}' is missing or too large.");
        }

        return info.OpenRead();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Directory.Delete(RootDirectory, recursive: true);
            _owner.Released();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The git layer as the verification runner uses it: only <see cref="ArchiveCommitAsync"/>, which records what it was asked
/// (repository, commit and folder) and, when told, copies the files of a folder (without <c>.git</c>) into the copy, as a
/// commit would bring them. Everything else is not for the runner.
/// </summary>
internal sealed class FakeCommitArchive : IWorktreeManager
{
    private readonly Lock _gate = new();
    private readonly List<(string Repository, string Commit, string Destination)> _calls = [];

    /// <summary>The folder whose files every copy gets, or null for an empty copy.</summary>
    public string? CopyFrom { get; init; }

    public IReadOnlyList<(string Repository, string Commit, string Destination)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public Task ArchiveCommitAsync(string repoRoot, string commit, string destination, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add((repoRoot, commit, destination));
        }

        if (CopyFrom is { } from)
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file);
                if (relative.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        return Task.CompletedTask;
    }

    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken) =>
        throw NotForTheRunner();

    public Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken) => throw NotForTheRunner();

    public Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken) => throw NotForTheRunner();

    private static NotSupportedException NotForTheRunner() => new("The verification runner only copies commits.");
}
