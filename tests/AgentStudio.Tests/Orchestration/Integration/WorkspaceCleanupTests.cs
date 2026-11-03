using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Tests.Orchestration.Integration;

public sealed class WorkspaceCleanupTests
{
    private static readonly RunId Run = new("20261016-a1b2c3");
    private static readonly RunId OtherRun = new("20261016-ffffff");

    private readonly FakeRepository _repo = new();
    private readonly RecordingApprovalGate _gate = new();
    private readonly FakeWorktreeManager _worktrees;

    public WorkspaceCleanupTests()
    {
        _worktrees = new FakeWorktreeManager(_repo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hidden(string name, RunId? run = null) => IntegrationNames.RunRefPrefix(run ?? Run) + name;

    private static string Start(string name, RunId? run = null) => IntegrationNames.RunRefPrefix(run ?? Run) + "_start/" + name;

    private static string Branch => IntegrationNames.BranchRef(Run);

    private ProjectContext Project => new("proyecto-1", _repo.Root);

    private WorktreeLease Lease(string name, RunId? run = null) => FakeWorktreeManager.LeaseFor(_repo, run ?? Run, name, _repo.Base);

    private Task<CleanupReport> CleanAsync() => new WorkspaceCleanup(_worktrees, _gate).CleanAsync(Project, Run, Ct);

    /// <summary>
    /// A finished run as the git layer leaves it: worktrees T1, T2 and _integration with their hidden and start refs,
    /// and the run's branch with T1 and T2 merged. With <paramref name="delivered"/> the user's branch contains it.
    /// </summary>
    private (string T1, string T2, string Integration) FinishedRun(bool delivered = true)
    {
        var t1 = _repo.Commit(_repo.Base);
        var t2 = _repo.Commit(_repo.Base);
        var integration = _repo.Commit(_repo.Commit(_repo.Base, t1), t2);
        foreach (var (name, tip) in new[] { ("T1", t1), ("T2", t2), (IntegrationNames.IntegrationWorktree, _repo.Base) })
        {
            _repo.Refs[Hidden(name)] = tip;
            _repo.Refs[Start(name)] = _repo.Base;
            _worktrees.Owned.Add(Lease(name));
        }

        _repo.Refs[Branch] = integration;
        if (delivered)
        {
            _repo.UserHead = integration;
        }

        return (t1, t2, integration);
    }

    private IEnumerable<string> RefsOfTheRun() =>
        _repo.Refs.Keys.Where(k => k == Branch || k.StartsWith(IntegrationNames.RunRefPrefix(Run), StringComparison.Ordinal));

    [Fact]
    public async Task A_delivered_run_is_cleaned_without_asking()
    {
        FinishedRun();

        var report = await CleanAsync();

        Assert.Empty(_gate.Requests);
        Assert.Equal(3, report.RemovedWorktrees.Count);
        Assert.Equal(7, report.RemovedRefs.Count);
        Assert.Empty(report.Kept);
        Assert.Empty(RefsOfTheRun());
        Assert.All(_worktrees.Removals, r => Assert.False(r.DiscardChanges));
    }

    [Fact]
    public async Task The_candidate_of_a_verified_integration_goes_without_asking_and_a_rejected_one_needs_the_approval()
    {
        var (_, t2, integration) = FinishedRun(delivered: false);
        var verified = IntegrationNames.CandidateRef(Run, new TaskId("I1"));
        var rejected = IntegrationNames.CandidateRef(Run, new TaskId("I2"));
        _repo.Refs[verified] = integration;
        _repo.Refs[rejected] = _repo.Commit(integration, t2);
        _gate.Approve = false;

        var report = await CleanAsync();

        // The verified candidate is the head of the run's branch; the rejected one is in no branch: work not integrated.
        var request = Assert.Single(_gate.Requests);
        Assert.Contains(rejected, request.AffectedPaths);
        Assert.DoesNotContain(verified, request.AffectedPaths);
        Assert.Contains(verified, report.RemovedRefs);
        Assert.Contains(rejected, report.Kept);
    }

    [Fact]
    public async Task Only_the_run_being_cleaned_is_touched()
    {
        FinishedRun();
        var other = Lease("T1", OtherRun);
        _worktrees.Owned.Add(other);
        _repo.Refs[Hidden("T1", OtherRun)] = _repo.Commit(_repo.Base);
        _repo.Refs[IntegrationNames.BranchRef(OtherRun)] = _repo.Commit(_repo.Base);

        await CleanAsync();

        Assert.Equal([other], _worktrees.Owned);
        Assert.Equal(3, _worktrees.Removals.Count);
        Assert.True(_repo.Refs.ContainsKey(Hidden("T1", OtherRun)));
        Assert.True(_repo.Refs.ContainsKey(IntegrationNames.BranchRef(OtherRun)));
        Assert.DoesNotContain(_worktrees.Calls, c => c.Contains(OtherRun.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dirty_worktree_is_discarded_only_after_a_destructive_approval()
    {
        FinishedRun();
        _worktrees.Dirty.Add("T2");

        var report = await CleanAsync();

        var request = Assert.Single(_gate.Requests);
        Assert.Equal("discard-run-work", request.Action);
        Assert.Equal(RiskLevel.Destructive, request.Risk);
        Assert.Equal(("proyecto-1", Run.Value), (request.ProjectId, request.RunId));
        Assert.Contains(Lease("T2").Path, request.AffectedPaths);
        Assert.Contains(("T2", true), _worktrees.Removals);
        Assert.Empty(report.Kept);
        Assert.Empty(RefsOfTheRun());
    }

    [Fact]
    public async Task When_the_approval_is_denied_a_dirty_worktree_and_its_refs_stay()
    {
        FinishedRun();
        _worktrees.Dirty.Add("T2");
        _gate.Approve = false;

        var report = await CleanAsync();

        Assert.DoesNotContain(("T2", true), _worktrees.Removals);
        Assert.Contains(Lease("T2").Path, report.Kept);
        Assert.Contains(Hidden("T2"), report.Kept);
        Assert.Contains(Start("T2"), report.Kept);
        Assert.Equal([Hidden("T2"), Start("T2")], RefsOfTheRun().Order(StringComparer.Ordinal));
        Assert.Equal(2, report.RemovedWorktrees.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_branch_that_was_not_delivered_is_deleted_only_after_approval(bool approve)
    {
        FinishedRun(delivered: false);
        _gate.Approve = approve;

        var report = await CleanAsync();

        var request = Assert.Single(_gate.Requests);
        Assert.Contains(Branch, request.AffectedPaths);
        Assert.Equal(3, report.RemovedWorktrees.Count);
        if (approve)
        {
            Assert.Empty(RefsOfTheRun());
        }
        else
        {
            // The work of T1 and T2 survives in the branch that is kept, so their own refs can go.
            Assert.Equal([Branch], RefsOfTheRun());
            Assert.Equal([Branch], report.Kept);
        }
    }

    [Fact]
    public async Task Work_that_is_in_neither_the_users_branch_nor_the_run_branch_needs_approval()
    {
        FinishedRun();
        _repo.Refs[Hidden("T3")] = _repo.Commit(_repo.Base);
        _gate.Approve = false;

        var report = await CleanAsync();

        Assert.Equal([Hidden("T3")], Assert.Single(_gate.Requests).AffectedPaths);
        Assert.Equal([Hidden("T3")], report.Kept);
        Assert.Equal([Hidden("T3")], RefsOfTheRun());
    }

    [Fact]
    public async Task One_approval_covers_all_the_destructive_work()
    {
        FinishedRun();
        _worktrees.Dirty.Add("T1");
        _worktrees.Dirty.Add("T2");
        _repo.Refs[Hidden("T3")] = _repo.Commit(_repo.Base);

        await CleanAsync();

        var request = Assert.Single(_gate.Requests);
        Assert.Equal([Lease("T1").Path, Lease("T2").Path, Hidden("T3")], request.AffectedPaths);
    }

    [Fact]
    public async Task A_ref_outside_the_run_is_never_deleted_even_if_it_is_listed()
    {
        FinishedRun();
        _repo.Refs["refs/heads/main"] = _repo.UserHead;
        _worktrees.ExtraListedRefs.Add(new RefEntry("refs/heads/main", _repo.UserHead));

        var report = await CleanAsync();

        Assert.True(_repo.Refs.ContainsKey("refs/heads/main"));
        Assert.DoesNotContain(_worktrees.Calls, c => c == "delete refs/heads/main");
        Assert.DoesNotContain("refs/heads/main", report.RemovedRefs);
    }

    [Fact]
    public async Task A_ref_that_moves_after_it_was_listed_is_kept()
    {
        FinishedRun();
        var moved = _repo.Commit(_repo.Base);
        _worktrees.BeforeDelete = name =>
        {
            if (name == Hidden("T1"))
            {
                _repo.Refs[name] = moved;
            }
        };

        var report = await CleanAsync();

        Assert.Contains(Hidden("T1"), report.Kept);
        Assert.Equal(moved, _repo.Refs[Hidden("T1")]);
    }

    [Fact]
    public async Task A_worktree_still_in_use_is_kept_with_its_refs_and_the_rest_is_cleaned()
    {
        FinishedRun();
        _worktrees.InUse.Add("T1");

        var report = await CleanAsync();

        Assert.Contains(Lease("T1").Path, report.Kept);
        Assert.Contains(Hidden("T1"), report.Kept);
        Assert.Contains(Start("T1"), report.Kept);
        Assert.Equal([Hidden("T1"), Start("T1")], RefsOfTheRun().Order(StringComparer.Ordinal));
        Assert.Equal(2, report.RemovedWorktrees.Count);
    }
}
