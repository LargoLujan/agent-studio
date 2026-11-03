using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Providers.Skills;

/// <summary>
/// Windows path-alias defences for the protected-folder check: non-canonical spellings are rejected up front and the
/// physical identity of a path (final path of an open directory handle, which resolves junctions, symlinks, 8.3 names,
/// the <c>\\?\</c> prefix and <c>\\localhost\C$</c> aliases) is what gets compared.
/// </summary>
internal static partial class PathIdentity
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;
    private const uint ShareAll = 7;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, Span<char> path, uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint WriteLow;
        public uint WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>
    /// Appends <paramref name="line"/> to the text file unless it already has that line. The file is opened once and the
    /// hard-link count is checked on that same handle: a file with more than one link (it could be a hard link to a
    /// protected file) is left untouched. Returns false in that case.
    /// </summary>
    public static bool TryAppendLineOnce(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (OperatingSystem.IsWindows()
            && (!GetFileInformationByHandle(stream.SafeFileHandle, out var information) || information.NumberOfLinks != 1))
        {
            return false;
        }

        string existing;
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            existing = reader.ReadToEnd();
        }

        if (existing.Split('\n').Any(l => l.Trim() == line))
        {
            return true;
        }

        var prefix = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
        stream.Seek(0, SeekOrigin.End);
        stream.Write(new System.Text.UTF8Encoding(false).GetBytes(prefix + line + "\n"));
        return true;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> for a spelling that can alias another path: device or extended
    /// prefixes (<c>\\?\</c>, <c>\\.\</c>), UNC paths, and segments ending in a dot or a space.
    /// </summary>
    public static void RequireCanonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Empty path.");
        }

        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing non-canonical path '{path}': device, extended-length and UNC paths are not accepted.");
        }

        foreach (var segment in normalized.Split('\\'))
        {
            if (segment is "." or ".." || segment.Length == 0)
            {
                continue;
            }

            if (segment[^1] is '.' or ' ')
            {
                throw new InvalidOperationException($"Refusing non-canonical path '{path}': a segment ends in a dot or a space.");
            }
        }
    }

    /// <summary>
    /// Physical path of <paramref name="path"/>: the deepest existing ancestor is resolved through a directory handle and
    /// the part that does not exist yet is appended. Returns null when it cannot be resolved (non-Windows, no access).
    /// </summary>
    public static string? FinalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var existing = full;
        var remainder = new Stack<string>();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return null;
            }

            remainder.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = ResolveExisting(existing);
        if (resolved is null)
        {
            return null;
        }

        while (remainder.Count > 0)
        {
            resolved = Path.Combine(resolved, remainder.Pop());
        }

        return resolved;
    }

    private static string? ResolveExisting(string existing)
    {
        using var handle = CreateFile(existing, 0, ShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
        {
            return null;
        }

        string text;
        if (length > buffer.Length)
        {
            var big = new char[length + 1];
            var second = GetFinalPathNameByHandle(handle, big, (uint)big.Length, 0);
            if (second == 0 || second >= big.Length)
            {
                return null;
            }

            text = new string(big, 0, (int)second);
        }
        else
        {
            text = new string(buffer[..(int)length]);
        }

        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + text[8..];
        }

        return text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }
}
