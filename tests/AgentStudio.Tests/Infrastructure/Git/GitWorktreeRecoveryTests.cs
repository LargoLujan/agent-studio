using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// What is left behind by crashes, kills, files held open by Windows and half-finished operations, and how the git
/// layer recovers from it without ever touching what is not its own.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeRecoveryTests : GitManagerTestBase
{
    // ---- a vanished worktree is recreated without pruning anything else -------------------------------------

    [Fact]
    public async Task Recreating_a_vanished_worktree_never_prunes_the_users_own_worktrees()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        // The user has a worktree of their own on a removable drive that is unplugged right now.
        var usb = Path.GetFullPath(Path.Combine(Repo.WorktreeRoot, "..", "usb"));
        var feature = Path.Combine(usb, "feature");
        Repo.Run("worktree", "add", "-q", "-b", "feature", feature, Repo.BaseCommit);
        var unplugged = usb + "-desenchufado";
        Directory.Move(usb, unplugged);

        // Agent Studio's worktree vanished too.
        DeleteDirectory(lease.Path);

        var again = await EnsureAsync(manager, "T1");

        Assert.Equal(lease, again);
        Assert.True(File.Exists(Path.Combine(again.Path, "README.md")));
        Assert.Contains(ListWorktrees(), e => e.Branch == "refs/heads/feature");

        // Once the drive is back, the user's worktree works as before.
        Directory.Move(unplugged, usb);
        Assert.Equal(Repo.BaseCommit, TempGitRepo.Git(feature, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Ensure_recreates_a_vanished_worktree_whose_run_folder_is_gone_too()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        DeleteDirectory(Path.GetDirectoryName(lease.Path)!);

        var again = await EnsureAsync(manager, "T1");

        Assert.Equal(lease, again);
        Assert.True(File.Exists(Path.Combine(again.Path, "README.md")));
        Assert.Equal(ExpectedLockReason("T1"), Assert.Single(ListWorktrees().Skip(1)).LockReason);
    }

    // ---- a file held open (antivirus, indexer, build server) -----------------------------------------------

    [Fact]
    public async Task Remove_waits_for_a_file_released_shortly_and_then_removes_the_worktree()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var held = Path.Combine(lease.Path, "bloqueado.bin");
        File.WriteAllBytes(held, [1, 2, 3]);

        // Longer than git's own retries when it deletes a file in use (about 0.3 s).
        var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(1200, Ct);
                await stream.DisposeAsync();
            },
            Ct);

        var outcome = await manager.RemoveAsync(lease, discardChanges: true, Ct);
        await release;

        Assert.Equal(RemovalOutcome.Removed, outcome);
        Assert.False(Directory.Exists(lease.Path));
        Assert.Empty(ListWorktrees().Skip(1));
        Assert.Equal(lease, await EnsureAsync(manager, "T1"));
    }

    [Fact]
    public async Task Remove_that_keeps_failing_leaves_the_worktree_registered_locked_and_intact()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var held = Path.Combine(lease.Path, "bloqueado.bin");
        File.WriteAllBytes(held, [1, 2, 3]);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => manager.RemoveAsync(lease, discardChanges: true, Ct));

            // Nothing was half done: still registered, still locked by Agent Studio, every file in place.
            var entry = Assert.Single(ListWorktrees().Skip(1));
            Assert.Equal(ExpectedLockReason("T1"), entry.LockReason);
            Assert.True(File.Exists(Path.Combine(lease.Path, "README.md")));
            Assert.True(File.Exists(Path.Combine(lease.Path, ".git")));
        }

        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: true, Ct));
        Assert.Equal(lease, await EnsureAsync(manager, "T1"));
    }

    [Fact]
    public async Task Remove_deletes_an_orphan_left_by_an_interrupted_git_worktree_remove_only_when_discarding()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        // A plain `git worktree remove` hit a file held open: git dropped the registration and the lock, not the folder.
        var held = Path.Combine(lease.Path, "bloqueado.bin");
        File.WriteAllBytes(held, [1, 2, 3]);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.NotEqual(0, TempGitRepo.TryGit(Repo.Root, "worktree", "remove", "--force", "--force", lease.Path).ExitCode);
        }

        Assert.Empty(ListWorktrees().Skip(1));
        Assert.True(Directory.Exists(lease.Path));

        // The orphan holds a link to something that must survive.
        var victim = Path.GetFullPath(Path.Combine(Repo.WorktreeRoot, "..", "victima"));
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "importante.txt"), "no borrar");
        CreateJunction(Path.Combine(lease.Path, "enlace"), victim);

        // Its state can no longer be checked, so it counts as dirty until the caller approves discarding it.
        Assert.Equal(RemovalOutcome.RefusedDirty, await manager.RemoveAsync(lease, discardChanges: false, Ct));
        Assert.True(Directory.Exists(lease.Path));

        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: true, Ct));
        Assert.False(Directory.Exists(lease.Path));
        Assert.True(File.Exists(Path.Combine(victim, "importante.txt")));

        // The name can be used again.
        var again = await EnsureAsync(manager, "T1");
        Assert.True(File.Exists(Path.Combine(again.Path, "README.md")));
    }

    // ---- an interrupted creation ---------------------------------------------------------------------------

    [Fact]
    public async Task Ensure_refuses_to_reuse_a_worktree_whose_creation_did_not_finish()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");

        // `git worktree add` killed before its checkout: registered and locked by Agent Studio, but no files and no index.
        var path = Path.Combine(Path.GetDirectoryName(t1.Path)!, "T9");
        Repo.Run("worktree", "add", "-q", "--detach", "--no-checkout", "--lock", "--reason", ExpectedLockReason("T9"), path, Repo.BaseCommit);

        await Assert.ThrowsAsync<WorktreeMismatchException>(() => EnsureAsync(manager, "T9"));

        // The way out: remove it, discarding whatever it holds, and create it again.
        var broken = Assert.Single(await manager.ListOwnedAsync(Project, Ct), l => l.Name == "T9");
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(broken, discardChanges: true, Ct));
        var fixedLease = await EnsureAsync(manager, "T9");
        Assert.True(File.Exists(Path.Combine(fixedLease.Path, "README.md")));
        Assert.Equal(string.Empty, Repo.Run("-C", fixedLease.Path, "status", "--porcelain=v1"));
    }

    // ---- lock files left by a killed git ---------------------------------------------------------------------

    [Fact]
    public async Task CommitAll_reports_a_stale_index_lock_and_leaves_it_in_place()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "nuevo.txt", "x\n");

        // A git process killed while it held the index of the worktree.
        var lockFile = Path.Combine(lease.AdminDirectory, "index.lock");
        File.WriteAllText(lockFile, string.Empty);

        var error = await Assert.ThrowsAsync<GitLockFileException>(() => manager.CommitAllAsync(lease, "mensaje", Ct));

        Assert.True(SamePath(lockFile, error.LockFile));
        Assert.Contains(error.LockFile, error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(lockFile), "Agent Studio must never delete a git lock file.");
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task MergeInto_reports_a_stale_ref_lock_and_leaves_it_in_place()
    {
        var manager = NewManager();
        var integration = await EnsureAsync(manager, "_integration");
        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");
        var lockFile = Path.Combine(Repo.Root, ".git", "refs", "agentstudio", TestRun.Value, "_integration.lock");
        File.WriteAllText(lockFile, string.Empty);

        var error = await Assert.ThrowsAsync<GitLockFileException>(
            () => manager.MergeIntoAsync(Repo.Root, integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct));

        Assert.True(SamePath(lockFile, error.LockFile));
        Assert.True(File.Exists(lockFile), "Agent Studio must never delete a git lock file.");
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", integration.RefName).Trim());
    }

    // ---- the hidden ref of a task ---------------------------------------------------------------------------

    [Fact]
    public async Task CommitAll_does_not_overwrite_a_task_ref_that_moved_meanwhile()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var competitor = Repo.Run("commit-tree", Repo.BaseCommit + "^{tree}", "-p", Repo.BaseCommit, "-m", "competidor").Trim();

        // Somebody else moves the ref right after the commit was made and before the ref is updated.
        recorder.AfterRun = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "commit")
            {
                recorder.AfterRun = null;
                Repo.Run("update-ref", lease.RefName, competitor);
            }
        };
        WriteIn(lease, "nuevo.txt", "x\n");

        var stale = await Assert.ThrowsAsync<StaleRefException>(() => manager.CommitAllAsync(lease, "mensaje", Ct));

        Assert.Equal(competitor, Repo.Run("rev-parse", lease.RefName).Trim());
        Assert.Equal(Repo.BaseCommit, stale.Expected);
        Assert.Equal(competitor, stale.Actual);
    }
}
