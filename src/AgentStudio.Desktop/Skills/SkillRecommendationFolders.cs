using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Desktop.Skills;

/// <summary>
/// The folders of the assisted recommendation (docs/14 D-29 and D-30), under the app's local data, by default
/// <c>%LOCALAPPDATA%\AgentStudio\skill-recommendations</c>. Each run gets <c>rec-&lt;random&gt;</c> with:
/// <list type="bullet">
/// <item><c>work</c>, empty: the working directory of the session, so it sees no repository;</item>
/// <item><c>snapshot.json</c> next to it, outside what the session reads: the snapshot the MCP server serves, written
/// with <c>CreateNew</c> in UTF-8 without BOM.</item>
/// </list>
/// The root must not be a link, or nothing is created through it. Folders left by a crash, older than
/// <see cref="StaleAfter"/>, are swept when a new one is created. <see cref="Delete"/> only removes a folder of this root
/// and never follows a link.
/// </summary>
public sealed class SkillRecommendationFolders : ISkillRecommendationFolders
{
    public const string FolderPrefix = "rec-";

    public const string WorkFolder = "work";

    public const string SnapshotFile = "snapshot.json";

    /// <summary>Far longer than any recommendation (its budget is minutes): an older folder is a leftover.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public SkillRecommendationFolders(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    /// <summary><c>%LOCALAPPDATA%\AgentStudio\skill-recommendations</c>.</summary>
    public static SkillRecommendationFolders ForCurrentUser() => new(Path.Combine(LocalDataPaths.ForCurrentUser().BaseDirectory, "skill-recommendations"));

    /// <exception cref="IOException">The root is a link, or the folder or the snapshot cannot be written.</exception>
    public async Task<SkillRecommendationFolder> CreateAsync(string snapshotJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        Directory.CreateDirectory(Root);
        if (IsLink(new DirectoryInfo(Root)))
        {
            throw new IOException($"The folder of the skill recommendations '{Root}' is a link: nothing is created through it.");
        }

        Sweep();
        var run = Path.Combine(Root, FolderPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        try
        {
            Directory.CreateDirectory(run);
            var work = Directory.CreateDirectory(Path.Combine(run, WorkFolder)).FullName;
            var snapshot = Path.Combine(run, SnapshotFile);
            var stream = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(true))
            {
                await stream.WriteAsync(Utf8.GetBytes(snapshotJson), ct).ConfigureAwait(true);
            }

            return new SkillRecommendationFolder(work, snapshot);
        }
        catch
        {
            DeleteQuietly(run);
            throw;
        }
    }

    public void Delete(SkillRecommendationFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (string.IsNullOrWhiteSpace(folder.SnapshotPath)
            || Path.GetDirectoryName(Path.GetFullPath(folder.SnapshotPath)) is not { } run
            || !IsRunOfThisRoot(run))
        {
            return;
        }

        DeleteQuietly(run);
    }

    private bool IsRunOfThisRoot(string run) =>
        string.Equals(Path.GetDirectoryName(run), Root, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(run).StartsWith(FolderPrefix, StringComparison.Ordinal);

    /// <summary>Best effort: the runs of this root last written before <see cref="StaleAfter"/>.</summary>
    private void Sweep()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var run in new DirectoryInfo(Root).EnumerateDirectories(FolderPrefix + "*"))
            {
                if (now - run.LastWriteTimeUtc > StaleAfter)
                {
                    DeleteQuietly(run.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Sweep of '{Root}' failed: {ex.Message}");
        }
    }

    /// <summary>Removes the folder; a link is only unlinked, and Directory.Delete removes the links inside as links.</summary>
    private static void DeleteQuietly(string run)
    {
        try
        {
            var info = new DirectoryInfo(run);
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
            Debug.WriteLine($"The skill recommendation folder '{run}' could not be deleted: {ex.Message}");
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
