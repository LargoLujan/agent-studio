using System.IO;
using System.Text;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Skills;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>
/// docs/14 D-29 and D-30: every assisted recommendation gets a folder of its own under the app's local data: an empty
/// working directory for the session (it sees no repository) and, outside it, the snapshot the MCP server reads, written
/// with <c>CreateNew</c>. The folder goes away after the run.
/// </summary>
public sealed class SkillRecommendationFoldersTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_temp.Path, "skill-recommendations");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_folder_has_an_empty_working_directory_and_the_snapshot_outside_it()
    {
        var folders = new SkillRecommendationFolders(Root);

        var folder = await folders.CreateAsync("{\"schema\":\"x\"}", Ct);

        Assert.True(Directory.Exists(folder.WorkingDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.WorkingDirectory));
        Assert.Equal("{\"schema\":\"x\"}", await File.ReadAllTextAsync(folder.SnapshotPath, Ct));
        Assert.False(File.ReadAllBytes(folder.SnapshotPath).AsSpan().StartsWith(Encoding.UTF8.Preamble), "The snapshot carries a BOM.");
        Assert.Equal(Path.GetDirectoryName(folder.WorkingDirectory), Path.GetDirectoryName(folder.SnapshotPath));
        Assert.Equal(Path.GetFullPath(Root), Path.GetDirectoryName(Path.GetDirectoryName(folder.SnapshotPath)));
        Assert.True(Path.IsPathFullyQualified(folder.SnapshotPath));
    }

    [Fact]
    public async Task Every_run_gets_a_new_folder()
    {
        var folders = new SkillRecommendationFolders(Root);

        var first = await folders.CreateAsync("{}", Ct);
        var second = await folders.CreateAsync("{}", Ct);

        Assert.NotEqual(first.SnapshotPath, second.SnapshotPath);
        Assert.NotEqual(first.WorkingDirectory, second.WorkingDirectory);
    }

    [Fact]
    public async Task Deleting_removes_the_whole_folder_and_is_safe_to_repeat()
    {
        var folders = new SkillRecommendationFolders(Root);
        var folder = await folders.CreateAsync("{}", Ct);
        var run = Path.GetDirectoryName(folder.SnapshotPath)!;

        folders.Delete(folder);
        folders.Delete(folder);

        Assert.False(Directory.Exists(run));
        Assert.True(Directory.Exists(Root));
    }

    [Fact]
    public void A_folder_outside_the_root_is_never_deleted()
    {
        var outside = _temp.CreateDirectory("ajena/work");
        var snapshot = _temp.WriteFile("ajena/snapshot.json", "{}");
        var folders = new SkillRecommendationFolders(Root);

        folders.Delete(new SkillRecommendationFolder(outside, snapshot));

        Assert.True(Directory.Exists(outside));
        Assert.True(File.Exists(snapshot));
    }

    [Fact]
    public async Task Leftovers_of_a_crash_older_than_a_day_are_swept_when_a_folder_is_created()
    {
        var folders = new SkillRecommendationFolders(Root);
        var stale = await folders.CreateAsync("{}", Ct);
        var staleRun = Path.GetDirectoryName(stale.SnapshotPath)!;
        Directory.SetLastWriteTimeUtc(staleRun, DateTime.UtcNow.AddDays(-2));
        var recent = await folders.CreateAsync("{}", Ct);

        await folders.CreateAsync("{}", Ct);

        Assert.False(Directory.Exists(staleRun));
        Assert.True(Directory.Exists(Path.GetDirectoryName(recent.SnapshotPath)));
    }

    [Fact]
    public async Task A_root_that_is_a_junction_is_refused()
    {
        var target = _temp.CreateDirectory("destino");
        MakeJunction(Root, target);

        var folders = new SkillRecommendationFolders(Root);

        await Assert.ThrowsAsync<IOException>(() => folders.CreateAsync("{}", Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    private static void MakeJunction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /J failed");
    }
}
