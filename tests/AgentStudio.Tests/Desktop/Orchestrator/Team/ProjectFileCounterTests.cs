using System.Diagnostics;
using System.IO;
using AgentStudio.Desktop.Orchestrator.Team;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>
/// The size of the project for the recommendation of a model (docs/17 D-24, P49): the files of its folder counted once, up to a
/// cap, without what the explorer hides nor the state of Agent Studio itself. Real temporary folders, never the project.
/// </summary>
public sealed class ProjectFileCounterTests
{
    [Fact]
    public void Counts_the_files_of_the_project_and_not_the_folders_that_are_not_its_own()
    {
        using var root = new TempDirectory();
        root.WriteFile("README.md");
        root.WriteFile("src/Calc/Calc.cs");
        root.WriteFile("src/Calc/deep/er/Deeper.cs");
        root.WriteFile("tests/CalcTests.cs");

        // What is not the project: git, builds, packages, the IDE and the state of Agent Studio.
        root.WriteFile(".git/HEAD");
        root.WriteFile("bin/Debug/Calc.dll");
        root.WriteFile("src/Calc/obj/project.assets.json");
        root.WriteFile("node_modules/left-pad/index.js");
        root.WriteFile(".vs/settings.json");
        root.WriteFile(".agentstudio/bitacora/2026-11-11.md");
        root.WriteFile(".agentstudio/memory/decisions.md");

        var count = ProjectFileCounter.Count(root.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ProjectFileCount(4, IsTruncated: false), count);
    }

    [Fact]
    public void Stops_at_the_cap_and_says_the_count_is_a_floor()
    {
        using var root = new TempDirectory();
        for (var i = 0; i < 12; i++)
        {
            root.WriteFile($"src/f{i}.cs");
        }

        var count = ProjectFileCounter.Count(root.Path, maxFiles: 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ProjectFileCount(5, IsTruncated: true), count);
    }

    [Fact]
    public void A_project_with_exactly_the_cap_is_not_truncated()
    {
        using var root = new TempDirectory();
        for (var i = 0; i < 5; i++)
        {
            root.WriteFile($"f{i}.cs");
        }

        Assert.Equal(new ProjectFileCount(5, IsTruncated: false), ProjectFileCounter.Count(root.Path, maxFiles: 5, cancellationToken: TestContext.Current.CancellationToken));
        root.WriteFile("one-more.cs");
        Assert.Equal(new ProjectFileCount(5, IsTruncated: true), ProjectFileCounter.Count(root.Path, maxFiles: 5, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_folder_that_does_not_exist_says_so_and_a_cancelled_walk_stops()
    {
        using var root = new TempDirectory();
        root.WriteFile("a.cs");

        Assert.Throws<DirectoryNotFoundException>(() => ProjectFileCounter.Count(root.Combine("missing"), cancellationToken: TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectFileCounter.Count(root.Path, cancellationToken: cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectFileCounter.Count(root.Path, maxFiles: 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_folder_link_is_not_followed_so_neither_what_it_points_at_nor_a_cycle_counts()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        root.WriteFile("src/a.cs");
        root.WriteFile("README.md");
        outside.WriteFile("big/one.cs");
        outside.WriteFile("big/two.cs");
        outside.WriteFile("big/three.cs");

        // One link out of the project, and one back to the project itself: followed, the first would count three files that are not
        // the project's and the second would count the same files again for as long as the path can grow.
        var outward = root.Combine("linked");
        var loop = root.Combine("src", "loop");
        if (!TryCreateJunction(outward, outside.Path) || !TryCreateJunction(loop, root.Path))
        {
            Assert.Skip("Directory junctions cannot be created in the temporary folder.");
        }

        var count = ProjectFileCounter.Count(root.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ProjectFileCount(2, IsTruncated: false), count);
    }

    [Fact]
    public void The_cap_is_the_one_of_the_index_of_the_explorer()
    {
        Assert.Equal(20_000, ProjectFileCounter.DefaultMaxFiles);
    }

    /// <summary>A directory junction made by <c>mklink /J</c>, which needs no privilege (the tests of the explorer index do the same).</summary>
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
