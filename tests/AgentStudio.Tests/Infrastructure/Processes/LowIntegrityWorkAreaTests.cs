using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>
/// The Low work area of an isolated verification (ADR-0017) over real git and real Low-integrity processes: folders only
/// the processes of the area can write, holding the copy of one commit that the git layer writes
/// (<see cref="GitWorktreeManager.ArchiveCommitAsync"/>, with its own tests), removed without following links. The root
/// is a temporary folder the factory creates, except in the test of the default root.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class LowIntegrityWorkAreaTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly string _container = Path.Combine(Path.GetTempPath(), "as-lowwa-" + Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_container, "verify");

    // A lock of these tests alone, with their root: the lock of the user is held by the isolated runs of other test classes.
    private string LockFile => Path.Combine(_container, "verify.lock");

    private LowIntegrityWorkAreaFactory Factory() => new(new LowIntegrityWorkAreaOptions(Root, LockFile: LockFile));

    /// <summary>What the verification runner gives the factory: the git layer copying the commit from the repository.</summary>
    private Func<string, CancellationToken, Task> CopyOf(string commit)
    {
        var git = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));
        return (destination, ct) => git.ArchiveCommitAsync(_repo.Root, commit, destination, ct);
    }

    private static async Task<int> LowAsync(params string[] command)
    {
        var result = await new ProcessRunner().RunAsync(
            new ProcessSpec("cmd.exe", ["/c", .. command], Timeout: TimeSpan.FromSeconds(30), Isolation: ProcessIsolation.LowIntegrity), Ct);
        return result.ExitCode;
    }

    private static Task<int> LowWriteAsync(string folder) => LowAsync("copy", "/y", "NUL", Path.Combine(folder, "escrito-por-low.txt"));

    private static async Task JunctionAsync(string link, string target)
    {
        var made = await new ProcessRunner().RunAsync(new ProcessSpec("cmd.exe", ["/c", "mklink", "/J", link, target]), Ct);
        Assert.True(made.ExitCode == 0, made.StandardOutput + made.StandardError);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string[] Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Relative(root, f)).Order(StringComparer.Ordinal)];

    private string[] Areas() => Directory.Exists(Root) ? [.. Directory.EnumerateFileSystemEntries(Root)] : [];

    [Fact]
    public async Task An_area_holds_exactly_the_tree_of_the_commit_and_only_its_folders_are_writable_at_Low_integrity()
    {
        _repo.Write("src/Calc/Más.cs", "public static class Mas { }\n");
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-m", "segundo");
        var commit = _repo.Head;

        // What is in the working directory but not in the commit never reaches the copy.
        _repo.Write("shared.txt", "cambiado sin commit\n");
        _repo.Write("sin-seguimiento.txt", "nuevo\n");
        _repo.Write("ignorado.log", "ignorado\n");
        var before = _repo.Snapshot();

        await using var area = await Factory().CreateAsync(commit, CopyOf(commit), Ct);

        Assert.Equal(commit, area.Commit);
        var committed = TempGitRepo.Git(_repo.Root, "-c", "core.quotepath=false", "ls-tree", "-r", "--name-only", commit);
        Assert.Equal(committed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal), Files(area.SourceDirectory));
        Assert.Equal(TempGitRepo.Git(_repo.Root, "show", commit + ":shared.txt"), File.ReadAllText(Path.Combine(area.SourceDirectory, "shared.txt")));
        Assert.False(Path.Exists(Path.Combine(area.SourceDirectory, ".git")));

        // Every folder of the area is under its root, outside the repository, and writable by a Low process.
        string[] folders =
        [
            area.SourceDirectory, Path.Combine(area.SourceDirectory, "src", "Calc"), area.TempDirectory, area.CliHomeDirectory,
            area.NuGetPackagesDirectory, area.NuGetHttpCacheDirectory, area.NuGetPluginsCacheDirectory, area.CreateResultsDirectory(),
        ];
        foreach (var folder in folders)
        {
            Assert.StartsWith(area.RootDirectory + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(folder), folder);
            Assert.Equal(0, await LowWriteAsync(folder));
        }

        // The repository the commit came from is not: that is the point of the area.
        Assert.NotEqual(0, await LowWriteAsync(_repo.Root));
        Assert.Equal(before, _repo.Snapshot());
    }

    [Fact]
    public async Task The_copy_is_written_while_no_Low_process_can_write_the_area()
    {
        var commit = _repo.Head;
        int? lowWhileCopying = null;
        string? copiedInto = null;
        var copy = CopyOf(commit);

        await using var area = await Factory().CreateAsync(
            commit,
            async (destination, ct) =>
            {
                copiedInto = destination;
                lowWhileCopying = await LowWriteAsync(destination);
                await copy(destination, ct);
            },
            Ct);

        Assert.Equal(area.SourceDirectory, copiedInto);
        Assert.NotEqual(0, lowWhileCopying);
        Assert.False(File.Exists(Path.Combine(area.SourceDirectory, "escrito-por-low.txt")));
        Assert.Equal(0, await LowWriteAsync(area.SourceDirectory));
    }

    [Fact]
    public async Task A_commit_whose_copy_is_refused_leaves_nothing_behind_and_says_why()
    {
        var target = Path.Combine(_container, "objetivo.txt");
        Directory.CreateDirectory(_container);
        File.WriteAllText(target, Path.Combine(_repo.Root, "README.md"));
        var blob = _repo.Run("hash-object", "-w", target).Trim();
        _repo.Run("update-index", "--add", "--cacheinfo", "120000," + blob + ",enlace");
        _repo.Run("commit", "-q", "-m", "enlace");

        var error = await Assert.ThrowsAsync<ProcessIsolationException>(() => Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct));

        Assert.Contains("enlace", error.Message, StringComparison.Ordinal);
        Assert.Empty(Areas());
    }

    [Theory]
    [InlineData("HEAD", false)]
    [InlineData("main", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("zz23456789abcdef0123456789abcdef01234567", false)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    public async Task A_commit_that_is_not_the_full_id_of_an_existing_commit_is_refused_and_nothing_is_left(string commit, bool copied)
    {
        var asked = false;
        var copy = CopyOf(commit);

        await Assert.ThrowsAsync<ProcessIsolationException>(() => Factory().CreateAsync(
            commit,
            (destination, ct) =>
            {
                asked = true;
                return copy(destination, ct);
            },
            Ct));

        // A malformed id is refused before anything is made; a well-formed one the repository lacks fails the copy.
        Assert.Equal(copied, asked);
        Assert.Empty(Areas());
    }

    [Fact]
    public async Task A_copy_that_outlives_its_timeout_is_stopped_and_nothing_is_left()
    {
        var factory = new LowIntegrityWorkAreaFactory(new LowIntegrityWorkAreaOptions(Root, CopyTimeout: TimeSpan.FromMilliseconds(200), LockFile: LockFile));

        var error = await Assert.ThrowsAsync<ProcessIsolationException>(() => factory.CreateAsync(_repo.Head, (_, ct) => Task.Delay(Timeout.Infinite, ct), Ct));

        Assert.Contains("took longer than", error.Message, StringComparison.Ordinal);
        Assert.Empty(Areas());
    }

    [Fact]
    public async Task Disposing_removes_the_area_and_never_follows_a_link_left_inside_it()
    {
        var outside = Path.Combine(_container, "del usuario");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "importante.txt"), "no se borra\n");
        var area = await Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct);
        var link = Path.Combine(area.SourceDirectory, "enlace");
        var made = await new ProcessRunner().RunAsync(new ProcessSpec("cmd.exe", ["/c", "mklink", "/J", link, outside]), Ct);
        Assert.True(made.ExitCode == 0, made.StandardOutput + made.StandardError);
        Assert.True(File.Exists(Path.Combine(link, "importante.txt")));

        await area.DisposeAsync();

        Assert.False(Directory.Exists(area.RootDirectory));
        Assert.Equal("no se borra\n", File.ReadAllText(Path.Combine(outside, "importante.txt")));
    }

    [Fact]
    public async Task A_report_is_opened_only_as_a_regular_file_inside_the_area_reached_without_links()
    {
        var outside = Path.Combine(_container, "fuera");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "test.trx"), "<TestRun />");
        await using var area = await Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct);
        var results = area.CreateResultsDirectory();
        File.WriteAllText(Path.Combine(results, "test.trx"), "<TestRun />");
        var link = Path.Combine(area.RootDirectory, "enlace");
        var made = await new ProcessRunner().RunAsync(new ProcessSpec("cmd.exe", ["/c", "mklink", "/J", link, outside]), Ct);
        Assert.Equal(0, made.ExitCode);

        await using (var report = area.OpenReport(Path.Combine(results, "test.trx"), 1024))
        {
            Assert.Equal("<TestRun />", await new StreamReader(report).ReadToEndAsync(Ct));
        }

        Assert.ThrowsAny<IOException>(() => area.OpenReport(Path.Combine(outside, "test.trx"), 1024));
        Assert.ThrowsAny<IOException>(() => area.OpenReport(Path.Combine(link, "test.trx"), 1024));
        Assert.ThrowsAny<IOException>(() => area.OpenReport(Path.Combine(results, "test.trx"), 4));
        Assert.ThrowsAny<IOException>(() => area.OpenReport(Path.Combine(results, "no-existe.trx"), 1024));
        Assert.ThrowsAny<IOException>(() => area.OpenReport(results, 1024));
    }

    [Fact]
    public async Task Nothing_is_read_or_made_through_a_link_above_the_area_that_now_leads_elsewhere()
    {
        // The root of the areas is reached through a junction, which a process could repoint while an area lives.
        var real = Directory.CreateDirectory(Path.Combine(_container, "real")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_container, "otro")).FullName;
        var link = Path.Combine(_container, "enlace");
        await JunctionAsync(link, real);
        await using var area = await new LowIntegrityWorkAreaFactory(new LowIntegrityWorkAreaOptions(Path.Combine(link, "verify"), LockFile: LockFile)).CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct);
        var results = area.CreateResultsDirectory();
        File.WriteAllText(Path.Combine(results, "test.trx"), "<TestRun />");

        // Now the junction leads to the same folders elsewhere, holding another report.
        var elsewhere = Directory.CreateDirectory(Path.Combine(other, Path.GetRelativePath(link, results))).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "test.trx"), "<TestRun>plantado</TestRun>");
        Directory.Delete(link);
        await JunctionAsync(link, other);
        try
        {
            Assert.ThrowsAny<IOException>(() => area.OpenReport(Path.Combine(results, "test.trx"), 1024));
            Assert.ThrowsAny<IOException>(() => area.CreateResultsDirectory());
        }
        finally
        {
            // Back to the real folders, so the area removes itself.
            Directory.Delete(link);
            await JunctionAsync(link, real);
        }
    }

    [Fact]
    public async Task An_area_holds_the_lock_of_the_user_while_it_lives_and_no_Low_process_can_ever_open_it()
    {
        var area = await Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct);

        Assert.True(File.Exists(LockFile), "No lock was taken for the area.");
        Assert.ThrowsAny<IOException>(() => File.Open(LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose());
        await area.DisposeAsync();

        // Released, the user can open it again; a Low process cannot even read it, so none can take it and hold it.
        File.Open(LockFile, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
        Assert.NotEqual(0, await LowAsync("type", LockFile));
    }

    [Fact]
    public async Task No_area_is_made_while_another_process_holds_the_lock_of_the_user()
    {
        // Another instance of Agent Studio: a process that holds the lock, until it is told to let it go.
        await (await Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct)).DisposeAsync();
        using var other = Process.Start(new ProcessStartInfo(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"$f = [IO.File]::Open('{LockFile}', 'Open', 'Read', 'None'); [Console]::Out.WriteLine('held'); [Console]::Out.Flush(); [void][Console]::In.ReadLine(); $f.Dispose()"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.Equal("held", await other.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct));

            var creating = Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Assert.False(creating.IsCompleted, "An area was made while another process held the lock of the user.");
            Assert.Empty(Areas());

            await other.StandardInput.WriteLineAsync();
            await other.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await using var area = await creating.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Single(Areas());
        }
        finally
        {
            if (!other.HasExited)
            {
                other.Kill();
            }
        }
    }

    [Fact]
    public async Task The_areas_of_runs_that_did_not_end_are_removed_before_the_next_without_following_links()
    {
        var victim = Directory.CreateDirectory(Path.Combine(_container, "del usuario")).FullName;
        File.WriteAllText(Path.Combine(victim, "importante.txt"), "no se borra\n");

        // What a run that crashed leaves: its area, with a link a step made inside it.
        var leftover = Directory.CreateDirectory(Path.Combine(Root, "v-0123456789ab", "src")).Parent!.FullName;
        await JunctionAsync(Path.Combine(leftover, "src", "enlace"), victim);

        // Not areas of a version that takes the lock: an area of an older version may be in use, the rest is not ours.
        var older = Directory.CreateDirectory(Path.Combine(Root, "0123456789ab")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(Root, "otro")).FullName;

        await using var area = await Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct);

        Assert.False(Directory.Exists(leftover));
        Assert.Equal("no se borra\n", File.ReadAllText(Path.Combine(victim, "importante.txt")));
        Assert.True(Directory.Exists(older));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(area.RootDirectory));
    }

    /// <summary>
    /// The label of the lock is only set when Agent Studio creates it (ADR-0018, a cheap mitigation of a residual): a lock
    /// that already exists without it was made by someone else, and with no label (Windows reads it as Medium with
    /// no-write-up alone) or a Low one, a Low process can open it and hold it so that no verification ever runs. It is
    /// refused, never relabelled nor replaced, and nothing is made.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("M")]
    [InlineData("L")]
    public async Task A_lock_that_already_exists_without_the_label_of_the_lock_is_refused_and_nothing_is_made(string? level)
    {
        Directory.CreateDirectory(_container);
        await File.WriteAllTextAsync(LockFile, string.Empty, Ct);
        if (level is not null)
        {
            var labelled = await new ProcessRunner().RunAsync(new ProcessSpec("icacls.exe", [LockFile, "/setintegritylevel", level], Timeout: TimeSpan.FromSeconds(30)), Ct);
            Assert.True(labelled.ExitCode == 0, labelled.StandardOutput + labelled.StandardError);
        }

        var refusal = await Assert.ThrowsAsync<ProcessIsolationException>(() => Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct));

        Assert.Contains(LockFile, refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("label", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(Areas());

        // Refused as it was found, and let go: nothing relabelled it, and nothing holds it any more.
        Assert.Equal(0, await LowAsync("type", LockFile));
        File.Open(LockFile, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
    }

    [Fact]
    public async Task An_area_that_cannot_be_prepared_lets_the_lock_go()
    {
        await Assert.ThrowsAsync<ProcessIsolationException>(() => Factory().CreateAsync(_repo.Head, (_, _) => throw new IOException("copia rota"), Ct));

        await using var area = await Factory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Single(Areas());
    }

    [Fact]
    public void A_lock_of_its_own_needs_a_root_of_its_own()
    {
        // The leftovers of a root are only removed under the lock every run in that root takes.
        Assert.Throws<ArgumentException>(() => new LowIntegrityWorkAreaFactory(new LowIntegrityWorkAreaOptions(LockFile: LockFile)));
    }

    [Fact]
    public async Task By_default_the_areas_live_under_LocalLow()
    {
        var localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

        string root;
        await using (var area = await new LowIntegrityWorkAreaFactory().CreateAsync(_repo.Head, CopyOf(_repo.Head), Ct))
        {
            root = area.RootDirectory;
            Assert.StartsWith(Path.Combine(localLow, "AgentStudio", "verify") + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await LowWriteAsync(area.SourceDirectory));

            // LocalLow is Low, but the folders above the areas are labelled Medium: no Low process can plant anything there.
            Assert.NotEqual(0, await LowWriteAsync(Path.Combine(localLow, "AgentStudio", "verify")));
            Assert.NotEqual(0, await LowAsync("mkdir", Path.Combine(localLow, "AgentStudio", "verify", "plantado")));
            Assert.False(Directory.Exists(Path.Combine(localLow, "AgentStudio", "verify", "plantado")));
        }

        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_container))
        {
            DeleteTree(_container);
        }
    }

    // A junction is removed as a link: a recursive delete would ask for DeleteVolumeMountPoint, which a user without
    // administrator rights is refused.
    private static void DeleteTree(string folder)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo directory)
            {
                DeleteTree(directory.FullName);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        Directory.Delete(folder);
    }
}
