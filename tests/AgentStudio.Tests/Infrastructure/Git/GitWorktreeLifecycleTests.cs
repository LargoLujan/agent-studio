using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>Create, reuse, list, inspect, check out and remove worktrees on a real temporary repository.</summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeLifecycleTests : GitManagerTestBase
{
    // ---- Inspect ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inspect_reports_root_head_branch_dirty_flag_and_git_version()
    {
        var manager = NewManager();

        var clean = await manager.InspectAsync(Repo.Root, Ct);

        Assert.True(SamePath(Repo.Root, clean.Root));
        Assert.Equal(Repo.BaseCommit, clean.HeadCommit);
        Assert.Equal("main", clean.HeadBranch);
        Assert.False(clean.HasUncommittedChanges);
        Assert.True(clean.GitVersion >= GitWorktreeManager.MinimumGitVersion, $"git {clean.GitVersion}");

        File.WriteAllText(Path.Combine(Repo.Root, "sin-seguimiento.txt"), "x");
        var dirty = await manager.InspectAsync(Repo.Root, Ct);
        Assert.True(dirty.HasUncommittedChanges);

        Repo.Run("checkout", "-q", "--detach");
        var detached = await manager.InspectAsync(Repo.Root, Ct);
        Assert.Null(detached.HeadBranch);
        Assert.Equal(Repo.BaseCommit, detached.HeadCommit);
    }

    [Fact]
    public async Task Inspect_does_not_count_what_Agent_Studio_keeps_in_its_folder_as_changes_of_the_user()
    {
        // V21: the demonstration repository after a day of work, clean but for the journal and the memory export.
        var manager = NewManager();
        Repo.Write(".agentstudio/bitacora/2026-10-29.md", "- prompt\n");
        Repo.Write(".agentstudio/memory/cronologico-2026-10.md", "# Memoria\n");
        Repo.Write(".agentstudio/memory/tema-errores-conocidos.md", "# Memoria\n");

        Assert.False((await manager.InspectAsync(Repo.Root, Ct)).HasUncommittedChanges);

        // A file of the user's that only looks like the export is the user's.
        Repo.Write(".agentstudio/memory/tema-notas.md", "mis notas\n");
        Assert.True((await manager.InspectAsync(Repo.Root, Ct)).HasUncommittedChanges);
    }

    [Fact]
    public async Task Inspect_of_a_project_in_a_subfolder_leaves_out_the_files_of_that_projects_own_folder()
    {
        var manager = NewManager();
        Repo.Write("src/Calc/.agentstudio/bitacora/2026-10-29.md", "- prompt\n");

        Assert.False((await manager.InspectAsync(Path.Combine(Repo.Root, "src", "Calc"), Ct)).HasUncommittedChanges);
        Assert.True((await manager.InspectAsync(Repo.Root, Ct)).HasUncommittedChanges);
    }

    [Fact]
    public async Task Inspect_from_a_subfolder_returns_the_repository_root()
    {
        var state = await NewManager().InspectAsync(Path.Combine(Repo.Root, "src", "Calc"), Ct);

        Assert.True(SamePath(Repo.Root, state.Root));
    }

    [Fact]
    public async Task Inspect_fails_for_a_folder_that_is_not_a_repository()
    {
        var folder = Path.Combine(Repo.WorktreeRoot, "..", "no-repo");
        Directory.CreateDirectory(folder);

        await Assert.ThrowsAsync<GitCommandException>(() => NewManager().InspectAsync(folder, Ct));
    }

    [Fact]
    public async Task Inspect_fails_for_a_repository_without_commits_because_there_is_nothing_to_base_a_worktree_on()
    {
        var empty = Path.Combine(Repo.WorktreeRoot, "..", "sin-commits");
        Directory.CreateDirectory(empty);
        TempGitRepo.Git(empty, "init", "-q", "-b", "main");

        await Assert.ThrowsAsync<GitCommandException>(() => NewManager().InspectAsync(empty, Ct));
    }

    [Fact]
    public async Task Nothing_inside_the_worktree_root_is_accepted_as_a_repository()
    {
        // A worktree passed as a repository would make git follow its .git file, which the agent controls.
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.InspectAsync(lease.Path, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.ChangedPathsAsync(lease.Path, Repo.BaseCommit, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.WriteDiffAsync(lease.Path, Repo.BaseCommit, Repo.BaseCommit, Stream.Null, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.MergeIntoAsync(lease.Path, lease.RefName, Repo.BaseCommit, Repo.BaseCommit, "m", Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.EnsureRefAsync(lease.Path, "agentstudio/run-x", Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.ListOwnedAsync(new ProjectContext(ProjectId, lease.Path), Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.EnsureAsync(Request("T2", project: new ProjectContext(ProjectId, lease.Path)), Ct));
    }

    // ---- Ensure ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ensure_creates_a_detached_locked_worktree_outside_the_repository()
    {
        var lease = await EnsureAsync(NewManager(), "T1");

        Assert.True(Directory.Exists(lease.Path));
        Assert.StartsWith(Repo.WorktreeRoot + Path.DirectorySeparatorChar, lease.Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(lease.Path.StartsWith(Repo.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(Path.Combine("a1b2c3", "T1"), lease.Path, StringComparison.Ordinal);
        Assert.Equal("T1", lease.Name);
        Assert.Equal("refs/agentstudio/20261015-a1b2c3/T1", lease.RefName);
        Assert.Equal(Repo.BaseCommit, lease.BaseCommit);
        Assert.Equal(TestRun, lease.Run);

        var entry = Assert.Single(ListWorktrees().Skip(1));
        Assert.True(SamePath(lease.Path, entry.Path));
        Assert.True(entry.Detached);
        Assert.True(entry.Locked);
        Assert.Equal(ExpectedLockReason("T1"), entry.LockReason);
        Assert.Equal(Repo.BaseCommit, entry.Head);

        // The hidden ref keeps the objects alive; it is not a branch.
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
        Assert.DoesNotContain("agentstudio", Repo.Run("branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_is_idempotent_and_reuses_the_existing_worktree()
    {
        var manager = NewManager();
        var first = await EnsureAsync(manager, "T1");
        var marker = Path.Combine(first.Path, "marcador.txt");
        File.WriteAllText(marker, "sigue aquí");

        var second = await EnsureAsync(manager, "T1");
        var afterRestart = await EnsureAsync(NewManager(), "T1");

        Assert.Equal(first, second);
        Assert.Equal(first, afterRestart);
        Assert.True(File.Exists(marker), "The worktree must be reused, not recreated.");
        Assert.Single(ListWorktrees().Skip(1));
    }

    [Fact]
    public async Task Ensure_fails_closed_when_the_existing_worktree_was_created_at_another_base()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var next = await CommitInAsync(manager, lease, "nuevo.txt", "x");

        var mismatch = await Assert.ThrowsAsync<WorktreeMismatchException>(() => EnsureAsync(manager, "T1", next));

        Assert.Contains(Repo.BaseCommit, mismatch.Message, StringComparison.Ordinal);
        Assert.Single(ListWorktrees().Skip(1));
        Assert.Equal(next, Repo.Run("-C", lease.Path, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Ensure_fails_closed_when_the_path_holds_a_foreign_directory()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: false, Ct));

        Directory.CreateDirectory(lease.Path);
        var foreign = Path.Combine(lease.Path, "ajeno.txt");
        File.WriteAllText(foreign, "no es un worktree");

        await Assert.ThrowsAsync<WorktreeMismatchException>(() => EnsureAsync(manager, "T1"));

        Assert.True(File.Exists(foreign));
        Assert.Empty(ListWorktrees().Skip(1));
    }

    [Fact]
    public async Task Ensure_fails_closed_when_the_path_is_registered_with_another_lock_reason()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        // The same path asked for by another project: the lock reason differs.
        var other = Request("T1", project: new ProjectContext("otro-proyecto", Repo.Root));

        await Assert.ThrowsAsync<WorktreeMismatchException>(() => manager.EnsureAsync(other, Ct));

        Assert.Equal(ExpectedLockReason("T1"), Assert.Single(ListWorktrees().Skip(1)).LockReason);
        Assert.True(Directory.Exists(lease.Path));
    }

    [Fact]
    public async Task Ensure_recreates_a_registered_worktree_whose_directory_disappeared()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        DeleteDirectory(lease.Path);

        var again = await EnsureAsync(manager, "T1");

        Assert.Equal(lease, again);
        Assert.True(Directory.Exists(again.Path));
        var entry = Assert.Single(ListWorktrees().Skip(1));
        Assert.Equal(ExpectedLockReason("T1"), entry.LockReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("-rf")]
    [InlineData("T 1")]
    [InlineData("_start")]
    [InlineData("_other")]
    [InlineData("T1\n")]
    [InlineData("1T")]
    public async Task Ensure_rejects_names_that_are_not_task_ids_or_system_names(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewManager().EnsureAsync(Request(name), Ct));

        Assert.Empty(ListWorktrees().Skip(1));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("--detach")]
    [InlineData("abc123")]
    public async Task Ensure_requires_a_full_object_id_as_base(string baseCommit)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewManager().EnsureAsync(Request("T1", baseCommit), Ct));

        Assert.Empty(ListWorktrees().Skip(1));
    }

    [Fact]
    public async Task Ensure_fails_for_a_commit_that_does_not_exist()
    {
        var missing = new string('a', 40);

        await Assert.ThrowsAsync<GitCommandException>(() => NewManager().EnsureAsync(Request("T1", missing), Ct));

        Assert.Empty(ListWorktrees().Skip(1));
    }

    [Fact]
    public async Task Ensure_refuses_a_worktree_root_inside_the_repository()
    {
        var inside = new GitWorktreeManager(
            new ProcessRunner(),
            new ExecutableLocator(),
            new GitWorktreeOptions(Path.Combine(Repo.Root, "wt")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => inside.EnsureAsync(Request("T1"), Ct));

        Assert.Empty(ListWorktrees().Skip(1));
        Assert.False(Directory.Exists(Path.Combine(Repo.Root, "wt")));
    }

    [Fact]
    public async Task Ensure_serialises_parallel_requests_for_the_same_repository()
    {
        var manager = NewManager();

        var leases = await Task.WhenAll(
            EnsureAsync(manager, "T1"),
            EnsureAsync(manager, "T2"),
            EnsureAsync(manager, "T3"),
            EnsureAsync(manager, "_integration"));

        Assert.Equal(4, leases.Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(4, ListWorktrees().Skip(1).Count());
        Assert.All(ListWorktrees().Skip(1), e => Assert.True(e.Locked));
    }

    // ---- ListOwned and the lock reason -----------------------------------------------------------------------

    [Fact]
    public async Task The_lock_reason_is_visible_in_git_worktree_list_porcelain_and_read_back_by_ListOwned()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var integration = await EnsureAsync(manager, "_integration");

        // Raw, line based porcelain exactly as a person would read it.
        var porcelain = Repo.Run("worktree", "list", "--porcelain");
        Assert.Contains("locked " + ExpectedLockReason("T1"), porcelain, StringComparison.Ordinal);
        Assert.Contains("locked " + ExpectedLockReason("_integration"), porcelain, StringComparison.Ordinal);

        var owned = await manager.ListOwnedAsync(Project, Ct);

        Assert.Equal(2, owned.Count);
        Assert.Contains(t1, owned);
        Assert.Contains(integration, owned);
        Assert.All(owned, lease => Assert.Equal(Repo.BaseCommit, lease.BaseCommit));
    }

    [Fact]
    public async Task ListOwned_ignores_foreign_worktrees_and_other_projects()
    {
        var manager = NewManager();
        var mine = await EnsureAsync(manager, "T1");
        var otherProject = new ProjectContext("otro-proyecto", Repo.Root);
        await manager.EnsureAsync(Request("T2", project: otherProject), Ct);

        // A worktree made by hand, outside the root and without our lock.
        var manual = Path.Combine(Repo.WorktreeRoot, "..", "manual");
        Repo.Run("worktree", "add", "--detach", manual);

        var owned = await manager.ListOwnedAsync(Project, Ct);

        Assert.Equal(mine, Assert.Single(owned));
        Assert.Equal(3, ListWorktrees().Skip(1).Count());
    }

    [Fact]
    public async Task ListOwned_reports_the_base_each_worktree_was_created_at()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var next = await CommitInAsync(manager, t1, "nuevo.txt", "x");
        var t2 = await EnsureAsync(manager, "T2", next);

        var owned = await manager.ListOwnedAsync(Project, Ct);

        Assert.Equal(Repo.BaseCommit, Assert.Single(owned, l => l.Name == "T1").BaseCommit);
        Assert.Equal(next, Assert.Single(owned, l => l.Name == "T2").BaseCommit);
        Assert.Equal(next, t2.BaseCommit);
    }

    // ---- Checkout --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Checkout_puts_the_worktree_exactly_on_the_commit_and_cleans_it()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        await CommitInAsync(manager, lease, "intento.txt", "primer intento");

        // Leftovers of an interrupted attempt: a modified tracked file, an untracked file and an ignored build output.
        WriteIn(lease, "README.md", "modificado\n");
        WriteIn(lease, "basura/sin-seguimiento.txt", "x");
        WriteIn(lease, "build.log", "ignorado por .gitignore");
        WriteIn(lease, ".agents/skills/s.md", "habilidad aprovisionada");

        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);

        Assert.Equal(Repo.BaseCommit, Repo.Run("-C", lease.Path, "rev-parse", "HEAD").Trim());
        Assert.Equal("# temp\n", File.ReadAllText(Path.Combine(lease.Path, "README.md")));
        Assert.False(File.Exists(Path.Combine(lease.Path, "intento.txt")));
        Assert.False(Directory.Exists(Path.Combine(lease.Path, "basura")));
        Assert.False(File.Exists(Path.Combine(lease.Path, "build.log")));
        Assert.True(File.Exists(Path.Combine(lease.Path, ".agents", "skills", "s.md")), "Provisioned skills must survive a checkout.");
        Assert.Equal(string.Empty, Repo.Run("-C", lease.Path, "status", "--porcelain=v1", "--", ".", ":(exclude).agents"));
    }

    [Fact]
    public async Task Checkout_also_removes_nested_repositories_created_in_the_worktree()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var nested = Path.Combine(lease.Path, "anidado");
        Directory.CreateDirectory(nested);
        TempGitRepo.Git(nested, "init", "-q");
        File.WriteAllText(Path.Combine(nested, "x.txt"), "x");

        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);

        Assert.False(Directory.Exists(nested));
    }

    [Fact]
    public async Task Checkout_works_when_a_file_is_named_like_the_target_commit()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        await CommitInAsync(manager, lease, "intento.txt", "x\n");

        // Without "--" git cannot tell the revision from the path and refuses.
        WriteIn(lease, Repo.BaseCommit, "un fichero con nombre de commit\n");

        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);

        Assert.Equal(Repo.BaseCommit, Repo.Run("-C", lease.Path, "rev-parse", "HEAD").Trim());
        Assert.False(File.Exists(Path.Combine(lease.Path, Repo.BaseCommit)));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("--force")]
    public async Task Checkout_requires_a_full_object_id(string commit)
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CheckoutAsync(lease, commit, Ct));
    }

    // ---- Remove ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_refuses_a_dirty_worktree_unless_the_changes_are_discarded()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "sin-confirmar.txt", "trabajo sin confirmar");

        var refused = await manager.RemoveAsync(lease, discardChanges: false, Ct);

        Assert.Equal(RemovalOutcome.RefusedDirty, refused);
        Assert.True(File.Exists(Path.Combine(lease.Path, "sin-confirmar.txt")));
        Assert.Single(ListWorktrees().Skip(1));

        var removed = await manager.RemoveAsync(lease, discardChanges: true, Ct);

        Assert.Equal(RemovalOutcome.Removed, removed);
        Assert.False(Directory.Exists(lease.Path));
        Assert.Empty(ListWorktrees().Skip(1));

        // Only the worktree goes: the refs stay until a cleanup with approval deletes them.
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task Remove_refuses_modified_tracked_files_too()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, "README.md", "editado\n");

        Assert.Equal(RemovalOutcome.RefusedDirty, await manager.RemoveAsync(lease, discardChanges: false, Ct));
        Assert.True(Directory.Exists(lease.Path));
    }

    [Fact]
    public async Task Remove_deletes_a_clean_worktree_and_the_empty_folders_it_leaves()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        WriteIn(lease, ".agents/skills/s.md", "solo la carpeta de habilidades");

        var removed = await manager.RemoveAsync(lease, discardChanges: false, Ct);
        var again = await manager.RemoveAsync(lease, discardChanges: false, Ct);

        Assert.Equal(RemovalOutcome.Removed, removed);
        Assert.Equal(RemovalOutcome.AlreadyRemoved, again);
        Assert.False(Directory.Exists(lease.Path));
        Assert.Empty(ListWorktrees().Skip(1));

        // Only the empty file git receives as core.hooksPath remains.
        var left = Assert.Single(Directory.EnumerateFileSystemEntries(Repo.WorktreeRoot));
        Assert.Equal(".no-hooks", Path.GetFileName(left));
        Assert.True(File.Exists(left), $"'{left}' is not a file.");
        Assert.Equal(0, new FileInfo(left).Length);
    }

    [Fact]
    public async Task Remove_keeps_other_worktrees_of_the_same_run()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var t2 = await EnsureAsync(manager, "T2");

        await manager.RemoveAsync(t1, discardChanges: false, Ct);

        Assert.True(Directory.Exists(t2.Path));
        Assert.Equal(t2, Assert.Single(await manager.ListOwnedAsync(Project, Ct)));
    }
}
