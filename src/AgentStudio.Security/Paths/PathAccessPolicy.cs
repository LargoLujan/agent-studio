using AgentStudio.Domain.Agents;

namespace AgentStudio.Security.Paths;

/// <summary>
/// Decides what an agent may do with a path from its <see cref="PathGrant"/>s. Paths are normalised with
/// <see cref="Path.GetFullPath(string)"/> (so <c>..</c> segments cannot climb out of a grant), compared ignoring case
/// on Windows, and matched on folder boundaries (<c>C:\repo</c> does not grant <c>C:\repo2</c>). When several grants
/// cover a path, the one with the longest root (the most specific) wins, even if it is more restrictive; a path
/// covered by no grant gets <see cref="FileAccessLevel.None"/>. Paths and grant roots must be fully qualified
/// (<see cref="Path.IsPathFullyQualified(string)"/>); a relative one is rejected with <see cref="ArgumentException"/>
/// instead of being resolved against the process current directory.
/// </summary>
/// <remarks>
/// The check is purely lexical: it does not touch the file system, so it does not resolve symbolic links, junctions or
/// other reparse points. A link inside a granted folder that points outside it is NOT detected. Resolving links is
/// pending for Phase 7 and, until then, this policy must not be the only barrier for untrusted agents.
/// </remarks>
public sealed class PathAccessPolicy
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Sorted from the most specific (longest) root to the least, so the first match is the answer.
    private readonly List<(string Root, FileAccessLevel Access)> _grants;

    public PathAccessPolicy(IEnumerable<PathGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        _grants = grants
            .Select(g => (Root: Normalize(g.Root), g.Access))
            .OrderByDescending(g => g.Root.Length)
            .ToList();
    }

    public FileAccessLevel Evaluate(string path)
    {
        var full = Normalize(path);
        foreach (var (root, access) in _grants)
        {
            if (IsSameOrInside(full, root))
            {
                return access;
            }
        }

        return FileAccessLevel.None;
    }

    public bool CanRead(string path) => Evaluate(path) >= FileAccessLevel.Read;

    public bool CanWrite(string path) => Evaluate(path) == FileAccessLevel.ReadWrite;

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("La ruta debe estar totalmente cualificada (absoluta); no se resuelve contra el directorio actual.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsSameOrInside(string path, string root)
    {
        if (path.Equals(root, Comparison))
        {
            return true;
        }

        // Compare on a separator boundary so that "C:\repo" never matches "C:\repo2". A drive root such as "C:\"
        // already ends with a separator after trimming ("C:\" stays "C:\"), hence the extra check.
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }
}
