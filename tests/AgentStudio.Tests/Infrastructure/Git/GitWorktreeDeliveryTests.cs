using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Security;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The approved delivery, the only operation that changes the user's branch (ADR-0012). It needs the approval it was
/// given, does nothing when HEAD left the approved branch or commit or when the user has work in a delivered path, and
/// otherwise runs git like any other content command (R3-05): with the user's identity, but without the user's hooks or
/// signing, and through the same allowlist of callbacks.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GitWorktreeDeliveryTests : GitManagerTestBase
{
    private const string Message = "Agent Studio: entrega de la ejecución\n\nIntegra agentstudio/run-20261015-a1b2c3.";

    private static ApprovalRequest Request(string binding) =>
        new("deliver-integration", "Entregar la ejecución", RiskLevel.Critical, null, [], ProjectId, TestRun.Value, Binding: binding);

    private static ApprovalDecision Yes(string binding) => new(true, "usuario", DateTimeOffset.UtcNow, null, binding);

    private string NoHooks => "core.hooksPath=" + Path.Combine(Repo.WorktreeRoot, ".no-hooks");

    /// <summary>A delivery as <c>DeliveryService</c> hands it over: the request bound to exactly it, and the gate's yes answering that request.</summary>
    private ApprovedDelivery Delivery(string commit, DeliveryMode mode = DeliveryMode.FastForwardOnly, string? expectedHead = null, string branch = "main", string? repositoryRoot = null)
    {
        var head = expectedHead ?? Repo.Head;
        var root = repositoryRoot ?? Repo.Root;
        var binding = DeliveryApproval.Bind(root, branch, mode, commit, head);
        return new(root, branch, head, commit, mode, Message, Request(binding), Yes(binding));
    }

    private Task<UserRepositoryState> CaptureAsync(GitWorktreeManager manager) => manager.CaptureUserRepositoryStateAsync(Repo.Root, Ct);

    /// <summary>A task commit on the base, as the system makes it: the commit a delivery takes to the user's branch.</summary>
    private async Task<string> TaskCommitAsync(GitWorktreeManager manager, params (string Path, string Content)[] files)
    {
        var lease = await EnsureAsync(manager, "T1");
        foreach (var (path, content) in files)
        {
            WriteIn(lease, path, content);
        }

        var commit = await manager.CommitAllAsync(lease, "T1: cambios", Ct);
        Assert.True(commit.Committed);
        return commit.Commit;
    }

    /// <summary>The user keeps working on main: a commit of their own that touches <paramref name="file"/>.</summary>
    private string UserCommit(string file, string content)
    {
        Repo.Write(file, content);
        Repo.Run("add", "--", file);
        Repo.Run("commit", "-q", "-m", "trabajo del usuario");
        return Repo.Head;
    }

    private void PlantUserHook(string name, bool failing = false) =>
        Repo.Write(".git/hooks/" + name, failing ? HookScripts.Failing(name) : HookScripts.Logging(name));

    private string HookLog => Path.Combine(Repo.Root, ".git", HookScripts.LogName);

    // ---- approval -----------------------------------------------------------------------------------------------

    public static TheoryData<string> MissingApprovals() =>
        ["denied", "nobody", "destructive", "low", "no-request", "no-decision", "no-binding", "answers-another-request"];

    [Theory]
    [MemberData(nameof(MissingApprovals))]
    public async Task Without_an_explicit_critical_yes_from_someone_named_to_this_request_nothing_runs_and_nothing_changes(string problem)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var before = await CaptureAsync(manager);
        var specs = recorder.Specs.Count;
        var approved = Delivery(commit);
        var delivery = problem switch
        {
            "denied" => approved with { Decision = approved.Decision with { Approved = false, Comment = "todavía no" } },
            "nobody" => approved with { Decision = approved.Decision with { DecidedBy = " " } },
            "destructive" => approved with { Request = approved.Request with { Risk = RiskLevel.Destructive } },
            "low" => approved with { Request = approved.Request with { Risk = RiskLevel.Low } },
            "no-request" => approved with { Request = null! },
            "no-decision" => approved with { Decision = null! },
            "no-binding" => approved with { Request = approved.Request with { Binding = null }, Decision = approved.Decision with { Answers = null } },

            // A yes the gate gave to another request for the very same delivery.
            _ => approved with { Decision = approved.Decision with { Answers = DeliveryApproval.Bind(Repo.Root, "main", DeliveryMode.FastForwardOnly, commit, Repo.Head) } },
        };

        await Assert.ThrowsAsync<DeliveryNotApprovedException>(() => manager.DeliverToUserBranchAsync(delivery, Ct));

        Assert.Equal(specs, recorder.Specs.Count);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }

    public static TheoryData<string> OtherDeliveries() => ["commit", "branch", "mode", "head", "repository", "request-rebound"];

    [Theory]
    [MemberData(nameof(OtherDeliveries))]
    public async Task An_approval_for_one_delivery_never_authorizes_another_and_nothing_runs(string change)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var approvedCommit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var t2 = await EnsureAsync(manager, "T2");
        var otherCommit = await CommitInAsync(manager, t2, "otro.txt", "otro\n");
        Repo.Run("branch", "feature");
        using var otherRepo = TempGitRepo.Create();
        var approved = Delivery(approvedCommit);
        var reused = change switch
        {
            "commit" => approved with { Commit = otherCommit },
            "branch" => approved with { TargetBranch = "feature" },
            "mode" => approved with { Mode = DeliveryMode.MergeCommit },
            "head" => approved with { ExpectedHead = otherCommit },
            "repository" => approved with { RepositoryRoot = otherRepo.Root },

            // The request is re-bound to the other commit, but the yes answers the request that was asked.
            _ => approved with
            {
                Commit = otherCommit,
                Request = approved.Request with { Binding = DeliveryApproval.Bind(Repo.Root, "main", DeliveryMode.FastForwardOnly, otherCommit, Repo.Head) },
            },
        };
        var before = await CaptureAsync(manager);
        var specs = recorder.Specs.Count;

        await Assert.ThrowsAsync<DeliveryNotApprovedException>(() => manager.DeliverToUserBranchAsync(reused, Ct));

        Assert.Equal(specs, recorder.Specs.Count);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
        Assert.Equal(otherRepo.BaseCommit, otherRepo.Head);
    }

    [Fact]
    public async Task An_approval_is_used_once_in_the_process_even_by_another_manager()
    {
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var approved = Delivery(commit);
        Assert.Equal(UserBranchDeliveryStatus.Delivered, (await manager.DeliverToUserBranchAsync(approved, Ct)).Status);
        Repo.Run("reset", "-q", "--hard", Repo.BaseCommit);
        var recorder = new RecordingProcessRunner(new ProcessRunner());

        await Assert.ThrowsAsync<DeliveryNotApprovedException>(() => NewManager(recorder).DeliverToUserBranchAsync(approved, Ct));

        Assert.Empty(recorder.Specs);
        Assert.Equal(Repo.BaseCommit, Repo.Head);
    }

    // ---- fast-forward -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_approved_fast_forward_moves_only_the_users_branch_with_ff_only_and_without_the_users_hooks()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("src/Calc/Multiply.cs", "// multiplica\n"));
        Repo.Run("tag", "v1");
        Repo.Run("branch", "feature");
        PlantUserHook("post-merge");
        var before = await CaptureAsync(manager);
        var start = recorder.Specs.Count;

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Equal(commit, outcome.HeadCommit);
        Assert.Equal(commit, Repo.Head);
        Assert.Equal("// multiplica\n", File.ReadAllText(Path.Combine(Repo.Root, "src", "Calc", "Multiply.cs")));

        // Only HEAD, main and what main's checkout staged changed: no other branch, tag, stash or status line.
        var differences = UserRepositoryState.Compare(before, await CaptureAsync(manager));
        Assert.Equal($"HEAD moved from {Repo.BaseCommit} to {commit}.", differences[0]);
        Assert.Equal($"Branch 'refs/heads/main' moved from {Repo.BaseCommit} to {commit}.", differences[1]);
        Assert.All(differences.Skip(2), d => Assert.StartsWith("Index changed", d, StringComparison.Ordinal));

        // R3-05: the delivery runs like any other content command: no hook of the user runs, nothing is signed, and
        // the user's identity is not overridden (no user.* or author.* -c).
        Assert.False(File.Exists(HookLog), "the user's post-merge hook ran during delivery.");
        var specs = recorder.Specs.Skip(start).ToList();
        var merge = Assert.Single(specs, s => RecordingProcessRunner.SubCommand(s) == "merge");
        Assert.Equal(["merge", "--ff-only", "--no-autostash", "--end-of-options", commit], merge.Arguments.SkipWhile(a => a != "merge"));
        Assert.Contains("commit.gpgsign=false", merge.Arguments);
        Assert.Contains("tag.gpgsign=false", merge.Arguments);
        Assert.DoesNotContain(merge.Arguments, a => a.StartsWith("user.", StringComparison.Ordinal) || a.StartsWith("author.", StringComparison.Ordinal));
        Assert.Contains(merge.Arguments, a => string.Equals(a, "--git-dir=" + Path.Combine(Repo.Root, ".git"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(merge.Arguments, a => string.Equals(a, "--work-tree=" + Repo.Root, StringComparison.OrdinalIgnoreCase));
        Assert.All(specs, s => Assert.Contains(NoHooks, s.Arguments));
    }

    [Fact]
    public async Task An_approved_delivery_does_not_run_a_users_hook_that_runs_a_repository_script_the_diff_changed()
    {
        // R3-05: the user trusts a post-merge hook that runs a versioned script of the repository. The delivered diff
        // changes that script; a delivery with the user's hooks would run the delivered version. It must not run at all.
        var mark = Path.Combine(Path.GetTempPath(), "as-centinela-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        try
        {
            Repo.Write("tools/postmerge.sh", "#!/bin/sh\nexit 0\n");
            Repo.Run("add", "-A");
            Repo.Run("commit", "-q", "-m", "script del hook");
            var head = Repo.Head;
            Repo.Write(".git/hooks/post-merge", "#!/bin/sh\nsh tools/postmerge.sh\n");

            var manager = NewManager();
            var lease = await EnsureAsync(manager, "T1", head);
            WriteIn(lease, "tools/postmerge.sh", "#!/bin/sh\necho ran >> '" + mark.Replace('\\', '/') + "'\n");
            var commit = (await manager.CommitAllAsync(lease, "T1: cambia el script", Ct)).Commit;

            var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, expectedHead: head), Ct);

            Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
            Assert.Equal(commit, Repo.Head);
            Assert.False(File.Exists(mark), "the user's post-merge hook ran the delivered script.");
        }
        finally
        {
            File.Delete(mark);
        }
    }

    [Fact]
    public async Task A_control_shows_the_same_hook_runs_the_delivered_script_under_a_plain_git_merge()
    {
        // Control for the test above: with plain git (the user's hooks), the same setup does run the delivered script, so
        // the absence of the mark there proves the delivery skipped the hook, not that the hook could never run.
        var mark = Path.Combine(Path.GetTempPath(), "as-centinela-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        try
        {
            Repo.Write("tools/postmerge.sh", "#!/bin/sh\nexit 0\n");
            Repo.Run("add", "-A");
            Repo.Run("commit", "-q", "-m", "script del hook");
            var head = Repo.Head;
            Repo.Write(".git/hooks/post-merge", "#!/bin/sh\nsh tools/postmerge.sh\n");

            var manager = NewManager();
            var lease = await EnsureAsync(manager, "T1", head);
            WriteIn(lease, "tools/postmerge.sh", "#!/bin/sh\necho ran >> '" + mark.Replace('\\', '/') + "'\n");
            var commit = (await manager.CommitAllAsync(lease, "T1: cambia el script", Ct)).Commit;

            Repo.Run("merge", "-q", "--ff-only", commit);

            Assert.True(File.Exists(mark), "Control: plain git merge did not run the post-merge hook, so the test above proves nothing.");
        }
        finally
        {
            File.Delete(mark);
        }
    }

    [Fact]
    public async Task A_merge_commit_delivery_is_not_signed_even_when_the_user_signs_by_default()
    {
        // R3-05: signing is off for the delivery. The user signs every commit with a program that would fail if it ran
        // as gpg (git-lfs, outside the repository, so the allowlist accepts it): the merge commit is made, unsigned.
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "del agente\n"));
        var userHead = UserCommit("user.txt", "trabajo del usuario\n");
        Repo.Run("config", "commit.gpgsign", "true");
        Repo.Run("config", "gpg.program", "git-lfs");

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, userHead), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Equal($"{userHead} {commit}", Repo.Run("log", "-1", "--format=%P", "HEAD").Trim());
        Assert.Equal("N", Repo.Run("-c", "gpg.program=git-lfs", "log", "-1", "--format=%G?", "HEAD").Trim());
    }

    [Fact]
    public async Task A_fast_forward_that_is_no_longer_possible_is_refused_by_git_and_changes_nothing()
    {
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var userHead = UserCommit("user.txt", "trabajo del usuario\n");
        var before = await CaptureAsync(manager);

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, expectedHead: userHead), Ct);

        Assert.Equal(UserBranchDeliveryStatus.GitFailed, outcome.Status);
        Assert.Equal(userHead, outcome.HeadCommit);
        Assert.NotEmpty(outcome.Output);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }

    [Fact]
    public async Task Delivering_a_commit_the_branch_already_contains_changes_nothing()
    {
        var manager = NewManager();
        var before = await CaptureAsync(manager);

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(Repo.BaseCommit), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }

    [Fact]
    public async Task A_checkout_between_the_check_and_the_merge_makes_a_fast_forward_fail_closed_and_moves_no_branch()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("src/Calc/Multiply.cs", "// multiplica\n"));
        var approvedHead = Repo.Head;

        // Another branch of the user, at the approved head: a plain `git merge --ff-only` run while it is checked out
        // would fast-forward it to the delivered commit instead of main.
        Repo.Run("branch", "feature");

        // The user switches HEAD to that branch in the window after the delivery's last read and before the merge.
        var switched = false;
        recorder.AfterRun = spec =>
        {
            if (!switched && RecordingProcessRunner.SubCommand(spec) == "status")
            {
                switched = true;
                Repo.Run("checkout", "-q", "feature");
            }
        };

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, expectedHead: approvedHead), Ct);

        Assert.Equal(UserBranchDeliveryStatus.RepositoryChanged, outcome.Status);
        Assert.Equal(approvedHead, Repo.Run("rev-parse", "refs/heads/main").Trim());
        Assert.Equal(approvedHead, Repo.Run("rev-parse", "refs/heads/feature").Trim());
        Assert.DoesNotContain(recorder.Specs, s => RecordingProcessRunner.SubCommand(s) == "merge");
    }

    [Fact]
    public async Task A_checkout_between_the_check_and_the_merge_makes_a_merge_commit_fail_closed_and_moves_no_branch()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "del agente\n"));
        var userHead = UserCommit("user.txt", "trabajo del usuario\n");
        Repo.Run("branch", "feature", userHead);

        var switched = false;
        recorder.AfterRun = spec =>
        {
            if (!switched && RecordingProcessRunner.SubCommand(spec) == "status")
            {
                switched = true;
                Repo.Run("checkout", "-q", "feature");
            }
        };

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, userHead), Ct);

        Assert.Equal(UserBranchDeliveryStatus.RepositoryChanged, outcome.Status);
        Assert.Equal(userHead, Repo.Run("rev-parse", "refs/heads/main").Trim());
        Assert.Equal(userHead, Repo.Run("rev-parse", "refs/heads/feature").Trim());
        Assert.False(File.Exists(Path.Combine(Repo.Root, ".git", "MERGE_HEAD")));
    }

    // ---- compare-and-swap on the user's HEAD ------------------------------------------------------------------

    [Theory]
    [InlineData("moved")]
    [InlineData("switched")]
    [InlineData("detached")]
    public async Task A_users_HEAD_that_moved_or_switched_after_the_approval_is_never_touched(string change)
    {
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var approvedHead = Repo.Head;
        switch (change)
        {
            case "moved":
                UserCommit("user.txt", "trabajo del usuario\n");
                break;
            case "switched":
                Repo.Run("checkout", "-q", "-b", "feature");
                break;
            default:
                Repo.Run("checkout", "-q", "--detach");
                break;
        }

        var before = await CaptureAsync(manager);

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, expectedHead: approvedHead), Ct);

        Assert.Equal(UserBranchDeliveryStatus.RepositoryChanged, outcome.Status);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }

    // ---- the user's work in delivered paths ---------------------------------------------------------------------

    [Theory]
    [InlineData("modified", "src/Calc/Calc.cs")]
    [InlineData("staged", "src/Calc/Calc.cs")]
    [InlineData("untracked", "datos/cache.txt")]
    [InlineData("ignored", "datos/cache.txt")]
    [InlineData("ignored-folder", "datos/cache.txt")]
    public async Task Work_of_the_user_in_a_delivered_path_stops_the_delivery_and_is_kept(string kind, string path)
    {
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("src/Calc/Calc.cs", "// del agente\n"), ("datos/cache.txt", "del agente\n"));
        var exclude = Path.Combine(Repo.Root, ".git", "info", "exclude");
        if (kind == "ignored")
        {
            File.AppendAllText(exclude, "datos/cache.txt\n");
        }
        else if (kind == "ignored-folder")
        {
            File.AppendAllText(exclude, "datos/\n");
        }

        Repo.Write(path, "del usuario\n");
        if (kind == "staged")
        {
            Repo.Run("add", "--", path);
        }

        var before = await CaptureAsync(manager);

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit), Ct);

        Assert.Equal(UserBranchDeliveryStatus.DirtyPaths, outcome.Status);
        Assert.Equal([path], outcome.Paths);
        Assert.Equal("del usuario\n", File.ReadAllText(Path.Combine(Repo.Root, path.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(Repo.BaseCommit, Repo.Head);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }

    [Fact]
    public async Task Work_of_the_user_elsewhere_does_not_stop_the_delivery_and_is_kept()
    {
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("src/Calc/Multiply.cs", "// multiplica\n"));
        Repo.Write("README.md", "# temp\nedición del usuario\n");
        Repo.Write("notas.txt", "sin seguimiento\n");

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Equal("# temp\nedición del usuario\n", File.ReadAllText(Path.Combine(Repo.Root, "README.md")));
        Assert.Equal("sin seguimiento\n", File.ReadAllText(Path.Combine(Repo.Root, "notas.txt")));
        Assert.Equal([" M README.md", "?? notas.txt"], (await CaptureAsync(manager)).PorcelainStatus);
    }

    // ---- merge commit -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_merge_commit_is_made_with_the_users_identity_without_their_hooks_and_its_message_comes_on_standard_input()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "del agente\n"));
        var userHead = UserCommit("user.txt", "trabajo del usuario\n");
        foreach (var hook in new[] { "pre-commit", "prepare-commit-msg", "commit-msg", "post-commit", "pre-merge-commit", "post-merge" })
        {
            PlantUserHook(hook);
        }

        var start = recorder.Specs.Count;

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, userHead), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Equal(Repo.Head, outcome.HeadCommit);
        Assert.Equal($"{userHead} {commit}", Repo.Run("log", "-1", "--format=%P", "HEAD").Trim());
        Assert.Equal(Message, Repo.Run("log", "-1", "--format=%B", "HEAD").Trim());
        Assert.Equal("Agent Studio Tests|tests@localhost", Repo.Run("log", "-1", "--format=%an|%ae", "HEAD").Trim());
        Assert.Equal("del agente\n", File.ReadAllText(Path.Combine(Repo.Root, "nuevo.txt")));

        // R3-05: no hook of the user runs, and nothing is signed; the identity is the user's own (no -c override).
        Assert.False(File.Exists(HookLog), "Hooks ran: " + (File.Exists(HookLog) ? File.ReadAllText(HookLog) : string.Empty));
        var specs = recorder.Specs.Skip(start).ToList();
        var merge = Assert.Single(specs, s => RecordingProcessRunner.SubCommand(s) == "merge");
        Assert.Equal(["merge", "--no-ff", "--no-commit", "--no-autostash", "--end-of-options", commit], merge.Arguments.SkipWhile(a => a != "merge"));
        var commitSpec = Assert.Single(specs, s => RecordingProcessRunner.SubCommand(s) == "commit");
        Assert.Equal(["commit", "--quiet", "-F", "-"], commitSpec.Arguments.SkipWhile(a => a != "commit"));
        Assert.Equal(Message + "\n", commitSpec.StandardInput);
        Assert.All(specs, s => Assert.DoesNotContain(s.Arguments, a => a.Contains("entrega de la ejecución", StringComparison.Ordinal)));
        Assert.All(new[] { merge, commitSpec }, s =>
        {
            Assert.Contains("commit.gpgsign=false", s.Arguments);
            Assert.Contains("tag.gpgsign=false", s.Arguments);
            Assert.DoesNotContain(s.Arguments, a => a.StartsWith("user.", StringComparison.Ordinal) || a.StartsWith("author.", StringComparison.Ordinal) || a.StartsWith("committer.", StringComparison.Ordinal));
        });
        Assert.All(specs, s => Assert.Contains(NoHooks, s.Arguments));
    }

    [Fact]
    public async Task A_merge_commit_that_would_conflict_is_refused_before_anything_reaches_the_working_tree()
    {
        var manager = NewManager();
        var shared = File.ReadAllText(Path.Combine(Repo.Root, "shared.txt"));
        var commit = await TaskCommitAsync(manager, ("shared.txt", shared.Replace("linea 1\n", "linea 1 (agente)\n", StringComparison.Ordinal)));
        var userHead = UserCommit("shared.txt", shared.Replace("linea 1\n", "linea 1 (usuario)\n", StringComparison.Ordinal));
        var before = await CaptureAsync(manager);

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, userHead), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Conflicts, outcome.Status);
        Assert.Equal(["shared.txt"], outcome.Paths);
        Assert.DoesNotContain("<<<<<<<", File.ReadAllText(Path.Combine(Repo.Root, "shared.txt")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Repo.Root, ".git", "MERGE_HEAD")));
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }

    [Fact]
    public async Task A_merge_of_the_user_still_in_progress_stops_the_delivery_and_is_never_reported_as_ours()
    {
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "del agente\n"));

        // The user started a merge of their own and has not concluded it.
        Repo.Run("checkout", "-q", "-b", "lateral");
        UserCommit("lateral.txt", "rama lateral\n");
        Repo.Run("checkout", "-q", "main");
        Repo.Run("merge", "-q", "--no-ff", "--no-commit", "lateral");
        var before = await CaptureAsync(manager);

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit), Ct);

        Assert.Equal(UserBranchDeliveryStatus.GitFailed, outcome.Status);
        Assert.Contains("in progress", outcome.Output, StringComparison.Ordinal);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
        Repo.Run("merge", "--abort");
    }

    // ---- an in-repository hooks folder and in-repository callbacks ----------------------------------------------

    /// <summary>The user's husky-style setup: a versioned hook under <c>.husky/</c> and <c>core.hooksPath</c> pointing at it.</summary>
    private void CommitHuskyHooks()
    {
        Repo.Write(".husky/post-merge", HookScripts.Logging("post-merge"));
        Repo.Run("add", "-A");
        Repo.Run("commit", "-q", "-m", "hooks husky");
        Repo.Run("config", "core.hooksPath", ".husky");
    }

    [Fact]
    public async Task A_delivery_whose_diff_rewrites_a_hook_of_an_in_repository_hooks_path_is_delivered_and_no_hook_runs()
    {
        // R3-05: the delivery no longer runs the user's hooks, so a diff that rewrites .husky/post-merge is no longer a
        // reason to refuse: it is delivered, and neither the old nor the new version of the hook runs.
        var manager = NewManager();
        CommitHuskyHooks();
        var head = Repo.Head;

        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, ".husky/post-merge", HookScripts.Logging("agent-post-merge"));
        var commit = (await manager.CommitAllAsync(lease, "T1: cambia el hook", Ct)).Commit;

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, expectedHead: head), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Equal(commit, Repo.Head);
        Assert.False(File.Exists(HookLogAtRoot), "a hook of the in-repository hooks path ran during delivery.");
    }

    [Fact]
    public async Task A_delivery_that_does_not_touch_the_in_repository_hooks_path_is_delivered_without_running_the_users_hook()
    {
        var manager = NewManager();
        CommitHuskyHooks();
        var head = Repo.Head;

        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, "src/Calc/Multiply.cs", "// multiplica\n");
        var commit = (await manager.CommitAllAsync(lease, "T1: multiplica", Ct)).Commit;

        var outcome = await manager.DeliverToUserBranchAsync(Delivery(commit, expectedHead: head), Ct);

        Assert.Equal(UserBranchDeliveryStatus.Delivered, outcome.Status);
        Assert.Equal(commit, Repo.Head);
        Assert.False(File.Exists(HookLogAtRoot), "the user's husky hook ran during delivery.");
    }

    // A husky hook logs next to its folder: `.husky/../hooks-ran.log`, the root of the working tree.
    private string HookLogAtRoot => Path.Combine(Repo.Root, HookScripts.LogName);

    [Fact]
    public async Task A_delivery_with_an_in_repository_callback_in_the_users_configuration_is_refused_before_any_merge()
    {
        // The merge runs through the same allowlist as every content command: an in-repository filter is refused, the
        // branch does not move, and the program never runs.
        var mark = Path.Combine(Path.GetTempPath(), "as-centinela-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        try
        {
            var manager = NewManager();
            var commit = await TaskCommitAsync(manager, ("src/Calc/Multiply.cs", "// multiplica\n"));
            Repo.Write("tools/format.sh", "#!/bin/sh\necho ran >> '" + mark.Replace('\\', '/') + "'\ncat\n");
            Repo.Write(".gitattributes", "*.cs filter=format\n");
            Repo.Run("config", "filter.format.clean", "sh tools/format.sh");
            Repo.Run("config", "filter.format.smudge", "sh tools/format.sh");

            var error = await Record.ExceptionAsync(() => manager.DeliverToUserBranchAsync(Delivery(commit), Ct));

            Assert.IsType<UnsafeGitCallbackException>(error);
            Assert.Equal(Repo.BaseCommit, Repo.Head);
            Assert.False(File.Exists(mark), "the repository's filter ran during delivery.");
        }
        finally
        {
            File.Delete(mark);
        }
    }

    [Fact]
    public async Task The_check_asked_before_the_approval_returns_the_classifier_refusals_of_the_users_configuration_and_runs_nothing()
    {
        var manager = NewManager();
        CommitHuskyHooks();
        var head = Repo.Head;
        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, ".husky/post-merge", HookScripts.Logging("agent-post-merge"));
        WriteIn(lease, "tools/format.sh", "exit 0\n");
        WriteIn(lease, "src/a.txt", "a\n");
        var commit = (await manager.CommitAllAsync(lease, "T1: cambia el hook y el filtro", Ct)).Commit;

        // A callback that runs a script of the repository; set after the system's own commits, which would refuse it.
        Repo.Run("config", "filter.format.clean", "sh tools/format.sh");

        var refusals = await manager.FindDeliveryRefusalsAsync(Repo.Root, head, commit, Ct);

        // Only the callback: the in-repository hooks path is no longer a refusal (no hook runs during a delivery).
        var refusal = Assert.Single(refusals);
        Assert.Equal("filter.format.clean", refusal.Key);
        Assert.Contains("git-lfs", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(head, Repo.Head);
        Assert.False(File.Exists(HookLogAtRoot), "a hook ran.");
    }

    [Fact]
    public async Task The_check_asked_before_the_approval_finds_nothing_when_the_configuration_has_no_in_repository_callback()
    {
        var manager = NewManager();
        CommitHuskyHooks();
        var head = Repo.Head;
        var lease = await EnsureAsync(manager, "T1", head);
        WriteIn(lease, "src/Calc/Multiply.cs", "// multiplica\n");
        var commit = (await manager.CommitAllAsync(lease, "T1: multiplica", Ct)).Commit;

        Assert.Empty(await manager.FindDeliveryRefusalsAsync(Repo.Root, head, commit, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, "HEAD", commit, Ct));
        await Assert.ThrowsAsync<GitCommandException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, head, new string('a', 40), Ct));
    }

    // ---- what the merge writes, reached through a link (H-03) -------------------------------------------------------
    // Measured with git 2.45.2: a delivery writes the user's branch, appends to its reflog and to logs/HEAD, and writes
    // ORIG_HEAD; with a merge commit also MERGE_HEAD, MERGE_MSG, MERGE_MODE, AUTO_MERGE and COMMIT_EDITMSG. Through a
    // junction in logs/refs/heads or in a parent folder of the branch, git wrote outside the repository; through a hard
    // link it appended to logs/HEAD and the branch's reflog and overwrote MERGE_MSG, MERGE_MODE and COMMIT_EDITMSG. Each is
    // refused by the check DeliveryService asks before the approval, and again right before the merge.

    private static bool RanMerge(IEnumerable<AgentStudio.Application.Execution.ProcessSpec> specs) =>
        specs.Any(s => RecordingProcessRunner.SubCommand(s) == "merge");

    [Fact]
    public async Task A_junction_in_the_reflog_folder_of_the_users_branches_is_refused_before_the_approval_and_before_the_merge()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var logs = Path.Combine(Repo.Root, ".git", "logs", "refs", "heads");
        var victim = NewVictim("otros-logs");
        ReplaceWithJunction(logs, victim);
        var before = File.ReadAllText(Path.Combine(victim, "main"));
        try
        {
            var start = recorder.Specs.Count;

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, Repo.BaseCommit, commit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeliverToUserBranchAsync(Delivery(commit), Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal(before, File.ReadAllText(Path.Combine(victim, "main")));
            Assert.Equal(Repo.BaseCommit, Repo.Head);
            Assert.False(RanMerge(recorder.Specs.Skip(start)), "git merge ran with the reflog of the user's branch reached through a junction.");
        }
        finally
        {
            RemoveJunction(logs);
        }
    }

    [Fact]
    public async Task A_junction_in_place_of_a_parent_folder_of_the_users_branch_is_refused_before_the_approval_and_before_the_merge()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));

        // The user works on feature/x, and refs/heads/feature becomes a junction to a folder outside the repository.
        Repo.Run("checkout", "-q", "-b", "feature/x");
        var parent = Path.Combine(Repo.Root, ".git", "refs", "heads", "feature");
        var victim = NewVictim("otras-ramas");
        ReplaceWithJunction(parent, victim);
        try
        {
            var start = recorder.Specs.Count;

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, Repo.BaseCommit, commit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeliverToUserBranchAsync(Delivery(commit, branch: "feature/x"), Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal(Repo.BaseCommit, File.ReadAllText(Path.Combine(victim, "x")).Trim());
            Assert.False(RanMerge(recorder.Specs.Skip(start)), "git merge ran with the user's branch reached through a junction.");
        }
        finally
        {
            RemoveJunction(parent);
        }
    }

    public static TheoryData<string> FilesTheMergeWrites() =>
        ["logs/HEAD", "logs/refs/heads/main", "ORIG_HEAD", "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "AUTO_MERGE", "COMMIT_EDITMSG"];

    [Theory]
    [MemberData(nameof(FilesTheMergeWrites))]
    public async Task A_hard_link_at_a_file_the_merge_writes_is_refused_before_the_approval_and_before_the_merge(string file)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var victim = Path.Combine(NewVictim("otro-repositorio"), "precioso.txt");
        File.WriteAllText(victim, "precioso\n");

        // A second name for a file outside the repository, where git writes the file (made with mklink /H, no privilege).
        var planted = Path.Combine(Repo.Root, ".git", file.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(planted);
        using var links = new AgentStudio.Tests.Infrastructure.Persistence.PlantedLinks();
        links.HardLink(planted, victim);
        var start = recorder.Specs.Count;

        var refusals = new[]
        {
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, Repo.BaseCommit, commit, Ct)),
            await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, Repo.BaseCommit), Ct)),
        };

        Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
        Assert.Equal("precioso\n", File.ReadAllText(victim));
        Assert.Equal(Repo.BaseCommit, Repo.Head);
        Assert.False(RanMerge(recorder.Specs.Skip(start)), $"git merge ran with {file} hard-linked to a file outside the repository.");
    }

    /// <summary>
    /// P-02: a merge commit writes its tree and its commit into the user's object store (the merge-tree preview too), so a
    /// junction in it is refused before the approval and again before the preview, as the files the merge writes are.
    /// </summary>
    [Fact]
    public async Task A_junction_in_the_object_store_is_refused_before_the_approval_and_before_the_merge_writes_objects()
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        UserCommit("del-usuario.txt", "trabajo\n");
        var head = Repo.Head;
        var pack = Path.Combine(Repo.Root, ".git", "objects", "pack");
        var victim = NewVictim("otros-paquetes");
        ReplaceWithJunction(pack, victim);
        try
        {
            var start = recorder.Specs.Count;

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, head, commit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, head), Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal(head, Repo.Head);
            Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
            Assert.False(
                recorder.Specs.Skip(start).Any(s => RecordingProcessRunner.SubCommand(s) is "merge-tree" or "merge" or "commit"),
                "the delivery wrote objects with objects/pack reached through a junction.");
        }
        finally
        {
            RemoveJunction(pack);
        }
    }

    // ---- G-04: the user's index -------------------------------------------------------------------------------------
    // Measured with git 2.45.2, the delivery as Agent Studio runs it: with .git/index a symbolic link to a file outside the
    // repository, `merge --ff-only` and `merge --no-ff --no-commit` wrote the new index through it (git's lock file follows
    // the link) and it stayed a link; with a hard link, git replaced the name by a rename and the other file kept its bytes.
    // Both are refused before the approval, right before the merge and right before each later command that writes it.

    /// <summary>Replaces the user's index by a link to a copy of it outside the repository, or skips when the machine cannot make a symbolic link.</summary>
    private string PlantIndexLink(string link, AgentStudio.Tests.Infrastructure.Persistence.PlantedLinks links)
    {
        var index = Path.Combine(Repo.Root, ".git", "index");
        var victim = Path.Combine(NewVictim("otro-repositorio-" + Guid.NewGuid().ToString("N")[..6]), "index");
        File.Copy(index, victim);
        File.Delete(index);
        if (link == "hard")
        {
            links.HardLink(index, victim);
            return victim;
        }

        try
        {
            File.CreateSymbolicLink(index, victim);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Copy(victim, index);
            Assert.Skip("Creating a symbolic link needs Developer Mode or elevation on this machine.");
        }

        return victim;
    }

    private void RemoveIndexLink()
    {
        var index = new FileInfo(Path.Combine(Repo.Root, ".git", "index"));
        if (index.Exists && index.LinkTarget is not null)
        {
            index.Delete();
        }
    }

    public static TheoryData<string, DeliveryMode> IndexLinks() => new()
    {
        { "hard", DeliveryMode.FastForwardOnly },
        { "hard", DeliveryMode.MergeCommit },
        { "symbolic", DeliveryMode.FastForwardOnly },
        { "symbolic", DeliveryMode.MergeCommit },
    };

    [Theory]
    [MemberData(nameof(IndexLinks))]
    public async Task A_link_at_the_users_index_is_refused_before_the_approval_and_before_the_merge(string link, DeliveryMode mode)
    {
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var head = mode == DeliveryMode.MergeCommit ? UserCommit("user.txt", "trabajo del usuario\n") : Repo.Head;
        using var links = new AgentStudio.Tests.Infrastructure.Persistence.PlantedLinks();
        var victim = PlantIndexLink(link, links);
        var before = File.ReadAllBytes(victim);
        try
        {
            var start = recorder.Specs.Count;

            var errors = new[]
            {
                await Record.ExceptionAsync(() => manager.FindDeliveryRefusalsAsync(Repo.Root, head, commit, Ct)),
                await Record.ExceptionAsync(() => manager.DeliverToUserBranchAsync(Delivery(commit, mode, head), Ct)),
            };

            // First what matters: nothing was written through the link.
            Assert.Equal(before, File.ReadAllBytes(victim));
            Assert.All(errors, error => Assert.Contains("reached through a link", Assert.IsType<WorktreeOwnershipException>(error).Message, StringComparison.Ordinal));
            Assert.Equal(head, Repo.Run("rev-parse", "refs/heads/main").Trim());
            Assert.False(RanMerge(recorder.Specs.Skip(start)), $"git merge ran with the user's index as a {link} link to a file outside the repository.");
        }
        finally
        {
            RemoveIndexLink();
        }
    }

    [Fact]
    public async Task A_link_planted_at_the_users_index_after_the_merge_is_refused_before_the_commit_writes_through_it()
    {
        SkipUnlessSymbolicLinks();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "del agente\n"));
        var head = UserCommit("user.txt", "trabajo del usuario\n");
        using var links = new AgentStudio.Tests.Infrastructure.Persistence.PlantedLinks();
        string? victim = null;
        byte[]? before = null;
        recorder.AfterRun = spec =>
        {
            if (victim is null && RecordingProcessRunner.SubCommand(spec) == "merge")
            {
                victim = PlantIndexLink("symbolic", links);
                before = File.ReadAllBytes(victim);
            }
        };
        try
        {
            var error = await Record.ExceptionAsync(() => manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, head), Ct));

            Assert.NotNull(victim);
            Assert.Equal(before, File.ReadAllBytes(victim));
            Assert.Contains("reached through a link", Assert.IsType<WorktreeOwnershipException>(error).Message, StringComparison.Ordinal);
            var afterMerge = recorder.Specs.SkipWhile(s => RecordingProcessRunner.SubCommand(s) != "merge").Skip(1);
            Assert.DoesNotContain(afterMerge, s => RecordingProcessRunner.SubCommand(s) == "commit");
            Assert.Equal(head, Repo.Run("rev-parse", "refs/heads/main").Trim());
        }
        finally
        {
            RemoveIndexLink();
        }
    }

    [Fact]
    public async Task A_link_planted_at_the_users_index_after_the_merge_is_refused_before_an_abort_writes_through_it()
    {
        SkipUnlessSymbolicLinks();
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "del agente\n"));
        var head = UserCommit("user.txt", "trabajo del usuario\n");
        using var links = new AgentStudio.Tests.Infrastructure.Persistence.PlantedLinks();
        string? victim = null;
        byte[]? before = null;

        // After the merge the index becomes a link and main moves: the delivery must not commit, and the abort it then runs
        // writes the index too.
        recorder.AfterRun = spec =>
        {
            if (victim is null && RecordingProcessRunner.SubCommand(spec) == "merge")
            {
                victim = PlantIndexLink("symbolic", links);
                before = File.ReadAllBytes(victim);
                Repo.Run("update-ref", "refs/heads/main", Repo.BaseCommit, head);
            }
        };
        try
        {
            var error = await Record.ExceptionAsync(() => manager.DeliverToUserBranchAsync(Delivery(commit, DeliveryMode.MergeCommit, head), Ct));

            Assert.NotNull(victim);
            Assert.Equal(before, File.ReadAllBytes(victim));
            Assert.Contains("reached through a link", Assert.IsType<WorktreeOwnershipException>(error).Message, StringComparison.Ordinal);
            var afterMerge = recorder.Specs.SkipWhile(s => RecordingProcessRunner.SubCommand(s) != "merge").Skip(1);
            Assert.DoesNotContain(afterMerge, s => RecordingProcessRunner.SubCommand(s) is "merge" or "commit");
        }
        finally
        {
            RemoveIndexLink();
        }
    }

    /// <summary>
    /// G-01: the delivery merge can run the git-lfs filters of the user's repository (a smudge of every delivered lfs file),
    /// and git-lfs writes into <c>&lt;common&gt;/lfs</c>. A junction there is refused before the approval and again before the merge.
    /// </summary>
    [Fact]
    public async Task A_junction_in_the_git_lfs_storage_is_refused_before_the_approval_and_before_the_merge()
    {
        Assert.SkipUnless(new ExecutableLocator().Find("git-lfs") is not null, "git-lfs is not installed.");
        var recorder = new RecordingProcessRunner(new ProcessRunner());
        var manager = NewManager(recorder);
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        Repo.Run("lfs", "install", "--local");
        var objects = Path.Combine(Repo.Root, ".git", "lfs", "objects");
        var victim = NewVictim("otros-objetos-lfs");
        Directory.CreateDirectory(Path.GetDirectoryName(objects)!);
        CreateJunction(objects, victim);
        try
        {
            var start = recorder.Specs.Count;

            var refusals = new[]
            {
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.FindDeliveryRefusalsAsync(Repo.Root, Repo.BaseCommit, commit, Ct)),
                await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeliverToUserBranchAsync(Delivery(commit), Ct)),
            };

            Assert.All(refusals, refusal => Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal));
            Assert.Equal(Repo.BaseCommit, Repo.Head);
            Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
            Assert.False(RanMerge(recorder.Specs.Skip(start)), "git merge ran with the git-lfs storage reached through a junction.");
        }
        finally
        {
            RemoveJunction(objects);
        }
    }

    // ---- the repository comes from the project ------------------------------------------------------------------

    [Fact]
    public async Task A_worktree_whose_gitfile_names_the_users_repository_is_never_delivered_to()
    {
        var manager = NewManager();
        var commit = await TaskCommitAsync(manager, ("nuevo.txt", "x\n"));
        var lease = await EnsureAsync(manager, "_integration");
        RedirectGitFile(lease, Path.Combine(Repo.Root, ".git"));
        var before = await CaptureAsync(manager);

        await Assert.ThrowsAsync<WorktreeOwnershipException>(() => manager.DeliverToUserBranchAsync(Delivery(commit, repositoryRoot: lease.Path), Ct));

        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync(manager)));
    }
}
