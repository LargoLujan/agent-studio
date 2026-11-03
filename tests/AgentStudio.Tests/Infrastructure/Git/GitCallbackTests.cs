using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// git runs programs that its configuration names (filters, merge drivers, textconv...). When such a program is a file of
/// the repository or of a worktree, an agent can rewrite it in its worktree, and the next internal git command of Agent
/// Studio would run it with the user's full permissions, before any isolated verification (N-01). Every internal command
/// that can run one inspects the effective configuration first and refuses; a program outside the repository and the
/// worktrees, as git-lfs is, keeps working.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitCallbackTests : GitManagerTestBase
{
    /// <summary>The user's repository with a filtered file, its versioned filter script (which changes nothing) and the given attributes.</summary>
    private string CommitFilteredRepository(string attributes = "*.fmt filter=format\n")
    {
        Repo.Write(".gitattributes", attributes);
        Repo.Write("tools/filter.sh", "#!/bin/sh\ncat\n");
        Repo.Write("doc.fmt", "contenido del usuario\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "filtro del repositorio");
        return Repo.Head;
    }

    private void ConfigureRepositoryFilter()
    {
        Repo.Run("config", "filter.format.clean", "sh tools/filter.sh");
        Repo.Run("config", "filter.format.smudge", "sh tools/filter.sh");
    }

    // ---- what an agent can plant -------------------------------------------------------------------------------

    [Fact]
    public async Task A_filter_that_runs_a_script_of_the_repository_is_refused_before_the_agents_version_runs()
    {
        using var sentinel = new Sentinel();
        var manager = NewManager();
        var head = CommitFilteredRepository();
        var lease = await EnsureAsync(manager, "T1", head);
        ConfigureRepositoryFilter();

        // The agent rewrites the versioned script in its worktree and touches a filtered file.
        WriteIn(lease, "tools/filter.sh", sentinel.Script("clean"));
        WriteIn(lease, "doc.fmt", "contenido del agente\n");

        var error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: cambios", Ct));

        Assert.False(sentinel.Ran, "git ran the agent's filter script with the user's permissions.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.format.clean", refusal.Message, StringComparison.Ordinal);
        // Only the git-lfs filters run on the content, so `sh tools/filter.sh` is refused before the script path matters.
        Assert.Contains("git-lfs", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(head, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task A_smudge_filter_of_the_repository_never_runs_on_a_checkout_of_the_agents_commit()
    {
        using var sentinel = new Sentinel();
        var manager = NewManager();
        var head = CommitFilteredRepository();
        var t1 = await EnsureAsync(manager, "T1", head);
        var integration = await EnsureAsync(manager, "_integration", head);

        // The agent's commit carries its own version of the script and a filtered file that git writes after it (in
        // path order), so a checkout would smudge that file with the agent's script.
        WriteIn(t1, "tools/filter.sh", sentinel.Script("smudge"));
        WriteIn(t1, "zona/nuevo.fmt", "contenido del agente\n");
        var candidate = (await manager.CommitAllAsync(t1, "T1: cambios", Ct)).Commit;
        ConfigureRepositoryFilter();

        var error = await Record.ExceptionAsync(() => manager.CheckoutAsync(integration, candidate, Ct));

        Assert.False(sentinel.Ran, "git ran the agent's smudge script with the user's permissions.");
        Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Equal(head, WorktreeHead(integration.AdminDirectory));
    }

    [Theory]
    [InlineData("exec ./tools/filter.sh")] // `exec` was a bare first token the denylist took for a safe PATH name.
    [InlineData("! ./tools/filter.sh")] // `!` was not among the refused shell metacharacters.
    public async Task A_filter_that_uses_shell_syntax_to_run_a_repository_script_is_refused_before_it_runs(string clean)
    {
        using var sentinel = new Sentinel();
        var manager = NewManager();
        var head = CommitFilteredRepository();
        var lease = await EnsureAsync(manager, "T1", head);
        Repo.Run("config", "filter.format.clean", clean);

        // The agent rewrites the versioned script in its worktree and touches a filtered file.
        WriteIn(lease, "tools/filter.sh", sentinel.Script("clean"));
        WriteIn(lease, "doc.fmt", "contenido del agente\n");

        var error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: cambios", Ct));

        Assert.False(sentinel.Ran, "git ran the agent's filter script through shell syntax.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.format.clean", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(head, Repo.Run("rev-parse", lease.RefName).Trim());
    }

    [Fact]
    public async Task A_merge_driver_that_runs_a_script_of_the_repository_is_refused_before_it_runs()
    {
        using var sentinel = new Sentinel();
        var manager = NewManager();
        Repo.Write(".gitattributes", "*.fmt merge=custom\n");
        Repo.Write("tools/merge.sh", sentinel.Script("merge"));
        Repo.Write("doc.fmt", "linea 1\nlinea 2\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "driver de fusión");
        var head = Repo.Head;
        var t1 = await EnsureAsync(manager, "T1", head);
        var t2 = await EnsureAsync(manager, "T2", head);
        var c1 = await CommitInAsync(manager, t1, "doc.fmt", "linea 1 (T1)\nlinea 2\n");
        var c2 = await CommitInAsync(manager, t2, "doc.fmt", "linea 1\nlinea 2 (T2)\n");
        var task = new TaskId("I1");
        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, task, head, Ct);
        var first = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, task, candidate, c1, "Integra T1", Ct);
        Repo.Run("config", "merge.custom.driver", "sh tools/merge.sh %O %A %B");

        var error = await Record.ExceptionAsync(() => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, task, first.ResultCommit!, c2, "Integra T2", Ct));

        Assert.False(sentinel.Ran, "git ran the repository's merge driver.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("merge.custom.driver", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(first.ResultCommit, Repo.Run("rev-parse", $"refs/agentstudio/{TestRun.Value}/_candidate/I1").Trim());
    }

    [Fact]
    public async Task Creating_a_worktree_is_refused_when_the_base_commit_runs_a_repository_smudge_filter()
    {
        using var sentinel = new Sentinel();
        var manager = NewManager();

        // The base commit carries the agent's version of the filter script and a filtered file that git checks out after
        // it (in path order), so `worktree add` would smudge the file with that script, before any checked command runs.
        Repo.Write(".gitattributes", "*.fmt filter=format\n");
        Repo.Write("tools/filter.sh", sentinel.Script("smudge"));
        Repo.Write("zz.fmt", "contenido\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "filtro versionado por el agente");
        var head = Repo.Head;
        Repo.Run("config", "filter.format.smudge", "sh tools/filter.sh");

        var error = await Record.ExceptionAsync(() => EnsureAsync(manager, "T1", head));

        Assert.False(sentinel.Ran, "git ran the repository's smudge filter while adding the worktree.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.format.smudge", refusal.Message, StringComparison.Ordinal);
    }

    // ---- A-02: the new worktree's own config context, not only the parent's ------------------------------------

    /// <summary>
    /// A filter that only a worktree's own configuration context sees — pulled in by an
    /// <c>includeIf "gitdir:**/worktrees/**"</c> planted in the shared <c>.git/config</c>, invisible to the parent —
    /// must be caught before the base commit is checked out with the agent's script. Measured with git 2.45.2: such an
    /// <c>includeIf</c> activates under the new worktree's explicit <c>--git-dir</c>/<c>--work-tree</c> (and under
    /// natural resolution) but not the parent's, so inspecting only the parent (the old behaviour) let the checkout of
    /// <c>worktree add</c> run the smudge (A-02). The worktree is created with <c>--no-checkout</c> and the checkout is
    /// a <c>reset --hard</c> inspected in that same context.
    /// </summary>
    [Fact]
    public async Task Creating_a_worktree_inspects_the_new_worktrees_own_config_so_an_includeif_only_it_sees_is_refused()
    {
        using var sentinel = new Sentinel();
        var manager = NewManager();

        // The base commit carries a filtered file and the agent's script, so a checkout would smudge the file.
        Repo.Write(".gitattributes", "*.fmt filter=format\n");
        Repo.Write("tools/filter.sh", sentinel.Script("smudge"));
        Repo.Write("zz.fmt", "contenido\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "base con filtro versionado");
        var head = Repo.Head;

        // The smudge filter is defined in an include file pulled in ONLY for gitdirs under worktrees/: the parent's
        // gitdir (.git) never matches, every worktree's admin gitdir (.git/worktrees/<id>) does.
        var includeFile = Path.Combine(Repo.Root, ".git", "agent-include.config");
        File.WriteAllText(includeFile, "[filter \"format\"]\n\tsmudge = sh tools/filter.sh\n\tclean = sh tools/filter.sh\n");
        File.AppendAllText(
            Path.Combine(Repo.Root, ".git", "config"),
            "[includeIf \"gitdir:**/worktrees/**\"]\n\tpath = " + includeFile.Replace('\\', '/') + "\n");

        var error = await Record.ExceptionAsync(() => EnsureAsync(manager, "T1", head));

        Assert.False(sentinel.Ran, "git ran the agent's smudge script while creating the worktree.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.format.smudge", refusal.Message, StringComparison.Ordinal);

        // The half-created worktree is dropped: no linked worktree is registered and no Agent Studio ref was written.
        Assert.Empty(ListWorktrees().Skip(1));
        Assert.Equal(string.Empty, Repo.Run("for-each-ref", "--format=%(refname)", "refs/agentstudio/").Trim());
    }

    // ---- X-01: a program of the user's own repository, named from a worktree's config context ------------------

    /// <summary>
    /// X-01: a filter that only a worktree's configuration context sees (an <c>includeIf "gitdir:**/worktrees/**"</c> in
    /// the shared configuration) names, in the exact form git-lfs installs, a program of the user's own repository by its
    /// absolute path. The inspection of the new worktree only took the worktree root and the worktree as roots, so that
    /// program looked outside them, and the checkout's <c>reset --hard</c> ran it (measured with git 2.45.2: git runs a
    /// shell script named <c>git-lfs.exe</c> through its sh). The second case keeps the program in a common directory
    /// outside the working tree (<c>git init --separate-git-dir</c>), so only the common directory as a root can refuse it.
    /// </summary>
    [Theory]
    [InlineData("working-tree")]
    [InlineData("separate-git-dir")]
    public async Task A_worktree_only_filter_running_a_program_of_the_users_repository_is_refused_before_the_checkout_runs_it(string where)
    {
        using var sentinel = new Sentinel();
        using var project = AsciiRepository.Create();
        if (where == "separate-git-dir")
        {
            project.SeparateGitDirectory();
        }

        var program = where == "working-tree"
            ? Path.Combine(project.Root, "tools", "git-lfs.exe")
            : Path.Combine(project.CommonDirectory, "lfs-tools", "git-lfs.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, sentinel.Script("filter-process"));
        project.Write(".gitattributes", "*.dat filter=x\n");
        project.Write("doc.dat", "contenido\n");
        var head = project.Commit("base con un programa llamado git-lfs");

        // The include only matches the git directory of a worktree (<common>/worktrees/<id>), never the parent's.
        var include = Path.Combine(project.CommonDirectory, "worktree-only.config");
        File.WriteAllText(include, "[filter \"x\"]\n\tprocess = '" + program.Replace('\\', '/') + "' filter-process\n");
        File.AppendAllText(
            Path.Combine(project.CommonDirectory, "config"),
            "[includeIf \"gitdir:**/worktrees/**\"]\n\tpath = " + include.Replace('\\', '/') + "\n");
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(project.WorktreeRoot));

        var error = await Record.ExceptionAsync(() => manager.EnsureAsync(new WorktreeRequest(new ProjectContext(ProjectId, project.Root), TestRun, "T1", head), Ct));

        Assert.False(sentinel.Ran, "the checkout of the new worktree ran a program of the user's repository with the user's permissions.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.x.process", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(GitPorcelain.ParseWorktreeList(TempGitRepo.Git(project.Root, "worktree", "list", "--porcelain", "-z")).Skip(1));
        Assert.Equal(string.Empty, TempGitRepo.Git(project.Root, "for-each-ref", "--format=%(refname)", "refs/agentstudio/").Trim());
    }

    /// <summary>
    /// The normal path still works: with no hostile configuration, creating a worktree checks out the base commit (the
    /// checkout is now a <c>reset --hard</c> after <c>worktree add --no-checkout</c>), so the working tree is populated.
    /// </summary>
    [Fact]
    public async Task Creating_a_worktree_still_checks_out_the_base_commit()
    {
        var manager = NewManager();

        var lease = await EnsureAsync(manager, "T1");

        var readme = Path.Combine(lease.Path, "README.md");
        Assert.True(File.Exists(readme), "the base commit was not checked out into the worktree.");
        Assert.Equal("# temp\n", File.ReadAllText(readme).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(Repo.BaseCommit, WorktreeHead(AdminDirectoryOf(lease)));
    }

    // ---- G-03: a program named git-lfs is accepted only when it is the installed git-lfs --------------------------

    /// <summary>
    /// A filter of the exact form git-lfs installs, whose program is a file named git-lfs outside the repository and the
    /// worktrees but not the installed git-lfs, is refused before it runs (G-03). Until the seventh round it was accepted
    /// (R4-01 only looked at the name and at where the file is), and every internal command ran it.
    /// </summary>
    [Fact]
    public async Task A_program_named_git_lfs_outside_the_repository_that_is_not_the_installed_one_is_refused_before_it_runs()
    {
        using var outside = new Sentinel();
        var tool = Path.Combine(outside.Folder, "git-lfs");
        File.WriteAllText(
            tool,
            "#!/bin/sh\necho \"$1\" >> '" + outside.Mark.Replace('\\', '/') + "'\nif [ \"$1\" = clean ]; then tr a-z A-Z; else tr A-Z a-z; fi\n");
        var program = "'" + tool.Replace('\\', '/') + "'";
        Repo.Run("config", "filter.fakelfs.clean", program + " clean -- %f");
        Repo.Run("config", "filter.fakelfs.smudge", program + " smudge -- %f");
        Repo.Run("config", "filter.fakelfs.required", "true");
        Repo.Write(".gitattributes", "*.bin filter=fakelfs\n");
        Repo.Write("datos.bin", "base\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "lfs");
        File.Delete(outside.Mark);
        var head = Repo.Head;
        var manager = NewManager();

        var error = await Record.ExceptionAsync(() => EnsureAsync(manager, "T1", head));

        Assert.False(outside.Ran, "an internal command ran a program named git-lfs that is not the installed git-lfs.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.fakelfs", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("installed", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(ListWorktrees().Skip(1));
    }

    /// <summary>
    /// G-03, the case the roots missed: another linked worktree of the user. It is not the project root, the common
    /// directory, the main working tree, the worktree root or the working directory, so a fake <c>git-lfs.exe</c> there,
    /// named by a filter only a worktree's context sees (an <c>includeIf "gitdir:**/worktrees/**"</c> in the shared
    /// configuration), looked outside the repository and the checkout of the new worktree ran it.
    /// </summary>
    [Fact]
    public async Task A_fake_git_lfs_in_another_linked_worktree_of_the_user_is_refused_before_the_checkout_runs_it()
    {
        using var sentinel = new Sentinel();
        using var project = AsciiRepository.Create();
        project.Write(".gitattributes", "*.dat filter=x\n");
        project.Write("doc.dat", "contenido\n");
        var head = project.Commit("base con un fichero filtrado");

        // The user's own worktree, outside every root the classifier knew, holds a program named git-lfs.exe.
        var userWorktree = Path.Combine(project.Container, "otro-worktree");
        TempGitRepo.Git(project.Root, "worktree", "add", "-q", "--detach", userWorktree, head);
        var program = Path.Combine(userWorktree, "tools", "git-lfs.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, sentinel.Script("filter-process"));
        project.EnableForWorktreesOnly("[filter \"x\"]\n\tprocess = '" + program.Replace('\\', '/') + "' filter-process\n");
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(project.WorktreeRoot));

        var error = await Record.ExceptionAsync(() => manager.EnsureAsync(new WorktreeRequest(new ProjectContext(ProjectId, project.Root), TestRun, "T1", head), Ct));

        Assert.False(sentinel.Ran, "the checkout of the new worktree ran a program named git-lfs.exe from another worktree of the user.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.x.process", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("installed", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(project.Worktrees(), path => path.StartsWith(project.WorktreeRoot, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// G-03, the other case the roots missed: the project is a linked worktree of a repository whose main working tree has a
    /// separate git directory. The main working tree was derived only from a common directory named <c>.git</c>, so a fake
    /// <c>git-lfs.exe</c> in it looked outside the repository.
    /// </summary>
    [Fact]
    public async Task A_fake_git_lfs_in_a_main_working_tree_with_a_separate_git_directory_is_refused_before_it_runs()
    {
        using var sentinel = new Sentinel();
        using var main = AsciiRepository.Create();
        main.SeparateGitDirectory();
        main.Write(".gitattributes", "*.dat filter=x\n");
        main.Write("doc.dat", "contenido\n");
        var head = main.Commit("base con un fichero filtrado");
        var program = Path.Combine(main.Root, "tools", "git-lfs.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, sentinel.Script("filter-process"));

        // The project Agent Studio works on is a linked worktree of that repository.
        var projectRoot = Path.Combine(main.Container, "proyecto");
        TempGitRepo.Git(main.Root, "worktree", "add", "-q", "--detach", projectRoot, head);
        main.EnableForWorktreesOnly("[filter \"x\"]\n\tprocess = '" + program.Replace('\\', '/') + "' filter-process\n");
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(main.WorktreeRoot));

        var error = await Record.ExceptionAsync(() => manager.EnsureAsync(new WorktreeRequest(new ProjectContext(ProjectId, projectRoot), TestRun, "T1", head), Ct));

        Assert.False(sentinel.Ran, "an internal command ran a program named git-lfs.exe from the main working tree.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.x.process", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("installed", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(main.Worktrees(), path => path.StartsWith(main.WorktreeRoot, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// G-03, what keeps working: the installed git-lfs named by its absolute path (the file the bare name resolves to) passes
    /// the inspection of every internal command, and its clean runs on the agent's lfs file. The hooks it would install go
    /// nowhere: the hooks path is an empty file (GitLfsHooksTests).
    /// </summary>
    [Fact]
    public async Task The_installed_git_lfs_by_its_absolute_path_is_still_allowed()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var locator = new ExecutableLocator();
        var installed = GitCallbacks.ResolveProgramOnPath(locator, locator.Find("git"), "git-lfs")!;
        var program = "'" + installed.Replace('\\', '/') + "'";
        Repo.Run("config", "filter.lfs.process", program + " filter-process");
        Repo.Run("config", "filter.lfs.clean", program + " clean -- %f");
        Repo.Run("config", "filter.lfs.smudge", program + " smudge -- %f");
        Repo.Run("config", "filter.lfs.required", "true");
        Repo.Write(".gitattributes", "*.dat filter=lfs diff=lfs merge=lfs -text\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "lfs por ruta absoluta");
        var head = Repo.Head;
        var manager = NewManager();

        await manager.InspectAsync(Repo.Root, Ct);
        var lease = await EnsureAsync(manager, "T1", head);
        var commit = await CommitInAsync(manager, lease, "nuevo.dat", "x\n");

        Assert.StartsWith("version https://git-lfs.github.com/spec/v1", Repo.Run("cat-file", "-p", commit + ":nuevo.dat"), StringComparison.Ordinal);
        Assert.Empty(await manager.FindDeliveryRefusalsAsync(Repo.Root, head, commit, Ct));
    }

    /// <summary>
    /// The same program outside the repository, under any name but git-lfs, is refused before it runs (R4-01): a program
    /// outside the repository can still run code from a file an agent wrote (MSBuild with a project, a tool with a plugin),
    /// and the allowlist cannot tell which ones do.
    /// </summary>
    [Fact]
    public async Task Any_other_filter_program_outside_the_repository_is_refused_before_it_runs()
    {
        using var outside = new Sentinel();
        var tool = Path.Combine(outside.Folder, "format-tool");
        File.WriteAllText(tool, "#!/bin/sh\necho ran >> '" + outside.Mark.Replace('\\', '/') + "'\ncat\n");
        Repo.Run("config", "filter.format.clean", "'" + tool.Replace('\\', '/') + "' clean -- %f");
        Repo.Write(".gitattributes", "*.fmt filter=format\n");
        Repo.Write("doc.fmt", "base\n");
        var head = Repo.Head;
        var manager = NewManager();

        var error = await Record.ExceptionAsync(() => EnsureAsync(manager, "T1", head));

        Assert.False(File.Exists(outside.Mark), "git ran a filter program that is not git-lfs.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.format.clean", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git-lfs", refusal.Message, StringComparison.Ordinal);
    }

    // ---- A-03: git-lfs transitive callbacks (extensions, custom transfers) ------------------------------------

    private static bool GitLfsAvailable => new ExecutableLocator().Find("git-lfs") is not null;

    /// <summary>The user's repository with git-lfs installed locally and <c>*.dat</c> tracked by lfs.</summary>
    private string CommitLfsRepository()
    {
        Repo.Run("lfs", "install", "--local");
        Repo.Write(".gitattributes", "*.dat filter=lfs diff=lfs merge=lfs -text\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "repositorio git-lfs");
        return Repo.Head;
    }

    /// <summary>
    /// git-lfs runs an extension's clean program on every <c>git add</c> of a tracked file (measured with git-lfs 3.5.1:
    /// the extension configured in the repository's git config ran the sentinel). Agent Studio inspects the effective
    /// configuration and refuses before the agent's program runs; only the base git-lfs filters run on content (A-03).
    /// </summary>
    [Fact]
    public async Task A_git_lfs_extension_configured_in_the_repository_is_refused_before_it_runs()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        using var sentinel = new Sentinel();
        var manager = NewManager();
        var head = CommitLfsRepository();
        var lease = await EnsureAsync(manager, "T1", head);

        // The agent (or a prior run) configures a git-lfs extension in the shared config, its clean the sentinel.
        var script = Path.Combine(sentinel.Folder, "ext.sh");
        File.WriteAllText(script, sentinel.Script("extension"));
        Repo.Run("config", "lfs.extension.foo.clean", "sh " + script.Replace('\\', '/') + " %f");
        Repo.Run("config", "lfs.extension.foo.smudge", "sh " + script.Replace('\\', '/') + " %f");
        Repo.Run("config", "lfs.extension.foo.priority", "0");
        WriteIn(lease, "big.dat", "contenido del agente\n");

        var error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: datos", Ct));

        Assert.False(sentinel.Ran, "git-lfs ran the repository's extension program with the user's permissions.");
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("lfs.extension.foo.clean", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git-lfs", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A program-valued key in the versioned <c>.lfsconfig</c> an agent can write never runs: measured with git-lfs
    /// 3.5.1, it drops such "unsafe" keys from <c>.lfsconfig</c> with a warning. Agent Studio's inspection reads git's
    /// configuration, not <c>.lfsconfig</c>, so it does not refuse here; git-lfs's own safe-config handling is what
    /// stops the program (A-03). Pinned to git-lfs 3.5.1; re-measure on upgrade.
    /// </summary>
    [Fact]
    public async Task A_program_in_lfsconfig_never_runs_because_git_lfs_ignores_it()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        using var sentinel = new Sentinel();
        var manager = NewManager();
        Repo.Run("lfs", "install", "--local");
        var script = Path.Combine(sentinel.Folder, "ext.sh");
        File.WriteAllText(script, sentinel.Script("lfsconfig"));
        var program = "sh " + script.Replace('\\', '/') + " %f";
        Repo.Write(".lfsconfig", "[lfs \"extension.foo\"]\n\tclean = " + program + "\n\tsmudge = " + program + "\n\tpriority = 0\n");
        Repo.Write(".gitattributes", "*.dat filter=lfs diff=lfs merge=lfs -text\n");
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "repositorio con .lfsconfig");
        var head = Repo.Head;
        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, "big.dat", "contenido del agente\n");

        var error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: datos", Ct));

        Assert.False(sentinel.Ran, "git-lfs honoured a program-valued key from .lfsconfig (3.5.1 should ignore it).");
        Assert.False(error is UnsafeGitCallbackException, "the gate refused on .lfsconfig, which it does not read; git-lfs's safe-config handles it.");
    }

    /// <summary>
    /// P-03: <c>lfs.storage</c> chooses where git-lfs writes the content of the large files. Measured with git-lfs 3.5.1
    /// (offline): in the repository's git config, absolute or relative (to the common directory), and also in a worktree's
    /// <c>config.worktree</c>, it made the clean filter of <c>git add</c> write the object outside the repository. The
    /// base commit has no lfs file, so creating the worktree runs no git-lfs; the key is planted afterwards.
    /// </summary>
    [Fact]
    public async Task A_git_lfs_storage_outside_the_repository_is_refused_before_git_lfs_writes_there()
    {
        Assert.SkipUnless(GitLfsAvailable, "git-lfs is not installed.");
        var manager = NewManager();
        var head = CommitLfsRepository();
        var lease = await EnsureAsync(manager, "T1", head);
        var outside = NewVictim("almacen-lfs");
        Repo.Run("config", "lfs.storage", outside.Replace('\\', '/'));
        WriteIn(lease, "big.dat", "contenido del agente\n");

        var error = await Record.ExceptionAsync(() => manager.CommitAllAsync(lease, "T1: datos", Ct));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside, "*", SearchOption.AllDirectories));
        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("lfs.storage", refusal.Message, StringComparison.Ordinal);
    }

    // Note: "plain lfs filters still work" is covered by The_installed_git_lfs_by_its_absolute_path_is_still_allowed, the
    // classifier tests (base filters allowed, benign lfs.* keys not classified) and, end to end with REAL git-lfs through
    // every internal command, by GitLfsHooksTests: git-lfs installs its hooks into core.hooksPath whenever its filter
    // starts, and Agent Studio's hooks path is an empty file, below which git-lfs installs nothing and git finds nothing.

    [Fact]
    public async Task Every_diff_the_layer_runs_disables_the_external_diff_and_textconv()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var t1 = await EnsureAsync(manager, "T1");
        var commit = await CommitInAsync(manager, t1, "nuevo.txt", "x\n");
        await manager.ChangedPathsAsync(Repo.Root, Repo.BaseCommit, commit, Ct);
        using (var patch = new MemoryStream())
        {
            await manager.WriteDiffAsync(Repo.Root, Repo.BaseCommit, commit, patch, Ct);
        }

        var diffs = recorder.Specs.Where(s => RecordingProcessRunner.SubCommand(s) == "diff").ToList();
        Assert.NotEmpty(diffs);
        Assert.All(diffs, s =>
        {
            Assert.Contains("--no-ext-diff", s.Arguments);
            Assert.Contains("--no-textconv", s.Arguments);
        });
    }

    /// <summary>
    /// A user's repository at an ASCII path, with one commit, and a worktree root next to it. The usual test repository
    /// lives under 'as git tést', and the allowlist refuses a program whose path has an 'é' by its charset, before where
    /// the program is matters.
    /// </summary>
    private sealed class AsciiRepository : IDisposable
    {
        private AsciiRepository(string container)
        {
            Container = container;
            Root = Path.Combine(container, "repo");
            WorktreeRoot = Path.Combine(container, "wt");
            CommonDirectory = Path.Combine(Root, ".git");
        }

        public string Container { get; }

        public string Root { get; }

        public string WorktreeRoot { get; }

        public string CommonDirectory { get; private set; }

        public static AsciiRepository Create()
        {
            var repository = new AsciiRepository(Path.Combine(Path.GetTempPath(), "as-repo-" + Guid.NewGuid().ToString("N")[..12]));
            Directory.CreateDirectory(repository.Root);
            TempGitRepo.Git(repository.Root, "init", "-q", "-b", "main");
            TempGitRepo.Git(repository.Root, "config", "user.name", "Agent Studio Tests");
            TempGitRepo.Git(repository.Root, "config", "user.email", "tests@localhost");
            TempGitRepo.Git(repository.Root, "config", "commit.gpgsign", "false");
            TempGitRepo.Git(repository.Root, "config", "core.autocrlf", "false");
            repository.Write("README.md", "# temp\n");
            repository.Commit("base");
            return repository;
        }

        /// <summary>Moves the git directory out of the working tree, which keeps a <c>.git</c> file pointing at it.</summary>
        public void SeparateGitDirectory()
        {
            var gitDirectory = Path.Combine(Container, "gitdir");
            TempGitRepo.Git(Root, "init", "-q", "--separate-git-dir=" + gitDirectory);
            CommonDirectory = gitDirectory;
        }

        public void Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string Commit(string message)
        {
            TempGitRepo.Git(Root, "add", "-A");
            TempGitRepo.Git(Root, "commit", "-q", "-m", message);
            return TempGitRepo.Git(Root, "rev-parse", "HEAD").Trim();
        }

        /// <summary>
        /// Puts <paramref name="configuration"/> in an include file of the common directory that only the git directory of a
        /// linked worktree (<c>&lt;common&gt;/worktrees/&lt;id&gt;</c>) pulls in, never the main working tree's.
        /// </summary>
        public void EnableForWorktreesOnly(string configuration)
        {
            var include = Path.Combine(CommonDirectory, "worktree-only.config");
            File.WriteAllText(include, configuration);
            File.AppendAllText(Path.Combine(CommonDirectory, "config"), "[includeIf \"gitdir:**/worktrees/**\"]\n\tpath = " + include.Replace('\\', '/') + "\n");
        }

        /// <summary>The working trees git lists for this repository, the main one first, as full paths.</summary>
        public IReadOnlyList<string> Worktrees() =>
            [.. GitPorcelain.ParseWorktreeList(TempGitRepo.Git(Root, "worktree", "list", "--porcelain", "-z")).Select(e => Path.GetFullPath(e.Path))];

        public void Dispose() => TempFolders.Delete(Container);
    }

    /// <summary>
    /// A folder outside every repository and worktree, with an ASCII path, where a script git should never have run leaves
    /// a mark.
    /// </summary>
    private sealed class Sentinel : IDisposable
    {
        public Sentinel()
        {
            Folder = Path.Combine(Path.GetTempPath(), "as-centinela-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Folder);
        }

        public string Folder { get; }

        public string Mark => Path.Combine(Folder, "ran.txt");

        public bool Ran => File.Exists(Mark);

        /// <summary>A shell script that leaves the mark and copies standard input to standard output, as a filter does.</summary>
        public string Script(string name) => "#!/bin/sh\necho " + name + " >> '" + Mark.Replace('\\', '/') + "'\ncat\n";

        public void Dispose() => TempFolders.Delete(Folder);
    }
}
