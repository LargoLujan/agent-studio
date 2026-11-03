using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Integration;

public sealed class IntegratorTests
{
    private static readonly RunId Run = new("20261016-a1b2c3");

    private static readonly TaskId IntegrationTask = new("T9");

    private readonly FakeRepository _repo = new();
    private readonly InMemoryEvidenceStore _store = new();
    private readonly FakeVerificationRunner _verification = new();
    private readonly FakeWorktreeManager _worktrees;

    public IntegratorTests()
    {
        _worktrees = new FakeWorktreeManager(_repo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Branch => IntegrationNames.BranchRef(Run);

    private static string Candidate => IntegrationNames.CandidateRef(Run, IntegrationTask);

    private static IReadOnlyList<VerificationStep> Steps => DotNetVerificationProfile.Propose().Steps;

    private ProjectContext Project => new("proyecto-1", _repo.Root);

    private Integrator Integrator() => new(_worktrees, _verification, _store);

    /// <summary>A task that committed one change on the base, with its hidden ref.</summary>
    private IntegrationSource Committed(string id)
    {
        var reference = IntegrationNames.RunRefPrefix(Run) + id;
        _repo.Refs[reference] = _repo.Commit(_repo.Base);
        return new IntegrationSource(new TaskId(id), reference);
    }

    private IntegrationRequest Request(params IntegrationSource[] sources) =>
        new(Project, Run, IntegrationTask, 1, _repo.Base, sources, Steps);

    private Task<IntegrationResult> IntegrateAsync(IntegrationRequest request) => Integrator().IntegrateAsync(request, null, Ct);

    // ---- a clean integration ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_sources_merge_in_the_given_order_into_the_candidate_which_is_verified_in_the_integration_worktree()
    {
        var t2 = Committed("T2");
        var t1 = Committed("T1");

        var result = await IntegrateAsync(Request(t2, t1));

        Assert.Equal([(Branch, _repo.Base)], _worktrees.CreatedRefs);
        Assert.Equal([(Candidate, _repo.Base, _repo.Base)], _worktrees.Prepared);
        Assert.Equal(2, _worktrees.Merges.Count);
        var (first, second) = (_worktrees.Merges[0], _worktrees.Merges[1]);
        Assert.Equal((Candidate, _repo.Base, _repo.Refs[t2.Ref]), (first.Target, first.Expected, first.Source));
        Assert.Equal((Candidate, first.Result, _repo.Refs[t1.Ref]), (second.Target, second.Expected, second.Source));
        Assert.Contains("T2", first.Message, StringComparison.Ordinal);

        Assert.True(result.Succeeded);
        Assert.Empty(result.ConflictPaths);
        Assert.Equal(second.Result, result.ResultCommit);
        Assert.Equal(_repo.Refs[Candidate], result.ResultCommit);
        Assert.True(_repo.IsAncestor(_repo.Refs[t1.Ref], result.ResultCommit!) && _repo.IsAncestor(_repo.Refs[t2.Ref], result.ResultCommit!));

        var ensured = Assert.Single(_worktrees.Ensured);
        Assert.Equal((Run, IntegrationNames.IntegrationWorktree, _repo.Base), (ensured.Run, ensured.Name, ensured.BaseCommit));
        Assert.Equal([(IntegrationNames.IntegrationWorktree, result.ResultCommit!)], _worktrees.Checkouts);

        var verification = Assert.Single(_verification.Runs);
        Assert.Equal(FakeWorktreeManager.LeaseFor(_repo, Run, IntegrationNames.IntegrationWorktree, _repo.Base).Path, verification.WorkingDirectory);
        Assert.Equal(["build", "test"], verification.Steps.Select(s => s.Id));
        // The evidence of the verification is bound to the candidate it verified: the commit under review.
        // The repository of the project rides along: an isolated step copies the candidate from its objects, not from _integration.
        Assert.Equal(new EvidenceScope("proyecto-1", Run, IntegrationTask, 1, result.ResultCommit, _repo.Root), verification.Scope);
        Assert.Equal(["build", "test"], result.Verification.Select(v => v.StepId));
        Assert.Equal(result.Verification.Select(v => v.Evidence.Id), result.Evidence);
    }

    [Fact]
    public async Task The_run_branch_moves_only_once_every_step_passed_by_a_fast_forward_to_the_verified_candidate()
    {
        var t1 = Committed("T1");
        string? branchWhileVerifying = null;
        _verification.OnRun = _ => branchWhileVerifying = _repo.Refs[Branch];

        var result = await IntegrateAsync(Request(t1));

        Assert.True(result.Succeeded);
        Assert.Equal(_repo.Base, branchWhileVerifying);
        Assert.Equal([(_repo.Base, result.ResultCommit!)], _worktrees.FastForwards);

        // Afterwards the three coincide: the head of the run's branch, the commit the tests verified and the result.
        Assert.Equal(result.ResultCommit, _repo.Refs[Branch]);
        Assert.Equal(result.ResultCommit, Assert.Single(_verification.Runs).Scope.SubjectCommit);
    }

    [Fact]
    public async Task Integrating_again_merges_nothing_new_and_gives_the_same_commit()
    {
        var t1 = Committed("T1");
        var t2 = Committed("T2");
        var first = await IntegrateAsync(Request(t1, t2));
        var commits = _repo.CommitCount;

        var second = await IntegrateAsync(Request(t1, t2));

        Assert.True(second.Succeeded);
        Assert.Equal(first.ResultCommit, second.ResultCommit);
        Assert.Equal(first.ResultCommit, _repo.Refs[Branch]);
        Assert.Equal(commits, _repo.CommitCount);
        Assert.Single(_worktrees.CreatedRefs);
    }

    [Fact]
    public async Task Without_sources_the_base_itself_is_verified_and_the_run_branch_stays_on_it()
    {
        var result = await IntegrateAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(_repo.Base, result.ResultCommit);
        Assert.Empty(_worktrees.Merges);
        Assert.Equal([(IntegrationNames.IntegrationWorktree, _repo.Base)], _worktrees.Checkouts);
        Assert.Equal(_repo.Base, _repo.Refs[Branch]);
        Assert.Empty(_worktrees.FastForwards);
    }

    // ---- conflicts --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_conflict_stops_the_integration_with_a_conflict_report_and_nothing_half_merged()
    {
        var t1 = Committed("T1");
        var t2 = Committed("T2");
        var t3 = Committed("T3");
        _repo.Conflicts[_repo.Refs[t2.Ref]] = (["shared.txt"], "Auto-merging shared.txt\nCONFLICT (content): Merge conflict in shared.txt");

        var result = await IntegrateAsync(Request(t1, t2, t3));

        Assert.False(result.Succeeded);
        Assert.Equal(["shared.txt"], result.ConflictPaths);
        Assert.Equal([_repo.Refs[t1.Ref], _repo.Refs[t2.Ref]], _worktrees.Merges.Select(m => m.Source));

        // The clean merge before the conflict is the partial head, which only the candidate holds: it was never
        // verified, so the run's branch did not move.
        Assert.Equal(_worktrees.Merges[0].Result, result.ResultCommit);
        Assert.Equal(_repo.Refs[Candidate], result.ResultCommit);
        Assert.False(_repo.IsAncestor(_repo.Refs[t2.Ref], result.ResultCommit!));
        Assert.Equal(_repo.Base, _repo.Refs[Branch]);
        Assert.Empty(_worktrees.FastForwards);
        Assert.Empty(_worktrees.Checkouts);
        Assert.Empty(_verification.Runs);
        Assert.Empty(result.Verification);

        var report = _store.Single(Assert.Single(result.Evidence));
        Assert.Equal(EvidenceKind.ConflictReport, report.Draft.Kind);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, report.Draft.Origin);
        Assert.Equal((Run.Value, "T9", 1), (report.Draft.RunId, report.Draft.TaskId, report.Draft.Attempt));
        Assert.Equal("proyecto-1", report.ProjectId);
        var text = _store.Text(report.Record.Id);
        Assert.Contains("CONFLICT (content): Merge conflict in shared.txt", text, StringComparison.Ordinal);
        Assert.Contains("# candidate: " + Candidate + " at " + result.ResultCommit, text, StringComparison.Ordinal);
        Assert.Contains("# run branch: " + Branch + " stays at " + _repo.Base, text, StringComparison.Ordinal);
        Assert.Contains("# conflicting source: T2 " + t2.Ref, text, StringComparison.Ordinal);
        Assert.Contains("# merged before it: T1", text, StringComparison.Ordinal);
        Assert.Contains("# not attempted: T3", text, StringComparison.Ordinal);
        Assert.Contains("\nshared.txt\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_conflict_is_reported_again_and_nothing_moves_when_integrating_again()
    {
        var t1 = Committed("T1");
        var t2 = Committed("T2");
        _repo.Conflicts[_repo.Refs[t2.Ref]] = (["shared.txt"], "CONFLICT (content): Merge conflict in shared.txt");
        var first = await IntegrateAsync(Request(t1, t2));
        var commits = _repo.CommitCount;

        var second = await IntegrateAsync(Request(t1, t2));

        Assert.False(second.Succeeded);
        Assert.Equal(first.ResultCommit, second.ResultCommit);
        Assert.Equal(first.ConflictPaths, second.ConflictPaths);
        Assert.Equal(commits, _repo.CommitCount);
        Assert.Equal(_repo.Base, _repo.Refs[Branch]);
        Assert.Empty(_worktrees.Checkouts);
    }

    // ---- verification -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_verification_leaves_the_run_branch_where_it_was_and_keeps_the_rejected_candidate_as_evidence()
    {
        var t1 = Committed("T1");
        _verification.Results = steps => [FakeVerificationRunner.Pass(steps[0]), FakeVerificationRunner.Fail(steps[1])];

        var result = await IntegrateAsync(Request(t1));

        Assert.False(result.Succeeded);
        Assert.Equal(_repo.Refs[Candidate], result.ResultCommit);
        Assert.True(_repo.IsAncestor(_repo.Refs[t1.Ref], result.ResultCommit!));
        Assert.Equal(_repo.Base, _repo.Refs[Branch]);
        Assert.Empty(_worktrees.FastForwards);
        Assert.Equal(2, result.Verification.Count);

        // The verification's own evidence, then a report of the candidate that was rejected.
        Assert.Equal(3, result.Evidence.Count);
        Assert.Equal(result.Verification.Select(v => v.Evidence.Id), result.Evidence.Take(2));
        var report = _store.Single(result.Evidence[2]);
        Assert.Equal(EvidenceKind.Document, report.Draft.Kind);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, report.Draft.Origin);
        Assert.Equal(result.ResultCommit, report.Draft.SubjectCommit);
        Assert.Equal((Run.Value, "T9", 1), (report.Draft.RunId, report.Draft.TaskId, report.Draft.Attempt));
        var text = _store.Text(report.Record.Id);
        Assert.Contains("# candidate: " + Candidate + " at " + result.ResultCommit, text, StringComparison.Ordinal);
        Assert.Contains("# run branch: " + Branch + " stays at " + _repo.Base, text, StringComparison.Ordinal);
        Assert.Contains("# merged: T1 " + _repo.Refs[t1.Ref], text, StringComparison.Ordinal);
        Assert.Contains("build: passed", text, StringComparison.Ordinal);
        Assert.Contains("test: failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verification_that_stopped_before_the_last_step_fails_the_integration_and_moves_nothing()
    {
        var t1 = Committed("T1");
        _verification.Results = steps => [FakeVerificationRunner.Pass(steps[0])];

        var result = await IntegrateAsync(Request(t1));

        Assert.False(result.Succeeded);
        Assert.Equal(_repo.Base, _repo.Refs[Branch]);
        Assert.Empty(_worktrees.FastForwards);
        Assert.Contains("test: did not run", _store.Text(result.Evidence[^1]), StringComparison.Ordinal);
    }

    /// <summary>
    /// The result carries the record of every evidence it names, exactly as it was captured (by the verification, or by
    /// the integrator as the store returned it): the engine anchors those, never a row it would read back later.
    /// </summary>
    [Theory]
    [InlineData("clean")]
    [InlineData("conflict")]
    [InlineData("rejected")]
    public async Task The_record_of_every_evidence_of_an_integration_is_handed_back_as_it_was_captured(string outcome)
    {
        var t1 = Committed("T1");
        var t2 = Committed("T2");
        if (outcome == "conflict")
        {
            _repo.Conflicts[_repo.Refs[t2.Ref]] = (["shared.txt"], "CONFLICT (content): Merge conflict in shared.txt");
        }
        else if (outcome == "rejected")
        {
            _verification.Results = steps => [FakeVerificationRunner.Pass(steps[0]), FakeVerificationRunner.Fail(steps[1])];
        }

        var result = await IntegrateAsync(Request(t1, t2));

        Assert.NotEmpty(result.Evidence);
        Assert.NotNull(result.EvidenceRecords);
        Assert.Equal(result.Evidence, result.EvidenceRecords.Select(r => r.Id));
        var verified = result.Verification.Select(v => v.Evidence).ToList();
        Assert.All(result.EvidenceRecords, r => Assert.Same(verified.FirstOrDefault(v => v.Id == r.Id) ?? _store.Single(r.Id).Record, r));
    }

    [Fact]
    public async Task A_failed_verification_after_a_verified_integration_leaves_the_run_branch_at_that_integration()
    {
        var t1 = Committed("T1");
        var verified = await IntegrateAsync(Request(t1));
        var t2 = Committed("T2");
        var next = new TaskId("T8");
        _verification.Results = steps => [FakeVerificationRunner.Pass(steps[0]), FakeVerificationRunner.Fail(steps[1])];

        var rejected = await IntegrateAsync(new IntegrationRequest(Project, Run, next, 1, _repo.Base, [t1, t2], Steps));

        Assert.False(rejected.Succeeded);
        Assert.Equal(verified.ResultCommit, _repo.Refs[Branch]);
        Assert.Equal([(Candidate, _repo.Base, _repo.Base), (IntegrationNames.CandidateRef(Run, next), verified.ResultCommit!, verified.ResultCommit!)], _worktrees.Prepared);
        Assert.True(_repo.IsAncestor(verified.ResultCommit!, rejected.ResultCommit!));
        Assert.Equal(rejected.ResultCommit, _repo.Refs[IntegrationNames.CandidateRef(Run, next)]);
    }

    [Fact]
    public async Task A_run_branch_that_moved_during_the_verification_is_not_fast_forwarded()
    {
        var t1 = Committed("T1");
        var elsewhere = _repo.Commit(_repo.Base);
        _verification.OnRun = _ => _repo.Refs[Branch] = elsewhere;

        await Assert.ThrowsAsync<InvalidOperationException>(() => IntegrateAsync(Request(t1)));

        Assert.Equal(elsewhere, _repo.Refs[Branch]);
        Assert.Empty(_worktrees.FastForwards);
    }

    // ---- resuming ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_crash_between_the_verification_and_the_fast_forward_is_completed_on_resume_with_the_same_candidate_verified_once_more()
    {
        var t1 = Committed("T1");
        var t2 = Committed("T2");
        _worktrees.BeforeFastForward = () => throw new IOException("The app closed.");
        await Assert.ThrowsAsync<IOException>(() => IntegrateAsync(Request(t1, t2)));
        var candidate = _repo.Refs[Candidate];
        var commits = _repo.CommitCount;
        Assert.Equal(_repo.Base, _repo.Refs[Branch]);
        Assert.Single(_verification.Runs);
        _worktrees.BeforeFastForward = null;

        var resumed = await IntegrateAsync(Request(t1, t2) with { Attempt = 2 });

        Assert.True(resumed.Succeeded);
        Assert.Equal(candidate, resumed.ResultCommit);
        Assert.Equal(candidate, _repo.Refs[Branch]);
        Assert.Equal(commits, _repo.CommitCount);
        Assert.Equal([(_repo.Base, candidate)], _worktrees.FastForwards);

        // The resumed attempt verified the candidate once: what the crashed attempt verified never reached the log.
        Assert.Equal(2, _verification.Runs.Count);
        Assert.Equal(new EvidenceScope("proyecto-1", Run, IntegrationTask, 2, candidate, _repo.Root), _verification.Runs[1].Scope);
    }

    [Fact]
    public async Task A_crash_after_the_fast_forward_is_detected_on_resume_and_the_run_branch_stays_on_the_verified_candidate()
    {
        var t1 = Committed("T1");
        var first = await IntegrateAsync(Request(t1));
        var commits = _repo.CommitCount;

        // The engine never recorded the result, so the integration runs again.
        var resumed = await IntegrateAsync(Request(t1) with { Attempt = 2 });

        Assert.True(resumed.Succeeded);
        Assert.Equal(first.ResultCommit, resumed.ResultCommit);
        Assert.Equal(first.ResultCommit, _repo.Refs[Branch]);
        Assert.Equal(commits, _repo.CommitCount);
        Assert.Single(_worktrees.FastForwards);
        Assert.All(_worktrees.Merges.Skip(1), m => Assert.Equal(first.ResultCommit, m.Result));
    }

    // ---- refused ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_source_ref_fails_before_anything_is_merged()
    {
        var t1 = Committed("T1");
        var ghost = new IntegrationSource(new TaskId("T5"), IntegrationNames.RunRefPrefix(Run) + "T5");

        await Assert.ThrowsAsync<InvalidOperationException>(() => IntegrateAsync(Request(t1, ghost)));

        Assert.Empty(_worktrees.Merges);
        Assert.Empty(_worktrees.Prepared);
        Assert.False(_repo.Refs.ContainsKey(Branch));
    }

    [Fact]
    public async Task A_run_branch_that_does_not_descend_from_the_base_is_never_merged_into()
    {
        var foreign = _repo.Commit();
        _repo.Refs[Branch] = foreign;
        var t1 = Committed("T1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => IntegrateAsync(Request(t1)));

        Assert.Empty(_worktrees.Merges);
        Assert.Empty(_worktrees.Prepared);
        Assert.Equal(foreign, _repo.Refs[Branch]);
    }

    public static TheoryData<string> UnsafeRequests() => ["duplicate-task", "duplicate-ref", "no-verification", "invalid-verification"];

    [Theory]
    [MemberData(nameof(UnsafeRequests))]
    public async Task Requests_that_cannot_be_integrated_safely_are_refused_before_git_is_touched(string problem)
    {
        var t1 = Committed("T1");
        var t2 = Committed("T2");
        var request = problem switch
        {
            "duplicate-task" => Request(t1, t2 with { Task = t1.Task }),
            "duplicate-ref" => Request(t1, t2 with { Ref = t1.Ref }),
            "no-verification" => Request(t1) with { Verification = [] },
            _ => Request(t1) with { Verification = [Steps[1] with { Report = TestReportFormat.None, ReportPath = null }] },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => IntegrateAsync(request));

        Assert.Empty(_worktrees.Calls);
        Assert.Empty(_worktrees.Merges);
        Assert.Empty(_verification.Runs);
    }
}
