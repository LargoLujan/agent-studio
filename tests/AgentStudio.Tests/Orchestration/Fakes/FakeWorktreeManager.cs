using System.IO;
using System.Text;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>
/// A worktree manager over real folders and an in-memory object store. A commit is a snapshot of the files of a
/// worktree (<c>.agents/</c> excluded, as the real one does); refs and worktree heads point at snapshots. It is enough
/// for the engine: create or adopt a worktree on a base, commit everything, list changed paths, write a diff, reset to a
/// commit, and capture the user's repository (which a test can change behind the engine's back).
/// </summary>
internal sealed class FakeWorktreeManager : IWorktreeManager
{
    private readonly Lock _gate = new();
    private readonly string _root;
    private readonly Dictionary<string, Dictionary<string, string>> _commits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Worktree> _worktrees = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _calls = [];
    private int _captures;

    public FakeWorktreeManager(string root, string repositoryRoot, IReadOnlyDictionary<string, string>? initialFiles = null)
    {
        _root = root;
        RepositoryRoot = repositoryRoot;
        InitialCommit = AddCommit(new Dictionary<string, string>(initialFiles ?? new Dictionary<string, string> { ["README.md"] = "hola" }, StringComparer.Ordinal), "initial");
        UserState = new UserRepositoryState(
            InitialCommit,
            "refs/heads/main",
            new Dictionary<string, string> { ["refs/heads/main"] = InitialCommit },
            new Dictionary<string, string>(),
            null,
            "index-0",
            []);
    }

    public string RepositoryRoot { get; }

    public string InitialCommit { get; }

    /// <summary>What <see cref="CaptureUserRepositoryStateAsync"/> returns.</summary>
    public UserRepositoryState UserState { get; set; }

    public bool HasUncommittedChanges { get; set; }

    /// <summary>Called on each capture with its number (1-based) before the state is returned.</summary>
    public Action<int>? OnCapture { get; set; }

    /// <summary>When set, each capture comes from it (the real capture of a real repository, say) instead of <see cref="UserState"/>.</summary>
    public Func<CancellationToken, Task<UserRepositoryState>>? CaptureFrom { get; set; }

    public int Captures => Volatile.Read(ref _captures);

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

    public List<string> CommitMessages { get; } = [];

    public Task<RepositoryState> InspectAsync(string repoRoot, CancellationToken cancellationToken) =>
        Task.FromResult(new RepositoryState(repoRoot, UserState.HeadCommit, "main", HasUncommittedChanges, new Version(2, 45, 2)));

    public Task<UserRepositoryState> CaptureUserRepositoryStateAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _captures);
        OnCapture?.Invoke(number);
        return CaptureFrom is { } capture ? capture(cancellationToken) : Task.FromResult(UserState);
    }

    public Task<WorktreeLease> EnsureAsync(WorktreeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var path = Path.Combine(_root, request.Run.Value, request.Name);
            var refName = $"refs/agentstudio/{request.Run.Value}/{request.Name}";
            _calls.Add($"ensure {request.Name} {request.BaseCommit}");
            if (_worktrees.TryGetValue(path, out var existing))
            {
                if (existing.Base != request.BaseCommit)
                {
                    throw new InvalidOperationException($"Worktree {request.Name} exists on {existing.Base}, not {request.BaseCommit}.");
                }

                return Task.FromResult(Lease(existing));
            }

            if (!_commits.ContainsKey(request.BaseCommit))
            {
                throw new InvalidOperationException($"Unknown base commit {request.BaseCommit}.");
            }

            var worktree = new Worktree(path, request.Name, refName, request.BaseCommit, request.Run) { Head = request.BaseCommit };
            Materialize(path, _commits[request.BaseCommit]);
            _worktrees[path] = worktree;
            _refs[refName] = request.BaseCommit;
            return Task.FromResult(Lease(worktree));
        }
    }

    public Task<CommitResult> CommitAllAsync(WorktreeLease lease, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var worktree = Find(lease);
            _calls.Add($"commit {worktree.Name}");
            CommitMessages.Add(message);
            var files = Read(worktree.Path);
            if (Same(files, _commits[worktree.Head]))
            {
                _refs[worktree.RefName] = worktree.Head;
                return Task.FromResult(new CommitResult(false, worktree.Head));
            }

            var commit = AddCommit(files, worktree.Head + "\n" + message);
            worktree.Head = commit;
            _refs[worktree.RefName] = commit;
            return Task.FromResult(new CommitResult(true, commit));
        }
    }

    public Task<IReadOnlyList<string>> ChangedPathsAsync(string repoRoot, string from, string to, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Changed(_commits[Resolve(from)], _commits[Resolve(to)]));
        }
    }

    public async Task WriteDiffAsync(string repoRoot, string from, string to, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string text;
        lock (_gate)
        {
            var paths = Changed(_commits[Resolve(from)], _commits[Resolve(to)]);
            text = $"diff {Resolve(from)}..{Resolve(to)}\n" + string.Concat(paths.Select(p => $"M {p}\n"));
        }

        await destination.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    public Task<MergeAttempt> MergeIntoAsync(string repoRoot, string targetRef, string expectedTarget, string source, string message, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The engine merges through IIntegrator.");

    public Task CheckoutAsync(WorktreeLease lease, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            var worktree = Find(lease);
            _calls.Add($"checkout {worktree.Name} {commit}");
            var target = Resolve(commit);
            Materialize(worktree.Path, _commits[target]);
            worktree.Head = target;
        }

        return Task.CompletedTask;
    }

    public Task<RemovalOutcome> RemoveAsync(WorktreeLease lease, bool discardChanges, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The engine never removes worktrees.");

    public Task<string?> ResolveCommitAsync(string repoRoot, string revision, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_commits.ContainsKey(revision) ? revision : _refs.GetValueOrDefault(revision));
        }
    }

    /// <summary>Moves a ref behind the engine's back, as an agent with a shell could (refs are shared by every worktree).</summary>
    public void MoveRef(string refName, string commit)
    {
        lock (_gate)
        {
            if (!_refs.ContainsKey(refName) || !_commits.ContainsKey(commit))
            {
                throw new InvalidOperationException($"Cannot move unknown ref {refName} to {commit}.");
            }

            _refs[refName] = commit;
        }
    }

    public Task<IReadOnlyList<WorktreeLease>> ListOwnedAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WorktreeLease>>([.. _worktrees.Values.Select(Lease)]);
        }
    }

    /// <summary>Merges the changes of every source (relative to <paramref name="baseCommit"/>) on top of it; two sources that change one path differently conflict.</summary>
    public string? Merge(string baseCommit, IReadOnlyList<string> sources, out IReadOnlyList<string> conflicts)
    {
        lock (_gate)
        {
            var baseFiles = _commits[Resolve(baseCommit)];
            var merged = new Dictionary<string, string>(baseFiles, StringComparer.Ordinal);
            var changedBy = new Dictionary<string, string?>(StringComparer.Ordinal);
            var conflicting = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var files = _commits[Resolve(source)];
                foreach (var path in Changed(baseFiles, files))
                {
                    var value = files.GetValueOrDefault(path);
                    if (changedBy.TryGetValue(path, out var previous) && previous != value)
                    {
                        conflicting.Add(path);
                        continue;
                    }

                    changedBy[path] = value;
                    if (value is null)
                    {
                        merged.Remove(path);
                    }
                    else
                    {
                        merged[path] = value;
                    }
                }
            }

            conflicts = [.. conflicting];
            return conflicting.Count > 0 ? null : AddCommit(merged, "merge " + string.Join(",", sources));
        }
    }

    public IReadOnlyDictionary<string, string> Files(string commit)
    {
        lock (_gate)
        {
            return _commits[Resolve(commit)];
        }
    }

    public string Head(string worktreeName)
    {
        lock (_gate)
        {
            return _worktrees.Values.Single(w => w.Name == worktreeName).Head;
        }
    }

    public bool HasWorktree(string worktreeName)
    {
        lock (_gate)
        {
            return _worktrees.Values.Any(w => w.Name == worktreeName);
        }
    }

    public string Resolve(string revision)
    {
        lock (_gate)
        {
            if (_commits.ContainsKey(revision))
            {
                return revision;
            }

            return _refs.TryGetValue(revision, out var commit)
                ? commit
                : throw new InvalidOperationException($"Unknown revision {revision}.");
        }
    }

    private static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, string> from, IReadOnlyDictionary<string, string> to) =>
        [.. from.Keys.Union(to.Keys, StringComparer.Ordinal)
            .Where(p => !string.Equals(from.GetValueOrDefault(p), to.GetValueOrDefault(p), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) => Changed(a, b).Count == 0;

    private static Dictionary<string, string> Read(string path)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(path))
        {
            return files;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            if (relative.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files[relative] = File.ReadAllText(file);
        }

        return files;
    }

    private static void Materialize(string path, IReadOnlyDictionary<string, string> files)
    {
        Directory.CreateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            if (!relative.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private string AddCommit(Dictionary<string, string> files, string salt)
    {
        var canonical = salt + "\n" + string.Concat(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key.Length}:{f.Key}{f.Value.Length}:{f.Value}"));
        var id = ContentHash.Sha256Hex(canonical)[..40];
        _commits[id] = files;
        return id;
    }

    private Worktree Find(WorktreeLease lease) =>
        _worktrees.TryGetValue(lease.Path, out var worktree) ? worktree : throw new InvalidOperationException($"Not an owned worktree: {lease.Path}.");

    private WorktreeLease Lease(Worktree worktree) =>
        new(worktree.Path, worktree.Name, worktree.RefName, worktree.Base, worktree.Run, RepositoryRoot, Path.Combine(RepositoryRoot, ".git", "worktrees", worktree.Name));

    private sealed record Worktree(string Path, string Name, string RefName, string Base, RunId Run)
    {
        public string Head { get; set; } = Base;
    }
}
