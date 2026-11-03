using System.IO;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// Opening a project inside a git repository keeps Agent Studio's runtime data out of the user's <c>git status</c>
/// through <c>info/exclude</c>, never through a <c>.gitignore</c> of the user. The journal and the Markdown memory are
/// versioned (ADR-0008), so only the database, the evidence, the temporary files, the trusted verification profile
/// (ADR-0016) and the canvas layout of the orchestrator (node positions on this machine) are excluded.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitExcludeTests : IDisposable
{
    private static readonly string[] Lines =
    [
        ".agentstudio/*.db*",
        ".agentstudio/evidence/",
        ".agentstudio/tmp/",
        ".agentstudio/verification.json",
        ".agentstudio/canvas-layout.json",
    ];

    private readonly TempGitRepo _repo = TempGitRepo.Create();

    public void Dispose() => _repo.Dispose();

    private string ExcludeFile => Path.Combine(_repo.Root, ".git", "info", "exclude");

    private static IReadOnlyList<string> ReadLines(string path) =>
        File.ReadAllText(path).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>Puts a file in each place Agent Studio writes, so <c>git status</c> would list whatever is not excluded.</summary>
    private static void TouchRuntimeFiles(string projectRoot)
    {
        var data = Path.Combine(projectRoot, ".agentstudio");
        Directory.CreateDirectory(Path.Combine(data, "evidence"));
        Directory.CreateDirectory(Path.Combine(data, "tmp"));
        Directory.CreateDirectory(Path.Combine(data, "bitacora"));
        File.WriteAllText(Path.Combine(data, "evidence", new string('a', 64)), "evidence");
        File.WriteAllText(Path.Combine(data, "tmp", "partial.tmp"), "partial");
        File.WriteAllText(Path.Combine(data, "verification.json"), "{}");
        File.WriteAllText(Path.Combine(data, "canvas-layout.json"), "{}");
        File.WriteAllText(Path.Combine(data, "bitacora", "2026-10-15.md"), "# bitácora");
    }

    private static string Status(string workingDirectory) =>
        TempGitRepo.Git(workingDirectory, "status", "--porcelain=v1", "--untracked-files=all");

    [Fact]
    public async Task Opening_excludes_the_database_the_evidence_and_the_temporary_files_but_not_the_journal()
    {
        var gitignore = File.ReadAllText(Path.Combine(_repo.Root, ".gitignore"));

        using var instance = await ProjectInstance.OpenAsync(_repo.Root);
        TouchRuntimeFiles(_repo.Root);

        var lines = ReadLines(ExcludeFile);
        foreach (var line in Lines)
        {
            Assert.Contains(line, lines);
        }

        var status = Status(_repo.Root);
        Assert.DoesNotContain("agentstudio.db", status, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence", status, StringComparison.Ordinal);
        Assert.DoesNotContain("tmp", status, StringComparison.Ordinal);
        Assert.DoesNotContain("verification.json", status, StringComparison.Ordinal);
        Assert.DoesNotContain("canvas-layout.json", status, StringComparison.Ordinal);
        Assert.Contains(".agentstudio/bitacora/2026-10-15.md", status, StringComparison.Ordinal);
        Assert.Equal(gitignore, File.ReadAllText(Path.Combine(_repo.Root, ".gitignore")));
    }

    [Fact]
    public async Task A_hard_linked_exclude_file_is_never_modified_and_the_project_still_opens()
    {
        // What an agent with a shell can plant: info/exclude as a second name of a file it wants Agent Studio to write.
        var protectedFile = Path.Combine(Path.GetTempPath(), "as-protegido-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(protectedFile, "original\n");
        try
        {
            File.Delete(ExcludeFile);
            CreateHardLink(ExcludeFile, protectedFile);

            using var instance = await ProjectInstance.OpenAsync(_repo.Root);

            Assert.Equal("original\n", File.ReadAllText(protectedFile));
            Assert.Equal("original\n", File.ReadAllText(ExcludeFile));
            Assert.True(File.Exists(Path.Combine(_repo.Root, ".agentstudio", "agentstudio.db")));
        }
        finally
        {
            File.Delete(ExcludeFile);
            File.Delete(protectedFile);
        }
    }

    /// <summary>A hard link made by <c>mklink /H</c> (.NET has no API for it); both names must be on the same volume.</summary>
    private static void CreateHardLink(string link, string target)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/H", link, target })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /H failed: {error}");
    }

    [Fact]
    public async Task Excluding_is_idempotent_and_keeps_what_the_user_wrote()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ExcludeFile)!);
        File.WriteAllText(ExcludeFile, "# mis exclusiones\n*.mio\nsin-salto-final");

        for (var i = 0; i < 3; i++)
        {
            using var instance = await ProjectInstance.OpenAsync(_repo.Root);
        }

        var lines = ReadLines(ExcludeFile);
        Assert.Equal("# mis exclusiones", lines[0]);
        Assert.Equal("*.mio", lines[1]);
        Assert.Equal("sin-salto-final", lines[2]);
        foreach (var line in Lines)
        {
            Assert.Equal(1, lines.Count(l => l == line));
        }

        Assert.EndsWith("\n", File.ReadAllText(ExcludeFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_the_user_already_has_is_not_written_again()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ExcludeFile)!);
        File.WriteAllText(ExcludeFile, ".agentstudio/evidence/\r\n");

        using var instance = await ProjectInstance.OpenAsync(_repo.Root);

        var lines = ReadLines(ExcludeFile);
        Assert.Equal(1, lines.Count(l => l == ".agentstudio/evidence/"));
        Assert.Contains(".agentstudio/*.db*", lines);
    }

    [Fact]
    public async Task In_a_linked_worktree_the_lines_go_to_the_common_git_directory()
    {
        var worktree = Path.Combine(_repo.WorktreeRoot, "otro árbol");
        Directory.CreateDirectory(_repo.WorktreeRoot);
        _repo.Run("worktree", "add", "--detach", worktree, "HEAD");

        using var instance = await ProjectInstance.OpenAsync(worktree);
        TouchRuntimeFiles(worktree);

        foreach (var line in Lines)
        {
            Assert.Contains(line, ReadLines(ExcludeFile));
        }

        var privateExclude = Directory.GetDirectories(Path.Combine(_repo.Root, ".git", "worktrees"))
            .Select(d => Path.Combine(d, "info", "exclude"));
        Assert.All(privateExclude, path => Assert.False(File.Exists(path), $"{path} should not exist."));
        var status = Status(worktree);
        Assert.DoesNotContain("evidence", status, StringComparison.Ordinal);
        Assert.DoesNotContain("agentstudio.db", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_below_the_top_of_the_repository_is_excluded_by_its_own_path()
    {
        var project = Path.Combine(_repo.Root, "sub dir", "app");
        Directory.CreateDirectory(project);

        using var instance = await ProjectInstance.OpenAsync(project);
        TouchRuntimeFiles(project);

        var lines = ReadLines(ExcludeFile);
        Assert.Contains("sub dir/app/.agentstudio/*.db*", lines);
        Assert.Contains("sub dir/app/.agentstudio/evidence/", lines);
        Assert.Contains("sub dir/app/.agentstudio/tmp/", lines);
        var status = Status(_repo.Root);
        Assert.DoesNotContain("evidence", status, StringComparison.Ordinal);
        Assert.DoesNotContain("agentstudio.db", status, StringComparison.Ordinal);
        Assert.Contains("sub dir/app/.agentstudio/bitacora/2026-10-15.md", status.Replace("\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }
}

public sealed class OutsideGitTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_folder_outside_any_repository_opens_without_writing_git_files()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.False(Directory.Exists(Path.Combine(_folder.Root, ".git")));
        Assert.True(File.Exists(_folder.DatabasePath));
    }
}
