using System.Runtime.Versioning;
using AgentStudio.Infrastructure.Processes.Isolation;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>
/// Whether a path lies inside one of the untrusted roots, compared by where Windows really reaches it (ADR-0018, addendum of
/// 2026-11-12): links and junctions resolved at any depth of the path (<c>GetFinalPathNameByHandle</c>, which also expands short
/// 8.3 names and gives the real case), the <c>\\?\</c> prefix and trailing separators dropped, case ignored, and a folder only
/// contains what starts with its name followed by a separator (<c>C:\proj</c> does not contain <c>C:\project2</c>). A path that
/// cannot be opened is compared by the longest part of it that can, plus the rest as written. Hard links cannot be told apart:
/// a second name for a file is a second name, and that is a residual the ADR writes down.
/// </summary>
internal static class UntrustedPaths
{
    /// <summary>
    /// The roots as they are compared: resolved to where they really are. A root that is not a full path is dropped, and so is one
    /// that is too broad (<see cref="BroadFolders"/>: the profile of the person, the program folders, the system or a whole drive: it would hide the programs the person installed). Empty,
    /// and no access to the disk, when there are no roots.
    /// </summary>
    internal static IReadOnlyList<string> Resolve(IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0)
        {
            return [];
        }

        var resolved = new List<string>(roots.Count);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !IsFullyQualified(root))
            {
                continue;
            }

            var canonical = Canonical(root);
            if (canonical.Length == 0 || BroadFolders.IsTooBroadCanonical(canonical))
            {
                continue;
            }

            resolved.Add(canonical);
        }

        return resolved;
    }

    /// <summary>Whether <paramref name="path"/> lies inside one of <paramref name="resolvedRoots"/> (from <see cref="Resolve"/>), or is one.</summary>
    internal static bool IsInside(string path, IReadOnlyList<string> resolvedRoots)
    {
        if (resolvedRoots.Count == 0)
        {
            return false;
        }

        var canonical = Canonical(path);
        foreach (var root in resolvedRoots)
        {
            if (IsInsideCanonical(canonical, root))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsInsideCanonical(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || (path.Length > root.Length
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && path[root.Length] == '\\');

    /// <summary>Where <paramref name="path"/> really is: resolved, with the real case, without prefix and without a trailing separator.</summary>
    internal static string Canonical(string path)
    {
        var plain = StripLongPathPrefix(path.Trim());
        string full;
        try
        {
            full = Path.GetFullPath(plain);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return TrimSeparators(plain);
        }

        return TrimSeparators(OperatingSystem.IsWindows() ? ResolveExisting(full) : full);
    }

    [SupportedOSPlatform("windows")]
    private static string ResolveExisting(string full)
    {
        // The longest existing part is resolved; what does not exist yet (or cannot be opened) follows as written.
        var rest = new Stack<string>();
        var current = full;
        while (true)
        {
            var final = IntegrityLabels.FinalPath(current);
            if (final is not null)
            {
                return rest.Count == 0 ? final : Path.Combine(final, string.Join('\\', rest));
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                return full;
            }

            rest.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    private static string StripLongPathPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path[4..]
        : path;

    private static string TrimSeparators(string path) => path.TrimEnd('\\', '/');

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(StripLongPathPrefix(path.Trim()));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
