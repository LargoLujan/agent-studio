using AgentStudio.Application.Git;

namespace AgentStudio.Memory.Consistency;

/// <summary>
/// What the copy of a commit holds, worked out once for every check that asks whether a path exists (docs/20 D-28): the files, the folders (every prefix of a path)
/// and the same two with no regard for case, which tells «it only fails by case» (git tells case apart and Windows does not) from «it is not there». «Exists» means «exists in
/// the copy of the commit», not in the folder of the person.
/// </summary>
internal sealed class SnapshotFiles
{
    private readonly HashSet<string> _files;
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byLowerCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _sorted;

    public SnapshotFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _files = new HashSet<string>(paths, StringComparer.Ordinal);
        _sorted = [.. _files.Order(StringComparer.Ordinal)];
        foreach (var path in _sorted)
        {
            _byLowerCase.TryAdd(path, path);
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                var folder = path[..slash];
                if (_folders.Add(folder))
                {
                    _byLowerCase.TryAdd(folder, folder);
                }
            }
        }
    }

    public static SnapshotFiles Of(IRepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SnapshotFiles(snapshot.Paths);
    }

    public IReadOnlyList<string> Paths => _sorted;

    /// <summary>A file or a folder of the copy, written exactly like this.</summary>
    public bool Exists(string path) => path.Length == 0 || _files.Contains(path) || _folders.Contains(path);

    public bool FolderExists(string path) => path.Length == 0 || _folders.Contains(path);

    /// <summary>The file or folder that is the same path but for the case of its letters, or null.</summary>
    public string? ExistsIgnoringCase(string path) => _byLowerCase.TryGetValue(path, out var real) ? real : null;

    /// <summary>Whether some path of the copy starts with <paramref name="prefix"/> (a fixed prefix of a pattern with <c>*</c>).</summary>
    public bool AnyStartsWith(string prefix)
    {
        var index = Array.BinarySearch(_sorted, prefix, StringComparer.Ordinal);
        if (index >= 0)
        {
            return true;
        }

        var next = ~index;
        return next < _sorted.Length && _sorted[next].StartsWith(prefix, StringComparison.Ordinal);
    }
}
