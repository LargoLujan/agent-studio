using System.IO;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>The files of a project folder as relative paths ("/" separators).</summary>
/// <param name="RelativePaths">Every indexed file, shallow files before deep ones.</param>
/// <param name="IsTruncated">True when the folder holds more files than the cap, so some are missing here.</param>
public sealed record ProjectFileIndex(IReadOnlyList<string> RelativePaths, bool IsTruncated);

/// <summary>Walks a project folder once to list its files for "go to file".</summary>
public static class ProjectFileIndexer
{
    /// <summary>Largest number of files indexed; a bigger project is cut here and says so.</summary>
    public const int DefaultMaxFiles = 20_000;

    /// <summary>
    /// Lists the files under <paramref name="root"/>, breadth first so that a capped index keeps the shallow ones.
    /// It hides exactly what the explorer hides (<see cref="ExplorerFilter"/>) and never enters a hidden folder.
    /// Folder links (junctions, symbolic links) are not followed: they could loop or leave the project.
    /// Folders that cannot be read are skipped.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> does not exist.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static ProjectFileIndex Build(string root, int maxFiles = DefaultMaxFiles, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);

        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"The project folder '{root}' does not exist.");
        }

        var prefixLength = Path.TrimEndingDirectorySeparator(rootDirectory.FullName).Length + 1;
        var files = new List<string>();
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(rootDirectory);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    var isDirectory = entry is DirectoryInfo;
                    if (ExplorerFilter.IsHidden(entry.Name, isDirectory, directory.Name))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        if (!IsLink(child))
                        {
                            pending.Enqueue(child);
                        }

                        continue;
                    }

                    if (files.Count == maxFiles)
                    {
                        return new ProjectFileIndex(files, IsTruncated: true);
                    }

                    files.Add(entry.FullName[prefixLength..].Replace('\\', '/'));
                }
            }
            catch (IOException)
            {
                // A folder that vanished or cannot be read while walking: the rest of the project is still useful.
            }
            catch (UnauthorizedAccessException)
            {
                // No permission for this folder: skip it, like the explorer does.
            }
        }

        return new ProjectFileIndex(files, IsTruncated: false);
    }

    /// <summary>True for a symbolic link or junction (other reparse points, such as cloud placeholders, are plain folders).</summary>
    private static bool IsLink(DirectoryInfo directory)
    {
        if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        try
        {
            return directory.LinkTarget is not null;
        }
        catch (IOException)
        {
            return true; // Cannot tell what it points at: do not follow it.
        }
    }
}
