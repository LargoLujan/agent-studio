using System.Buffers;
using System.Formats.Tar;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <summary>What <see cref="TarSnapshotExtractor"/> accepts at most: the limits only stop an unbounded archive.</summary>
public sealed record TarSnapshotLimits(long MaxTotalBytes, int MaxEntries)
{
    /// <summary>16 GiB of file content and one million entries.</summary>
    public static TarSnapshotLimits Default { get; } = new(16L * 1024 * 1024 * 1024, 1_000_000);
}

/// <summary>
/// What an extraction wrote. <paramref name="Comment"/> is the <c>comment</c> of the pax global header, where
/// <c>git archive</c> records the id of the commit it archived.
/// </summary>
public sealed record TarSnapshot(string? Comment, int Files, int Directories, long Bytes);

/// <summary>
/// Writes the tar <c>git archive</c> produces into a folder (ADR-0017), and nothing else: only regular files and folders,
/// only inside the destination, never over an existing file. An entry with an absolute path, a <c>..</c> or an empty
/// segment, a drive, a stream or a device name, a character Windows does not keep in a name, or any link or device is
/// refused before it is written, with <see cref="InvalidDataException"/>; so is an archive over the limits.
/// </summary>
public static class TarSnapshotExtractor
{
    private static readonly SearchValues<char> ForbiddenInNames = SearchValues.Create("<>:\"|?*\\");

    // Windows opens these as devices whatever their extension ("con.txt", "NUL").
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <param name="archive">The tar stream; it is read to the end-of-archive marker and not closed.</param>
    /// <param name="destination">An existing folder, normally empty.</param>
    /// <exception cref="InvalidDataException">The archive holds an entry a snapshot refuses, or is over the limits.</exception>
    public static TarSnapshot Extract(Stream archive, string destination, TarSnapshotLimits limits)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(limits);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The destination '{root}' does not exist.");
        }

        string? comment = null;
        int files = 0, folders = 0, entries = 0;
        long bytes = 0;
        using var reader = new TarReader(archive, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (++entries > limits.MaxEntries)
            {
                throw new InvalidDataException($"The archive has more than {limits.MaxEntries} entries; nothing more is extracted.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.GlobalExtendedAttributes:
                    if (entry is PaxGlobalExtendedAttributesTarEntry global && global.GlobalExtendedAttributes.TryGetValue("comment", out var value))
                    {
                        comment = value;
                    }

                    break;

                case TarEntryType.Directory:
                    CreateFolder(Target(root, entry.Name, isDirectory: true), entry.Name);
                    folders++;
                    break;

                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    var path = Target(root, entry.Name, isDirectory: false);
                    CreateFolder(Path.GetDirectoryName(path)!, entry.Name);
                    bytes += WriteFile(entry, path, limits.MaxTotalBytes - bytes);
                    files++;
                    break;

                default:
                    throw new InvalidDataException(
                        $"The archive holds '{entry.Name}', which is a {entry.EntryType} entry; a snapshot only holds regular files and folders, so nothing more is extracted.");
            }
        }

        return new TarSnapshot(comment, files, folders, bytes);
    }

    private static string Target(string root, string name, bool isDirectory)
    {
        var relative = isDirectory && name.EndsWith('/') ? name[..^1] : name;
        if (relative.Length == 0 || relative[0] == '/')
        {
            throw Refused(name, "it is absolute or empty");
        }

        var segments = relative.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw Refused(name, "it has an empty, '.' or '..' segment");
            }

            if (segment.AsSpan().IndexOfAny(ForbiddenInNames) >= 0 || segment.AsSpan().IndexOfAnyInRange('\0', '\u001f') >= 0)
            {
                throw Refused(name, "it has a character Windows does not keep in a name (a drive, a stream, a backslash or a control character)");
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw Refused(name, "a segment ends with a dot or a space, which Windows drops");
            }

            if (DeviceNames.Contains(segment.Split('.')[0].TrimEnd(' ')))
            {
                throw Refused(name, "a segment is a device name");
            }
        }

        var full = Path.GetFullPath(Path.Join(root, string.Join(Path.DirectorySeparatorChar, segments)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw Refused(name, "it resolves outside the destination");
    }

    private static void CreateFolder(string path, string name)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"The folder of '{name}' cannot be created: {ex.Message}", ex);
        }
    }

    private static long WriteFile(TarEntry entry, string path, long remaining)
    {
        if (entry.Length > remaining)
        {
            throw new InvalidDataException($"'{entry.Name}' takes the archive over its size limit; nothing more is extracted.");
        }

        if (Path.Exists(path))
        {
            throw new InvalidDataException($"'{entry.Name}' appears twice in the archive (perhaps with another case); it is not overwritten.");
        }

        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
        if (entry.DataStream is null)
        {
            return 0;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long written = 0;
            int read;
            while ((read = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > remaining)
                {
                    throw new InvalidDataException($"'{entry.Name}' takes the archive over its size limit; nothing more is extracted.");
                }

                output.Write(buffer, 0, read);
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static InvalidDataException Refused(string name, string reason) =>
        new($"The archive holds '{name}', which a snapshot refuses because {reason}; nothing more is extracted.");
}
