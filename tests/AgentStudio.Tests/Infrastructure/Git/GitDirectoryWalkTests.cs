using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The eighth round (S-01 to S-03) found again the class of the five rounds before it: a link planted beforehand under the
/// user's git directory, which an internal git command or the git-lfs filter later writes through, each time at a new
/// destination. The control for the class is one walk of the git directory before every command that can write into it
/// (every content command, every ref and object write) and in the delivery's checks: no reparse point anywhere, no file
/// with a second name outside the object stores, no folder that cannot be listed. These tests plant the link where no
/// check named it, keep a victim outside the repository, and check the victim and the commands that ran, not only the
/// exception.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitDirectoryWalkTests : GitManagerTestBase
{
    private const string Message = "Agent Studio: entrega de la ejecución";

    // Every git sub-command that can write into the git directory: the content commands and the ref and object writers.
    private static readonly string[] Writers =
        ["status", "add", "commit", "reset", "clean", "checkout", "merge", "merge-tree", "archive", "update-ref", "commit-tree"];

    private string CommonDirectory => Path.Combine(Repo.Root, ".git");

    private string Container => Path.GetDirectoryName(Repo.Root)!;

    private string RunBranch => "refs/heads/agentstudio/run-" + TestRun.Value;

    private static bool GitLfsAvailable => new ExecutableLocator().Find("git-lfs") is not null;

    private static bool Ran(IEnumerable<AgentStudio.Application.Execution.ProcessSpec> specs, string subCommand, string? argument = null) =>
        specs.Any(s => RecordingProcessRunner.SubCommand(s) == subCommand && (argument is null || s.Arguments.Contains(argument)));

    /// <summary>The first command among <paramref name="specs"/> that can write into the git directory (<c>worktree add</c> and <c>remove</c> too), or null.</summary>
    private static string? FirstWriter(IEnumerable<AgentStudio.Application.Execution.ProcessSpec> specs)
    {
        foreach (var spec in specs)
        {
            var sub = RecordingProcessRunner.SubCommand(spec);
            var index = spec.Arguments.ToList().IndexOf(sub);
            if (Writers.Contains(sub) || (sub == "worktree" && index + 1 < spec.Arguments.Count && spec.Arguments[index + 1] is "add" or "remove"))
            {
                return sub;
            }
        }

        return null;
    }

    private static string Listing(string folder) =>
        string.Join(",", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f) + "=" + File.ReadAllText(f)).Order(StringComparer.Ordinal));

    /// <summary>git-lfs installed in the repository and <c>*.dat</c> tracked by it; the commit has no lfs file, so creating a worktree on it runs no filter.</summary>
    private string CommitLfsTracking()
    {
        Repo.Run("lfs", "install", "--local");
        Repo.Write(".gitattributes", "*.dat filter=lfs diff=lfs merge=lfs -text\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "seguimiento lfs");
        return Repo.Head;
    }

    /// <summary>A file outside the repository with <paramref name="content"/>, the other name a planted hard link gives it.</summary>
    private string NewVictimFile(string folder, string content)
    {
        var victim = Path.Combine(NewVictim(folder), "precioso.bin");
        File.WriteAllText(victim, content);
        return victim;
    }

    /// <summary>Replaces <paramref name="file"/> by a link (hard or symbolic) to a copy of it outside the repository, which is returned.</summary>
    private string PlantFileLink(string file, string kind, PlantedLinks links)
    {
        var victim = Path.Combine(NewVictim("fuera-" + Guid.NewGuid().ToString("N")[..6]), Path.GetFileName(file));
        File.Copy(file, victim);
        File.Delete(file);
        if (kind == "hard")
        {
            links.HardLink(file, victim);
        }
        else
        {
            File.CreateSymbolicLink(file, victim);
        }

        return victim;
    }

    private static void RemoveSymbolicLink(string file)
    {
        var info = new FileInfo(file);
        if (info.Exists && info.LinkTarget is not null)
        {
            info.Delete();
        }
    }

    /// <summary>How many names <paramref name="file"/> has, as <c>fsutil hardlink list</c> lists them (no privilege needed).</summary>
    private static int NamesOf(string file)
    {
        var start = new ProcessStartInfo("fsutil.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "hardlink", "list", file })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"fsutil hardlink list failed: {error.GetAwaiter().GetResult()}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static ApprovedDelivery FastForward(string root, string commit, string head)
    {
        var binding = DeliveryApproval.Bind(root, "main", DeliveryMode.FastForwardOnly, commit, head);
        var request = new ApprovalRequest("deliver-integration", "Entregar la ejecución", RiskLevel.Critical, null, [], ProjectId, TestRun.Value, Binding: binding);
        var decision = new ApprovalDecision(true, "usuario", DateTimeOffset.UtcNow, null, binding);
        return new ApprovedDelivery(root, "main", head, commit, DeliveryMode.FastForwardOnly, Message, request, decision);
    }

    // ---- S-01: the configuration git-lfs writes -------------------------------------------------------------------------
    // Measured with git-lfs 3.5.1 (git 2.45.2): the first filter run in a repository whose configuration lacks
    // lfs.repositoryformatversion (a `git add` of a tracked file) wrote `[lfs] repositoryformatversion = 0` into .git/config
    // through `git config`, and git resolves a symbolic link at the file it locks. `git status` runs that filter too (G-01).

    [Theory]
    [InlineData("symbolic")]
    [InlineData("hard")]
    public async Task S01_A_link_at_the_common_config_is_refused_before_a_content_command_lets_git_lfs_write_it(string kind)
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        if (kind == "symbolic")
        {
            SkipUnlessSymbolicLinks();
        }

        var tracking = CommitLfsTracking();
        Repo.Write("big.dat", "uno\n");
        Repo.Run("add", "--", "big.dat");
        Repo.Run("commit", "-q", "-m", "fichero lfs");
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1", tracking);
        WriteIn(lease, "otro.dat", "contenido del agente " + Guid.NewGuid().ToString("N") + "\n");

        // The user's lfs file changed with the same size and an older time: the status of InspectAsync runs the clean.
        var userFile = Path.Combine(Repo.Root, "big.dat");
        File.WriteAllText(userFile, "dos\n");
        File.SetLastWriteTimeUtc(userFile, DateTime.UtcNow.AddMinutes(-3));

        // What git lfs install leaves when git-lfs has not run its filter here yet: no lfs.repositoryformatversion.
        Repo.Run("config", "--unset", "lfs.repositoryformatversion");
        var config = Path.Combine(CommonDirectory, "config");
        using var links = new PlantedLinks();
        var victim = PlantFileLink(config, kind, links);
        Assert.Equal(1, TempGitRepo.TryGit(Repo.Root, "config", "--get", "lfs.repositoryformatversion").ExitCode);
        var before = File.ReadAllBytes(victim);
        try
        {
            var start = recorder.Specs.Count;

            var errors = new[]
            {
                await Record.ExceptionAsync(() => manager.InspectAsync(Repo.Root, Ct)),
                await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: datos", Ct)),
            };

            // First what matters: nothing was written through the link.
            Assert.Equal(before, File.ReadAllBytes(victim));
            Assert.All(errors, error => Assert.Contains("reached through a link", Assert.IsType<WorktreeOwnershipException>(error).Message, StringComparison.Ordinal));
            var after = recorder.Specs.Skip(start).ToList();
            Assert.False(Ran(after, "status") || Ran(after, "add") || Ran(after, "commit"), $"a content command ran with the common config as a {kind} link, so git-lfs could write through it.");
        }
        finally
        {
            RemoveSymbolicLink(config);
        }
    }

    // ---- S-02: packed-refs ---------------------------------------------------------------------------------------------
    // `update-ref -d` of a packed ref rewrites packed-refs through packed-refs.lock, and git resolves a symbolic link at the
    // file it locks, so through one the rewrite replaces the file it points to. Every ref write walks the git directory first.

    [Theory]
    [InlineData("hard")]
    [InlineData("symbolic")]
    public async Task S02_A_link_at_packed_refs_is_refused_before_an_Agent_Studio_ref_is_deleted_or_moved(string kind)
    {
        if (kind == "symbolic")
        {
            SkipUnlessSymbolicLinks();
        }

        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        var child = Repo.Run("commit-tree", "-p", Repo.BaseCommit, "-m", "hijo", Repo.BaseCommit + "^{tree}").Trim();
        Repo.Run("pack-refs", "--all");
        var packedRefs = Path.Combine(CommonDirectory, "packed-refs");
        Assert.Contains(RunBranch, File.ReadAllText(packedRefs), StringComparison.Ordinal);
        using var links = new PlantedLinks();
        var victim = PlantFileLink(packedRefs, kind, links);
        var before = File.ReadAllBytes(victim);
        try
        {
            var start = recorder.Specs.Count;

            var errors = new[]
            {
                await Record.ExceptionAsync(() => manager.DeleteRefAsync(Repo.Root, RunBranch, Repo.BaseCommit, Ct)),
                await Record.ExceptionAsync(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, child, Ct)),
            };

            Assert.Equal(before, File.ReadAllBytes(victim));
            Assert.All(errors, error => Assert.Contains("reached through a link", Assert.IsType<WorktreeOwnershipException>(error).Message, StringComparison.Ordinal));
            Assert.False(Ran(recorder.Specs.Skip(start), "update-ref"), $"update-ref ran with packed-refs as a {kind} link to a file outside the repository.");
            Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", RunBranch).Trim());
        }
        finally
        {
            RemoveSymbolicLink(packedRefs);
        }
    }

    // ---- S-03: a partial download of git-lfs ------------------------------------------------------------------------------

    /// <summary>
    /// S-03: git-lfs keeps a partial download in <c>lfs/incomplete/&lt;oid&gt;.part</c> and reopens it read-write to resume
    /// it, so through a second name the download writes another file. A hard link anywhere in the git-lfs storage but at its
    /// object files is refused before any content command, whose filters can download, and before the delivery.
    /// </summary>
    [Fact]
    public async Task S03_A_hard_link_at_a_partial_download_of_git_lfs_is_refused_before_a_content_command()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var tracking = CommitLfsTracking();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1", tracking);
        WriteIn(lease, "big.dat", "contenido del agente " + Guid.NewGuid().ToString("N") + "\n");
        var oid = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(lease.Path, "big.dat"))));
        var incomplete = Path.Combine(CommonDirectory, "lfs", "incomplete");
        Directory.CreateDirectory(incomplete);
        var victim = NewVictimFile("otro-repositorio", "precioso\n");
        using var links = new PlantedLinks();
        links.HardLink(Path.Combine(incomplete, oid + ".part"), victim);
        var start = recorder.Specs.Count;

        var refusals = new[]
        {
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.InspectAsync(Repo.Root, Ct)),
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(lease, "T1: datos", Ct)),
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, tracking, tracking, Ct)),
        };

        Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
        Assert.Equal("precioso\n", File.ReadAllText(victim));
        Assert.Null(FirstWriter(recorder.Specs.Skip(start)));
    }

    /// <summary>
    /// O-01 (ninth round): the walk inspects the entries of the git directory, and also the directory itself. A common
    /// directory that is a junction leads every write of git elsewhere, so it is refused before any command that writes.
    /// </summary>
    [Fact]
    public async Task O01_A_common_directory_that_is_a_junction_is_refused_before_a_command_that_writes()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var head = Repo.Head;
        var victim = Path.Combine(Container, "git-de-fuera");
        ReplaceWithJunction(CommonDirectory, victim);
        try
        {
            var before = Listing(victim);
            var start = recorder.Specs.Count;

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.InspectAsync(Repo.Root, Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, Listing(victim));
            Assert.Null(FirstWriter(recorder.Specs.Skip(start)));
        }
        finally
        {
            RemoveJunction(CommonDirectory);
            CopyDirectory(victim, CommonDirectory);
        }

        Assert.Equal(head, Repo.Head);
    }

    // ---- destinations no list names -----------------------------------------------------------------------------------
    // No check names these files: the walk refuses them because it walks everything, the worktrees of the user, the hooks
    // and the git directories of the submodules included.

    public static TheoryData<string, string> UnknownDestinations() => new()
    {
        { "hard", "FETCH_HEAD" },
        { "hard", "info/exclude" },
        { "hard", "refs/tags/v1" },
        { "junction", "hooks/lib" },
        { "junction", "modules/sub" },
        { "junction", "worktrees/del-usuario/logs" },
        { "symbolic", "description" },
    };

    [Theory]
    [MemberData(nameof(UnknownDestinations))]
    public async Task A_link_planted_where_no_check_names_it_is_refused_by_the_walk(string kind, string where)
    {
        if (kind == "symbolic")
        {
            SkipUnlessSymbolicLinks();
        }

        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var child = Repo.Run("commit-tree", "-p", Repo.BaseCommit, "-m", "hijo", Repo.BaseCommit + "^{tree}").Trim();
        Repo.Run("tag", "v1");

        // A worktree of the user's own, which Agent Studio does not own: its administrative folder is under the common one.
        Repo.Run("worktree", "add", "-q", "--detach", Path.Combine(Container, "del-usuario"), Repo.BaseCommit);
        var planted = Path.Combine(CommonDirectory, where.Replace('/', Path.DirectorySeparatorChar));
        using var links = new PlantedLinks();
        string victim;
        string before;
        if (kind == "junction")
        {
            victim = NewVictim("fuera-" + Path.GetFileName(planted));
            if (Directory.Exists(planted))
            {
                ReplaceWithJunction(planted, victim);
            }
            else
            {
                File.WriteAllText(Path.Combine(victim, "precioso.txt"), "del usuario\n");
                Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
                CreateJunction(planted, victim);
            }

            before = Listing(victim);
        }
        else
        {
            if (!File.Exists(planted))
            {
                File.WriteAllText(planted, "precioso\n");
            }

            victim = PlantFileLink(planted, kind, links);
            before = File.ReadAllText(victim);
        }

        try
        {
            var start = recorder.Specs.Count;

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.InspectAsync(Repo.Root, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, Repo.BaseCommit, child, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct)),
            };

            Assert.All(refusals, refusal =>
            {
                Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
                Assert.Contains(planted, refusal.Message, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal(before, kind == "junction" ? Listing(victim) : File.ReadAllText(victim));
            Assert.Null(FirstWriter(recorder.Specs.Skip(start)));
        }
        finally
        {
            RemoveJunction(planted);
            RemoveSymbolicLink(planted);
        }
    }

    /// <summary>
    /// The class, not a destination: with one hard link planted where nothing names it, no operation that writes the
    /// repository runs a command that can write into the git directory (content commands, ref and object writes), and the
    /// cleanup, which only drops the registration of its own worktree, still works.
    /// </summary>
    [Fact]
    public async Task No_operation_runs_a_command_that_can_write_into_the_git_directory_while_a_link_is_planted_in_it()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var commit = await CommitInAsync(manager, lease, "uno.txt", "1\n");
        WriteIn(lease, "dos.txt", "2\n");
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        var task = new TaskId("I1");
        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, task, Repo.BaseCommit, Ct);
        var archive = NewVictim("copia");
        var victim = NewVictimFile("otro-repositorio", "precioso\n");
        using var links = new PlantedLinks();
        links.HardLink(Path.Combine(CommonDirectory, "FETCH_HEAD"), victim);
        var start = recorder.Specs.Count;

        var operations = new (string Name, Func<Task> Run)[]
        {
            ("InspectAsync", () => manager.InspectAsync(Repo.Root, Ct)),
            ("CaptureUserRepositoryStateAsync", () => manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)),
            ("EnsureAsync", () => EnsureAsync(manager, "T2")),
            ("CommitAllAsync", () => manager.CommitAllAsync(lease, "T1: más", Ct)),
            ("CheckoutAsync", () => manager.CheckoutAsync(lease, Repo.BaseCommit, Ct)),
            ("RemoveAsync without discarding", () => manager.RemoveAsync(lease, discardChanges: false, Ct)),
            ("MergeIntoAsync", () => manager.MergeIntoAsync(Repo.Root, RunBranch, Repo.BaseCommit, commit, "Integra", Ct)),
            ("MergeIntoCandidateAsync", () => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, task, candidate, commit, "Integra T1", Ct)),
            ("PrepareCandidateAsync", () => manager.PrepareCandidateAsync(Repo.Root, TestRun, new TaskId("I2"), Repo.BaseCommit, Ct)),
            ("EnsureRunBranchAsync", () => manager.EnsureRunBranchAsync(Repo.Root, new AgentStudio.Domain.Orchestration.RunId("20261015-ffffff"), Repo.BaseCommit, Ct)),
            ("EnsureRefAsync", () => manager.EnsureRefAsync(Repo.Root, "refs/heads/agentstudio/run-20261015-eeeeee", Repo.BaseCommit, Ct)),
            ("FastForwardRunBranchAsync", () => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, commit, Ct)),
            ("DeleteRefAsync", () => manager.DeleteRefAsync(Repo.Root, RunBranch, Repo.BaseCommit, Ct)),
            ("ArchiveCommitAsync", () => manager.ArchiveCommitAsync(Repo.Root, commit, archive, Ct)),
            ("FindDeliveryRefusalsAsync", () => manager.FindDeliveryRefusalsAsync(Repo.Root, Repo.BaseCommit, commit, Ct)),
            ("DeliverToUserBranchAsync", () => manager.DeliverToUserBranchAsync(FastForward(Repo.Root, commit, Repo.BaseCommit), Ct)),
        };

        foreach (var (name, run) in operations)
        {
            var error = await Record.ExceptionAsync(run);
            Assert.True(error is WorktreeOwnershipException, $"{name} did not refuse the planted link: {error?.GetType().Name ?? "no exception"} {error?.Message}");
            Assert.Contains("reached through a link", error!.Message, StringComparison.Ordinal);
        }

        Assert.Equal("precioso\n", File.ReadAllText(victim));
        Assert.Null(FirstWriter(recorder.Specs.Skip(start)));
        Assert.Equal(Repo.BaseCommit, Repo.Head);
        Assert.Empty(Directory.EnumerateFileSystemEntries(archive));

        // The cleanup still works: it only drops the registration of its own worktree, whose folder it walks itself.
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: true, Ct));
        Assert.Equal("precioso\n", File.ReadAllText(victim));
    }

    /// <summary>R3-03 for the walk: a folder of the git directory that cannot be listed may hide a link, so it is refused.</summary>
    [Fact]
    public async Task A_folder_of_the_git_directory_that_cannot_be_listed_is_refused()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var locked = new DirectoryInfo(Path.Combine(CommonDirectory, "hooks", "lib"));
        locked.Create();
        File.WriteAllText(Path.Combine(locked.FullName, "helper.sh"), "#!/bin/sh\n");
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var acl = locked.GetAccessControl();
        acl.AddAccessRule(rule);
        locked.SetAccessControl(acl);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => locked.EnumerateFileSystemInfos().ToList());

            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.InspectAsync(Repo.Root, Ct));

            Assert.Contains("could not be listed", refusal.Message, StringComparison.Ordinal);
            Assert.Contains(locked.FullName, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Ran(recorder.Specs, "status"), "git status ran although a folder of the git directory could not be checked for links.");
        }
        finally
        {
            acl = locked.GetAccessControl();
            acl.RemoveAccessRule(rule);
            locked.SetAccessControl(acl);
        }
    }

    /// <summary>
    /// The project may itself be a linked worktree of another repository: then its git directory is not the common one
    /// (<c>&lt;common&gt;/worktrees/&lt;id&gt;</c>), and what its commands write there is walked too.
    /// </summary>
    [Fact]
    public async Task A_link_in_the_git_directory_of_a_project_that_is_a_linked_worktree_is_refused()
    {
        var project = Path.Combine(Container, "proyecto");
        Repo.Run("worktree", "add", "-q", "-b", "proyecto", project);
        var gitDirectory = Path.GetFullPath(TempGitRepo.Git(project, "rev-parse", "--absolute-git-dir").Trim());
        Assert.NotEqual(Path.GetFullPath(CommonDirectory), gitDirectory);
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var index = Path.Combine(gitDirectory, "index");
        using var links = new PlantedLinks();
        var victim = PlantFileLink(index, "hard", links);
        var before = File.ReadAllBytes(victim);

        var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.InspectAsync(project, Ct));

        Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(victim));
        Assert.False(Ran(recorder.Specs, "status"), "git status ran with the index of the project hard-linked to a file outside it.");
    }

    // ---- what keeps working ---------------------------------------------------------------------------------------------
    // Measured with git 2.45.2 and git-lfs 3.5.1: git clone --local gives the clone a second name for every file of objects/
    // (loose objects, packs, objects/info/packs, the commit-graph and objects/info/commit-graphs/*), and the smudge of its
    // checkout links the lfs objects of the source (lfs/objects/<aa>/<bb>/<oid>). Those files are immutable, and git and
    // git-lfs replace them by a rename, never in place, so a second name there is not refused.

    [Fact]
    public async Task A_repository_made_with_git_clone_local_whose_objects_are_hard_links_keeps_working()
    {
        Repo.Write("dos.txt", "dos\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "dos");
        Repo.Run("repack", "-a", "-d", "-q");
        Repo.Run("commit-graph", "write", "--reachable", "--split");
        Repo.Run("update-server-info");
        Repo.Write("tres.txt", "tres\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "tres");
        var clone = Path.Combine(Container, "clon");
        TempGitRepo.Git(
            Container,
            "clone", "-q", "--local", "-c", "core.autocrlf=false", "-c", "user.name=Agent Studio Tests", "-c", "user.email=tests@localhost", "-c", "commit.gpgsign=false",
            Repo.Root, clone);
        var objects = Path.Combine(clone, ".git", "objects");
        var loose = Directory.EnumerateDirectories(objects).Where(d => Path.GetFileName(d).Length == 2).SelectMany(Directory.EnumerateFiles).First();
        foreach (var file in new[] { loose, Directory.EnumerateFiles(Path.Combine(objects, "pack"), "*.pack").Single(), Path.Combine(objects, "info", "packs"), Path.Combine(objects, "info", "commit-graphs", "commit-graph-chain") })
        {
            Assert.True(NamesOf(file) > 1, $"git clone --local did not hard-link '{file}'; the measurement this test relies on changed.");
        }

        var manager = NewManager();
        var head = TempGitRepo.Git(clone, "rev-parse", "HEAD").Trim();
        var project = new ProjectContext(ProjectId, clone);

        await manager.InspectAsync(clone, Ct);
        await manager.CaptureUserRepositoryStateAsync(clone, Ct);
        var lease = await manager.EnsureAsync(new WorktreeRequest(project, TestRun, "T1", head), Ct);
        var commit = await CommitInAsync(manager, lease, "nuevo.txt", "del agente\n");
        await manager.EnsureRunBranchAsync(clone, TestRun, head, Ct);
        var task = new TaskId("I1");
        var candidate = await manager.PrepareCandidateAsync(clone, TestRun, task, head, Ct);
        var merged = (await manager.MergeIntoCandidateAsync(clone, TestRun, task, candidate, commit, "Integra T1", Ct)).ResultCommit!;
        await manager.FastForwardRunBranchAsync(clone, TestRun, head, merged, Ct);
        Assert.Empty(await manager.FindDeliveryRefusalsAsync(clone, head, merged, Ct));
        Assert.Equal(UserBranchDeliveryStatus.Delivered, (await manager.DeliverToUserBranchAsync(FastForward(clone, merged, head), Ct)).Status);
        Assert.True(await manager.DeleteRefAsync(clone, $"refs/agentstudio/{TestRun.Value}/_candidate/I1", merged, Ct));
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: false, Ct));
        Assert.Equal(merged, TempGitRepo.Git(clone, "rev-parse", "HEAD").Trim());
    }

    /// <summary>
    /// A normal repository with git-lfs, whose lfs objects are hard links as a clone with --local leaves them. The agent's
    /// commit runs the clean filter on a new lfs file; GitLfsHooksTests covers the filter through every other command.
    /// </summary>
    [Fact]
    public async Task A_repository_with_git_lfs_whose_lfs_objects_are_hard_links_keeps_working()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var tracking = CommitLfsTracking();
        Repo.Write("big.dat", "contenido grande\n");
        Repo.Run("add", "--", "big.dat");
        Repo.Run("commit", "-q", "-m", "fichero lfs");
        var clone = Path.Combine(Container, "clon-lfs");
        TempGitRepo.Git(
            Container,
            "clone", "-q", "--local", "-c", "core.autocrlf=false", "-c", "user.name=Agent Studio Tests", "-c", "user.email=tests@localhost", "-c", "commit.gpgsign=false",
            Repo.Root, clone);
        var lfsObjects = Path.Combine(clone, ".git", "lfs", "objects");
        var lfsObject = Directory.EnumerateFiles(lfsObjects, "*", SearchOption.AllDirectories).Single();
        Assert.True(NamesOf(lfsObject) > 1, "the checkout of the clone did not hard-link the lfs object; the measurement this test relies on changed.");

        // The index of the clone settles, so no status of Agent Studio cleans big.dat again (racy git).
        File.SetLastWriteTimeUtc(Path.Combine(clone, "big.dat"), DateTime.UtcNow.AddMinutes(-2));
        TempGitRepo.Git(clone, "update-index", "-q", "--refresh");

        var manager = NewManager();
        var head = TempGitRepo.Git(clone, "rev-parse", "HEAD").Trim();
        var project = new ProjectContext(ProjectId, clone);

        await manager.InspectAsync(clone, Ct);
        await manager.CaptureUserRepositoryStateAsync(clone, Ct);
        var lease = await manager.EnsureAsync(new WorktreeRequest(project, TestRun, "T1", tracking), Ct);
        var commit = await CommitInAsync(manager, lease, "nuevo.dat", "del agente\n");
        Assert.StartsWith("version https://git-lfs.github.com/spec/v1", TempGitRepo.Git(clone, "cat-file", "-p", commit + ":nuevo.dat"), StringComparison.Ordinal);
        await manager.EnsureRunBranchAsync(clone, TestRun, tracking, Ct);
        await manager.FastForwardRunBranchAsync(clone, TestRun, tracking, commit, Ct);
        Assert.Empty(await manager.FindDeliveryRefusalsAsync(clone, head, commit, Ct));
        Assert.True(await manager.DeleteRefAsync(clone, RunBranch, commit, Ct));
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: false, Ct));
        Assert.True(NamesOf(lfsObject) > 1);
    }
}
