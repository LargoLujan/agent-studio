using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Orchestration.Integration;

/// <summary>
/// The real <see cref="DeliveryService"/> over the real <see cref="GitWorktreeManager"/>, its only git path, on a
/// temporary repository: the run's branch is one task commit ahead of the user's <c>main</c>.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class DeliveryLocalToolsTests : IDisposable
{
    private const string ProjectId = "entrega-local";

    private static readonly RunId Run = new("20261016-e0e0e0");

    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly RecordingApprovalGate _gate = new();
    private readonly DeliveryRecords _records = new(ProjectId);
    private readonly GitWorktreeManager _worktrees;

    public DeliveryLocalToolsTests()
    {
        _worktrees = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectContext Project => new(ProjectId, _repo.Root);

    private IDeliveryService Service() => new CodedTextChecks(new DeliveryService(_worktrees, _gate, _records.Recorder, _records.Verdicts));

    private Task<UserRepositoryState> CaptureAsync() => _worktrees.CaptureUserRepositoryStateAsync(_repo.Root, Ct);

    /// <summary>
    /// A task that writes <paramref name="files"/> on the base, committed by the system; the run's branch points at its
    /// integration, which the log shows the run ended with and a judge approved.
    /// </summary>
    private async Task<string> RunBranchWithAsync(params (string Path, string Content)[] files)
    {
        var integration = await BranchOnlyAsync(files);
        await _records.SucceededAsync(Run, integration);
        return integration;
    }

    private async Task<string> BranchOnlyAsync(params (string Path, string Content)[] files)
    {
        var lease = await _worktrees.EnsureAsync(new WorktreeRequest(Project, Run, "T1", _repo.BaseCommit), Ct);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(lease.Path, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        var commit = await _worktrees.CommitAllAsync(lease, "T1: cambios", Ct);
        Assert.True(commit.Committed);
        var head = await _worktrees.EnsureRunBranchAsync(_repo.Root, Run, _repo.BaseCommit, Ct);
        var merge = await _worktrees.MergeIntoAsync(_repo.Root, IntegrationNames.BranchRef(Run), head, commit.Commit, "Integra T1", Ct);
        return merge.ResultCommit!;
    }

    [Fact]
    public async Task An_approved_fast_forward_delivers_the_run_branch_and_only_moves_the_users_branch()
    {
        var integration = await RunBranchWithAsync(("src/Calc/Multiply.cs", "// multiplica\n"));
        _repo.Run("branch", "feature");
        var before = await CaptureAsync();

        var result = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);

        Assert.True(result.Delivered, result.Message);
        Assert.Equal(integration, result.ResultCommit);
        Assert.Equal(integration, _repo.Head);
        Assert.Equal(["src/Calc/Multiply.cs"], Assert.Single(_gate.Requests).AffectedPaths);
        var differences = UserRepositoryState.Compare(before, await CaptureAsync());
        Assert.Equal($"HEAD moved from {_repo.BaseCommit} to {integration}.", differences[0]);
        Assert.Equal($"Branch 'refs/heads/main' moved from {_repo.BaseCommit} to {integration}.", differences[1]);
        Assert.All(differences.Skip(2), d => Assert.StartsWith("Index changed", d, StringComparison.Ordinal));
        var delivered = Assert.Single(_records.Payloads<IntegrationPerformedPayload>(), p => p.IsDelivery);
        Assert.Equal(("refs/heads/main", integration), (delivered.TargetRef, delivered.ResultCommit));
    }

    [Fact]
    public async Task A_run_branch_an_agent_moved_after_the_review_is_not_delivered_and_the_users_branch_is_kept()
    {
        var approved = await RunBranchWithAsync(("src/Calc/Multiply.cs", "// multiplica\n"));

        // With a shell, an agent can move the run's branch to a commit of its own after the judge approved the integration.
        var lease = await _worktrees.EnsureAsync(new WorktreeRequest(Project, Run, "T9", approved), Ct);
        File.WriteAllText(Path.Combine(lease.Path, "puerta-trasera.txt"), "x\n");
        var planted = (await _worktrees.CommitAllAsync(lease, "T9", Ct)).Commit;
        _repo.Run("update-ref", IntegrationNames.BranchRef(Run), planted, approved);
        var before = await CaptureAsync();

        var result = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);

        Assert.False(result.Delivered, result.Message);
        Assert.Empty(_gate.Requests);
        Assert.Equal(_repo.BaseCommit, _repo.Head);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync()));
    }

    [Fact]
    public async Task The_approval_says_hooks_and_signing_are_not_run_and_names_the_manual_merge_command()
    {
        // R3-05: the delivery runs like any internal command. The approval says so and gives the exact git merge the
        // user can run by hand after reviewing, if they want their hooks or their signature.
        var integration = await RunBranchWithAsync((".husky/pre-commit", "npx lint-staged\n"), ("src/a.txt", "a\n"));
        _gate.Approve = false;
        var before = await CaptureAsync();

        var result = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);

        var request = Assert.Single(_gate.Requests);
        Assert.Equal([".husky/pre-commit", "src/a.txt"], request.AffectedPaths);
        Assert.Contains("NO ejecuta tus hooks ni firma", request.Description, StringComparison.Ordinal);
        Assert.Contains($"git merge --ff-only {integration}", request.Description, StringComparison.Ordinal);
        Assert.False(result.Delivered);
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync()));
    }

    private string HookLog => Path.Combine(_repo.Root, HookScripts.LogName);

    [Fact]
    public async Task A_delivery_that_rewrites_a_hook_of_the_in_repository_hooks_path_is_delivered_and_no_hook_runs()
    {
        // husky's setup: core.hooksPath inside the repository, and the run rewrites the hook. The delivery no longer runs
        // the user's hooks (R3-05), so it is asked and delivered, and neither version of the hook runs.
        _repo.Run("config", "core.hooksPath", ".husky");
        var integration = await RunBranchWithAsync((".husky/post-merge", HookScripts.Logging("agent-post-merge")), ("src/a.txt", "a\n"));

        var result = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);

        Assert.True(result.Delivered, result.Message);
        Assert.Single(_gate.Requests);
        Assert.Equal(integration, _repo.Head);
        Assert.False(File.Exists(HookLog), "a hook ran during delivery.");
    }

    [Fact]
    public async Task A_delivery_that_does_not_touch_the_in_repository_hooks_path_is_still_delivered_and_no_hook_runs()
    {
        _repo.Run("config", "core.hooksPath", ".husky");
        var integration = await RunBranchWithAsync(("src/Calc/Multiply.cs", "// multiplica\n"));

        var result = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);

        Assert.True(result.Delivered, result.Message);
        Assert.Single(_gate.Requests);
        Assert.Equal(integration, _repo.Head);
        Assert.False(File.Exists(HookLog), "a hook ran during delivery.");
    }

    // ---- what the merge would refuse is refused before the approval (N-01/R3-05) ------------------------------------

    [Fact]
    public async Task A_refused_callback_in_the_users_configuration_is_refused_before_the_approval_is_asked()
    {
        // The user's configuration runs a script of the repository as a filter. The first inspection of the user's
        // repository already goes through the allowlist (its `status` is a content command), so the delivery fails closed
        // before asking: no approval is requested or spent, and the branch does not move.
        await RunBranchWithAsync(("src/a.txt", "a\n"));
        _repo.Run("config", "filter.format.clean", "sh tools/format.sh");

        var error = await Record.ExceptionAsync(() => Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct));

        var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
        Assert.Contains("filter.format.clean", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_gate.Requests);
        Assert.Equal(_repo.BaseCommit, _repo.Head);
    }

    [Fact]
    public async Task A_configuration_that_changes_while_the_approval_is_pending_is_still_refused_after_the_approval()
    {
        // Nothing to refuse when the delivery is checked; while the approval is pending the user's configuration gains an
        // in-repository filter. What runs after the approval goes through the same allowlist (the repeated inspection,
        // then the merge): nothing is delivered and the program never runs.
        var mark = Path.Combine(Path.GetTempPath(), "as-centinela-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        try
        {
            await RunBranchWithAsync(("src/a.txt", "a\n"));
            _gate.WhilePending = _ =>
            {
                _repo.Write("tools/format.sh", "#!/bin/sh\necho ran >> '" + mark.Replace('\\', '/') + "'\ncat\n");
                _repo.Write(".gitattributes", "*.txt filter=format\n");
                _repo.Run("config", "filter.format.smudge", "sh tools/format.sh");
            };

            var error = await Record.ExceptionAsync(() => Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct));

            var refusal = Assert.IsType<UnsafeGitCallbackException>(error);
            Assert.Contains("filter.format.smudge", refusal.Message, StringComparison.Ordinal);
            Assert.Single(_gate.Requests);
            Assert.Equal(_repo.BaseCommit, _repo.Head);
            Assert.False(File.Exists(mark), "the filter ran during delivery.");
        }
        finally
        {
            File.Delete(mark);
        }
    }

    [Fact]
    public async Task A_junction_in_the_reflog_folder_of_the_users_branches_is_refused_before_the_approval_is_asked()
    {
        // H-03: the delivery appends to the reflog of the user's branch, and through a junction git writes it outside the
        // repository (measured with git 2.45.2). The check DeliveryService runs before asking refuses it: no approval is
        // requested or spent, and nothing is written through the junction.
        await RunBranchWithAsync(("src/a.txt", "a\n"));
        var logs = Path.Combine(_repo.Root, ".git", "logs", "refs", "heads");
        var victim = Path.Combine(Path.GetDirectoryName(_repo.Root)!, "otros-logs");
        GitManagerTestBase.ReplaceWithJunction(logs, victim);
        var before = File.ReadAllText(Path.Combine(victim, "main"));
        try
        {
            var error = await Record.ExceptionAsync(() => Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct));

            var refusal = Assert.IsType<WorktreeOwnershipException>(error);
            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_gate.Requests);
            Assert.Equal(before, File.ReadAllText(Path.Combine(victim, "main")));
            Assert.Equal(_repo.BaseCommit, _repo.Head);
        }
        finally
        {
            GitManagerTestBase.RemoveJunction(logs);
        }
    }

    [Fact]
    public async Task An_ignored_file_of_the_user_in_a_delivered_path_stops_the_delivery_and_is_kept()
    {
        await RunBranchWithAsync(("datos/cache.txt", "del agente\n"));

        // The user ignores datos/ locally and keeps a file of their own there: git would overwrite it without a word.
        File.AppendAllText(Path.Combine(_repo.Root, ".git", "info", "exclude"), "datos/\n");
        _repo.Write("datos/cache.txt", "del usuario\n");
        var before = await CaptureAsync();

        var result = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);

        Assert.False(result.Delivered, result.Message);
        Assert.Contains("datos/cache.txt", result.Message, StringComparison.Ordinal);
        Assert.Equal("del usuario\n", File.ReadAllText(Path.Combine(_repo.Root, "datos", "cache.txt")));
        Assert.Empty(UserRepositoryState.Compare(before, await CaptureAsync()));
        Assert.Equal(_repo.BaseCommit, _repo.Head);
    }

    public void Dispose() => _repo.Dispose();
}
