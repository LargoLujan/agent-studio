using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>merge-tree, commit-tree and compare-and-swap update-ref, without ever touching a working tree.</summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeMergeTests : GitManagerTestBase
{
    private sealed record Setup(GitWorktreeManager Manager, WorktreeLease Integration, WorktreeLease T1, WorktreeLease T2);

    private async Task<Setup> StartAsync(IProcessRunner? runner = null)
    {
        var manager = NewManager(runner);
        return new Setup(
            manager,
            await EnsureAsync(manager, "_integration"),
            await EnsureAsync(manager, "T1"),
            await EnsureAsync(manager, "T2"));
    }

    private static string SharedWithFirstLine(string firstLine) =>
        firstLine + "\n" + string.Concat(Enumerable.Range(2, 7).Select(i => $"linea {i}\n"));

    [Fact]
    public async Task MergeInto_merges_independent_tasks_with_a_merge_commit_and_moves_only_the_target_ref()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "src/Calc/Multiply.cs", "// multiplica\n");
        var c2 = await CommitInAsync(s.Manager, s.T2, "tests/Calc.Tests/MultiplyTests.cs", "// prueba\n");

        var first = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1\n\nAgent-Studio-Attempt: T1/1", Ct);

        Assert.True(first.Merged);
        Assert.False(first.AlreadyMerged);
        Assert.Empty(first.ConflictPaths);
        Assert.NotNull(first.ResultCommit);
        Assert.Equal(first.ResultCommit, Repo.Run("rev-parse", s.Integration.RefName).Trim());
        Assert.Equal($"{Repo.BaseCommit} {c1}", Repo.Run("log", "-1", "--format=%P", first.ResultCommit!).Trim());
        Assert.Equal(c1, Repo.Run("rev-parse", s.T1.RefName).Trim());

        var second = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, first.ResultCommit!, c2, "Integra T2", Ct);

        Assert.True(second.Merged);
        Assert.Equal($"{first.ResultCommit} {c2}", Repo.Run("log", "-1", "--format=%P", second.ResultCommit!).Trim());
        var tree = Repo.Run("ls-tree", "-r", "--name-only", second.ResultCommit!);
        Assert.Contains("src/Calc/Multiply.cs", tree, StringComparison.Ordinal);
        Assert.Contains("tests/Calc.Tests/MultiplyTests.cs", tree, StringComparison.Ordinal);
        Assert.Equal("Integra T2", Repo.Run("log", "-1", "--format=%B", second.ResultCommit!).Trim());
        Assert.Equal(
            "Agent Studio|agent-studio@localhost",
            Repo.Run("log", "-1", "--format=%an|%ae", second.ResultCommit!).Trim());
        Assert.Contains("Agent-Studio-Attempt: T1/1", Repo.Run("log", "-1", "--format=%B", first.ResultCommit!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeInto_merges_changes_to_different_parts_of_the_same_file()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "shared.txt", SharedWithFirstLine("linea 1 (T1)"));
        WriteIn(s.T2, "shared.txt", string.Concat(Enumerable.Range(1, 7).Select(i => $"linea {i}\n")) + "linea 8 (T2)\n");
        var c2 = (await s.Manager.CommitAllAsync(s.T2, "cambia el final", Ct)).Commit;

        var first = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct);
        var second = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, first.ResultCommit!, c2, "Integra T2", Ct);

        Assert.True(second.Merged);
        Assert.Contains("Auto-merging shared.txt", second.Messages, StringComparison.Ordinal);
        var merged = Repo.Run("show", second.ResultCommit! + ":shared.txt");
        Assert.StartsWith("linea 1 (T1)\n", merged, StringComparison.Ordinal);
        Assert.EndsWith("linea 8 (T2)\n", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeInto_is_idempotent_when_the_source_is_already_part_of_the_target()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");
        var first = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct);

        // The caller crashed before recording the merge and repeats it with the values it had at the time.
        var repeated = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct);
        var withNewValue = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, first.ResultCommit!, c1, "Integra T1", Ct);

        foreach (var attempt in new[] { repeated, withNewValue })
        {
            Assert.False(attempt.Merged);
            Assert.True(attempt.AlreadyMerged);
            Assert.Equal(first.ResultCommit, attempt.ResultCommit);
            Assert.Empty(attempt.ConflictPaths);
        }

        Assert.Equal(first.ResultCommit, Repo.Run("rev-parse", s.Integration.RefName).Trim());
        Assert.Equal("3", Repo.Run("rev-list", "--count", first.ResultCommit!).Trim());
    }

    [Fact]
    public async Task MergeInto_reports_conflicts_with_their_paths_and_leaves_everything_untouched()
    {
        var s = await StartAsync();
        WriteIn(s.T1, "shared.txt", SharedWithFirstLine("linea 1 (T1)"));
        WriteIn(s.T1, "docs/guía de uso.md", "uno (T1)\ndos\ntres\n");
        var c1 = (await s.Manager.CommitAllAsync(s.T1, "T1 cambia la primera línea", Ct)).Commit;
        WriteIn(s.T2, "shared.txt", SharedWithFirstLine("linea 1 (T2)"));
        WriteIn(s.T2, "docs/guía de uso.md", "uno (T2)\ndos\ntres\n");
        WriteIn(s.T2, "solo-t2.txt", "no choca\n");
        var c2 = (await s.Manager.CommitAllAsync(s.T2, "T2 cambia la primera línea", Ct)).Commit;
        var merged = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct);
        var before = Repo.Snapshot();

        var attempt = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, merged.ResultCommit!, c2, "Integra T2", Ct);

        Assert.False(attempt.Merged);
        Assert.False(attempt.AlreadyMerged);
        Assert.Null(attempt.ResultCommit);
        Assert.Equal(["docs/guía de uso.md", "shared.txt"], attempt.ConflictPaths.Order(StringComparer.Ordinal));
        Assert.Contains("CONFLICT", attempt.Messages, StringComparison.Ordinal);

        // Nothing half-merged anywhere: the ref did not move, no merge state exists, no worktree changed.
        Assert.Equal(merged.ResultCommit, Repo.Run("rev-parse", s.Integration.RefName).Trim());
        Assert.Equal(before, Repo.Snapshot());
        Assert.False(File.Exists(Path.Combine(Repo.Root, ".git", "MERGE_HEAD")));
        foreach (var lease in new[] { s.Integration, s.T1, s.T2 })
        {
            Assert.Equal(string.Empty, Repo.Run("-C", lease.Path, "status", "--porcelain=v1"));
            Assert.False(File.Exists(Repo.Run("-C", lease.Path, "rev-parse", "--absolute-git-dir").Trim() + "/MERGE_HEAD"));
        }
    }

    [Fact]
    public async Task MergeInto_fails_with_a_stale_expected_target_and_does_not_move_the_ref()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");
        var c2 = await CommitInAsync(s.Manager, s.T2, "dos.txt", "2\n");
        var first = await s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct);

        // T2 is not merged yet, but the caller still believes the target is at the base commit.
        var stale = await Assert.ThrowsAsync<StaleRefException>(
            () => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c2, "Integra T2", Ct));

        Assert.Equal(Repo.BaseCommit, stale.Expected);
        Assert.Equal(first.ResultCommit, stale.Actual);
        Assert.Equal(s.Integration.RefName, stale.RefName);
        Assert.Equal(first.ResultCommit, Repo.Run("rev-parse", s.Integration.RefName).Trim());
    }

    [Fact]
    public async Task MergeInto_compare_and_swap_detects_a_ref_that_moves_while_the_merge_is_in_flight()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var s = await StartAsync(recorder);
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");
        var c2 = await CommitInAsync(s.Manager, s.T2, "dos.txt", "2\n");

        // A competitor moves the target right after the merge commit was created and before update-ref runs.
        recorder.AfterRun = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "commit-tree")
            {
                recorder.AfterRun = null;
                Repo.Run("update-ref", s.Integration.RefName, c2, Repo.BaseCommit);
            }
        };

        var stale = await Assert.ThrowsAsync<StaleRefException>(
            () => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct));

        Assert.Equal(Repo.BaseCommit, stale.Expected);
        Assert.Equal(c2, stale.Actual);
        Assert.Equal(c2, Repo.Run("rev-parse", s.Integration.RefName).Trim());
    }

    // A ref outside the namespace is an ownership violation.
    [Theory]
    [InlineData("refs/heads/main")]
    [InlineData("main")]
    [InlineData("HEAD")]
    [InlineData("refs/tags/v1")]
    [InlineData("refs/remotes/origin/main")]
    [InlineData("agentstudio")]
    [InlineData("refs/heads/agentstudio")]
    public async Task MergeInto_refuses_to_move_refs_outside_the_Agent_Studio_namespace(string targetRef)
    {
        var before = Repo.Snapshot();

        // The ref is validated before anything else, so the commits can be any valid ones.
        await Assert.ThrowsAsync<WorktreeOwnershipException>(
            () => NewManager().MergeIntoAsync(Repo.Root, targetRef, Repo.BaseCommit, Repo.BaseCommit, "Integra T1", Ct));

        Assert.Equal(before, Repo.Snapshot());
    }

    // A malformed ref inside the namespace (traversal, lock files, nothing after the prefix) is an invalid argument.
    [Theory]
    [InlineData("refs/agentstudio/../heads/main")]
    [InlineData("refs/heads/agentstudio/../main")]
    [InlineData("agentstudio/../main")]
    [InlineData("agentstudio/x.lock")]
    [InlineData("agentstudio/a//b")]
    [InlineData("agentstudio/-delete")]
    [InlineData("agentstudio/with space")]
    [InlineData("refs/agentstudio/")]
    public async Task MergeInto_refuses_malformed_refs_inside_the_Agent_Studio_namespace(string targetRef)
    {
        var before = Repo.Snapshot();

        await Assert.ThrowsAsync<ArgumentException>(
            () => NewManager().MergeIntoAsync(Repo.Root, targetRef, Repo.BaseCommit, Repo.BaseCommit, "Integra T1", Ct));

        Assert.Equal(before, Repo.Snapshot());
    }

    [Fact]
    public async Task MergeInto_works_on_a_visible_agentstudio_branch_created_with_EnsureRef()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");
        var branch = "agentstudio/run-" + TestRun.Value;
        var created = await s.Manager.EnsureRefAsync(Repo.Root, branch, Repo.BaseCommit, Ct);
        var again = await s.Manager.EnsureRefAsync(Repo.Root, branch, Repo.BaseCommit, Ct);

        var merge = await s.Manager.MergeIntoAsync(Repo.Root, branch, Repo.BaseCommit, c1, "Integra T1", Ct);

        Assert.Equal(Repo.BaseCommit, created);
        Assert.Equal(created, again);
        Assert.True(merge.Merged);
        Assert.Equal(merge.ResultCommit, Repo.Run("rev-parse", "refs/heads/" + branch).Trim());
        Assert.Contains(branch, Repo.Run("branch", "--list"), StringComparison.Ordinal);
        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", "main").Trim());
    }

    [Fact]
    public async Task EnsureRef_fails_closed_when_the_ref_exists_at_another_commit_and_refuses_user_refs()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");
        var branch = "agentstudio/run-" + TestRun.Value;
        await s.Manager.EnsureRefAsync(Repo.Root, branch, Repo.BaseCommit, Ct);

        await Assert.ThrowsAsync<WorktreeMismatchException>(() => s.Manager.EnsureRefAsync(Repo.Root, branch, c1, Ct));
        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => s.Manager.EnsureRefAsync(Repo.Root, "refs/heads/feature", c1, Ct));

        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", "refs/heads/" + branch).Trim());
        Assert.DoesNotContain("feature", Repo.Run("branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeInto_rejects_abbreviated_ids_and_unknown_commits()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");

        await Assert.ThrowsAsync<ArgumentException>(
            () => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1[..12], "m", Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, "HEAD", c1, "m", Ct));
        await Assert.ThrowsAsync<GitCommandException>(
            () => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, new string('b', 40), "m", Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, " ", Ct));

        Assert.Equal(Repo.BaseCommit, Repo.Run("rev-parse", s.Integration.RefName).Trim());
    }

    [Fact]
    public async Task MergeInto_fails_when_the_target_ref_does_not_exist()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");

        await Assert.ThrowsAsync<GitCommandException>(
            () => s.Manager.MergeIntoAsync(Repo.Root, "refs/agentstudio/20261015-a1b2c3/no-existe", Repo.BaseCommit, c1, "m", Ct));

        Assert.Equal(string.Empty, Repo.Run("for-each-ref", "refs/agentstudio/20261015-a1b2c3/no-existe"));
    }

    [Fact]
    public async Task Parallel_merges_into_the_same_target_are_serialised()
    {
        var s = await StartAsync();
        var c1 = await CommitInAsync(s.Manager, s.T1, "uno.txt", "1\n");
        var c2 = await CommitInAsync(s.Manager, s.T2, "dos.txt", "2\n");

        // Both start from the same expectation: exactly one wins, the other is told the target is stale.
        var results = await Task.WhenAll(
            Attempt(() => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c1, "Integra T1", Ct)),
            Attempt(() => s.Manager.MergeIntoAsync(Repo.Root, s.Integration.RefName, Repo.BaseCommit, c2, "Integra T2", Ct)));

        Assert.Equal(1, results.Count(r => r is MergeAttempt { Merged: true }));
        Assert.Equal(1, results.Count(r => r is StaleRefException));
    }

    private static async Task<object> Attempt(Func<Task<MergeAttempt>> merge)
    {
        try
        {
            return await merge();
        }
        catch (StaleRefException ex)
        {
            return ex;
        }
    }
}
