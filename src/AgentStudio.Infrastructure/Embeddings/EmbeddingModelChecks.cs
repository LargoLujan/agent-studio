using System.Collections.Concurrent;
using AgentStudio.Application.Semantic;

namespace AgentStudio.Infrastructure.Embeddings;

/// <summary>
/// The checks of the files of a model in this run of the app (decision of 2026-11-03, review of WP4): each result is
/// remembered by the path of the file, with the size and the time of last write (UTC) of the file that was hashed, both
/// read from the handle that hashed it. While a file keeps its path, size and time, <see cref="EmbeddingModelFiles.CheckFile"/>
/// gives its result again without reading it; if any of the three changes, or the model is installed again
/// (<see cref="EmbeddingModelFolderChanges"/>), it hashes the file again. Nothing is kept between runs, so the first check
/// after the app starts always hashes.
/// <list type="bullet">
/// <item>Only what the content decides is remembered: a size or a SHA-256 that are or are not the pinned ones. A missing
/// file or one that could not be read is checked again every time.</item>
/// <item>A same-user process could swap a file and give it the size and time of the old one; the check would then say
/// what it said before. Loading is not affected: the source always hashes the bytes it loads (ADR-0047 point 4).</item>
/// </list>
/// Safe to use from several threads.
/// </summary>
internal sealed class EmbeddingModelChecks
{
    private readonly ConcurrentDictionary<string, Remembered> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The result remembered for <paramref name="path"/> when the file there still has the size and time it had and its
    /// folder has had no install since (<paramref name="installs"/>, as <see cref="EmbeddingModelFolderChanges.Version"/>
    /// gives it now); null otherwise.
    /// </summary>
    public (EmbeddingModelState State, string? Problem)? Known(string path, long installs)
    {
        if (!_files.TryGetValue(path, out var remembered) || remembered.Installs != installs)
        {
            return null;
        }

        var file = new FileInfo(path);
        return file.Exists && file.Length == remembered.Length && file.LastWriteTimeUtc == remembered.LastWriteUtc
            ? (remembered.State, remembered.Problem)
            : null;
    }

    /// <summary>
    /// Remembers what the content of the file at <paramref name="path"/>, with this size and time, gave, after
    /// <paramref name="installs"/> installs of its folder (taken before the file was read).
    /// </summary>
    public void Remember(string path, long length, DateTime lastWriteUtc, long installs, (EmbeddingModelState State, string? Problem) result) =>
        _files[path] = new Remembered(length, lastWriteUtc, installs, result.State, result.Problem);

    private sealed record Remembered(long Length, DateTime LastWriteUtc, long Installs, EmbeddingModelState State, string? Problem);
}
