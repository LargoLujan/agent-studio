using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The user's main working tree, HEAD, index and branches are never touched, and every mutating command is refused
/// unless it targets a worktree that git itself reports as owned by Agent Studio.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeSafetyTests : GitManagerTestBase
{
    private WorktreeLease Forged(string path, string name = "T1", string? refName = null, RunId? run = null, string? repositoryRoot = null) =>
        new(
            path,
            name,
            refName ?? $"refs/agentstudio/{(run ?? TestRun).Value}/{name}",
            Repo.BaseCommit,
            run ?? TestRun,
            repositoryRoot ?? Repo.Root,
            Path.Combine(Repo.Root, ".git", "worktrees", name));

    // The wording of each layer of the guard (see GitWorktreeManager.RequireOwnedAsync). Asserting it proves that a case is
    // refused by the layer it is meant to exercise and not by an earlier one by accident.
    private const string OutsideRoot = "is not inside the Agent Studio worktree root";
    private const string RepositoryInsideRoot = "is inside the Agent Studio worktree root";
    private const string NoRepository = "does not name the repository";
    private const string NotTheRepositoryRoot = "is not the root of its git working tree";
    private const string NotLinked = "is not a linked worktree";
    private const string NotLocked = "is not locked by Agent Studio";
    private const string LeaseLies = "does not describe the worktree";

    // Every mutating operation that takes a lease must refuse it for the expected reason, without changing anything in
    // the repository.
    private async Task AssertRefusedAsync(GitWorktreeManager manager, WorktreeLease lease, string reason)
    {
        var before = Repo.Snapshot();

        var refusals = new[]
        {
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(lease, "mensaje", Ct)),
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CheckoutAsync(lease, Repo.BaseCommit, Ct)),
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.RemoveAsync(lease, discardChanges: true, Ct)),
        };

        Assert.All(refusals, refusal => Assert.Contains(reason, refusal.Message, StringComparison.Ordinal));
        Assert.Equal(before, Repo.Snapshot());
    }

    [Fact]
    public async Task The_ownership_guard_rejects_the_main_working_tree()
    {
        var manager = NewManager();
        Repo.Write("README.md", "# temp\nedición del usuario\n");
        Repo.Write("notas.txt", "sin seguimiento\n");
        var branchesBefore = Repo.Run("branch", "--list");

        // It is outside the worktree root, which is the first layer.
        await AssertRefusedAsync(manager, Forged(Repo.Root), OutsideRoot);
        await AssertRefusedAsync(manager, Forged(Repo.Root + Path.DirectorySeparatorChar), OutsideRoot);
        await AssertRefusedAsync(manager, Forged(Path.Combine(Repo.Root, "src")), OutsideRoot);

        Assert.Equal("# temp\nedición del usuario\n", File.ReadAllText(Path.Combine(Repo.Root, "README.md")));
        Assert.Equal("sin seguimiento\n", File.ReadAllText(Path.Combine(Repo.Root, "notas.txt")));
        Assert.Equal(branchesBefore, Repo.Run("branch", "--list"));
        Assert.Equal(Repo.BaseCommit, Repo.Head);
    }

    [Fact]
    public async Task The_ownership_guard_rejects_the_main_working_tree_even_when_the_worktree_root_contains_it()
    {
        // A configuration mistake: the root is the parent of the repository, so the first layer lets the path through.
        // The repository is then inside the worktree root, and a repository there is never accepted.
        var manager = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(Path.GetDirectoryName(Repo.Root)!));
        Repo.Write("notas.txt", "sin seguimiento\n");

        await AssertRefusedAsync(manager, Forged(Repo.Root), RepositoryInsideRoot);

        // Even through a linked worktree of the user outside that root, the main working tree is not a linked
        // worktree: git lists it first, and that entry is never accepted.
        var linked = Path.Combine(Path.GetDirectoryName(Repo.Root)!, "..", Path.GetFileName(Path.GetDirectoryName(Repo.Root)!) + "-enlazado");
        Repo.Run("worktree", "add", "-q", "--detach", linked);
        try
        {
            await AssertRefusedAsync(manager, Forged(Repo.Root, repositoryRoot: Path.GetFullPath(linked)), NotLinked);
        }
        finally
        {
            Repo.Run("worktree", "remove", "--force", linked);
        }
    }

    [Fact]
    public async Task The_ownership_guard_takes_the_repository_from_the_lease_and_checks_it()
    {
        var manager = NewManager();
        var owned = await EnsureAsync(manager, "T1");
        var other = await EnsureAsync(manager, "T2");
        WriteIn(owned, "trabajo.txt", "sin confirmar\n");

        // No repository, a worktree as the repository, a folder that is not a working tree root, and another repository.
        await AssertRefusedAsync(manager, owned with { RepositoryRoot = string.Empty }, NoRepository);
        await AssertRefusedAsync(manager, owned with { RepositoryRoot = other.Path }, RepositoryInsideRoot);
        await AssertRefusedAsync(manager, owned with { RepositoryRoot = Path.Combine(Repo.Root, "src") }, NotTheRepositoryRoot);
        using (var stranger = TempGitRepo.Create())
        {
            await AssertRefusedAsync(manager, owned with { RepositoryRoot = stranger.Root }, NotLinked);
        }

        // The administrative folder of another worktree.
        await AssertRefusedAsync(manager, owned with { AdminDirectory = other.AdminDirectory }, LeaseLies);

        Assert.True(File.Exists(Path.Combine(owned.Path, "trabajo.txt")));
        Assert.True(SamePath(Path.Combine(Repo.Root, ".git", "worktrees", "T1"), owned.AdminDirectory));
        Assert.True(SamePath(Repo.Root, owned.RepositoryRoot));
    }

    [Fact]
    public async Task The_ownership_guard_rejects_paths_that_are_not_owned_worktrees()
    {
        var manager = NewManager();
        var owned = await EnsureAsync(manager, "T1");

        // 1. A folder under the root that is not a worktree.
        var plain = Path.Combine(Repo.WorktreeRoot, "zz", "yy", "T1");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "intocable.txt"), "x");
        await AssertRefusedAsync(manager, Forged(plain), NotLinked);
        Assert.True(File.Exists(Path.Combine(plain, "intocable.txt")));

        // 2. A real worktree under the root, made by hand: no lock.
        var manual = Path.Combine(Repo.WorktreeRoot, "manual", "x", "T1");
        Repo.Run("worktree", "add", "--detach", manual);
        await AssertRefusedAsync(manager, Forged(manual), NotLocked);

        // 3. The same, locked by somebody else.
        var other = Path.Combine(Repo.WorktreeRoot, "manual", "x", "T2");
        Repo.Run("worktree", "add", "--detach", "--lock", "--reason", "otro-programa", other);
        await AssertRefusedAsync(manager, Forged(other, "T2"), NotLocked);

        // 4. A real worktree outside the root, even with a lock reason in our format.
        var outside = Path.Combine(Repo.WorktreeRoot, "..", "fuera");
        Repo.Run("worktree", "add", "--detach", "--lock", "--reason", $"agent-studio:{ProjectId}:{TestRun.Value}:T3", outside);
        await AssertRefusedAsync(manager, Forged(outside, "T3"), OutsideRoot);

        // 5. Path traversal out of an owned worktree.
        await AssertRefusedAsync(manager, Forged(Path.Combine(owned.Path, "..", "..", "..")), OutsideRoot);
        await AssertRefusedAsync(manager, Forged(Path.Combine(owned.Path, "..", "..", "..", "..")), OutsideRoot);
        await AssertRefusedAsync(manager, Forged(Path.Combine(owned.Path, "..", "..", "..", "..", "repo")), OutsideRoot);

        // 6. A path under the root that does not exist.
        var missing = Path.Combine(owned.Path, "..", "T9");
        var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CommitAllAsync(Forged(missing, "T9"), "mensaje", Ct));
        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(RemovalOutcome.AlreadyRemoved, await manager.RemoveAsync(Forged(missing, "T9"), discardChanges: true, Ct));

        // None of that harmed the owned worktree.
        Assert.True(Directory.Exists(owned.Path));
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(owned, discardChanges: false, Ct));
    }

    [Fact]
    public async Task The_ownership_guard_trusts_git_not_the_lease()
    {
        var manager = NewManager();
        var owned = await EnsureAsync(manager, "T1");
        WriteIn(owned, "trabajo.txt", "sin confirmar\n");

        // A lease that points at a real owned worktree but lies about what it is. The ref one would move a user branch.
        await AssertRefusedAsync(manager, owned with { RefName = "refs/heads/main" }, LeaseLies);
        await AssertRefusedAsync(manager, owned with { RefName = $"refs/agentstudio/{TestRun.Value}/T2" }, LeaseLies);
        await AssertRefusedAsync(manager, owned with { Name = "T2", RefName = $"refs/agentstudio/{TestRun.Value}/T2" }, LeaseLies);
        await AssertRefusedAsync(manager, owned with { Run = new RunId("20261016-ffffff"), RefName = "refs/agentstudio/20261016-ffffff/T1" }, LeaseLies);

        Assert.True(File.Exists(Path.Combine(owned.Path, "trabajo.txt")));
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", "main").Trim());
    }

    [Fact]
    public async Task The_main_working_tree_stays_intact_through_a_complete_flow()
    {
        var manager = NewManager();

        // The user has work in progress in the main working tree.
        Repo.Write("README.md", "# temp\nedición del usuario\n");
        Repo.Write("notas.txt", "sin seguimiento\n");
        var before = Repo.Snapshot();

        var state = await manager.InspectAsync(Repo.Root, Ct);
        Assert.True(state.HasUncommittedChanges);

        var integration = await EnsureAsync(manager, "_integration");
        var t1 = await EnsureAsync(manager, "T1");
        var t2 = await EnsureAsync(manager, "T2");
        var c1 = await CommitInAsync(manager, t1, "src/Calc/Multiply.cs", "// multiplica\n");
        var c2 = await CommitInAsync(manager, t2, "tests/Calc.Tests/MultiplyTests.cs", "// prueba\n");
        Assert.Equal(["src/Calc/Multiply.cs"], await manager.ChangedPathsAsync(Repo.Root, Repo.BaseCommit, c1, Ct));
        using (var patch = new MemoryStream())
        {
            await manager.WriteDiffAsync(Repo.Root, Repo.BaseCommit, c2, patch, Ct);
            Assert.True(patch.Length > 0);
        }

        var branch = "agentstudio/run-" + TestRun.Value;
        await manager.EnsureRefAsync(Repo.Root, branch, Repo.BaseCommit, Ct);
        var m1 = await manager.MergeIntoAsync(Repo.Root, branch, Repo.BaseCommit, c1, "Integra T1", Ct);
        var m2 = await manager.MergeIntoAsync(Repo.Root, branch, m1.ResultCommit!, c2, "Integra T2", Ct);
        await manager.CheckoutAsync(integration, m2.ResultCommit!, Ct);
        await manager.CheckoutAsync(t1, Repo.BaseCommit, Ct);
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(t1, discardChanges: false, Ct));
        WriteIn(t2, "descartable.txt", "x");
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(t2, discardChanges: true, Ct));
        Assert.Equal(RemovalOutcome.Removed, await manager.RemoveAsync(integration, discardChanges: false, Ct));

        // Status, HEAD, the HEAD file, every user ref and the index file are exactly as they were.
        Assert.Equal(before, Repo.Snapshot());
        Assert.Equal("# temp\nedición del usuario\n", File.ReadAllText(Path.Combine(Repo.Root, "README.md")));
        Assert.Equal("sin seguimiento\n", File.ReadAllText(Path.Combine(Repo.Root, "notas.txt")));
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", "main").Trim());
        Assert.False(File.Exists(Path.Combine(Repo.Root, "src", "Calc", "Multiply.cs")));

        // Agent Studio's own refs hold the work.
        Assert.Equal(m2.ResultCommit, Repo.Run("rev-parse", "refs/heads/" + branch).Trim());
        Assert.Equal(c1, Repo.Run("rev-parse", t1.RefName).Trim());
        Assert.Equal(c2, Repo.Run("rev-parse", t2.RefName).Trim());
        Assert.Empty(ListWorktrees().Skip(1));
    }

    [Fact]
    public async Task Commands_never_prompt_use_long_paths_and_keep_free_text_out_of_the_arguments()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var integration = await EnsureAsync(manager, "_integration");
        var t1 = await EnsureAsync(manager, "T1");
        WriteIn(t1, "hola.txt", "hola\n");
        const string commitToken = "TOKEN-DE-COMMIT-\"con comillas\" & más";
        const string mergeToken = "TOKEN-DE-FUSION-%PATH%-$(whoami)";
        var commit = (await manager.CommitAllAsync(t1, commitToken, Ct)).Commit;
        await manager.MergeIntoAsync(Repo.Root, integration.RefName, Repo.BaseCommit, commit, mergeToken, Ct);
        await manager.CheckoutAsync(t1, Repo.BaseCommit, Ct);
        await manager.RemoveAsync(t1, discardChanges: false, Ct);

        var specs = recorder.Specs;
        Assert.NotEmpty(specs);
        Assert.All(specs, spec =>
        {
            Assert.NotNull(spec.Environment);
            Assert.Equal("0", spec.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("cat", spec.Environment["GIT_PAGER"]);
            Assert.Contains("core.longpaths=true", spec.Arguments);
            Assert.NotNull(spec.Timeout);
            Assert.DoesNotContain(spec.Arguments, a => a.Contains("TOKEN-DE-", StringComparison.Ordinal));

            // Every transport is closed: the fallback and each named protocol (a repository can set
            // protocol.<name>.allow=always, which beats the fallback).
            Assert.Contains("protocol.allow=never", spec.Arguments);
            foreach (var name in new[] { "file", "git", "ssh", "http", "https", "ftp", "ftps", "ext" })
            {
                Assert.Contains($"protocol.{name}.allow=never", spec.Arguments);
            }
        });

        var commitSpec = Assert.Single(specs, s => RecordingProcessRunner.SubCommand(s) == "commit");
        Assert.Contains("--no-verify", commitSpec.Arguments);
        Assert.Contains("commit.gpgsign=false", commitSpec.Arguments);
        Assert.Contains("user.name=Agent Studio", commitSpec.Arguments);
        Assert.Contains("user.email=agent-studio@localhost", commitSpec.Arguments);
        Assert.StartsWith(commitToken, commitSpec.StandardInput, StringComparison.Ordinal);

        var treeSpec = Assert.Single(specs, s => RecordingProcessRunner.SubCommand(s) == "commit-tree");
        Assert.Contains("commit.gpgsign=false", treeSpec.Arguments);
        Assert.Contains("user.name=Agent Studio", treeSpec.Arguments);
        Assert.Contains("user.email=agent-studio@localhost", treeSpec.Arguments);
        Assert.StartsWith(mergeToken, treeSpec.StandardInput, StringComparison.Ordinal);

        // Commands that change a working tree or an index run inside an owned worktree, never in the main working tree,
        // and name its administrative folder and its working tree explicitly: the .git file is never read. Each command
        // targets the worktree it runs in (work-tree == working directory), be it T1 or the reset that checks out a
        // worktree on creation (A-02); its admin folder is one under <repo>/.git/worktrees/.
        string[] mutating = ["add", "commit", "reset", "clean", "checkout", "status"];
        var worktreesAdmin = Path.Combine(Repo.Root, ".git", "worktrees") + Path.DirectorySeparatorChar;
        var inWorktrees = specs.Where(s => mutating.Contains(RecordingProcessRunner.SubCommand(s))).ToList();
        Assert.NotEmpty(inWorktrees);
        Assert.All(inWorktrees, s =>
        {
            Assert.StartsWith(Repo.WorktreeRoot + Path.DirectorySeparatorChar, s.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(s.Arguments, a => a.StartsWith("--git-dir=" + worktreesAdmin, StringComparison.Ordinal));
            Assert.Contains("--work-tree=" + s.WorkingDirectory, s.Arguments);
        });

        // No hook ever runs, no ref is followed to the branch a symbolic ref names, and nothing is pruned.
        var noHooks = "core.hooksPath=" + Path.Combine(Repo.WorktreeRoot, ".no-hooks");
        Assert.All(specs, s => Assert.Contains(noHooks, s.Arguments));
        Assert.All(specs.Where(s => RecordingProcessRunner.SubCommand(s) == "update-ref"), s => Assert.Contains("--no-deref", s.Arguments));
        Assert.DoesNotContain(specs, s => s.Arguments.Contains("prune"));

        // Reading the user's repository must not refresh its index.
        Assert.All(specs, s => Assert.Equal("0", s.Environment!["GIT_OPTIONAL_LOCKS"]));
    }

    [Fact]
    public async Task Commands_ignore_environment_variables_that_redirect_git_to_another_repository()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        await EnsureAsync(manager, "T1");

        string[] redirecting =
        [
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
            "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS",
            "GIT_AUTHOR_NAME", "GIT_COMMITTER_EMAIL",
        ];
        Assert.All(recorder.Specs, spec =>
        {
            foreach (var name in redirecting)
            {
                Assert.True(spec.Environment!.TryGetValue(name, out var value) && value is null, $"{name} must be removed from the environment.");
            }
        });
    }

    [Fact]
    public async Task Parallel_commits_in_different_worktrees_both_succeed_and_move_their_own_refs()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var t2 = await EnsureAsync(manager, "T2");
        WriteIn(t1, "uno.txt", "1\n");
        WriteIn(t2, "dos.txt", "2\n");

        var results = await Task.WhenAll(
            manager.CommitAllAsync(t1, "T1", Ct),
            manager.CommitAllAsync(t2, "T2", Ct));

        Assert.All(results, r => Assert.True(r.Committed));
        Assert.NotEqual(results[0].Commit, results[1].Commit);
        Assert.Equal(results[0].Commit, Repo.Run("rev-parse", t1.RefName).Trim());
        Assert.Equal(results[1].Commit, Repo.Run("rev-parse", t2.RefName).Trim());
    }

    [Fact]
    public async Task A_missing_git_executable_is_reported_as_unavailable()
    {
        var manager = new GitWorktreeManager(new ProcessRunner(), new NoExecutables(), new GitWorktreeOptions(Repo.WorktreeRoot));

        await Assert.ThrowsAsync<GitUnavailableException>(() => manager.InspectAsync(Repo.Root, Ct));
    }

    [Fact]
    public async Task A_git_that_exists_only_inside_the_project_is_reported_as_such_not_as_missing()
    {
        var manager = new GitWorktreeManager(new ProcessRunner(), new InsideOnly(@"C:\proyecto\bin\git.exe"), new GitWorktreeOptions(Repo.WorktreeRoot));

        var error = await Assert.ThrowsAsync<GitUnavailableException>(() => manager.InspectAsync(Repo.Root, Ct));

        Assert.Contains(@"C:\proyecto\bin\git.exe", error.Message, StringComparison.Ordinal);
        Assert.Contains("project folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_path_of_git_kept_from_an_earlier_lookup_is_checked_again_against_the_roots_of_now()
    {
        var locator = new Revocable(new ExecutableLocator().Find("git") ?? throw new InvalidOperationException("git is needed"));
        var manager = new GitWorktreeManager(new ProcessRunner(), locator, new GitWorktreeOptions(Repo.WorktreeRoot));

        await manager.InspectAsync(Repo.Root, Ct);
        locator.Untrusted = true;

        var error = await Assert.ThrowsAsync<GitUnavailableException>(() => manager.InspectAsync(Repo.Root, Ct));
        Assert.Contains("project folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_git_executable_goes_through_the_rule_too()
    {
        var real = new ExecutableLocator().Find("git") ?? throw new InvalidOperationException("git is needed");
        var locator = new Revocable(real) { Untrusted = true };
        var manager = new GitWorktreeManager(new ProcessRunner(), locator, new GitWorktreeOptions(Repo.WorktreeRoot, GitExecutable: real));

        await Assert.ThrowsAsync<GitUnavailableException>(() => manager.InspectAsync(Repo.Root, Ct));
    }

    private sealed class InsideOnly(string path) : IExecutableLocator
    {
        public string? Find(string command) => null;

        public string? FindInUntrustedRoot(string command) => path;
    }

    private sealed class Revocable(string path) : IExecutableLocator
    {
        public bool Untrusted { get; set; }

        public string? Find(string command) => Untrusted ? null : path;

        public string? FindInUntrustedRoot(string command) => Untrusted ? path : null;

        public bool IsUntrusted(string candidate) => Untrusted && string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class NoExecutables : IExecutableLocator
    {
        public string? Find(string command) => null;
    }
}
