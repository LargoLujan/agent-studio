using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>A fresh temporary repository and a real <see cref="GitWorktreeManager"/> over git.exe for every test.</summary>
public abstract class GitManagerTestBase : IDisposable
{
    protected const string ProjectId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    protected static readonly RunId TestRun = new("20261015-a1b2c3");

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected TempGitRepo Repo { get; } = TempGitRepo.Create();

    protected ProjectContext Project => new(ProjectId, Repo.Root);

    protected GitWorktreeManager NewManager(IProcessRunner? runner = null) =>
        new(runner ?? new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(Repo.WorktreeRoot));

    protected WorktreeRequest Request(string name, string? baseCommit = null, ProjectContext? project = null) =>
        new(project ?? Project, TestRun, name, baseCommit ?? Repo.BaseCommit);

    protected Task<WorktreeLease> EnsureAsync(GitWorktreeManager manager, string name, string? baseCommit = null) =>
        manager.EnsureAsync(Request(name, baseCommit), Ct);

    protected static string ExpectedLockReason(string name) => $"agent-studio:{ProjectId}:{TestRun.Value}:{name}";

    /// <summary>Writes a UTF-8 file (no BOM) inside a worktree, creating folders.</summary>
    protected static void WriteIn(WorktreeLease lease, string relativePath, string content)
    {
        var full = Path.Combine(lease.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Entries of <c>git worktree list --porcelain -z</c> as git itself reports them; the first one is the main working tree.</summary>
    protected IReadOnlyList<WorktreeListEntry> ListWorktrees() =>
        GitPorcelain.ParseWorktreeList(Repo.Run("worktree", "list", "--porcelain", "-z"));

    protected static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>A real, different commit made by a worktree, without moving anything in the main working tree.</summary>
    protected async Task<string> CommitInAsync(GitWorktreeManager manager, WorktreeLease lease, string file, string content, string message = "cambio")
    {
        WriteIn(lease, file, content);
        var result = await manager.CommitAllAsync(lease, message, Ct);
        Assert.True(result.Committed);
        return result.Commit;
    }

    /// <summary>The administrative folder of a worktree (<c>.git/worktrees/&lt;id&gt;</c>), read through its intact gitfile.</summary>
    protected static string AdminDirectoryOf(WorktreeLease lease) =>
        Path.GetFullPath(TempGitRepo.Git(lease.Path, "rev-parse", "--absolute-git-dir").Trim());

    /// <summary>What an agent can do with its own worktree: point the <c>.git</c> file somewhere else.</summary>
    protected static void RedirectGitFile(WorktreeLease lease, string gitDirectory)
    {
        // git for Windows creates the file hidden, which only stops a plain overwrite, not an agent.
        var gitFile = Path.Combine(lease.Path, ".git");
        File.SetAttributes(gitFile, FileAttributes.Normal);
        File.WriteAllText(gitFile, "gitdir: " + gitDirectory.Replace('\\', '/') + "\n");
    }

    /// <summary>The commit HEAD of a worktree points to, read from its administrative folder (the gitfile may be tampered).</summary>
    protected static string WorktreeHead(string adminDirectory) =>
        TempGitRepo.Git(Path.GetTempPath(), "--git-dir=" + adminDirectory, "rev-parse", "HEAD").Trim();

    /// <summary>
    /// Creates a directory junction (no privileges needed, unlike a symbolic link), the kind of link an agent with a
    /// shell can plant in its worktree.
    /// </summary>
    internal static void CreateJunction(string link, string target)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {error}");
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Deletes a folder and its read-only files; links inside are removed as links.</summary>
    protected static void DeleteDirectory(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }

    /// <summary>A folder outside the repository and the worktree root (inside the test's container, deleted with it).</summary>
    protected string NewVictim(string name)
    {
        var victim = Path.Combine(Path.GetDirectoryName(Repo.Root)!, name);
        Directory.CreateDirectory(victim);
        return victim;
    }

    /// <summary>Skips the test when this machine cannot make a symbolic link (it needs Developer Mode or elevation).</summary>
    protected void SkipUnlessSymbolicLinks()
    {
        var probe = Path.Combine(NewVictim("sonda-enlace"), "enlace");
        try
        {
            File.CreateSymbolicLink(probe, Path.Combine(Repo.Root, "README.md"));
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs Developer Mode or elevation on this machine.");
        }
    }

    /// <summary>Copies a folder tree: its files and subfolders.</summary>
    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>Moves a folder of the repository to <paramref name="victim"/> and plants a junction to it in its place.</summary>
    internal static void ReplaceWithJunction(string folder, string victim)
    {
        CopyDirectory(folder, victim);
        DeleteDirectory(folder);
        CreateJunction(folder, victim);
    }

    /// <summary>Removes a junction a test planted, if the code under test did not remove it already.</summary>
    internal static void RemoveJunction(string junction)
    {
        var info = new DirectoryInfo(junction);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();
        }
    }

    public void Dispose()
    {
        Repo.Dispose();
        GC.SuppressFinalize(this);
    }
}
