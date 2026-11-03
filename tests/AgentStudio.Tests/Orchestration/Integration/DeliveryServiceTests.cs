using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Tests.Orchestration.Integration;

public sealed class DeliveryServiceTests
{
    private static readonly RunId Run = new("20261016-a1b2c3");

    private readonly FakeRepository _repo = new();
    private readonly RecordingApprovalGate _gate = new();
    private readonly DeliveryRecords _records = new("proyecto-1");
    private readonly FakeWorktreeManager _worktrees;

    public DeliveryServiceTests()
    {
        _worktrees = new FakeWorktreeManager(_repo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectContext Project => new("proyecto-1", _repo.Root);

    private DeliveryService Service() => new(_worktrees, _gate, _records.Recorder, _records.Verdicts);

    /// <summary>The run's branch, one commit ahead of the base, and a run the log shows succeeded with that integration approved.</summary>
    private async Task<string> IntegrationAsync()
    {
        var commit = BranchAhead();
        await _records.SucceededAsync(Run, commit);
        return commit;
    }

    /// <summary>Only the run's branch, one commit ahead of the base: what the log says about it is up to the test.</summary>
    private string BranchAhead()
    {
        var commit = _repo.Commit(_repo.Base);
        _repo.Refs[IntegrationNames.BranchRef(Run)] = commit;
        return commit;
    }

    private Task<DeliveryResult> DeliverAsync(DeliveryMode mode = DeliveryMode.FastForwardOnly) => Service().DeliverAsync(Project, Run, mode, Ct);

    /// <summary>Nothing was asked and nothing reached the user's branch.</summary>
    private void AssertNothingDelivered(DeliveryResult result)
    {
        Assert.False(result.Delivered, result.Message);
        Assert.Null(result.ResultCommit);
        Assert.Empty(_gate.Requests);
        Assert.Empty(_worktrees.Deliveries);
        Assert.Equal(_repo.Base, _repo.UserHead);
    }

    // ---- only the integration the judge was shown ------------------------------------------------------------------

    [Fact]
    public async Task An_integration_that_landed_while_the_judge_reviewed_an_earlier_one_is_never_delivered()
    {
        // The judge was shown I1 (its dossier was built on the log at that head); I2 landed while it reviewed, and its
        // verdict came after I2's entry. By the order of the log alone the last integration before the verdict is I2.
        var shown = _repo.Commit(_repo.Base);
        await _records.IntegratedAsync(Run, shown);
        await _records.ReviewWorktreeAsync(Run, shown);
        var dossierHead = _records.Head;
        var later = BranchAhead();
        await _records.IntegratedAsync(Run, later);
        var verdict = await _records.VerdictAsync(Run, head: dossierHead);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, later, verdict);

        var readiness = await Service().CheckAsync(Project, Run, Ct);
        var result = await DeliverAsync();

        Assert.False(readiness.Ready);
        Assert.Contains(shown[..10], readiness.Reason, StringComparison.Ordinal);
        Assert.Contains(later[..10], readiness.Reason, StringComparison.Ordinal);
        AssertNothingDelivered(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Of_two_reviews_the_verdict_of_the_one_that_finished_last_decides_and_the_refusal_says_what_happened(bool firstFinishesLast)
    {
        // R1 started on I1 and was shown I1; I2 landed; R2 started on I2 and was shown I2. The run ends with the verdict of
        // the review that finished last (OrchestrationRun keeps the last one): R2 did review I2, but if R1 finished last,
        // the verdict the run ended with is about I1.
        var first = _repo.Commit(_repo.Base);
        await _records.IntegratedAsync(Run, first);
        await _records.ReviewWorktreeAsync(Run, first, "R1");
        var shownToR1 = _records.Head;
        var last = BranchAhead();
        await _records.IntegratedAsync(Run, last);
        string ending;
        if (firstFinishesLast)
        {
            await _records.VerdictAsync(Run, review: "R2");
            ending = await _records.VerdictAsync(Run, head: shownToR1, review: "R1");
        }
        else
        {
            await _records.VerdictAsync(Run, head: shownToR1, review: "R1");
            ending = await _records.VerdictAsync(Run, review: "R2");
        }

        await _records.FinishedAsync(Run, RunPhase.Succeeded, last, ending);

        var readiness = await Service().CheckAsync(Project, Run, Ct);

        if (firstFinishesLast)
        {
            Assert.False(readiness.Ready);
            Assert.Contains("R1", readiness.Reason, StringComparison.Ordinal);
            Assert.Contains(first[..10], readiness.Reason, StringComparison.Ordinal);
            Assert.Contains(last[..10], readiness.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("ningún juez", readiness.Reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(readiness.Ready, readiness.Reason);
            Assert.Equal(last, readiness.Commit);
        }
    }

    [Fact]
    public async Task A_judge_shown_the_last_integration_but_working_on_an_earlier_one_never_gets_it_delivered()
    {
        // The review started on I1 (its worktree, the code the judge has in front of it), and I2 landed before its dossier
        // was built, so the dossier named I2: the judge read about I2 but worked on I1.
        var first = _repo.Commit(_repo.Base);
        await _records.IntegratedAsync(Run, first);
        await _records.ReviewWorktreeAsync(Run, first);
        var last = BranchAhead();
        await _records.IntegratedAsync(Run, last);
        var verdict = await _records.VerdictAsync(Run);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, last, verdict);

        var readiness = await Service().CheckAsync(Project, Run, Ct);
        var result = await DeliverAsync();

        Assert.False(readiness.Ready);
        Assert.Contains(first[..10], readiness.Reason, StringComparison.Ordinal);
        Assert.Contains(last[..10], readiness.Reason, StringComparison.Ordinal);
        AssertNothingDelivered(result);
    }

    [Fact]
    public async Task A_verdict_whose_review_left_no_worktree_in_the_log_is_never_delivered()
    {
        // The worktree of another task on the very integration does not count: it is not the code of this judge.
        var integration = BranchAhead();
        await _records.IntegratedAsync(Run, integration);
        await _records.ReviewWorktreeAsync(Run, integration, "T1");
        var verdict = await _records.StoreVerdictAsync(Run, VerdictDecision.Approved, valid: true);
        await _records.AnnounceVerdictAsync(Run, verdict, VerdictDecision.Approved, valid: true);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, integration, verdict);

        var readiness = await Service().CheckAsync(Project, Run, Ct);

        Assert.False(readiness.Ready);
        Assert.Contains("espacio de trabajo", readiness.Reason, StringComparison.Ordinal);
        AssertNothingDelivered(await DeliverAsync());
    }

    [Fact]
    public async Task The_commit_a_judge_was_shown_is_the_dossiers_candidate_at_every_head_of_the_log()
    {
        // ShownCommit mirrors RunLog.CandidateCommit, which builds the dossier and which Orchestration cannot reference
        // (ADR-0002): on every head of a log that mixes two runs and every kind of integration, both name the same commit.
        var other = new RunId("20261016-ffffff");
        var random = new Random(26103002);
        for (var i = 0; i < 80; i++)
        {
            var run = random.Next(3) == 0 ? other : Run;
            var target = random.Next(6) switch
            {
                0 => IntegrationNames.CandidateRef(run, new TaskId("T2")), // a candidate that failed its verification (ADR-0012)
                1 => "refs/heads/main",
                2 => IntegrationNames.BranchName(run == Run ? other : Run),
                _ => IntegrationNames.BranchName(run),
            };
            var commit = random.Next(5) == 0 ? null : _repo.Commit(_repo.Base);
            IReadOnlyList<string> conflicts = random.Next(5) == 0 ? ["src/Calc/Calc.cs"] : [];
            await _records.RecordAsync(
                DecisionKind.IntegrationPerformed,
                new IntegrationPerformedPayload(new TaskId("T2"), target, [], commit, conflicts, IsDelivery: random.Next(6) == 0) { RunId = run.Value });
            if (random.Next(4) == 0)
            {
                await _records.ReviewWorktreeAsync(run, commit ?? _repo.Base);
            }
        }

        var log = _records.Entries;
        for (var p = 0; p <= log.Count; p++)
        {
            var head = p == 0 ? DecisionLog.GenesisHash : log[p - 1].Hash;
            foreach (var run in new[] { Run, other })
            {
                Assert.Equal(global::AgentStudio.Debate.Judging.RunLog.CandidateCommit(global::AgentStudio.Debate.Judging.RunLog.EntriesOf(log.Take(p), run.Value)), DeliveryService.ShownCommit(log, head, run));
            }
        }

        // Not only nulls are compared: the runs do have candidates.
        Assert.All(new[] { Run, other }, run => Assert.NotNull(DeliveryService.ShownCommit(log, log[^1].Hash, run)));
    }

    [Fact]
    public async Task The_same_integrations_are_delivered_when_the_judge_was_shown_the_last_one()
    {
        await _records.IntegratedAsync(Run, _repo.Commit(_repo.Base));
        var last = BranchAhead();
        await _records.IntegratedAsync(Run, last);
        var verdict = await _records.VerdictAsync(Run, head: _records.Head);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, last, verdict);

        var readiness = await Service().CheckAsync(Project, Run, Ct);

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(last, readiness.Commit);
    }

    [Fact]
    public async Task A_verdict_that_echoes_no_head_of_the_log_names_no_integration_and_is_never_delivered()
    {
        var integration = BranchAhead();
        await _records.IntegratedAsync(Run, integration);
        var verdict = await _records.VerdictAsync(Run, head: new string('e', 64));
        await _records.FinishedAsync(Run, RunPhase.Succeeded, integration, verdict);

        var readiness = await Service().CheckAsync(Project, Run, Ct);

        Assert.False(readiness.Ready);
        Assert.Null(readiness.Commit);
    }

    // ---- the delivery ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fast_forward_delivery_asks_for_a_critical_approval_and_then_moves_the_users_branch()
    {
        var integration = await IntegrationAsync();
        _worktrees.ChangedPaths = ["src/Calc/Calc.cs"];

        var result = await DeliverAsync();

        var request = Assert.Single(_gate.Requests);
        Assert.Equal("deliver-integration", request.Action);
        Assert.Equal(RiskLevel.Critical, request.Risk);
        Assert.Equal(("proyecto-1", Run.Value), (request.ProjectId, request.RunId));
        Assert.Null(request.RequestedBy);
        Assert.Equal(["src/Calc/Calc.cs"], request.AffectedPaths);
        Assert.Contains("main", request.Description, StringComparison.Ordinal);
        Assert.Contains("--ff-only", request.Description, StringComparison.Ordinal);
        Assert.Equal([(_repo.Base, integration)], _worktrees.ChangedPathsCalls);

        var delivery = Assert.Single(_worktrees.Deliveries);
        Assert.Equal((integration, DeliveryMode.FastForwardOnly), (delivery.Commit, delivery.Mode));
        Assert.True(result.Delivered, result.Message);
        Assert.Equal(integration, result.ResultCommit);
        Assert.Equal("main", result.TargetBranch);
        Assert.Equal(integration, _repo.UserHead);
    }

    [Fact]
    public async Task The_delivery_carries_the_request_that_was_asked_and_its_answer_and_the_exact_approved_commits()
    {
        var integration = await IntegrationAsync();
        _worktrees.ChangedPaths = ["src/Calc/Calc.cs"];

        await DeliverAsync();

        var delivery = Assert.Single(_worktrees.Deliveries);
        Assert.Same(Assert.Single(_gate.Requests), delivery.Request);
        Assert.True(delivery.Decision.Approved);
        Assert.Equal("usuario", delivery.Decision.DecidedBy);
        Assert.Equal((_repo.Root, "main", _repo.Base, integration), (delivery.RepositoryRoot, delivery.TargetBranch, delivery.ExpectedHead, delivery.Commit));
    }

    [Fact]
    public async Task The_approval_is_asked_for_exactly_the_delivery_that_is_made_and_a_new_one_for_each_delivery()
    {
        var integration = await IntegrationAsync();

        await DeliverAsync();
        _repo.UserHead = _repo.Base;
        await DeliverAsync();

        Assert.Equal(2, _gate.Requests.Count);
        Assert.All(_gate.Requests, r => Assert.NotNull(r.Binding));
        Assert.NotEqual(_gate.Requests[0].Binding, _gate.Requests[1].Binding);
        foreach (var (request, delivery) in _gate.Requests.Zip(_worktrees.Deliveries))
        {
            Assert.True(DeliveryApproval.Matches(request.Binding, delivery));
            Assert.Equal((_repo.Root, "main", _repo.Base, integration, DeliveryMode.FastForwardOnly), (delivery.RepositoryRoot, delivery.TargetBranch, delivery.ExpectedHead, delivery.Commit, delivery.Mode));
            Assert.Equal(request.Binding, delivery.Decision.Answers);
        }
    }

    [Fact]
    public async Task A_yes_that_does_not_answer_the_request_asked_delivers_nothing()
    {
        await IntegrationAsync();
        _gate.Answers = _ => "delivery-v1:otra-peticion";

        var result = await DeliverAsync();

        Assert.Single(_gate.Requests);
        Assert.Empty(_worktrees.Deliveries);
        Assert.False(result.Delivered);
        Assert.Equal(_repo.Base, _repo.UserHead);
    }

    [Theory]
    [InlineData(DeliveryMode.FastForwardOnly, "git merge --ff-only ")]
    [InlineData(DeliveryMode.MergeCommit, "git merge --no-ff ")]
    public async Task The_approval_says_hooks_and_signing_are_not_run_and_names_the_exact_manual_merge(DeliveryMode mode, string manual)
    {
        // R3-05: the delivery runs like any internal command, so the person approving it is told, and is given the exact
        // command to run by hand after reviewing if they want their hooks or signature.
        var integration = await IntegrationAsync();
        if (mode == DeliveryMode.MergeCommit)
        {
            _repo.UserHead = _repo.Commit(_repo.Base);
        }

        _worktrees.ChangedPaths = ["src/Calc/Calc.cs", ".husky/pre-commit"];

        await DeliverAsync(mode);

        var request = Assert.Single(_gate.Requests);
        Assert.Contains("NO ejecuta tus hooks ni firma", request.Description, StringComparison.Ordinal);
        Assert.Contains(manual + integration, request.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("hook-paths", _worktrees.Calls);
    }

    [Fact]
    public async Task A_delivery_the_git_layer_would_refuse_is_refused_before_any_approval_is_asked_or_spent()
    {
        // The user's configuration names a callback the allowlist refuses, so the delivery merge would be refused (R3-05).
        var integration = await IntegrationAsync();
        _worktrees.ChangedPaths = ["src/Calc/Calc.cs"];
        _worktrees.DeliveryRefusals =
        [
            new DeliveryRefusal("filter.format.clean", "file:.git/config", "its program 'sh' is an interpreter or shell, which runs code from its arguments"),
        ];

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
        Assert.Contains($"delivery-refusals {_repo.Base} {integration}", _worktrees.Calls);
        Assert.Equal("main", result.TargetBranch);
        Assert.Contains("«filter.format.clean»", result.Message, StringComparison.Ordinal);
        Assert.Contains("interpreter", result.Message, StringComparison.Ordinal);
        Assert.Contains("No se ha pedido ninguna aprobación", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_git_layer_would_refuse_is_asked_for_exactly_this_delivery_before_the_approval()
    {
        var integration = await IntegrationAsync();
        var moved = _repo.Commit(_repo.Base);
        _repo.UserHead = moved;
        var askedBefore = false;
        _gate.WhilePending = _ => askedBefore = _worktrees.Calls.Contains($"delivery-refusals {moved} {integration}");

        var result = await DeliverAsync(DeliveryMode.MergeCommit);

        Assert.True(result.Delivered, result.Message);
        Assert.True(askedBefore);
    }

    [Fact]
    public async Task A_denied_delivery_leaves_the_users_branch_alone()
    {
        await IntegrationAsync();
        _gate.Approve = false;

        var result = await DeliverAsync();

        Assert.Single(_gate.Requests);
        Assert.Empty(_worktrees.Deliveries);
        Assert.False(result.Delivered);
        Assert.Null(result.ResultCommit);
        Assert.Equal(_repo.Base, _repo.UserHead);
        Assert.Contains("usuario", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_detached_HEAD_is_never_delivered_to()
    {
        await IntegrationAsync();
        _repo.UserBranch = null;

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
    }

    [Fact]
    public async Task Without_a_run_branch_there_is_nothing_to_deliver()
    {
        await IntegrationAsync();
        _repo.Refs.Remove(IntegrationNames.BranchRef(Run));

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
    }

    [Fact]
    public async Task An_integration_the_users_branch_already_contains_counts_as_delivered_without_asking()
    {
        var integration = await IntegrationAsync();
        _repo.UserHead = _repo.Commit(integration);

        var result = await DeliverAsync();

        Assert.True(result.Delivered);
        Assert.Equal(_repo.UserHead, result.ResultCommit);
        Assert.Empty(_gate.Requests);
        Assert.Empty(_worktrees.Deliveries);
    }

    [Fact]
    public async Task A_fast_forward_that_is_no_longer_possible_is_refused_without_asking()
    {
        await IntegrationAsync();
        var moved = _repo.Commit(_repo.Base);
        _repo.UserHead = moved;

        var result = await DeliverAsync();

        Assert.False(result.Delivered);
        Assert.Empty(_gate.Requests);
        Assert.Empty(_worktrees.Deliveries);
        Assert.Equal(moved, _repo.UserHead);
    }

    [Fact]
    public async Task A_merge_commit_delivery_works_when_the_users_branch_moved_on()
    {
        var integration = await IntegrationAsync();
        var moved = _repo.Commit(_repo.Base);
        _repo.UserHead = moved;

        var result = await DeliverAsync(DeliveryMode.MergeCommit);

        var request = Assert.Single(_gate.Requests);
        Assert.Equal(RiskLevel.Critical, request.Risk);
        var delivery = Assert.Single(_worktrees.Deliveries);
        Assert.Equal((integration, DeliveryMode.MergeCommit, moved), (delivery.Commit, delivery.Mode, delivery.ExpectedHead));
        Assert.Contains(Run.Value, delivery.Message, StringComparison.Ordinal);
        Assert.True(result.Delivered, result.Message);
        Assert.True(_repo.IsAncestor(integration, result.ResultCommit!) && _repo.IsAncestor(moved, result.ResultCommit!));
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("branch")]
    public async Task A_users_branch_that_changes_while_the_approval_is_pending_is_not_touched(string change)
    {
        await IntegrationAsync();
        _gate.WhilePending = _ =>
        {
            if (change == "commit")
            {
                _repo.UserHead = _repo.Commit(_repo.UserHead);
            }
            else
            {
                _repo.UserBranch = "feature";
            }
        };

        var result = await DeliverAsync();

        Assert.Single(_gate.Requests);
        Assert.Empty(_worktrees.Deliveries);
        Assert.False(result.Delivered);
    }

    [Fact]
    public async Task A_failed_merge_is_reported_and_not_called_a_delivery()
    {
        await IntegrationAsync();
        _worktrees.FailedDelivery = new UserBranchDelivery(
            UserBranchDeliveryStatus.GitFailed,
            _repo.UserHead,
            [],
            "error: Your local changes to the following files would be overwritten by merge:\n\tsrc/Calc/Calc.cs");

        var result = await DeliverAsync();

        Assert.False(result.Delivered);
        Assert.Null(result.ResultCommit);
        Assert.Contains("would be overwritten", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_records.Payloads<IntegrationPerformedPayload>(), p => p.IsDelivery);
    }

    [Theory]
    [InlineData(UserBranchDeliveryStatus.DirtyPaths, "src/Calc/Calc.cs")]
    [InlineData(UserBranchDeliveryStatus.Conflicts, "src/Calc/Calc.cs")]
    [InlineData(UserBranchDeliveryStatus.IncompleteMerge, "git merge --abort")]
    [InlineData(UserBranchDeliveryStatus.RepositoryChanged, "cambió")]
    public async Task What_stopped_the_delivery_in_the_git_layer_is_told_to_the_user_and_nothing_counts_as_delivered(UserBranchDeliveryStatus status, string mention)
    {
        await IntegrationAsync();
        _worktrees.FailedDelivery = new UserBranchDelivery(status, _repo.UserHead, ["src/Calc/Calc.cs"], "salida de git");

        var result = await DeliverAsync(DeliveryMode.MergeCommit);

        Assert.False(result.Delivered);
        Assert.Null(result.ResultCommit);
        Assert.Contains(mention, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uncommitted_changes_of_the_user_are_mentioned_in_the_approval()
    {
        await IntegrationAsync();
        _repo.UserHasUncommittedChanges = true;

        await DeliverAsync();

        Assert.Contains("sin confirmar", Assert.Single(_gate.Requests).Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_mode_is_refused_before_anything_is_asked()
    {
        await IntegrationAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DeliverAsync((DeliveryMode)7));

        Assert.Empty(_gate.Requests);
        Assert.Equal(0, _worktrees.Inspections);
    }

    // ---- only what finished well and an independent judge approved is delivered ---------------------------------

    [Fact]
    public async Task A_run_that_succeeded_with_an_approved_integration_is_ready_for_that_exact_commit()
    {
        var integration = await IntegrationAsync();

        var readiness = await Service().CheckAsync(Project, Run, Ct);

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(integration, readiness.Commit);
        Assert.Equal(0, _worktrees.Inspections);
        Assert.Empty(_worktrees.Calls);
    }

    [Theory]
    [InlineData(RunPhase.Failed)]
    [InlineData(RunPhase.Cancelled)]
    [InlineData(RunPhase.Blocked)]
    public async Task A_run_that_did_not_finish_succeeded_is_never_delivered_and_nothing_is_asked(RunPhase phase)
    {
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit);
        var verdict = await _records.VerdictAsync(Run);
        await _records.FinishedAsync(Run, phase, commit, verdict);

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
        Assert.False((await Service().CheckAsync(Project, Run, Ct)).Ready);
    }

    [Fact]
    public async Task A_run_still_without_its_end_in_the_log_is_never_delivered()
    {
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit);
        await _records.VerdictAsync(Run);

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
    }

    public static TheoryData<string> NotApproved() =>
        ["changes-requested", "invalid-approval", "escalated-and-approved-by-a-person", "no-verdict", "not-in-the-store", "store-says-invalid", "superseded"];

    [Theory]
    [MemberData(nameof(NotApproved))]
    public async Task A_run_without_a_valid_approved_verdict_of_its_integration_is_never_delivered(string problem)
    {
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit);
        string? verdict;
        switch (problem)
        {
            case "changes-requested":
                verdict = await _records.VerdictAsync(Run, VerdictDecision.ChangesRequested);
                break;
            case "invalid-approval":
                verdict = await _records.VerdictAsync(Run, VerdictDecision.Approved, valid: false);
                break;
            case "escalated-and-approved-by-a-person":
                // The review needed a person, who said yes: the task completes and the run succeeds, but no judge approved.
                verdict = await _records.VerdictAsync(Run, VerdictDecision.EscalateToHuman);
                await _records.RecordAsync(
                    DecisionKind.ApprovalGranted,
                    new ApprovalAnsweredPayload("orchestration.review-escalation", true, "usuario", null, new TaskId("T3")) { RunId = Run.Value },
                    new Actor(ActorKind.User, "usuario"));
                break;
            case "no-verdict":
                verdict = null;
                break;
            case "not-in-the-store":
                verdict = Guid.NewGuid().ToString("N");
                await _records.AnnounceVerdictAsync(Run, verdict, VerdictDecision.Approved, valid: true);
                break;
            case "store-says-invalid":
                verdict = await _records.StoreVerdictAsync(Run, VerdictDecision.Approved, valid: false);
                await _records.AnnounceVerdictAsync(Run, verdict, VerdictDecision.Approved, valid: true);
                break;
            default:
                verdict = await _records.VerdictAsync(Run);
                await _records.VerdictAsync(Run, VerdictDecision.ChangesRequested, supersedes: verdict);
                break;
        }

        await _records.FinishedAsync(Run, RunPhase.Succeeded, commit, verdict);

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
        var readiness = await Service().CheckAsync(Project, Run, Ct);
        Assert.False(readiness.Ready);
        Assert.Null(readiness.Commit);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Reason));
    }

    [Fact]
    public async Task An_integration_made_after_the_verdict_was_not_reviewed_and_is_never_delivered()
    {
        var reviewed = BranchAhead();
        await _records.IntegratedAsync(Run, reviewed);
        var verdict = await _records.VerdictAsync(Run);
        var later = _repo.Commit(reviewed);
        _repo.Refs[IntegrationNames.BranchRef(Run)] = later;
        await _records.IntegratedAsync(Run, later);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, later, verdict);

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
    }

    [Fact]
    public async Task The_integration_the_judge_reviewed_must_be_the_one_the_run_ended_with()
    {
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit);
        var verdict = await _records.VerdictAsync(Run);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, _repo.Commit(commit), verdict);

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
    }

    [Fact]
    public async Task An_integration_with_conflicts_is_never_delivered()
    {
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit, conflicts: ["src/Calc/Calc.cs"]);
        var verdict = await _records.VerdictAsync(Run);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, commit, verdict);

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
    }

    [Fact]
    public async Task A_run_branch_moved_after_the_review_is_never_delivered_and_nothing_is_asked()
    {
        var approved = await IntegrationAsync();
        var moved = _repo.Commit(approved);
        _repo.Refs[IntegrationNames.BranchRef(Run)] = moved;

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
        Assert.Contains(moved[..10], result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decision_log_that_does_not_verify_stops_the_delivery_before_git_is_touched()
    {
        await IntegrationAsync();
        _records.Log.Tamper(_records.ProjectId, 0, e => e with { Summary = "reescrita" });

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
        Assert.Equal(0, _worktrees.Inspections);
        Assert.Contains("íntegro", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_runs_success_does_not_make_this_one_deliverable()
    {
        var commit = BranchAhead();
        await _records.SucceededAsync(new RunId("20261016-ffffff"), commit);

        var result = await DeliverAsync();

        AssertNothingDelivered(result);
    }

    // ---- what a delivery leaves in the decision log ---------------------------------------------------------------

    [Fact]
    public async Task A_delivery_is_recorded_as_an_integration_into_the_users_branch()
    {
        var integration = await IntegrationAsync();
        var before = _records.Entries.Count;

        var result = await DeliverAsync();

        Assert.True(result.Delivered, result.Message);
        var entry = Assert.Single(_records.Entries.Skip(before));
        Assert.Equal((DecisionKind.IntegrationPerformed, new Actor(ActorKind.System, "agent-studio")), (entry.Kind, entry.Actor));
        var payload = DecisionPayloadSerializer.Deserialize<IntegrationPerformedPayload>(entry.PayloadJson);
        Assert.True(payload.IsDelivery);
        Assert.Equal((Run.Value, "refs/heads/main", integration), (payload.RunId, payload.TargetRef, payload.ResultCommit));
        Assert.Equal([integration], payload.Sources);
        Assert.Empty(payload.ConflictPaths);
        Assert.Null(payload.Task);
        Assert.True((await _records.Recorder.LoadAsync(_records.ProjectId, Ct)).Verification.IsValid);
    }

    [Fact]
    public async Task A_delivery_that_cannot_be_recorded_still_says_it_was_delivered_and_warns()
    {
        var integration = await IntegrationAsync();
        _records.Log.FailFromSequence = _records.Entries.Count + 1;

        var result = await DeliverAsync();

        Assert.True(result.Delivered, result.Message);
        Assert.Equal(integration, _repo.UserHead);
        Assert.Contains("registro de decisiones", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_the_users_branch_already_contained_the_run()
    {
        var integration = await IntegrationAsync();
        _repo.UserHead = _repo.Commit(integration);
        var before = _records.Entries.Count;

        Assert.True((await DeliverAsync()).Delivered);

        Assert.Equal(before, _records.Entries.Count);
    }
}
