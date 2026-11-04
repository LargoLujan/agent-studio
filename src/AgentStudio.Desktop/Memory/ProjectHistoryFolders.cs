using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// The snapshots of the planner's tool <c>search_project_history</c> (docs/17 D-06; ADR-0024, addendum of 2026-11-03),
/// under the app's local data, by default <c>%LOCALAPPDATA%\AgentStudio\history-snapshots</c>, outside every project and
/// every working directory of a session. Each planning gets <c>his-&lt;random&gt;</c> with <c>history.json</c>, written with
/// <c>CreateNew</c> in UTF-8 without BOM: the snapshot the MCP server reads with <c>--history</c>. As with the folders of
/// the assisted recommendation (<see cref="Skills.SkillRecommendationFolders"/>), the root must not be a link, folders
/// left by a crash, older than <see cref="StaleAfter"/>, are swept when a new one is created, and <see cref="Delete"/> only
/// removes a folder of this root and never follows a link.
/// </summary>
public sealed class ProjectHistoryFolders : IProjectHistoryFolders
{
    public const string FolderPrefix = "his-";

    public const string SnapshotFile = "history.json";

    /// <summary>Far longer than any planning (its budget is minutes): an older folder is a leftover.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public ProjectHistoryFolders(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    /// <summary><c>%LOCALAPPDATA%\AgentStudio\history-snapshots</c>.</summary>
    public static ProjectHistoryFolders ForCurrentUser() => new(Path.Combine(LocalDataPaths.ForCurrentUser().BaseDirectory, "history-snapshots"));

    /// <exception cref="IOException">The root is a link, or the folder or the snapshot cannot be written.</exception>
    public async Task<ProjectHistoryFolder> CreateAsync(string snapshotJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        Directory.CreateDirectory(Root);
        if (IsLink(new DirectoryInfo(Root)))
        {
            throw new IOException($"The folder of the history snapshots '{Root}' is a link: nothing is created through it.");
        }

        Sweep();
        var folder = Path.Combine(Root, FolderPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        try
        {
            Directory.CreateDirectory(folder);
            var snapshot = Path.Combine(folder, SnapshotFile);
            var stream = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(Utf8.GetBytes(snapshotJson), ct).ConfigureAwait(false);
            }

            return new ProjectHistoryFolder(snapshot);
        }
        catch
        {
            DeleteQuietly(folder);
            throw;
        }
    }

    public void Delete(ProjectHistoryFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (string.IsNullOrWhiteSpace(folder.SnapshotPath)
            || Path.GetDirectoryName(Path.GetFullPath(folder.SnapshotPath)) is not { } snapshotFolder
            || !IsFolderOfThisRoot(snapshotFolder))
        {
            return;
        }

        DeleteQuietly(snapshotFolder);
    }

    private bool IsFolderOfThisRoot(string folder) =>
        string.Equals(Path.GetDirectoryName(folder), Root, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(folder).StartsWith(FolderPrefix, StringComparison.Ordinal);

    /// <summary>Best effort: the folders of this root last written before <see cref="StaleAfter"/>.</summary>
    private void Sweep()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var folder in new DirectoryInfo(Root).EnumerateDirectories(FolderPrefix + "*"))
            {
                if (now - folder.LastWriteTimeUtc > StaleAfter)
                {
                    DeleteQuietly(folder.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Sweep of '{Root}' failed: {ex.Message}");
        }
    }

    /// <summary>Removes the folder; a link is only unlinked, and Directory.Delete removes the links inside as links.</summary>
    private static void DeleteQuietly(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                return;
            }

            if (IsLink(info))
            {
                info.Delete();
                return;
            }

            info.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"The history snapshot folder '{folder}' could not be deleted: {ex.Message}");
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
