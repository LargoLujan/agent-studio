using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Infrastructure.Graph;

/// <summary>
/// Opens copies of a commit to read without running anything of them (docs/20 D-19, ADR-0062): the same call the isolated verification uses, the hardened git
/// layer's <see cref="IWorktreeManager.ArchiveCommitAsync"/>, into a work area that <see cref="IIsolatedWorkAreaFactory"/> prepares. The factory holds the lock of
/// «una a la vez» until the copy is disposed, so with a verification running this waits. No git runs here and no object of git is read.
/// </summary>
public sealed class RepositorySnapshots(IIsolatedWorkAreaFactory areas, IWorktreeManager git) : IRepositorySnapshots
{
    public async Task<IRepositorySnapshot> OpenAsync(ProjectContext project, string commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!TraceRef.IsFullCommitId(commit))
        {
            throw new ArgumentException("The commit is a full id (40 or 64 lowercase hexadecimal characters).", nameof(commit));
        }

        var area = await areas.CreateAsync(commit, (folder, token) => git.ArchiveCommitAsync(project.RootPath, commit, folder, token), ct).ConfigureAwait(false);
        try
        {
            return new RepositorySnapshot(area, commit);
        }
        catch
        {
            await area.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>One copy of a commit inside an isolated area: the files it holds are listed once, without following any link, and read through the area.</summary>
internal sealed class RepositorySnapshot : IRepositorySnapshot
{
    private readonly IIsolatedWorkArea _area;
    private readonly HashSet<string> _known;

    public RepositorySnapshot(IIsolatedWorkArea area, string commit)
    {
        _area = area;
        Commit = commit;
        Root = Path.GetFullPath(area.SourceDirectory);
        Paths = List(Root);
        _known = new HashSet<string>(Paths, StringComparer.Ordinal);
    }

    public string Commit { get; }

    public string Root { get; }

    public IReadOnlyList<string> Paths { get; }

    public Stream OpenRead(string relativePath, long maxBytes)
    {
        if (!_known.Contains(relativePath))
        {
            throw new ArgumentException("The path is not a file of the copy.", nameof(relativePath));
        }

        return _area.OpenReport(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)), maxBytes);
    }

    public ValueTask DisposeAsync() => _area.DisposeAsync();

    /// <summary>The regular files below <paramref name="root"/>, relative with <c>/</c> in ordinal order. A link, a junction or a path that is not safe is left out.</summary>
    internal static List<string> List(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            var full = folder.Length == 0 ? root : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                    {
                        continue;
                    }

                    var relative = folder.Length == 0 ? entry.Name : folder + "/" + entry.Name;
                    if (!RepositoryPaths.IsSafeRelative(relative))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        pending.Push(relative);
                    }
                    else
                    {
                        files.Add(relative);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed holds nothing that is read.
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
