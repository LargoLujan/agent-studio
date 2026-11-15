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
/// Existing path components are inspected, resolving symbolic links and junctions before matching the target. A path
/// needs access both by its requested name and by its resolved target; the lower level wins. Grant targets are captured
/// at construction, so replacing a granted folder with a link cannot silently grant another tree. A missing suffix is
/// allowed for creation, after its existing parents have been checked. Unreadable components, unknown reparse points and
/// link cycles fail closed. This is a check of the current file system, not an atomic open: another process can still
/// replace a component after the check. It neither recognises hard-link aliases nor confines a provider process.
/// Grants which resolve to the same root use the lower access level. A grant that cannot be inspected rejects the policy
/// with <see cref="IOException"/> rather than disappearing and exposing a wider parent grant.
/// </remarks>
public sealed class PathAccessPolicy
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Sorted from the most specific (longest) root to the least, so the first match is the answer.
    private readonly List<(string Root, FileAccessLevel Access)> _grants;
    private readonly List<(string Root, FileAccessLevel Access)> _targets;

    public PathAccessPolicy(IEnumerable<PathGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var resolved = grants.Select(g =>
        {
            var root = Normalize(g.Root);
            if (!TryResolve(root, out var target))
            {
                // Dropping a failed restrictive grant could give its parent's wider access instead.
                throw new IOException("A grant root could not be resolved safely.");
            }

            return (Root: root, Target: target, g.Access);
        }).ToList();
        _grants = resolved.Select(g => (g.Root, g.Access))
            .OrderByDescending(g => g.Root.Length)
            .ThenBy(g => g.Access)
            .ToList();
        _targets = resolved.Select(g => (Root: g.Target, g.Access))
            .OrderByDescending(g => g.Root.Length)
            .ThenBy(g => g.Access)
            .ToList();
    }

    public FileAccessLevel Evaluate(string path)
    {
        var full = Normalize(path);
        var namedAccess = AccessFor(full, _grants);
        if (namedAccess == FileAccessLevel.None || !TryResolve(full, out var target))
        {
            return FileAccessLevel.None;
        }

        var targetAccess = AccessFor(target, _targets);
        return (FileAccessLevel)Math.Min((int)namedAccess, (int)targetAccess);
    }

    private static FileAccessLevel AccessFor(string path, List<(string Root, FileAccessLevel Access)> grants)
    {
        foreach (var (root, access) in grants)
        {
            if (IsSameOrInside(path, root))
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
            throw new ArgumentException("The path must be fully qualified; it is not resolved against the current directory.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool TryResolve(string path, out string target)
    {
        target = path;
        try
        {
            // Resolve one hop at a time: a target can itself have a link in an intermediate component.
            // A finite bound also stops expanding cycles whose full path never repeats.
            const int maxLinks = 63;
            for (var links = 0; links <= maxLinks; links++)
            {
                var root = Path.GetPathRoot(target)!;
                var rootAttributes = File.GetAttributes(root);
                if (!rootAttributes.HasFlag(FileAttributes.Directory) || rootAttributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                var parts = target[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                var current = root;
                var redirected = false;
                for (var index = 0; index < parts.Length; index++)
                {
                    current = Path.Combine(current, parts[index]);
                    FileAttributes attributes;
                    try
                    {
                        // Exists hides access errors and can report false for a dangling link.
                        attributes = File.GetAttributes(current);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // No component below a missing parent exists yet. Keep that suffix for a future creation.
                        return true;
                    }

                    if (index < parts.Length - 1 && !attributes.HasFlag(FileAttributes.Directory))
                    {
                        return false;
                    }

                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        FileSystemInfo entry = attributes.HasFlag(FileAttributes.Directory)
                            ? new DirectoryInfo(current) : new FileInfo(current);
                        var next = entry.ResolveLinkTarget(returnFinalTarget: false);
                        if (next is null || links == maxLinks)
                        {
                            return false;
                        }

                        var suffix = index < parts.Length - 1 ? Path.Combine(parts[(index + 1)..]) : string.Empty;
                        target = Normalize(Path.Combine(next.FullName, suffix));
                        redirected = true;
                        break;
                    }
                }

                if (!redirected)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }

        return false;
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
