using AgentStudio.Application.Graph;
using AgentStudio.Domain.Common;

namespace AgentStudio.GraphWorker;

/// <summary>Why a path that came from a file of the repository cannot be opened (docs/20 D-16).</summary>
internal enum PathProblem
{
    None,

    /// <summary>Absolute, rooted, a drive, UNC, or leading out of the copy through <c>..</c>.</summary>
    OutsideRoot,

    /// <summary>Not a path that can be read safely: a device name, a character that is not allowed, too long.</summary>
    Invalid,

    /// <summary>A link or a junction is on the way.</summary>
    Link,

    /// <summary>The file is not in the copy.</summary>
    Missing,
}

internal static class PathProblems
{
    /// <summary>The gap code that tells a problem.</summary>
    public static string Code(PathProblem problem) => problem switch
    {
        PathProblem.OutsideRoot => GraphGapCodes.OutsideRoot,
        PathProblem.Link => GraphGapCodes.Link,
        PathProblem.Missing => GraphGapCodes.Missing,
        _ => GraphGapCodes.InvalidPath,
    };
}

/// <summary>
/// The only way the analysis reaches a file (docs/20 D-16, F6-12): every path that came from a file of the repository (the projects of a solution, the
/// <c>ProjectReference</c> of a project) is resolved against the folder of the file that names it, collapsed lexically, and must end up inside the root of
/// the copy; the result is checked with <see cref="RepositoryPaths.IsSafeRelative"/> and, on disk, no segment of it may be a link or a junction. A hostile
/// path (<c>..\..\secret.csproj</c>, <c>C:\Windows\x.csproj</c>, <c>\\server\share</c>) is never opened and never copied into the model.
/// </summary>
internal sealed class RepositoryFiles(string root)
{
    private readonly string _root = Path.GetFullPath(root);

    /// <summary>The folder of the copy.</summary>
    public string Root => _root;

    /// <summary>
    /// The path <paramref name="raw"/> names, relative to the root and in forward slashes. <paramref name="baseFile"/> is the file that says it (relative
    /// to the root, forward slashes), or empty for the root itself: the target is relative to its folder.
    /// </summary>
    public static PathProblem TryResolve(string baseFile, string raw, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1024 || UntrustedText.HasHidden(raw))
        {
            return PathProblem.Invalid;
        }

        var text = raw.Trim().Replace('\\', '/');

        // An absolute path, a drive (C:foo too), a stream, a UNC or a device path: never a path of the copy.
        if (text.StartsWith('/') || text.Contains(':') || text.StartsWith("~", StringComparison.Ordinal))
        {
            return PathProblem.OutsideRoot;
        }

        var segments = new List<string>();
        var slash = baseFile.LastIndexOf('/');
        if (slash > 0)
        {
            segments.AddRange(baseFile[..slash].Split('/'));
        }

        foreach (var segment in text.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return PathProblem.OutsideRoot;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        relative = string.Join('/', segments);
        if (!RepositoryPaths.IsSafeRelative(relative))
        {
            relative = string.Empty;
            return PathProblem.Invalid;
        }

        return PathProblem.None;
    }

    /// <summary>The full path of a relative path of the copy, after its shape is checked once more: nothing leaves the root by construction.</summary>
    public string Full(string relative)
    {
        if (!RepositoryPaths.IsSafeRelative(relative))
        {
            throw new ArgumentException("Not a relative path of the copy.", nameof(relative));
        }

        var full = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The path leads out of the copy.", nameof(relative));
        }

        return full;
    }

    /// <summary>Whether a file or folder exists at the path and no segment of it is a link or a junction.</summary>
    public PathProblem Probe(string relative, bool directory)
    {
        var current = _root;
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            FileSystemInfo info = directory || i < segments.Length - 1 ? new DirectoryInfo(current) : new FileInfo(current);
            try
            {
                if (!info.Exists)
                {
                    return PathProblem.Missing;
                }

                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                {
                    return PathProblem.Link;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PathProblem.Missing;
            }
        }

        return PathProblem.None;
    }

    /// <summary>
    /// Opens a regular file of the copy for reading. The path was resolved by <see cref="TryResolve"/>; here it is probed on disk (no link on the way) and
    /// what was opened is checked again: it must be a regular file of at most <paramref name="maxBytes"/> bytes.
    /// </summary>
    /// <exception cref="IOException">The file is a link, is too long or could not be opened.</exception>
    public FileStream OpenRead(string relative, long maxBytes)
    {
        if (Probe(relative, directory: false) != PathProblem.None)
        {
            throw new IOException("The file is not a regular file of the copy.");
        }

        var stream = new FileStream(Full(relative), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        try
        {
            if (stream.Length > maxBytes || new FileInfo(stream.Name).LinkTarget is not null
                || File.GetAttributes(stream.SafeFileHandle).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The file is a link or is longer than the limit.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The subfolders and files of a folder of the copy, links left out, in ordinal order; the folder is relative to the root.</summary>
    public (IReadOnlyList<string> Folders, IReadOnlyList<string> Files, bool SawLink) List(string relativeFolder)
    {
        var folders = new List<string>();
        var files = new List<string>();
        var sawLink = false;
        var path = relativeFolder.Length == 0 ? _root : Full(relativeFolder);
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                {
                    sawLink = true;
                    continue;
                }

                var relative = relativeFolder.Length == 0 ? entry.Name : relativeFolder + "/" + entry.Name;
                if (!RepositoryPaths.IsSafeRelative(relative))
                {
                    continue;
                }

                (entry is DirectoryInfo ? folders : files).Add(relative);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed has nothing to read.
        }

        folders.Sort(StringComparer.Ordinal);
        files.Sort(StringComparer.Ordinal);
        return (folders, files, sawLink);
    }
}
