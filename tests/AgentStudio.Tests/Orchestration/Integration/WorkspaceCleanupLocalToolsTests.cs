using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Orchestration.Integration;

/// <summary>The real <see cref="WorkspaceCleanup"/> over the real <see cref="GitWorktreeManager"/>, its only git path, on a temporary repository.</summary>
[Trait("Category", "LocalTools")]
public sealed class WorkspaceCleanupLocalToolsTests : IDisposable
{
    private const string ProjectId = "limpieza-local";

    private static readonly RunId Run = new("20261016-f0f0f0");

    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly RecordingApprovalGate _gate = new();
    private readonly GitWorktreeManager _worktrees;

    public WorkspaceCleanupLocalToolsTests()
    {
        _worktrees = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectContext Project => new(ProjectId, _repo.Root);

    private WorkspaceCleanup Cleanup() => new(_worktrees, _gate);

    [Fact]
    public async Task A_delivered_run_is_cleaned_without_asking_and_the_users_repository_is_left_as_it_was()
    {
        var t1 = await _worktrees.EnsureAsync(new WorktreeRequest(Project, Run, "T1", _repo.BaseCommit), Ct);
        File.WriteAllText(Path.Combine(t1.Path, "uno.txt"), "1\n");
        var commit = (await _worktrees.CommitAllAsync(t1, "T1", Ct)).Commit;
        await _worktrees.EnsureAsync(new WorktreeRequest(Project, Run, IntegrationNames.IntegrationWorktree, _repo.BaseCommit), Ct);
        var head = await _worktrees.EnsureRunBranchAsync(_repo.Root, Run, _repo.BaseCommit, Ct);
        var integration = (await _worktrees.MergeIntoAsync(_repo.Root, IntegrationNames.BranchRef(Run), head, commit, "Integra T1", Ct)).ResultCommit!;
        _repo.Run("merge", "-q", "--ff-only", integration);
        var before = await _worktrees.CaptureUserRepositoryStateAsync(_repo.Root, Ct);

        var report = await Cleanup().CleanAsync(Project, Run, Ct);

        Assert.Empty(_gate.Requests);
        Assert.Equal(2, report.RemovedWorktrees.Count);
        Assert.Equal(
            [
                IntegrationNames.RunRefPrefix(Run) + "T1",
                IntegrationNames.RunRefPrefix(Run) + "_integration",
                IntegrationNames.RunRefPrefix(Run) + "_start/T1",
                IntegrationNames.RunRefPrefix(Run) + "_start/_integration",
                IntegrationNames.BranchRef(Run),
            ],
            report.RemovedRefs);
        Assert.Empty(report.Kept);
        Assert.Empty(await _worktrees.ListRunRefsAsync(_repo.Root, Run, Ct));
        Assert.Empty(UserRepositoryState.Compare(before, await _worktrees.CaptureUserRepositoryStateAsync(_repo.Root, Ct)));
    }

    [Fact]
    public async Task A_junction_planted_in_the_refs_of_the_run_never_lets_the_cleanup_delete_a_user_branch()
    {
        // What an agent with a shell can do: turn the folder of the run's hidden refs into a junction to the user's
        // branches. Every user branch then looks like a hidden ref of the run whose work is already in HEAD.
        _repo.Run("branch", "feature");
        var refs = Path.Combine(_repo.Root, ".git", "refs");
        Directory.CreateDirectory(Path.Combine(refs, "agentstudio"));
        GitManagerTestBase.CreateJunction(Path.Combine(refs, "agentstudio", Run.Value), Path.Combine(refs, "heads"));
        try
        {
            var refusal = await Assert.ThrowsAsync<WorktreeOwnershipException>(() => Cleanup().CleanAsync(Project, Run, Ct));

            Assert.Contains("reached through a link", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(_repo.BaseCommit, _repo.Run("rev-parse", "--verify", "refs/heads/main").Trim());
            Assert.Equal(_repo.BaseCommit, _repo.Run("rev-parse", "--verify", "refs/heads/feature").Trim());
        }
        finally
        {
            Directory.Delete(Path.Combine(refs, "agentstudio", Run.Value));
        }
    }

    public void Dispose() => _repo.Dispose();
}
