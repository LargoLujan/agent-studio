using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// An agent can write anything inside its worktree, the <c>.git</c> file included, and with a shell it can also plant
/// links and refs. None of that may turn an Agent Studio command into a change to the user's repository.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeTamperingTests : GitManagerTestBase
{
    // ---- the .git file -------------------------------------------------------------------------------------

    [Fact]
    public async Task CommitAll_ignores_a_gitfile_redirected_to_the_user_repository()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        // The user has work in progress: a staged file, a modified tracked file and an untracked file.
        Repo.Write("staged.txt", "preparado por el usuario\n");
        Repo.Run("add", "staged.txt");
        Repo.Write("README.md", "# temp\nedición del usuario\n");
        Repo.Write("user.txt", "sin seguimiento\n");
        var before = Repo.Snapshot();

        // The agent points its worktree at the user's repository and leaves its work behind.
        RedirectGitFile(lease, Path.Combine(Repo.Root, ".git"));
        WriteIn(lease, "nuevo.txt", "trabajo del agente\n");

        var result = await manager.CommitAllAsync(lease, "Mensaje del sistema", Ct);

        Assert.Equal(before, Repo.Snapshot());
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", "refs/heads/main").Trim());

        // The commit went where it belongs: the worktree's own HEAD and its hidden ref.
        Assert.True(result.Committed);
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", result.Commit + "^").Trim());
        Assert.Equal("nuevo.txt", Repo.Run("diff-tree", "--no-commit-id", "--name-only", "-r", result.Commit).Trim());
        Assert.Equal(result.Commit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task Checkout_ignores_a_gitfile_redirected_to_the_user_repository()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var admin = AdminDirectoryOf(lease);
        await CommitInAsync(manager, lease, "intento.txt", "primer intento\n");

        // The user keeps working: a new commit on main and uncommitted edits.
        Repo.Write("user.txt", "trabajo del usuario U1\n");
        Repo.Run("add", "user.txt");
        Repo.Run("commit", "-q", "-m", "trabajo del usuario U1");
        var userCommit = Repo.Head;
        Repo.Write("README.md", "# temp\nedición sin confirmar\n");
        Repo.Write("notas.txt", "sin seguimiento\n");
        var before = Repo.Snapshot();

        RedirectGitFile(lease, Path.Combine(Repo.Root, ".git"));

        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);

        Assert.Equal(before, Repo.Snapshot());
        Assert.Equal(userCommit, Repo.Run("rev-parse", "refs/heads/main").Trim());
        Assert.Equal("# temp\nedición sin confirmar\n", File.ReadAllText(Path.Combine(Repo.Root, "README.md")));

        // The worktree itself is back on the base commit.
        Assert.Equal(Repo.BaseCommit, WorktreeHead(admin));
        Assert.False(File.Exists(Path.Combine(lease.Path, "intento.txt")));
    }

    [Fact]
    public async Task Remove_works_with_a_tampered_gitfile_and_leaves_the_user_repository_alone()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        Repo.Write("notas.txt", "sin seguimiento\n");
        var before = Repo.Snapshot();
        RedirectGitFile(lease, Path.Combine(Repo.Root, ".git"));

        var outcome = await manager.RemoveAsync(lease, discardChanges: true, Ct);

        Assert.Equal(RemovalOutcome.Removed, outcome);
        Assert.False(Directory.Exists(lease.Path));
        Assert.Empty(ListWorktrees().Skip(1));
        Assert.Equal(before, Repo.Snapshot());
    }

    // ---- links inside the worktree -------------------------------------------------------------------------

    [Fact]
    public async Task Remove_never_follows_a_junction_inside_the_worktree()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var victim = Path.GetFullPath(Path.Combine(Repo.WorktreeRoot, "..", "victima"));
        Directory.CreateDirectory(Path.Combine(victim, "sub"));
        File.WriteAllText(Path.Combine(victim, "importante.txt"), "no borrar");
        File.WriteAllText(Path.Combine(victim, "sub", "profundo.txt"), "no borrar");
        CreateJunction(Path.Combine(lease.Path, "enlace"), victim);

        var outcome = await manager.RemoveAsync(lease, discardChanges: true, Ct);

        Assert.True(File.Exists(Path.Combine(victim, "importante.txt")), "Removing a worktree deleted files through a junction.");
        Assert.True(File.Exists(Path.Combine(victim, "sub", "profundo.txt")));
        Assert.Equal(RemovalOutcome.Removed, outcome);
        Assert.False(Directory.Exists(lease.Path));
        Assert.Empty(ListWorktrees().Skip(1));
    }

    [Fact]
    public async Task Checkout_never_writes_or_deletes_through_a_junction_inside_the_worktree()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        // The user has uncommitted work under src/.
        Repo.Write("src/Calc/Calc.cs", "// edición del usuario sin confirmar\n");
        Repo.Write("src/Calc/Nuevo.cs", "// fichero nuevo del usuario\n");
        var before = Repo.Snapshot();

        // The agent replaces the tracked folder src/ with a junction to the user's src/.
        DeleteDirectory(Path.Combine(lease.Path, "src"));
        CreateJunction(Path.Combine(lease.Path, "src"), Path.Combine(Repo.Root, "src"));

        await manager.CheckoutAsync(lease, Repo.BaseCommit, Ct);

        Assert.Equal("// edición del usuario sin confirmar\n", File.ReadAllText(Path.Combine(Repo.Root, "src", "Calc", "Calc.cs")));
        Assert.True(File.Exists(Path.Combine(Repo.Root, "src", "Calc", "Nuevo.cs")), "Checkout deleted a user file through a junction.");
        Assert.Equal(before, Repo.Snapshot());

        // The worktree has a real src/ folder again, exactly as in the base commit.
        Assert.False(new DirectoryInfo(Path.Combine(lease.Path, "src")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(
            "public static class Calc { public static int Add(int a, int b) => a + b; }\n",
            File.ReadAllText(Path.Combine(lease.Path, "src", "Calc", "Calc.cs")));
        Assert.Equal(string.Empty, Repo.Run("-C", lease.Path, "status", "--porcelain=v1", "--", ".", ":(exclude).agents"));
    }

    // ---- refs ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CommitAll_never_moves_a_user_branch_through_a_symbolic_task_ref()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        // The agent turns the hidden ref of its task into a symbolic ref to the user's branch.
        Repo.Run("symbolic-ref", lease.RefName, "refs/heads/main");
        WriteIn(lease, "nuevo.txt", "x\n");

        var result = await manager.CommitAllAsync(lease, "mensaje", Ct);

        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", "refs/heads/main").Trim());
        Assert.Equal(result.Commit, Repo.Run("rev-parse", lease.RefName).Trim());
        Assert.NotEqual(0, TempGitRepo.TryGit(Repo.Root, "symbolic-ref", "--quiet", lease.RefName).ExitCode);
    }

    [Fact]
    public async Task MergeInto_never_moves_a_user_branch_through_a_symbolic_target_ref()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");
        var branch = "refs/heads/agentstudio/run-" + TestRun.Value;
        Repo.Run("symbolic-ref", branch, "refs/heads/main");

        var merge = await manager.MergeIntoAsync(Repo.Root, branch, Repo.BaseCommit, c1, "Integra T1", Ct);

        Assert.True(merge.Merged);
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", "refs/heads/main").Trim());
        Assert.Equal(merge.ResultCommit, Repo.Run("rev-parse", branch).Trim());
    }

    // ---- P-01: a symbolic HEAD in the administrative folder ----------------------------------------------------
    // Agent Studio creates its worktrees detached (--detach). Measured with git 2.45.2: with `ref: refs/heads/feature`
    // written in a worktree's administrative HEAD, or with HEAD a symbolic link whose target reads refs/heads/feature,
    // the worktree's `commit` and `reset --hard` moved that branch of the user. So HEAD must be detached at a commit when a
    // worktree is reused, and again right before every commit or reset in it.

    [Theory]
    [InlineData("commit")]
    [InlineData("reset")]
    [InlineData("reuse")]
    public async Task A_symbolic_HEAD_planted_in_an_owned_worktree_is_refused_and_the_users_branch_never_moves(string operation)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var lease = await EnsureAsync(manager, "T1");
        var work = await CommitInAsync(manager, lease, "previo.txt", "previo\n");
        var mainBefore = Repo.Run("rev-parse", "refs/heads/main").Trim();
        WriteIn(lease, "nuevo.txt", "x\n");

        // Planted while the app was closed: the worktree's HEAD names the user's branch.
        File.WriteAllText(Path.Combine(lease.AdminDirectory, "HEAD"), "ref: refs/heads/main\n");
        var start = recorder.Specs.Count;

        var error = await Record.ExceptionAsync(() => operation switch
        {
            "commit" => manager.CommitAllAsync(lease, "T1: cambios", Ct),
            "reset" => manager.CheckoutAsync(lease, work, Ct),
            _ => EnsureAsync(manager, "T1"),
        });

        Assert.Equal(mainBefore, Repo.Run("rev-parse", "refs/heads/main").Trim());
        var refusal = Assert.IsType<WorktreeOwnershipException>(error);
        Assert.Contains("not detached", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(recorder.Specs.Skip(start), s => RecordingProcessRunner.SubCommand(s) is "add" or "commit" or "reset");
    }

    /// <summary>
    /// A HEAD that is a symbolic link reading <c>refs/heads/main</c> is a symbolic ref to git (measured with git 2.45.2: it
    /// moved the branch). It is refused as a link inside the administrative folder (X-02) before HEAD is even read. Needs
    /// the privilege to create a symbolic link (Developer Mode), so it is skipped without it.
    /// </summary>
    [Fact]
    public async Task A_HEAD_turned_into_a_symbolic_link_to_a_branch_is_refused_and_the_branch_never_moves()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var mainBefore = Repo.Run("rev-parse", "refs/heads/main").Trim();
        WriteIn(lease, "nuevo.txt", "x\n");
        var head = Path.Combine(lease.AdminDirectory, "HEAD");
        var detached = File.ReadAllText(head);
        File.Delete(head);
        try
        {
            File.CreateSymbolicLink(head, "refs/heads/main");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.WriteAllText(head, detached);
            Assert.Skip("Creating a symbolic link needs Developer Mode or elevation on this machine.");
        }

        try
        {
            var error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: cambios", Ct));

            Assert.Equal(mainBefore, Repo.Run("rev-parse", "refs/heads/main").Trim());
            Assert.IsType<WorktreeOwnershipException>(error);
        }
        finally
        {
            // The link itself, never what it names.
            File.Delete(head);
        }
    }

    // ---- nested repositories and submodules ----------------------------------------------------------------

    [Fact]
    public async Task CommitAll_refuses_an_embedded_repository_the_agent_planted_and_runs_no_child_git()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");

        // The agent plants a nested git repository with a hostile fsmonitor (an absolute sentinel script) in its worktree.
        var sub = Path.Combine(lease.Path, "sub");
        var sentinel = Path.Combine(Repo.WorktreeRoot, "child-git-ran.log");
        var fsmonitor = Path.Combine(Repo.WorktreeRoot, "fsmon.sh");
        File.WriteAllText(fsmonitor, "#!/bin/sh\necho ran >> \"" + sentinel.Replace('\\', '/') + "\"\nexit 0\n", new System.Text.UTF8Encoding(false));
        TempGitRepo.Git(lease.Path, "init", "-q", "-b", "main", "sub");
        TempGitRepo.Git(sub, "config", "user.name", "a");
        TempGitRepo.Git(sub, "config", "user.email", "a@l");
        File.WriteAllText(Path.Combine(sub, "f.txt"), "del agente\n");
        TempGitRepo.Git(sub, "-c", "core.fsmonitor=false", "add", "f.txt");
        TempGitRepo.Git(sub, "-c", "core.fsmonitor=false", "commit", "-q", "-m", "subbase");
        TempGitRepo.Git(sub, "config", "core.fsmonitor", fsmonitor.Replace('\\', '/'));
        if (File.Exists(sentinel))
        {
            File.Delete(sentinel);
        }

        var error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "mensaje", Ct));

        Assert.IsType<EmbeddedRepositoryException>(error);
        Assert.False(File.Exists(sentinel), "a child git ran inside the planted repository.");
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task The_commands_that_could_spawn_a_child_git_in_a_submodule_disable_the_recursion()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");
        await manager.ChangedPathsAsync(Repo.Root, Repo.BaseCommit, c1, Ct);
        await manager.CheckoutAsync(t1, Repo.BaseCommit, Ct);

        foreach (var spec in recorder.Specs)
        {
            switch (RecordingProcessRunner.SubCommand(spec))
            {
                case "status":
                case "diff":
                    Assert.Contains("--ignore-submodules=all", spec.Arguments);
                    break;
                case "reset":
                case "checkout":
                    Assert.Contains("--no-recurse-submodules", spec.Arguments);
                    break;
                default:
                    break;
            }
        }

        // `worktree add` cannot take --no-recurse-submodules, so it is disabled by config.
        var worktreeAdd = Assert.Single(recorder.Specs, s => RecordingProcessRunner.SubCommand(s) == "worktree" && s.Arguments.Contains("add"));
        Assert.Contains("submodule.recurse=false", worktreeAdd.Arguments);
    }

    // ---- hooks ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hooks_from_a_relative_hooks_path_inside_the_worktree_never_run()
    {
        // A husky-style setup: the hooks are versioned under .husky/ and core.hooksPath is relative, so for a command
        // run in a worktree git would take them from the worktree, which the agent controls.
        Repo.Write(".husky/reference-transaction", HookScripts.Failing("reference-transaction"));
        Repo.Write(".husky/prepare-commit-msg", HookScripts.Failing("prepare-commit-msg"));
        foreach (var hook in new[] { "post-checkout", "post-commit", "post-index-change" })
        {
            Repo.Write(".husky/" + hook, HookScripts.Logging(hook));
        }

        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "husky");
        var head = Repo.Head;
        Repo.Run("config", "core.hooksPath", ".husky");

        // Control: the hooks are live for a plain git command.
        Assert.NotEqual(0, TempGitRepo.TryGit(Repo.Root, "update-ref", "refs/agentstudio/control", head).ExitCode);

        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, "nuevo.txt", "x\n");
        var result = await manager.CommitAllAsync(lease, "mensaje", Ct);

        Assert.False(File.Exists(Path.Combine(lease.Path, HookScripts.LogName)), "A hook from the worktree ran.");
        Assert.DoesNotContain(HookScripts.LogName, Repo.Run("ls-tree", "-r", "--name-only", result.Commit), StringComparison.Ordinal);

        await manager.CheckoutAsync(lease, head, Ct);
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(lease, discardChanges: false, Ct));
    }

    [Fact]
    public async Task A_hook_planted_at_the_hooks_path_stops_every_command()
    {
        // The hooks path is an empty file, so nothing can be put below it: an agent with a shell replaces it by a folder
        // with a hook.
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        var hooksPath = Path.Combine(Repo.WorktreeRoot, ".no-hooks");
        if (Directory.Exists(hooksPath))
        {
            Directory.Delete(hooksPath, recursive: true);
        }

        File.Delete(hooksPath);
        var planted = Path.Combine(hooksPath, "post-commit");
        Directory.CreateDirectory(hooksPath);
        File.WriteAllText(planted, HookScripts.Logging("post-commit"));
        WriteIn(lease, "nuevo.txt", "x\n");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.CommitAllAsync(lease, "mensaje", Ct));

        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", lease.RefName).Trim());
        Assert.False(File.Exists(Path.Combine(Repo.WorktreeRoot, HookScripts.LogName)), "The planted hook ran.");
    }
}

/// <summary>
/// Shell hooks for the tests. They log next to their own folder (<c>$(dirname "$0")/..</c>), so no path with spaces or
/// accents has to be written into the script.
/// </summary>
internal static class HookScripts
{
    public const string LogName = "hooks-ran.log";

    public static string Logging(string name) =>
        "#!/bin/sh\necho " + name + " >> \"$(dirname \"$0\")/../" + LogName + "\"\ncat >/dev/null 2>&1 || true\nexit 0\n";

    public static string Failing(string name) =>
        "#!/bin/sh\necho " + name + " >> \"$(dirname \"$0\")/../" + LogName + "\"\ncat >/dev/null 2>&1 || true\nexit 1\n";
}
