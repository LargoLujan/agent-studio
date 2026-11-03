using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The ref operations the integration, the delivery and the cleanup need: resolving, ancestry, the run's branch,
/// listing and deleting the refs of a run. Only refs of the Agent Studio namespace are ever written, always as a
/// compare-and-swap that never follows a symbolic ref, never through a link, and without running a hook.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeRefsTests : GitManagerTestBase
{
    private static readonly RunId OtherRun = new("20261015-ffffff");

    private static string RunBranch(RunId? run = null) => $"refs/heads/agentstudio/run-{(run ?? TestRun).Value}";

    private static string Hidden(string name, RunId? run = null) => $"refs/agentstudio/{(run ?? TestRun).Value}/{name}";

    private string NewCommit(string message) =>
        Repo.Run("commit-tree", Repo.BaseCommit + "^{tree}", "-p", Repo.BaseCommit, "-m", message).Trim();

    private string? ReadRef(string name)
    {
        var (exitCode, output, _) = TempGitRepo.TryGit(Repo.Root, "rev-parse", "--verify", "--quiet", name);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>Hooks in the repository every worktree shares, which an agent with a shell can write. Each logs its name if it runs.</summary>
    private string PlantHooks()
    {
        foreach (var hook in new[] { "reference-transaction", "post-checkout", "post-merge", "pre-commit", "post-commit", "post-index-change" })
        {
            var path = Path.Combine(Repo.Root, ".git", "hooks", hook);
            File.WriteAllText(path, HookScripts.Logging(hook));
        }

        return Path.Combine(Repo.Root, ".git", HookScripts.LogName);
    }

    // ---- resolving and ancestry ----------------------------------------------------------------------------------

    [Fact]
    public async Task ResolveCommit_gives_the_commit_of_an_object_id_or_an_Agent_Studio_ref_and_null_when_it_does_not_exist()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "T1");
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);

        Assert.Equal(Repo.BaseCommit, await manager.ResolveCommitAsync(Repo.Root, Repo.BaseCommit.ToUpperInvariant(), Ct));
        Assert.Equal(Repo.BaseCommit, await manager.ResolveCommitAsync(Repo.Root, lease.RefName, Ct));
        Assert.Equal(Repo.BaseCommit, await manager.ResolveCommitAsync(Repo.Root, "agentstudio/run-" + TestRun.Value, Ct));
        Assert.Null(await manager.ResolveCommitAsync(Repo.Root, Hidden("T9"), Ct));
        Assert.Null(await manager.ResolveCommitAsync(Repo.Root, new string('b', 40), Ct));
    }

    [Fact]
    public async Task IsAncestor_is_the_answer_of_merge_base_and_an_unknown_commit_is_an_error_not_a_no()
    {
        var manager = NewManager();
        var child = NewCommit("hijo");

        Assert.True(await manager.IsAncestorAsync(Repo.Root, Repo.BaseCommit, child, Ct));
        Assert.True(await manager.IsAncestorAsync(Repo.Root, child, child, Ct));
        Assert.False(await manager.IsAncestorAsync(Repo.Root, child, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<GitCommandException>(() => manager.IsAncestorAsync(Repo.Root, new string('b', 40), child, Ct));
    }

    // ---- validation before git runs -----------------------------------------------------------------------------

    public static TheoryData<string> RefsOutsideTheNamespace() =>
    [
        "refs/heads/main", "main", "HEAD", "refs/tags/v1", "refs/remotes/origin/main", "refs/heads/agentstudio",
        "refs/heads/agentstudio-evil/x", "-c", "--output=C:/x", "-refs/agentstudio/r/T1", "abc1234",
    ];

    public static TheoryData<string> MalformedRefsInsideTheNamespace() =>
    [
        "", " ", "refs/agentstudio/", "refs/agentstudio/r/../../heads/main", "refs/agentstudio/r/T1..refs/heads/main",
        "refs/agentstudio/r/T1@{1}", "refs/agentstudio/r/T1@{-1}", "refs/agentstudio/r/T1\n", "refs/agentstudio/r/T\u0001",
        "refs/agentstudio/r/T\u007f", "agentstudio/-delete", "refs/agentstudio/-r/T1", "refs/agentstudio/r/T1.lock",
        "refs/agentstudio/r//T1", "refs/agentstudio/r/T1 refs/heads/main", "agentstudio/run:x",
    ];

    [Theory]
    [MemberData(nameof(RefsOutsideTheNamespace))]
    [MemberData(nameof(MalformedRefsInsideTheNamespace))]
    public async Task Revisions_that_are_neither_a_commit_id_nor_an_Agent_Studio_ref_are_refused_before_git_runs(string revision)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ResolveCommitAsync(Repo.Root, revision, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.IsAncestorAsync(Repo.Root, revision, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.IsAncestorAsync(Repo.Root, Repo.BaseCommit, revision, Ct));

        Assert.Empty(recorder.Specs);
    }

    [Theory]
    [MemberData(nameof(RefsOutsideTheNamespace))]
    public async Task Deleting_a_ref_outside_the_Agent_Studio_namespace_is_an_ownership_violation(string refName)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var before = await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);
        var captureSpecs = recorder.Specs.Count;

        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeleteRefAsync(Repo.Root, refName, Repo.BaseCommit, Ct));

        Assert.Equal(captureSpecs, recorder.Specs.Count);
        Assert.Empty(UserRepositoryState.Compare(before, await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)));
    }

    [Theory]
    [MemberData(nameof(MalformedRefsInsideTheNamespace))]
    public async Task Deleting_a_malformed_ref_is_refused_before_git_runs(string refName)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => NewManager(recorder).DeleteRefAsync(Repo.Root, refName, Repo.BaseCommit, Ct));

        Assert.Empty(recorder.Specs);
    }

    [Fact]
    public async Task Writes_need_a_full_commit_id_and_a_valid_run()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.DeleteRefAsync(Repo.Root, Hidden("T1"), "HEAD", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.DeleteRefAsync(Repo.Root, Hidden("T1"), Repo.BaseCommit[..12], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.EnsureRunBranchAsync(Repo.Root, TestRun, "main", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.EnsureRunBranchAsync(Repo.Root, default, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.EnsureRunBranchAsync(Repo.Root, new RunId("_x"), Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ListRunRefsAsync(Repo.Root, new RunId("-x"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ListRunRefsAsync(Repo.Root, default, Ct));

        Assert.Empty(recorder.Specs);
    }

    // ---- the run's branch ---------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureRunBranch_creates_the_branch_at_the_base_once_and_then_gives_wherever_it_is()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");

        var created = await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        var merge = await manager.MergeIntoAsync(Repo.Root, RunBranch(), created, c1, "Integra T1", Ct);
        var again = await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);

        Assert.Equal(Repo.BaseCommit, created);
        Assert.Equal(merge.ResultCommit, again);
        Assert.Equal(merge.ResultCommit, ReadRef(RunBranch()));
        Assert.Equal(Repo.BaseCommit, ReadRef("refs/heads/main"));
    }

    [Fact]
    public async Task EnsureRunBranch_fails_for_a_base_that_does_not_exist_and_creates_nothing()
    {
        var manager = NewManager();

        await Assert.ThrowsAsync<GitCommandException>(() => manager.EnsureRunBranchAsync(Repo.Root, TestRun, new string('c', 40), Ct));

        Assert.Null(ReadRef(RunBranch()));
    }

    // ---- listing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListRunRefs_gives_the_branch_and_the_hidden_refs_of_the_run_and_nothing_else()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        Repo.Run("branch", "feature");
        Repo.Run("update-ref", Hidden("T1", OtherRun), c1);
        Repo.Run("update-ref", "refs/heads/agentstudio/run-" + OtherRun.Value, c1);

        var refs = await manager.ListRunRefsAsync(Repo.Root, TestRun, Ct);

        Assert.Equal(
            [
                new RefEntry(Hidden("T1"), c1),
                new RefEntry(Hidden("_start/T1"), Repo.BaseCommit),
                new RefEntry(RunBranch(), Repo.BaseCommit),
            ],
            refs);
    }

    [Fact]
    public async Task ListRunRefs_of_a_run_without_refs_is_empty()
    {
        Assert.Empty(await NewManager().ListRunRefsAsync(Repo.Root, TestRun, Ct));
    }

    // ---- deleting -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteRef_deletes_a_ref_that_is_still_where_it_was_listed()
    {
        var manager = NewManager();
        await EnsureAsync(manager, "T1");

        Assert.True(await manager.DeleteRefAsync(Repo.Root, Hidden("T1"), Repo.BaseCommit, Ct));

        Assert.Null(ReadRef(Hidden("T1")));
        Assert.Equal(Repo.BaseCommit, ReadRef(Hidden("_start/T1")));
    }

    [Fact]
    public async Task DeleteRef_is_a_compare_and_swap_a_ref_that_moved_is_kept()
    {
        var manager = NewManager();
        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");

        // Listed at the base, then the task committed: the expectation is stale.
        Assert.False(await manager.DeleteRefAsync(Repo.Root, Hidden("T1"), Repo.BaseCommit, Ct));

        Assert.Equal(c1, ReadRef(Hidden("T1")));
    }

    [Fact]
    public async Task DeleteRef_of_a_ref_that_is_already_gone_is_false()
    {
        Assert.False(await NewManager().DeleteRefAsync(Repo.Root, Hidden("T1"), Repo.BaseCommit, Ct));
    }

    [Fact]
    public async Task DeleteRef_deletes_a_symbolic_ref_itself_never_the_user_branch_it_names()
    {
        var manager = NewManager();
        Repo.Run("symbolic-ref", Hidden("T1"), "refs/heads/main");

        // Listed with the commit of main, the branch it names.
        var listed = Assert.Single(await manager.ListRunRefsAsync(Repo.Root, TestRun, Ct));
        Assert.True(await manager.DeleteRefAsync(Repo.Root, listed.Name, listed.Commit, Ct));

        Assert.Null(ReadRef(Hidden("T1")));
        Assert.Equal(Repo.BaseCommit, ReadRef("refs/heads/main"));
    }

    // ---- links ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_junction_planted_in_the_refs_of_the_run_is_refused_and_no_user_branch_is_deleted()
    {
        // With the folder of the run's hidden refs pointing at refs/heads, every user branch looks like a hidden ref of
        // the run, and deleting one would delete the user's branch file through the junction.
        var manager = NewManager();
        Repo.Run("branch", "feature");
        var before = await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);
        var refs = Path.Combine(Repo.Root, ".git", "refs");
        Directory.CreateDirectory(Path.Combine(refs, "agentstudio"));
        var junction = Path.Combine(refs, "agentstudio", TestRun.Value);
        CreateJunction(junction, Path.Combine(refs, "heads"));
        try
        {
            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.ListRunRefsAsync(Repo.Root, TestRun, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeleteRefAsync(Repo.Root, Hidden("feature"), Repo.BaseCommit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeleteRefAsync(Repo.Root, Hidden("main"), Repo.BaseCommit, Ct)),

                // S-01 to S-03: the status of the capture walks the git directory first, so it refuses the junction too.
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)),
            };
            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));

            Assert.Equal(Repo.BaseCommit, ReadRef("refs/heads/feature"));
            Assert.Equal(Repo.BaseCommit, ReadRef("refs/heads/main"));
        }
        finally
        {
            Directory.Delete(junction);
        }

        Assert.Empty(UserRepositoryState.Compare(before, await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)));
    }

    [Fact]
    public async Task A_junction_in_place_of_the_agentstudio_branches_is_refused_before_the_run_branch_is_written()
    {
        var manager = NewManager();
        var heads = Path.Combine(Repo.Root, ".git", "refs", "heads");
        var elsewhere = Path.Combine(Repo.WorktreeRoot, "..", "otras-refs");
        Directory.CreateDirectory(elsewhere);
        var junction = Path.Combine(heads, "agentstudio");
        CreateJunction(junction, elsewhere);
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    // ---- how git runs -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ref_operations_run_no_hook_never_follow_a_symbolic_ref_and_end_the_options_before_any_revision()
    {
        var log = PlantHooks();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        await EnsureAsync(manager, "T1");
        var start = recorder.Specs.Count;

        var head = await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        await manager.ResolveCommitAsync(Repo.Root, RunBranch(), Ct);
        await manager.IsAncestorAsync(Repo.Root, Repo.BaseCommit, head, Ct);
        foreach (var entry in await manager.ListRunRefsAsync(Repo.Root, TestRun, Ct))
        {
            Assert.True(await manager.DeleteRefAsync(Repo.Root, entry.Name, entry.Commit, Ct));
        }

        Assert.False(File.Exists(log), "A hook of the repository ran: " + (File.Exists(log) ? File.ReadAllText(log) : string.Empty));
        var specs = recorder.Specs.Skip(start).ToList();
        var noHooks = "core.hooksPath=" + Path.Combine(Repo.WorktreeRoot, ".no-hooks");
        Assert.All(specs, s => Assert.Contains(noHooks, s.Arguments));
        Assert.All(specs, s => Assert.Equal("0", s.Environment!["GIT_TERMINAL_PROMPT"]));

        // Once the repository is found from the project root, every command names its git directory explicitly.
        var gitDir = "--git-dir=" + Path.Combine(Repo.Root, ".git");
        var onRefs = specs.Where(s => RecordingProcessRunner.SubCommand(s) is "update-ref" or "merge-base" or "for-each-ref" || s.Arguments.Contains("--verify")).ToList();
        Assert.NotEmpty(onRefs);
        Assert.All(onRefs, s => Assert.Contains(s.Arguments, a => string.Equals(a, gitDir, StringComparison.OrdinalIgnoreCase)));

        var updates = specs.Where(s => RecordingProcessRunner.SubCommand(s) == "update-ref").ToList();
        Assert.Equal(4, updates.Count);
        Assert.All(updates, s => Assert.Contains("--no-deref", s.Arguments));
        foreach (var spec in specs.Where(s => RecordingProcessRunner.SubCommand(s) is "update-ref" or "merge-base" or "for-each-ref"))
        {
            var arguments = spec.Arguments.ToList();
            var end = arguments.IndexOf("--end-of-options");
            Assert.True(end > 0, $"{RecordingProcessRunner.SubCommand(spec)} must end its options before the revisions.");
            Assert.All(arguments.Skip(end + 1), a => Assert.False(a.StartsWith('-'), $"'{a}' comes after --end-of-options."));
        }
    }

    [Fact]
    public async Task A_tampered_gitfile_of_a_worktree_is_never_followed_and_a_worktree_is_never_taken_as_the_repository()
    {
        var manager = NewManager();
        var lease = await EnsureAsync(manager, "_integration");
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        RedirectGitFile(lease, Path.Combine(Repo.Root, ".git"));
        var before = await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);

        // The worktree, whose .git now names the user's repository, is never accepted as the repository.
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.ListRunRefsAsync(lease.Path, TestRun, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.EnsureRunBranchAsync(lease.Path, OtherRun, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeleteRefAsync(lease.Path, RunBranch(), Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.ResolveCommitAsync(lease.Path, RunBranch(), Ct));

        // From the user's repository everything works as before.
        Assert.Equal(3, (await manager.ListRunRefsAsync(Repo.Root, TestRun, Ct)).Count);
        Assert.True(await manager.DeleteRefAsync(Repo.Root, RunBranch(), Repo.BaseCommit, Ct));
        Assert.Null(ReadRef(RunBranch()));
        Assert.Null(ReadRef("refs/heads/agentstudio/run-" + OtherRun.Value));
        Assert.Empty(UserRepositoryState.Compare(before, await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)));
    }

    [Fact]
    public async Task Nothing_of_the_users_repository_changes_with_any_ref_operation()
    {
        var manager = NewManager();
        Repo.Run("tag", "v1");
        Repo.Run("branch", "feature");
        Repo.Write("README.md", "# temp\nedición del usuario\n");
        Repo.Write("notas.txt", "sin seguimiento\n");
        Repo.Write("preparado.txt", "x");
        Repo.Run("add", "preparado.txt");
        var before = await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);

        var t1 = await EnsureAsync(manager, "T1");
        var c1 = await CommitInAsync(manager, t1, "uno.txt", "1\n");
        var head = await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        var merge = await manager.MergeIntoAsync(Repo.Root, RunBranch(), head, c1, "Integra T1", Ct);
        Assert.Equal(merge.ResultCommit, await manager.ResolveCommitAsync(Repo.Root, RunBranch(), Ct));
        Assert.True(await manager.IsAncestorAsync(Repo.Root, c1, merge.ResultCommit!, Ct));
        foreach (var entry in await manager.ListRunRefsAsync(Repo.Root, TestRun, Ct))
        {
            await manager.DeleteRefAsync(Repo.Root, entry.Name, entry.Commit, Ct);
        }

        Assert.Empty(UserRepositoryState.Compare(before, await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)));
        Assert.Empty(await manager.ListRunRefsAsync(Repo.Root, TestRun, Ct));
    }
}
