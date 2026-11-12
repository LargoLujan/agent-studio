using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>
/// WP-0 of Phase 6 (ADR-0018, addendum of 2026-11-12): the locator never returns a program that lies inside an untrusted root (the
/// open project), whether the PATH names it with a relative path, with an absolute one, through a junction, with another case, with a
/// short 8.3 name or with a prefix of its own. Nothing here starts a process: it only looks at files in a temporary folder.
/// </summary>
public sealed class ExecutableLocatorTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "as-untrusted-" + Guid.NewGuid().ToString("N"));
    private readonly PlantedLinks _links = new();

    public ExecutableLocatorTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        _links.Dispose();
        TempFolders.Delete(_base);
    }

    private sealed class FixedRoots(params string[] roots) : IUntrustedRoots
    {
        public List<string> Items { get; } = [.. roots];

        public IReadOnlyList<string> Roots => Items;
    }

    private string MakeDir(params string[] segments)
    {
        var dir = Path.Combine([_base, .. segments]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Plant(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, "");
        return path;
    }

    private static ExecutableLocator Locator(string path, params string[] roots) =>
        new(() => path, () => ".COM;.EXE;.BAT;.CMD", roots.Length == 0 ? null : new FixedRoots(roots));

    [Fact]
    public void An_absolute_entry_of_the_PATH_inside_the_root_is_not_used()
    {
        var project = MakeDir("proj");
        var bin = MakeDir("proj", "tools");
        Plant(bin, "claude.cmd");

        var locator = Locator(bin, project);

        Assert.Null(locator.Find("claude"));
        Assert.Equal(Path.Combine(bin, "claude.cmd"), locator.FindInUntrustedRoot("claude"), ignoreCase: true);
    }

    [Fact]
    public void The_same_program_outside_the_root_is_used_and_nothing_is_reported_as_rejected()
    {
        var project = MakeDir("proj");
        var system = MakeDir("system");
        var expected = Plant(system, "claude.cmd");

        var locator = Locator(system, project);

        Assert.Equal(expected, locator.Find("claude"), ignoreCase: true);
        Assert.Null(locator.FindInUntrustedRoot("claude"));
    }

    [Fact]
    public void A_later_entry_outside_the_root_is_used_when_an_earlier_one_inside_it_is_skipped()
    {
        var project = MakeDir("proj");
        var inside = MakeDir("proj", "bin");
        var outside = MakeDir("system");
        Plant(inside, "node.exe");
        var expected = Plant(outside, "node.exe");

        var found = Locator(inside + Path.PathSeparator + outside, project).Find("node");

        Assert.Equal(expected, found, ignoreCase: true);
    }

    [Fact]
    public void Without_roots_an_absolute_entry_is_used_as_it_always_was()
    {
        var bin = MakeDir("proj", "tools");
        var expected = Plant(bin, "claude.cmd");

        Assert.Equal(expected, Locator(bin).Find("claude"), ignoreCase: true);
        Assert.Equal(expected, new ExecutableLocator(() => bin, () => ".CMD", new FixedRoots()).Find("claude"), ignoreCase: true);
    }

    [Fact]
    public void A_relative_entry_is_never_used_with_or_without_roots()
    {
        var project = MakeDir("proj");
        Plant(project, "claude.cmd");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = project;

            Assert.Null(Locator(".").Find("claude"));
            Assert.Null(Locator(@".\", project).Find("claude"));
            Assert.Null(Locator(Path.GetFileName(project)).Find("claude"));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void An_entry_that_is_a_junction_to_a_folder_of_the_project_is_not_used()
    {
        var project = MakeDir("proj");
        var bin = MakeDir("proj", "bin");
        Plant(bin, "git.exe");
        var outside = MakeDir("elsewhere");
        var link = Path.Combine(outside, "innocent");
        _links.Junction(link, bin);

        var locator = Locator(link, project);

        Assert.Null(locator.Find("git"));
        Assert.NotNull(locator.FindInUntrustedRoot("git"));
    }

    [Fact]
    public void A_junction_in_the_middle_of_the_path_is_resolved_too()
    {
        var project = MakeDir("proj");
        MakeDir("proj", "bin");
        Plant(Path.Combine(project, "bin"), "git.exe");
        var outside = MakeDir("elsewhere");
        var link = Path.Combine(outside, "alias");
        _links.Junction(link, project);

        Assert.Null(Locator(Path.Combine(link, "bin"), project).Find("git"));
    }

    [Fact]
    public void A_root_that_is_itself_reached_through_a_junction_is_compared_by_where_it_really_is()
    {
        var real = MakeDir("real-project");
        var bin = MakeDir("real-project", "bin");
        Plant(bin, "node.exe");
        var alias = Path.Combine(MakeDir("elsewhere"), "proj-alias");
        _links.Junction(alias, real);

        // The project is opened by its alias; the PATH names the real folder.
        Assert.Null(Locator(bin, alias).Find("node"));

        // And the other way round.
        Assert.Null(Locator(Path.Combine(alias, "bin"), real).Find("node"));
    }

    [Fact]
    public void The_comparison_ignores_case_trailing_separators_and_the_long_path_prefix()
    {
        var project = MakeDir("Proj");
        var bin = MakeDir("Proj", "Bin");
        Plant(bin, "node.exe");

        Assert.Null(Locator(bin.ToUpperInvariant(), project.ToLowerInvariant()).Find("node"));
        Assert.Null(Locator(bin.ToLowerInvariant(), project.ToUpperInvariant()).Find("node"));
        Assert.Null(Locator(bin + Path.DirectorySeparatorChar, project).Find("node"));
        Assert.Null(Locator(bin, project + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar).Find("node"));
        Assert.Null(Locator(bin, @"\\?\" + project).Find("node"));
        Assert.Null(Locator(@"\\?\" + bin, project).Find("node"));
        Assert.Null(Locator(bin, project.Replace('\\', '/')).Find("node"));
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_the_root_is_not_inside_it()
    {
        var project = MakeDir("proj");
        var sibling = MakeDir("proj2", "bin");
        var expected = Plant(sibling, "node.exe");

        Assert.Equal(expected, Locator(sibling, project).Find("node"), ignoreCase: true);
    }

    [Fact]
    public void A_short_8_3_name_of_a_folder_of_the_project_is_not_used()
    {
        var project = MakeDir("a-project-with-a-long-name");
        var bin = MakeDir("a-project-with-a-long-name", "programs and tools");
        Plant(bin, "node.exe");
        var shortBin = ShortPathOf(bin);

        // Where the volume makes no 8.3 names the short path is the long one and the case is the same as the others.
        Assert.Null(Locator(shortBin, project).Find("node"));
        Assert.Null(Locator(bin, ShortPathOf(project)).Find("node"));
    }

    [Fact]
    public void A_root_that_contains_the_system_folder_is_ignored_so_it_cannot_hide_the_whole_system()
    {
        var bin = MakeDir("tools");
        var expected = Plant(bin, "node.exe");
        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.Equal(expected, Locator(bin, driveRoot).Find("node"), ignoreCase: true);
    }

    [Fact]
    public void The_roots_are_read_at_each_lookup_so_a_project_opened_later_counts()
    {
        var project = MakeDir("proj");
        var bin = MakeDir("proj", "bin");
        var expected = Plant(bin, "node.exe");
        var roots = new FixedRoots();
        var locator = new ExecutableLocator(() => bin, () => ".EXE", roots);

        Assert.Equal(expected, locator.Find("node"), ignoreCase: true);

        roots.Items.Add(project);

        Assert.Null(locator.Find("node"));
    }

    [Fact]
    public void A_name_that_is_in_no_entry_is_not_found_and_not_reported_as_rejected()
    {
        var project = MakeDir("proj");
        var bin = MakeDir("system");

        var locator = Locator(bin, project);

        Assert.Null(locator.Find("nothing"));
        Assert.Null(locator.FindInUntrustedRoot("nothing"));
    }

    // ---- A project opened too wide, and drives that are really folders (second addendum of 2026-11-12) ----

    [Fact]
    public void A_root_that_is_the_whole_profile_of_the_person_does_not_hide_what_is_installed_under_it()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.SkipUnless(Path.GetFullPath(_base).StartsWith(home, StringComparison.OrdinalIgnoreCase), "The temporary folder is not under the profile on this machine.");
        var bin = MakeDir("herramientas-personales");
        var expected = Plant(bin, "claude.cmd");

        Assert.Equal(expected, Locator(bin, home).Find("claude"), ignoreCase: true);
        Assert.Equal(expected, Locator(bin, home + Path.DirectorySeparatorChar).Find("claude"), ignoreCase: true);
        Assert.Null(Locator(bin, home).FindInUntrustedRoot("claude"));
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.UserProfile, true)]
    [InlineData(Environment.SpecialFolder.ProgramFiles, true)]
    [InlineData(Environment.SpecialFolder.ProgramFilesX86, true)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData, true)]
    [InlineData(Environment.SpecialFolder.Windows, true)]
    public void The_profile_the_program_folders_and_the_system_are_too_broad_to_be_a_project(Environment.SpecialFolder folder, bool broad)
    {
        var path = Environment.GetFolderPath(folder);
        Assert.SkipWhen(string.IsNullOrEmpty(path), "This machine has no such folder.");

        Assert.Equal(broad, BroadFolders.IsTooBroad(path));
    }

    [Fact]
    public void The_root_of_a_drive_and_a_parent_of_the_profile_are_too_broad_but_a_folder_inside_it_is_not()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(BroadFolders.IsTooBroad(Path.GetPathRoot(home)!));
        Assert.True(BroadFolders.IsTooBroad(Path.GetDirectoryName(home)!));
        Assert.True(BroadFolders.IsTooBroad(home.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.False(BroadFolders.IsTooBroad(Path.Combine(home, "repos", "un-proyecto")));
        Assert.False(BroadFolders.IsTooBroad(MakeDir("proj")));
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public void A_drive_made_with_subst_is_compared_by_the_folder_it_stands_for()
    {
        var project = MakeDir("proj");
        var bin = MakeDir("proj", "bin");
        Plant(bin, "node.exe");
        var letter = "QRSTUVWXYZ".Select(c => c + ":").FirstOrDefault(d => !Directory.Exists(d + Path.DirectorySeparatorChar));
        Assert.SkipWhen(letter is null, "No free drive letter for subst.");

        if (!Subst(letter!, project))
        {
            Assert.Skip("subst is not available here.");
        }

        try
        {
            var viaDrive = letter + @"\bin";

            // The root by its real folder and the PATH by the letter, and the other way round.
            Assert.Null(Locator(viaDrive, project).Find("node"));
            Assert.Null(Locator(bin, letter + @"\").Find("node"));
            Assert.Null(Locator(viaDrive, letter + @"\").Find("node"));
        }
        finally
        {
            Subst(letter!, null);
        }
    }

    private static bool Subst(string drive, string? target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("subst.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(drive);
        if (target is null)
        {
            start.ArgumentList.Add("/d");
        }
        else
        {
            start.ArgumentList.Add(target);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
    private static string ShortPathOf(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        return length == 0 || length >= buffer.Capacity ? path : buffer.ToString();
    }

#pragma warning disable SYSLIB1054, CA5392 // A test-only call; the tests project has no unsafe code for LibraryImport.
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
#pragma warning restore SYSLIB1054, CA5392
}
