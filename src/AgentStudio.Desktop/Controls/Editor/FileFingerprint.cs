using System.IO;
using System.Security.Cryptography;

namespace AgentStudio.Desktop.Controls.Editor;

/// <summary>
/// What a file looked like when the editor loaded or last saved it. The hash of the bytes decides whether the file
/// changed; length and last write time are informative (a touched-but-identical file is not a conflict).
/// </summary>
public sealed record FileFingerprint(long Length, DateTime LastWriteUtc, string Hash)
{
    public static FileFingerprint FromBytes(byte[] bytes, DateTime lastWriteUtc) =>
        new(bytes.LongLength, lastWriteUtc, Convert.ToHexString(SHA256.HashData(bytes)));

    /// <summary>Reads the file and fingerprints it; null when it does not exist.</summary>
    public static async Task<FileFingerprint?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return FromBytes(bytes, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>The file on disk is not the one the editor loaded (edited by an agent, another program, or deleted).</summary>
public sealed class FileChangedOnDiskException : IOException
{
    public FileChangedOnDiskException(string path, bool deleted)
        : base(deleted
            ? $"The file '{path}' was deleted on disk since it was opened."
            : $"The file '{path}' changed on disk since it was opened.")
    {
        FilePath = path;
        Deleted = deleted;
    }

    public string FilePath { get; }

    public bool Deleted { get; }
}
