using System.IO;
using Microsoft.Data.Sqlite;

namespace AgentStudio.Tests.TestSupport;

/// <summary>
/// Deletes a temporary folder a test made, best effort and never through a link. Directory.Delete alone is not enough on
/// Windows: on a junction made without elevation it removes the link, then throws UnauthorizedAccessException (the
/// unmount it tries first is denied) and leaves the emptied parents behind; it refuses read-only files and folders; and
/// it gives up on a file that another handle still holds. So the links are removed as links first, at any depth and
/// without walking into them, the read-only bit is cleared only where it is set, and the delete is retried with a short
/// backoff after releasing the pooled SQLite connections. What still cannot be deleted is written to the test output
/// instead of failing the test.
/// </summary>
/// <remarks>
/// Attributes belong to the file, not to the name: clearing the read-only bit of a hard link clears it for its other
/// names too. The tests plant their hard-link victims inside their own temporary folders, so that stays inside them.
/// </remarks>
internal static class TempFolders
{
    private static readonly int[] BackoffMilliseconds = [50, 100, 200, 400];

    private static readonly EnumerationOptions OneLevel = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it, and returns whether it is gone. When the path itself is a
    /// link, only the link is removed.
    /// </summary>
    public static bool Delete(string path)
    {
        string? failure = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (DeleteOnce(path))
                {
                    return true;
                }

                failure = "it still exists after the delete";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (attempt == BackoffMilliseconds.Length)
            {
                break;
            }

            if (attempt == 0)
            {
                // A pooled connection keeps its database file open (SQLite does not share it for deletion).
                SqliteConnection.ClearAllPools();
            }

            Thread.Sleep(BackoffMilliseconds[attempt]);
        }

        Note($"Temporary folder left behind: '{path}' could not be deleted after {BackoffMilliseconds.Length + 1} attempts ({failure}).");
        return false;
    }

    private static bool DeleteOnce(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            RemoveLink(path, attributes);
        }
        else if (attributes.HasFlag(FileAttributes.Directory))
        {
            Prepare(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
        else
        {
            ClearReadOnly(new FileInfo(path));
            File.Delete(path);
        }

        // Directory.Delete says nothing when a folder is only marked for deletion, so look.
        return !Directory.Exists(path) && !File.Exists(path);
    }

    /// <summary>Removes the links under <paramref name="directory"/> as links and clears the read-only bits, never leaving the tree.</summary>
    private static void Prepare(DirectoryInfo directory)
    {
        ClearReadOnly(directory);
        foreach (var entry in directory.EnumerateFileSystemInfos("*", OneLevel).ToList())
        {
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    RemoveLink(entry.FullName, entry.Attributes);
                }
                else if (entry is DirectoryInfo child)
                {
                    Prepare(child);
                }
                else
                {
                    ClearReadOnly(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The delete that follows reports what is left.
            }
        }
    }

    // RemoveDirectory on a junction or a folder link and DeleteFile on a file link remove the link, never its target.
    private static void RemoveLink(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void ClearReadOnly(FileSystemInfo entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            entry.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    private static void Note(string message)
    {
        var output = TestContext.Current.TestOutputHelper;
        if (output is not null)
        {
            try
            {
                output.WriteLine(message);
                return;
            }
            catch (InvalidOperationException)
            {
                // The test is over: fall back to a diagnostic message.
            }
        }

        TestContext.Current.SendDiagnosticMessage(message);
    }
}
