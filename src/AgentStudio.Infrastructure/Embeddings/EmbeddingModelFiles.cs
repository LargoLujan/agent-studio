using System.Globalization;
using System.Security.Cryptography;
using AgentStudio.Application.Semantic;

namespace AgentStudio.Infrastructure.Embeddings;

/// <summary>
/// The check of the files of a pinned model (docs/17 decision 9; F12-6): a file is good only with the pinned size and
/// SHA-256. Shared by the source of the embedder, which loads only the bytes it verified, and by the installer, which
/// moves a download into place only when it verifies. Problems are in Spanish: the screens show them as they are.
/// </summary>
internal static class EmbeddingModelFiles
{
    private const int BufferBytes = 1 << 16;

    /// <summary>
    /// The state of every file of <paramref name="location"/>; the first one that is not ready decides it. With
    /// <paramref name="checks"/>, a file that kept its path, size and time is not hashed again.
    /// </summary>
    public static EmbeddingModelStatus Check(EmbeddingModelLocation location, CancellationToken ct, EmbeddingModelChecks? checks = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        foreach (var file in location.Files)
        {
            var (state, problem) = CheckFile(location, file, ct, checks);
            if (state != EmbeddingModelState.Ready)
            {
                return new EmbeddingModelStatus(state, location.ModelDirectory, problem);
            }
        }

        return Ready(location);
    }

    public static EmbeddingModelStatus Ready(EmbeddingModelLocation location) => new(EmbeddingModelState.Ready, location.ModelDirectory);

    /// <summary>
    /// The state of one file: missing, of another size or hash, unreadable, or ready. It hashes the file as it reads it,
    /// unless <paramref name="checks"/> remembers it with the same size and time (<see cref="EmbeddingModelChecks"/>).
    /// </summary>
    public static (EmbeddingModelState State, string? Problem) CheckFile(EmbeddingModelLocation location, EmbeddingModelFile file, CancellationToken ct, EmbeddingModelChecks? checks = null)
    {
        var path = location.PathOf(file);
        var installs = EmbeddingModelFolderChanges.Version(location.ModelDirectory);
        if (checks?.Known(path, installs) is { } known)
        {
            return known;
        }

        try
        {
            using var stream = OpenRead(path);
            if (stream is null)
            {
                return (EmbeddingModelState.Missing, MissingText(file));
            }

            // The size and time of the file this handle reads: no one can write it while it is open.
            var length = stream.Length;
            var lastWriteUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            var result = Hash(stream, file, ct);
            checks?.Remember(path, length, lastWriteUtc, installs, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (EmbeddingModelState.Unloadable, UnreadableText(file, ex));
        }
    }

    /// <summary>Whether the content of <paramref name="stream"/> gives the pinned size and SHA-256 of <paramref name="file"/>.</summary>
    private static (EmbeddingModelState State, string? Problem) Hash(FileStream stream, EmbeddingModelFile file, CancellationToken ct)
    {
        if (stream.Length != file.Bytes)
        {
            return (EmbeddingModelState.Mismatch, SizeText(file, stream.Length));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        if (total != file.Bytes)
        {
            return (EmbeddingModelState.Mismatch, SizeText(file, total));
        }

        return string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal)
            ? (EmbeddingModelState.Ready, null)
            : (EmbeddingModelState.Mismatch, HashText(file));
    }

    /// <summary>
    /// The bytes of <paramref name="file"/> when they give the pinned size and SHA-256, read once: what is loaded is exactly
    /// what was hashed, so nothing can change the file between the check and the load. Null, with the state and the
    /// problem, when they do not.
    /// </summary>
    public static byte[]? ReadVerified(EmbeddingModelLocation location, EmbeddingModelFile file, CancellationToken ct, out EmbeddingModelStatus status)
    {
        var path = location.PathOf(file);
        byte[] bytes;
        try
        {
            using var stream = OpenRead(path);
            if (stream is null)
            {
                status = new EmbeddingModelStatus(EmbeddingModelState.Missing, location.ModelDirectory, MissingText(file));
                return null;
            }

            if (stream.Length != file.Bytes)
            {
                status = new EmbeddingModelStatus(EmbeddingModelState.Mismatch, location.ModelDirectory, SizeText(file, stream.Length));
                return null;
            }

            bytes = new byte[file.Bytes];
            var offset = 0;
            while (offset < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, offset, Math.Min(BufferBytes, bytes.Length - offset));
                if (read == 0)
                {
                    status = new EmbeddingModelStatus(EmbeddingModelState.Mismatch, location.ModelDirectory, SizeText(file, offset));
                    return null;
                }

                offset += read;
            }

            // Opened without write sharing, but a file may still grow if someone held it open for writing before.
            if (stream.ReadByte() != -1)
            {
                status = new EmbeddingModelStatus(EmbeddingModelState.Mismatch, location.ModelDirectory, SizeText(file, stream.Length));
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = new EmbeddingModelStatus(EmbeddingModelState.Unloadable, location.ModelDirectory, UnreadableText(file, ex));
            return null;
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal))
        {
            status = new EmbeddingModelStatus(EmbeddingModelState.Mismatch, location.ModelDirectory, HashText(file));
            return null;
        }

        status = Ready(location);
        return bytes;
    }

    /// <summary>
    /// What the files of <paramref name="location"/> look like now (size and time of each, or missing) and how many installs
    /// their folder has had (<see cref="EmbeddingModelFolderChanges"/>), without reading them: a failed opening is
    /// remembered only while this stays the same.
    /// </summary>
    public static string Stamp(EmbeddingModelLocation location)
    {
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"installs:{EmbeddingModelFolderChanges.Version(location.ModelDirectory)}") };
        foreach (var file in location.Files)
        {
            var info = new FileInfo(location.PathOf(file));
            parts.Add(info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{file.FileName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}")
                : file.FileName + ":-");
        }

        return string.Join('|', parts);
    }

    public static string MissingText(EmbeddingModelFile file) =>
        $"El modelo no está descargado: falta el fichero «{file.FileName}».";

    public static string SizeText(EmbeddingModelFile file, long bytes) => string.Create(
        CultureInfo.InvariantCulture,
        $"El fichero «{file.FileName}» mide {bytes} bytes y el fijado mide {file.Bytes}: no coincide con su huella y no se carga.");

    public static string HashText(EmbeddingModelFile file) =>
        $"El fichero «{file.FileName}» no da su SHA-256 fijado: no coincide con su huella y no se carga.";

    private static string UnreadableText(EmbeddingModelFile file, Exception ex) =>
        $"No se ha podido leer el fichero «{file.FileName}» del modelo: {ex.Message}";

    /// <summary>
    /// The file, opened to read with no writer allowed, or null when it does not exist. The installer may still replace it
    /// (a rename) while it is read: the reader goes on with the content it opened.
    /// </summary>
    private static FileStream? OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferBytes, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
