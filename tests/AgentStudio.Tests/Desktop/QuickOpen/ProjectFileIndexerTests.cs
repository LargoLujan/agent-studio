using System.Diagnostics;
using System.IO;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop.QuickOpen;

public sealed class ProjectFileIndexerTests
{
    [Fact]
    public void Build_ListsTheFilesOfEveryLevel_WithSlashSeparators()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("README.md");
        temp.WriteFile("src/Program.cs");
        temp.WriteFile("src/Views/Main.xaml");

        var index = Build(temp.Path);

        Assert.Equal(["README.md", "src/Program.cs", "src/Views/Main.xaml"], index.RelativePaths.Order(StringComparer.Ordinal));
        Assert.False(index.IsTruncated);
    }

    [Fact]
    public void Build_LeavesOutWhatTheExplorerHides()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".git/config");
        temp.WriteFile("bin/app.dll");
        temp.WriteFile("obj/project.assets.json");
        temp.WriteFile("node_modules/lib/index.js");
        temp.WriteFile(".vs/settings.json");
        temp.WriteFile("src/bin/nested.dll");
        temp.WriteFile(".agentstudio/agentstudio.db");
        temp.WriteFile(".agentstudio/agentstudio.db-wal");
        temp.WriteFile(".agentstudio/notes.md");
        temp.WriteFile("src/Program.cs");

        var index = Build(temp.Path);

        Assert.Equal([".agentstudio/notes.md", "src/Program.cs"], index.RelativePaths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Build_NeverDescendsIntoHiddenFolders()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        temp.WriteFile("b.txt");
        for (var i = 0; i < 50; i++)
        {
            temp.WriteFile($"node_modules/pkg/f{i}.js");
        }

        // If the hidden folder were walked, its files would use up the cap and the index would claim to be truncated.
        var index = Build(temp.Path, maxFiles: 5);

        Assert.Equal(["a.txt", "b.txt"], index.RelativePaths.Order(StringComparer.Ordinal));
        Assert.False(index.IsTruncated);
    }

    [Fact]
    public void Build_StopsAtTheCap_AndSaysItWasTruncated()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 10; i++)
        {
            temp.WriteFile($"f{i}.txt");
        }

        var index = Build(temp.Path, maxFiles: 4);

        Assert.Equal(4, index.RelativePaths.Count);
        Assert.True(index.IsTruncated);
    }

    [Fact]
    public void Build_WithExactlyAsManyFilesAsTheCap_IsNotTruncated()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 4; i++)
        {
            temp.WriteFile($"f{i}.txt");
        }

        var index = Build(temp.Path, maxFiles: 4);

        Assert.Equal(4, index.RelativePaths.Count);
        Assert.False(index.IsTruncated);
    }

    [Fact]
    public void Build_WhenCapped_KeepsTheShallowFilesAndDropsTheDeepOnes()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("top1.txt");
        temp.WriteFile("top2.txt");
        temp.WriteFile("a/mid.txt");
        temp.WriteFile("a/b/c/d/deep1.txt");
        temp.WriteFile("a/b/c/d/deep2.txt");

        var index = Build(temp.Path, maxFiles: 3);

        Assert.True(index.IsTruncated);
        Assert.Contains("top1.txt", index.RelativePaths);
        Assert.Contains("top2.txt", index.RelativePaths);
        Assert.Contains("a/mid.txt", index.RelativePaths);
    }

    [Fact]
    public void Build_DoesNotFollowFolderLinks_SoACycleCannotLoopForever()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("real/file.txt");
        var link = temp.Combine("link");
        var loop = temp.Combine("real", "loop");
        if (!TryCreateJunction(link, temp.Combine("real")) || !TryCreateJunction(loop, temp.Path))
        {
            Assert.Skip("Directory junctions cannot be created in the temporary folder.");
        }

        var index = Build(temp.Path, maxFiles: 100);

        Assert.Equal(["real/file.txt"], index.RelativePaths);
        Assert.False(index.IsTruncated);
    }

    [Fact]
    public void Build_WithACancelledToken_Throws()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => ProjectFileIndexer.Build(temp.Path, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void Build_OfAMissingFolder_Throws()
    {
        using var temp = new TempDirectory();

        Assert.Throws<DirectoryNotFoundException>(() => Build(temp.Combine("nope")));
    }

    [Fact]
    public void Build_RejectsACapBelowOne()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentOutOfRangeException>(() => Build(temp.Path, maxFiles: 0));
    }

    private static ProjectFileIndex Build(string root, int maxFiles = ProjectFileIndexer.DefaultMaxFiles) =>
        ProjectFileIndexer.Build(root, maxFiles, TestContext.Current.CancellationToken);

    private static bool TryCreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
