using AgentStudio.Application.Projects;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// The files of a project's <c>.agentstudio</c> folder that other modules write (the prompt journal, the canvas layout and
/// the verification profile), through the same policy that opens the project (<see cref="ProjectStorageGuard"/>; ADR-0010,
/// addenda of 2026-10-18). Each call takes the final path of the project root again, so the project does not need to be
/// open in this instance:
/// <list type="bullet">
/// <item><c>.agentstudio</c> and the folder of the file are checked before they are created and after, and nothing is
/// created or opened before that;</item>
/// <item>a file to append to is opened without following a link and checked through that same handle: a regular file
/// with a single name, at its place under the final path of the root;</item>
/// <item>a file to replace is written whole in <c>tmp</c> first, by a handle checked like the evidence store's, and then
/// moved over the file, which must not be a link or a second name either;</item>
/// <item>a file to read (<see cref="IProjectStorageReader"/>: the journal the daily summary reads, docs/14 D-31) is
/// opened without following a link and checked through that same handle like a file to append, with each folder on the
/// way checked first; nothing is created, so a missing folder is a missing file.</item>
/// </list>
/// </summary>
/// <remarks>
/// A process of the same user running at the same time can still change a folder between the check and the write: that
/// race is a residual of ADR-0018 (point 2), narrowed by the checks on the handles.
/// </remarks>
public sealed class ProjectStorageFiles : IProjectStorageFiles, IProjectStorageReader
{
    public Stream OpenForRead(string projectRoot, string relativePath)
    {
        var (storage, file) = Locate(projectRoot, relativePath);

        // Nothing is created to read: each folder on the way is checked where it exists, and a missing one is a missing file.
        ProjectStorageGuard.CheckFolder(storage, storage.DataDirectory);
        var folder = Path.GetDirectoryName(file)!;
        if (!string.Equals(folder, storage.DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            ProjectStorageGuard.CheckFolder(storage, folder);
        }

        return ProjectStorageGuard.OpenForRead(storage, file);
    }

    public Stream OpenForAppend(string projectRoot, string relativePath)
    {
        var (storage, file) = Locate(projectRoot, relativePath);
        CreateFolderOf(storage, file);
        return ProjectStorageGuard.OpenForAppend(storage, file);
    }

    public void Replace(string projectRoot, string relativePath, ReadOnlySpan<byte> content)
    {
        var (storage, target) = Locate(projectRoot, relativePath);
        CreateFolderOf(storage, target);
        ProjectStorageGuard.CreateFolder(storage, storage.TempDirectory);
        ProjectStorageGuard.CheckFile(storage, target);

        // Same volume as the target, so the move is a rename that happens entirely or not at all.
        var temp = Path.Combine(storage.TempDirectory, Path.GetFileName(target) + "-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ProjectStorageGuard.CheckOpened(storage, file.SafeFileHandle, temp);
                file.Write(content);
                file.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            // After the move there is nothing here any more; after a failure this removes the partial file.
            TryDelete(temp);
        }
    }

    /// <summary>The storage of the project at <paramref name="projectRoot"/> and the full path of <paramref name="relativePath"/> in it.</summary>
    private static (ProjectStorage Storage, string File) Locate(string projectRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var parts = relativePath.Split('/', '\\');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException(
                $"'{relativePath}' is not a file of the project's storage: it is a file name, or a folder and a file name with '/' between them.",
                nameof(relativePath));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The project folder '{root}' does not exist.");
        }

        var storage = ProjectStorage.For(string.Empty, root, ProjectStorageGuard.FinalRoot(root));
        return (storage, Path.Combine([storage.DataDirectory, .. parts]));
    }

    /// <summary>The data folder and, for a file in one of its folders, that folder: each checked before it is created and after.</summary>
    private static void CreateFolderOf(ProjectStorage storage, string file)
    {
        ProjectStorageGuard.CreateDataFolder(storage);
        var folder = Path.GetDirectoryName(file)!;
        if (!string.Equals(folder, storage.DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            ProjectStorageGuard.CreateFolder(storage, folder);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover in tmp/ is harmless: it is never read and the folder is excluded from git.
        }
    }
}
