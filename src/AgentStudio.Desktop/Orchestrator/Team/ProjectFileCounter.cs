using System.IO;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>The files of a project, counted up to a cap (<see cref="ProjectFileCounter"/>).</summary>
/// <param name="Files">How many files were counted; the cap when <paramref name="IsTruncated"/>.</param>
/// <param name="IsTruncated">The project has more files than the cap, so <paramref name="Files"/> is a floor.</param>
public sealed record ProjectFileCount(int Files, bool IsTruncated);

/// <summary>
/// The size of a project for the recommendation of a model (docs/17 D-24, P49): how many files it has, counted by walking its
/// folder once and stopping at <see cref="DefaultMaxFiles"/>, so a huge repository is never walked in full. It hides what the
/// explorer hides (<see cref="ExplorerFilter"/>: <c>.git</c>, <c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>.vs</c>) and the folder
/// of Agent Studio itself (<see cref="StateFolder"/>), which is not the project; it does not follow folder links, and it skips a
/// folder it cannot read. It counts and keeps no path. The builder keeps the result for the life of the document: nothing here is
/// asked on an edit.
/// </summary>
public static class ProjectFileCounter
{
    /// <summary>The most files counted: the size of the index of the explorer (<see cref="ProjectFileIndexer.DefaultMaxFiles"/>).</summary>
    public const int DefaultMaxFiles = ProjectFileIndexer.DefaultMaxFiles;

    /// <summary>The folder where Agent Studio keeps its own state in a project (log, memory, layout, journal): it is not counted.</summary>
    public const string StateFolder = ".agentstudio";

    /// <summary>
    /// Counts the files under <paramref name="root"/>, breadth first, until <paramref name="maxFiles"/>; one more file than the cap
    /// makes the count a floor (<see cref="ProjectFileCount.IsTruncated"/>).
    /// </summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> does not exist.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static ProjectFileCount Count(string root, int maxFiles = DefaultMaxFiles, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"The project folder '{root}' does not exist.");
        }

        var files = 0;
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
                        if (!string.Equals(child.Name, StateFolder, StringComparison.OrdinalIgnoreCase) && !ProjectFileIndexer.IsLink(child))
                        {
                            pending.Enqueue(child);
                        }

                        continue;
                    }

                    if (files == maxFiles)
                    {
                        return new ProjectFileCount(files, IsTruncated: true);
                    }

                    files++;
                }
            }
            catch (IOException)
            {
                // A folder that vanished or cannot be read while walking: the rest of the project still says how big it is.
            }
            catch (UnauthorizedAccessException)
            {
                // No permission for this folder: skip it, as the explorer does.
            }
        }

        return new ProjectFileCount(files, IsTruncated: false);
    }
}
