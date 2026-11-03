using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The candidate of an integration (ADR-0012): the hidden ref <c>refs/agentstudio/&lt;run&gt;/_candidate/&lt;task&gt;</c> the
/// integration merges into and verifies, and the compare-and-swap fast-forward that is the only way an integration moves
/// the run's branch. Every write is a compare-and-swap of an Agent Studio ref that never follows a symbolic ref, never
/// goes through a link and runs no hook.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeCandidateTests : GitManagerTestBase
{
    private static readonly TaskId Integration = new("I1");

    private static string RunBranch => $"refs/heads/agentstudio/run-{TestRun.Value}";

    private static string Candidate(string task = "I1") => $"refs/agentstudio/{TestRun.Value}/_candidate/{task}";

    private string? ReadRef(string name)
    {
        var (exitCode, output, _) = TempGitRepo.TryGit(Repo.Root, "rev-parse", "--verify", "--quiet", name);
        return exitCode == 0 ? output.Trim() : null;
    }

    private string NewCommit(string parent, string message) =>
        Repo.Run("commit-tree", Repo.BaseCommit + "^{tree}", "-p", parent, "-m", message).Trim();

    /// <summary>Hooks in the repository every worktree shares, which an agent with a shell can write. Each logs its name if it runs.</summary>
    private string PlantHooks()
    {
        foreach (var hook in new[] { "reference-transaction", "post-checkout", "post-merge", "pre-commit", "post-commit", "post-index-change" })
        {
            File.WriteAllText(Path.Combine(Repo.Root, ".git", "hooks", hook), HookScripts.Logging(hook));
        }

        return Path.Combine(Repo.Root, ".git", HookScripts.LogName);
    }

    /// <summary>
    /// Two tasks that each committed one file on the base, and the run's branch, at the base. The manager is given as the
    /// port the integrator uses.
    /// </summary>
    private async Task<(IWorktreeManager Manager, string C1, string C2)> TwoTasksAsync(IProcessRunner? runner = null)
    {
        var manager = NewManager(runner);
        var c1 = await CommitInAsync(manager, await EnsureAsync(manager, "T1"), "uno.txt", "1\n", "T1: uno");
        var c2 = await CommitInAsync(manager, await EnsureAsync(manager, "T2"), "dos.txt", "2\n", "T2: dos");
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        return (manager, c1, c2);
    }

    /// <summary>The candidate of <see cref="Integration"/> prepared at <paramref name="start"/>, with the sources merged into it in order.</summary>
    private async Task<string> CandidateWithAsync(IWorktreeManager manager, string start, params string[] sources)
    {
        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, start, Ct);
        foreach (var source in sources)
        {
            var merged = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, candidate, source, "Integra " + source[..8], Ct);
            Assert.True(merged.Merged, merged.Messages);
            candidate = merged.ResultCommit!;
        }

        return candidate;
    }

    // ---- preparing the candidate --------------------------------------------------------------------------------

    [Fact]
    public async Task PrepareCandidate_creates_the_candidate_at_the_head_of_the_run_branch_and_moves_nothing_else()
    {
        var (manager, _, _) = await TwoTasksAsync();
        var before = Repo.Snapshot();

        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);

        Assert.Equal(Repo.BaseCommit, candidate);
        Assert.Equal(Repo.BaseCommit, ReadRef(Candidate()));
        Assert.Equal(Repo.BaseCommit, ReadRef(RunBranch));
        Assert.Equal(before, Repo.Snapshot());
    }

    [Fact]
    public async Task PrepareCandidate_keeps_a_candidate_that_already_holds_the_head_so_a_resumed_integration_reuses_its_merges()
    {
        var (manager, c1, _) = await TwoTasksAsync();
        var built = await CandidateWithAsync(manager, Repo.BaseCommit, c1);

        var again = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);

        Assert.Equal(built, again);
        Assert.Equal(built, ReadRef(Candidate()));
    }

    [Fact]
    public async Task PrepareCandidate_moves_a_candidate_built_on_an_older_head_of_the_run_branch_back_to_its_head()
    {
        var (manager, c1, c2) = await TwoTasksAsync();

        // This task's candidate holds T2 on the base; meanwhile another integration verified T1 and moved the branch.
        var stale = await CandidateWithAsync(manager, Repo.BaseCommit, c2);
        var other = new TaskId("I0");
        var start = await manager.PrepareCandidateAsync(Repo.Root, TestRun, other, Repo.BaseCommit, Ct);
        var head = (await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, other, start, c1, "Integra T1", Ct)).ResultCommit!;
        await manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, head, Ct);

        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, head, Ct);

        Assert.Equal(head, candidate);
        Assert.Equal(head, ReadRef(Candidate()));
        Assert.NotEqual(stale, candidate);
        Assert.Equal(head, ReadRef(RunBranch));
    }

    [Fact]
    public async Task PrepareCandidate_fails_for_a_start_that_does_not_exist_and_creates_nothing()
    {
        IWorktreeManager manager = NewManager();

        await Assert.ThrowsAsync<GitCommandException>(() => manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, new string('c', 40), Ct));

        Assert.Null(ReadRef(Candidate()));
    }

    // ---- merging into the candidate -----------------------------------------------------------------------------

    [Fact]
    public async Task MergeIntoCandidate_merges_into_the_candidate_with_merge_commits_and_the_run_branch_never_moves()
    {
        var (manager, c1, c2) = await TwoTasksAsync();
        var before = Repo.Snapshot();
        var start = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);

        var first = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, start, c1, "Integra T1\n\nAgent-Studio-Source: T1", Ct);
        var second = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, first.ResultCommit!, c2, "Integra T2", Ct);

        Assert.True(first.Merged);
        Assert.True(second.Merged);
        Assert.Equal(second.ResultCommit, ReadRef(Candidate()));
        Assert.Equal($"{Repo.BaseCommit} {c1}", Repo.Run("log", "-1", "--format=%P", first.ResultCommit!).Trim());
        Assert.Equal($"{first.ResultCommit} {c2}", Repo.Run("log", "-1", "--format=%P", second.ResultCommit!).Trim());
        Assert.Equal("Agent Studio|agent-studio@localhost", Repo.Run("log", "-1", "--format=%an|%ae", second.ResultCommit!).Trim());
        Assert.Contains("Agent-Studio-Source: T1", Repo.Run("log", "-1", "--format=%B", first.ResultCommit!), StringComparison.Ordinal);
        var files = Repo.Run("ls-tree", "-r", "--name-only", second.ResultCommit!);
        Assert.Contains("uno.txt", files, StringComparison.Ordinal);
        Assert.Contains("dos.txt", files, StringComparison.Ordinal);

        // Only the candidate moved: the run's branch is still at the base.
        Assert.Equal(Repo.BaseCommit, ReadRef(RunBranch));
        Assert.Equal(before, Repo.Snapshot());
    }

    [Fact]
    public async Task MergeIntoCandidate_of_a_source_already_in_the_candidate_gives_the_same_commit_and_writes_nothing()
    {
        var (manager, c1, _) = await TwoTasksAsync();
        var start = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);
        var first = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, start, c1, "Integra T1", Ct);

        // The integration stopped after this merge and runs again with the values it had then (a resume).
        var repeated = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, start, c1, "Integra T1", Ct);

        Assert.False(repeated.Merged);
        Assert.True(repeated.AlreadyMerged);
        Assert.Equal(first.ResultCommit, repeated.ResultCommit);
        Assert.Equal(first.ResultCommit, ReadRef(Candidate()));
        Assert.Equal("3", Repo.Run("rev-list", "--count", first.ResultCommit!).Trim());
    }

    [Fact]
    public async Task MergeIntoCandidate_reports_a_conflict_with_its_paths_and_writes_nothing()
    {
        var git = NewManager();
        var shared = File.ReadAllText(Path.Combine(Repo.Root, "shared.txt"));
        var c1 = await CommitInAsync(git, await EnsureAsync(git, "T1"), "shared.txt", shared.Replace("linea 1\n", "linea 1 (T1)\n", StringComparison.Ordinal));
        var c2 = await CommitInAsync(git, await EnsureAsync(git, "T2"), "shared.txt", shared.Replace("linea 1\n", "linea 1 (T2)\n", StringComparison.Ordinal));
        IWorktreeManager manager = git;
        await manager.EnsureRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, Ct);
        var start = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);
        var first = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, start, c1, "Integra T1", Ct);
        var before = Repo.Snapshot();

        var attempt = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, first.ResultCommit!, c2, "Integra T2", Ct);

        Assert.False(attempt.Merged);
        Assert.False(attempt.AlreadyMerged);
        Assert.Null(attempt.ResultCommit);
        Assert.Equal(["shared.txt"], attempt.ConflictPaths);
        Assert.Contains("CONFLICT", attempt.Messages, StringComparison.Ordinal);
        Assert.Equal(first.ResultCommit, ReadRef(Candidate()));
        Assert.Equal(Repo.BaseCommit, ReadRef(RunBranch));
        Assert.Equal(before, Repo.Snapshot());
    }

    [Fact]
    public async Task MergeIntoCandidate_is_a_compare_and_swap_and_a_stale_expectation_moves_nothing()
    {
        var (manager, c1, c2) = await TwoTasksAsync();
        var start = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);
        var first = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, start, c1, "Integra T1", Ct);

        // T2 is not merged yet, but the caller still believes the candidate is at the base.
        var stale = await Assert.ThrowsAsync<StaleRefException>(
            () => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, c2, "Integra T2", Ct));

        Assert.Equal((Candidate(), Repo.BaseCommit, first.ResultCommit), (stale.RefName, stale.Expected, stale.Actual));
        Assert.Equal(first.ResultCommit, ReadRef(Candidate()));
    }

    [Fact]
    public async Task MergeIntoCandidate_needs_a_prepared_candidate_and_creates_nothing_without_one()
    {
        var (manager, c1, _) = await TwoTasksAsync();

        var stale = await Assert.ThrowsAsync<StaleRefException>(
            () => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, c1, "Integra T1", Ct));

        Assert.Null(stale.Actual);
        Assert.Null(ReadRef(Candidate()));
        Assert.Equal(Repo.BaseCommit, ReadRef(RunBranch));
    }

    [Fact]
    public async Task A_symbolic_ref_planted_as_the_candidate_is_replaced_and_the_branch_it_names_never_moves()
    {
        var (manager, c1, _) = await TwoTasksAsync();
        Repo.Run("branch", "feature");
        Repo.Run("symbolic-ref", Candidate(), "refs/heads/feature");

        var start = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);
        var merged = await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, start, c1, "Integra T1", Ct);

        Assert.True(merged.Merged);
        Assert.Equal(Repo.BaseCommit, ReadRef("refs/heads/feature"));
        Assert.Equal(merged.ResultCommit, ReadRef(Candidate()));
        Assert.Equal(1, TempGitRepo.TryGit(Repo.Root, "symbolic-ref", "--quiet", Candidate()).ExitCode);
    }

    // ---- the fast-forward of the run's branch -------------------------------------------------------------------

    [Fact]
    public async Task FastForwardRunBranch_moves_the_branch_to_a_candidate_that_descends_from_its_head_and_nothing_else()
    {
        var (manager, c1, c2) = await TwoTasksAsync();
        var candidate = await CandidateWithAsync(manager, Repo.BaseCommit, c1, c2);
        var before = await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);

        await manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, candidate, Ct);

        Assert.Equal(candidate, ReadRef(RunBranch));
        Assert.Equal(candidate, ReadRef(Candidate()));
        Assert.Empty(UserRepositoryState.Compare(before, await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)));

        // Asked again with the same values, as after a crash right after it: there is nothing left to do.
        await manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, candidate, Ct);
        Assert.Equal(candidate, ReadRef(RunBranch));
    }

    [Fact]
    public async Task FastForwardRunBranch_refuses_a_commit_that_does_not_descend_from_the_head_and_moves_nothing()
    {
        var (manager, c1, c2) = await TwoTasksAsync();
        await manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, c1, Ct);

        // c2 does not contain c1: moving the branch there would drop it.
        var refusal = await Assert.ThrowsAsync<NotFastForwardException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, c1, c2, Ct));

        Assert.Equal((RunBranch, c1, c2), (refusal.RefName, refusal.From, refusal.To));
        Assert.Equal(c1, ReadRef(RunBranch));
    }

    [Fact]
    public async Task FastForwardRunBranch_refuses_when_the_branch_moved_meanwhile_and_leaves_it_where_it_was_moved()
    {
        var (manager, c1, _) = await TwoTasksAsync();
        var candidate = await CandidateWithAsync(manager, Repo.BaseCommit, c1);

        // Something with a shell moves the run's branch while the candidate is being verified.
        var elsewhere = NewCommit(Repo.BaseCommit, "de otro");
        Repo.Run("update-ref", RunBranch, elsewhere, Repo.BaseCommit);

        var stale = await Assert.ThrowsAsync<StaleRefException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, candidate, Ct));

        Assert.Equal((RunBranch, Repo.BaseCommit, elsewhere), (stale.RefName, stale.Expected, stale.Actual));
        Assert.Equal(elsewhere, ReadRef(RunBranch));
    }

    [Fact]
    public async Task FastForwardRunBranch_is_a_compare_and_swap_that_catches_a_branch_moved_between_its_checks_and_the_update()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var (manager, c1, _) = await TwoTasksAsync(recorder);
        var candidate = await CandidateWithAsync(manager, Repo.BaseCommit, c1);
        var elsewhere = NewCommit(Repo.BaseCommit, "de otro");

        // A competitor moves the branch right after the descendant check and before update-ref runs.
        var moved = false;
        recorder.AfterRun = spec =>
        {
            if (!moved && RecordingProcessRunner.SubCommand(spec) == "merge-base")
            {
                moved = true;
                TempGitRepo.Git(Repo.Root, "update-ref", RunBranch, elsewhere);
            }
        };

        var stale = await Assert.ThrowsAsync<StaleRefException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, candidate, Ct));

        Assert.True(moved);
        Assert.Equal(elsewhere, stale.Actual);
        Assert.Equal(elsewhere, ReadRef(RunBranch));
    }

    [Fact]
    public async Task FastForwardRunBranch_needs_the_run_branch_and_never_creates_it()
    {
        IWorktreeManager manager = NewManager();
        var child = NewCommit(Repo.BaseCommit, "hijo");

        var stale = await Assert.ThrowsAsync<StaleRefException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, child, Ct));

        Assert.Null(stale.Actual);
        Assert.Null(ReadRef(RunBranch));
    }

    // ---- validation before git runs, links and how git runs -----------------------------------------------------

    [Theory]
    [InlineData("_base")]
    [InlineData("_integration")]
    [InlineData("-x")]
    [InlineData("1abc")]
    [InlineData("I1/T2")]
    [InlineData("I1.lock")]
    [InlineData("I 1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Tasks_that_are_not_task_ids_are_refused_before_git_runs(string task)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        IWorktreeManager manager = NewManager(recorder);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.PrepareCandidateAsync(Repo.Root, TestRun, new TaskId(task), Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, new TaskId(task), Repo.BaseCommit, Repo.BaseCommit, "Integra", Ct));

        Assert.Empty(recorder.Specs);
    }

    [Fact]
    public async Task Writes_need_full_commit_ids_a_valid_run_a_task_and_a_message_before_git_runs()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        IWorktreeManager manager = NewManager(recorder);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, "HEAD", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit[..12], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.PrepareCandidateAsync(Repo.Root, default, Integration, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.PrepareCandidateAsync(Repo.Root, TestRun, default, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, "main", Repo.BaseCommit, "Integra", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, "refs/heads/main", "Integra", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Repo.BaseCommit, "con\0nul", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.MergeIntoCandidateAsync(Repo.Root, new RunId("_x"), Integration, Repo.BaseCommit, Repo.BaseCommit, "Integra", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, "main", Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, "--force", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.FastForwardRunBranchAsync(Repo.Root, new RunId("-x"), Repo.BaseCommit, Repo.BaseCommit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.FastForwardRunBranchAsync(Repo.Root, default, Repo.BaseCommit, Repo.BaseCommit, Ct));

        Assert.Empty(recorder.Specs);
    }

    [Fact]
    public async Task A_junction_in_place_of_the_candidates_folder_is_refused_and_no_user_branch_is_written()
    {
        // With the candidates' folder pointing at refs/heads, the candidate of I1 would be the user's branch I1.
        var (manager, c1, _) = await TwoTasksAsync();
        Repo.Run("branch", "I1");
        var before = await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);
        var junction = Path.Combine(Repo.Root, ".git", "refs", "agentstudio", TestRun.Value, "_candidate");
        CreateJunction(junction, Path.Combine(Repo.Root, ".git", "refs", "heads"));
        try
        {
            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, c1, "Integra T1", Ct)),

                // S-01 to S-03: the status of the capture walks the git directory first, so it refuses the junction too.
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal(Repo.BaseCommit, ReadRef("refs/heads/I1"));
        }
        finally
        {
            Directory.Delete(junction);
        }

        Assert.Empty(UserRepositoryState.Compare(before, await manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct)));
    }

    [Fact]
    public async Task A_junction_in_place_of_the_agentstudio_branches_is_refused_before_the_run_branch_moves()
    {
        IWorktreeManager manager = NewManager();
        var child = NewCommit(Repo.BaseCommit, "hijo");
        var elsewhere = Path.Combine(Repo.WorktreeRoot, "..", "otras-refs");
        Directory.CreateDirectory(elsewhere);
        var branchFile = Path.Combine(elsewhere, "run-" + TestRun.Value);
        File.WriteAllText(branchFile, Repo.BaseCommit + "\n");
        var junction = Path.Combine(Repo.Root, ".git", "refs", "heads", "agentstudio");
        CreateJunction(junction, elsewhere);
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, child, Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(Repo.BaseCommit + "\n", File.ReadAllText(branchFile));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task Candidate_operations_run_no_hook_name_the_git_directory_never_follow_a_symbolic_ref_and_end_the_options()
    {
        var log = PlantHooks();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var (manager, c1, c2) = await TwoTasksAsync(recorder);
        var start = recorder.Specs.Count;

        var candidate = await manager.PrepareCandidateAsync(Repo.Root, TestRun, Integration, Repo.BaseCommit, Ct);
        candidate = (await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, candidate, c1, "Integra T1", Ct)).ResultCommit!;
        candidate = (await manager.MergeIntoCandidateAsync(Repo.Root, TestRun, Integration, candidate, c2, "Integra T2", Ct)).ResultCommit!;
        await manager.FastForwardRunBranchAsync(Repo.Root, TestRun, Repo.BaseCommit, candidate, Ct);

        Assert.False(File.Exists(log), "A hook of the repository ran: " + (File.Exists(log) ? File.ReadAllText(log) : string.Empty));
        var specs = recorder.Specs.Skip(start).ToList();
        var noHooks = "core.hooksPath=" + Path.Combine(Repo.WorktreeRoot, ".no-hooks");
        Assert.All(specs, s => Assert.Contains(noHooks, s.Arguments));
        Assert.All(specs, s => Assert.Equal("0", s.Environment!["GIT_TERMINAL_PROMPT"]));
        Assert.All(specs, s => Assert.True(s.Environment!.TryGetValue("GIT_DIR", out var value) && value is null, "GIT_DIR must be removed from the environment."));

        // Once the repository is found from the project root, every command names its git directory explicitly.
        var gitDir = "--git-dir=" + Path.Combine(Repo.Root, ".git");
        var onTheRepository = specs.Where(s => !s.Arguments.Contains("--show-toplevel")).ToList();
        Assert.NotEmpty(onTheRepository);
        Assert.All(onTheRepository, s => Assert.Contains(s.Arguments, a => string.Equals(a, gitDir, StringComparison.OrdinalIgnoreCase)));

        // Four writes: the candidate created, two merges into it, and the one fast-forward of the run's branch.
        var updates = specs.Where(s => RecordingProcessRunner.SubCommand(s) == "update-ref").ToList();
        Assert.Equal(4, updates.Count);
        Assert.All(updates, s => Assert.Contains("--no-deref", s.Arguments));
        Assert.Single(updates, s => s.Arguments.Contains(RunBranch));
        foreach (var spec in onTheRepository.Where(s => RecordingProcessRunner.SubCommand(s) is "update-ref" or "merge-base" or "merge-tree" or "commit-tree" or "rev-parse"))
        {
            var arguments = spec.Arguments.ToList();
            var end = arguments.IndexOf("--end-of-options");
            Assert.True(end > 0, $"{RecordingProcessRunner.SubCommand(spec)} must end its options before the revisions.");
            Assert.All(arguments.Skip(end + 1), a => Assert.False(a.StartsWith('-'), $"'{a}' comes after --end-of-options."));
        }

        // The merge messages go on standard input, never on the command line.
        var commits = specs.Where(s => RecordingProcessRunner.SubCommand(s) == "commit-tree").ToList();
        Assert.Equal(["Integra T1\n", "Integra T2\n"], commits.Select(s => s.StandardInput));
        Assert.All(commits, s => Assert.DoesNotContain(s.Arguments, a => a.Contains("Integra", StringComparison.Ordinal)));
    }
}
