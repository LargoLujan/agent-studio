using System.Diagnostics;
using System.IO;
using System.Text;

namespace AgentStudio.Providers.Cli;

/// <summary>
/// Per-run temporary folder under the user's own application data (<c>%LOCALAPPDATA%\AgentStudio\runs\&lt;id&gt;</c>)
/// instead of the shared <c>%TEMP%</c> root, for files handed to a CLI by path (system instructions, output schema).
/// </summary>
public static class RunTempFolder
{
    /// <summary>Parent of every per-run folder.</summary>
    public static string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "runs");

    /// <summary>Run folders older than this (7 days, longer than any sensible run budget) are leftovers of a crash or of a failed delete and are swept.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// Best-effort sweep: deletes the run folders under <paramref name="baseDirectory"/> last written more than
    /// <paramref name="maxAge"/> before <paramref name="now"/>. Links are never followed (a link is only unlinked).
    /// </summary>
    /// <param name="stopAt">Ancestor (excluded) up to which a link on the way cancels the sweep; defaults to %LOCALAPPDATA%.</param>
    public static void SweepStale(string baseDirectory, TimeSpan maxAge, DateTimeOffset now, string? stopAt = null)
    {
        try
        {
            if (!Directory.Exists(baseDirectory) || HasLinkOnPath(baseDirectory, stopAt ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            {
                return;
            }

            foreach (var child in new DirectoryInfo(baseDirectory).EnumerateDirectories())
            {
                if (now - new DateTimeOffset(child.LastWriteTimeUtc, TimeSpan.Zero) <= maxAge)
                {
                    continue;
                }

                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    child.Delete();
                }
                else
                {
                    DeleteQuietly(child.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Run temp sweep failed: {ex.Message}");
        }
    }

    // True when the base or one of its existing ancestors below stopAt is a reparse point (junction/symlink).
    private static bool HasLinkOnPath(string baseDirectory, string stopAt)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        for (var current = new DirectoryInfo(Path.GetFullPath(baseDirectory)); current is not null; current = current.Parent)
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), stop, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Creates a new, empty per-run folder.</summary>
    public static string Create()
    {
        SweepStale(BaseDirectory, StaleAfter, DateTimeOffset.UtcNow);
        var directory = Path.Combine(BaseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Writes a new file (<c>FileMode.CreateNew</c>: never overwrites) with UTF-8 without BOM.</summary>
    public static async Task WriteNewFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the folder; retries once on <see cref="IOException"/> and logs if it still fails.</summary>
    public static void DeleteQuietly(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt == 0)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Run temp folder '{directory}' could not be deleted: {ex.Message}");
                return;
            }
        }
    }
}
