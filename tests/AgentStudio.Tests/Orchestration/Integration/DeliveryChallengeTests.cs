using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Tests.Orchestration.Integration;

/// <summary>
/// docs/15 D-35 and D-40 with decision 6(a): what a challenge of a verdict and a request of the auditor to replan do to the
/// delivery of a run, read from the verified decision log and the verdict store as <see cref="DeliveryService.CheckAsync"/>
/// reads everything else. A challenge without a closed revision blocks it; a revision that withdraws an approval blocks it;
/// a revision that turns towards an approval counts only once the user accepts it, and one that confirms the approval lets
/// it through (the finding of the architect: today any superseding verdict blocks). A request to replan blocks it until it
/// is dismissed or followed. Nothing of this asks the gate or touches git.
/// </summary>
public sealed class DeliveryChallengeTests
{
    private static readonly RunId Run = new("20261019-a1b2c3");

    private readonly FakeRepository _repo = new();
    private readonly RecordingApprovalGate _gate = new();
    private readonly DeliveryRecords _records = new("proyecto-1");
    private readonly ChallengeRecords _log;
    private readonly FakeWorktreeManager _worktrees;

    public DeliveryChallengeTests()
    {
        _worktrees = new FakeWorktreeManager(_repo);
        _log = new ChallengeRecords(_records);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectContext Project => new("proyecto-1", _repo.Root);

    private DeliveryService Service() => new(_worktrees, _gate, _records.Recorder, _records.Verdicts);

    private Task<DeliveryReadiness> CheckAsync() => Service().CheckAsync(Project, Run, Ct);

    /// <summary>The run's branch one commit ahead of the base, and the run succeeded with a valid approval of it.</summary>
    private async Task<(string Commit, string Verdict)> ApprovedRunAsync()
    {
        var commit = BranchAhead();
        var verdict = await _records.SucceededAsync(Run, commit);
        return (commit, verdict);
    }

    /// <summary>
    /// A run that succeeded because a person approved a review that needed a person: no judge approved its integration
    /// (ADR-0012, addendum of 2026-10-16), so it is not ready until a revision turns towards an approval and is accepted.
    /// </summary>
    private async Task<(string Commit, string Verdict)> EscalatedRunAsync()
    {
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit);
        var verdict = await _records.VerdictAsync(Run, VerdictDecision.EscalateToHuman);
        await _records.RecordAsync(
            DecisionKind.ApprovalGranted,
            new ApprovalAnsweredPayload("orchestration.review-escalation", true, "usuario", null, new TaskId("T3")) { RunId = Run.Value },
            new Actor(ActorKind.User, "usuario"));
        await _records.FinishedAsync(Run, RunPhase.Succeeded, commit, verdict);
        return (commit, verdict);
    }

    private string BranchAhead()
    {
        var commit = _repo.Commit(_repo.Base);
        _repo.Refs[IntegrationNames.BranchRef(Run)] = commit;
        return commit;
    }

    private static void AssertNotReady(DeliveryReadiness readiness, params string[] mentions)
    {
        Assert.False(readiness.Ready, readiness.Reason);
        Assert.Null(readiness.Commit);
        foreach (var mention in mentions)
        {
            Assert.Contains(mention, readiness.Reason, StringComparison.Ordinal);
        }
    }

    private async Task AssertNothingDeliveredAsync()
    {
        var result = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);
        Assert.False(result.Delivered, result.Message);
        Assert.Empty(_gate.Requests);
        Assert.Empty(_worktrees.Deliveries);
        Assert.Equal(_repo.Base, _repo.UserHead);
    }

    // ---- D-40: the three points, and the finding of the architect ----

    [Fact]
    public async Task A_revision_that_confirms_the_approval_lets_the_delivery_through_without_anything_else()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        var readiness = await CheckAsync();

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
        Assert.Contains(debate, readiness.Reason, StringComparison.Ordinal);
        var delivered = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);
        Assert.True(delivered.Delivered, delivered.Message);
        Assert.Equal(DeliveryService.ApprovalAction, Assert.Single(_gate.Requests).Action);
        Assert.Equal(commit, _repo.UserHead);
    }

    [Fact]
    public async Task A_challenge_whose_revision_has_not_closed_blocks_the_delivery_and_nothing_is_asked()
    {
        var (_, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict, open: true);

        AssertNotReady(await CheckAsync(), verdict, debate, "no ha terminado");
        await AssertNothingDeliveredAsync();

        // The judge of the revision already spoke, but the debate has not closed: still pending.
        await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        AssertNotReady(await CheckAsync(), "no ha terminado");
    }

    [Fact]
    public async Task A_revision_that_opened_against_the_verdict_blocks_even_if_the_challenge_was_never_written()
    {
        // The challenge is written once its revision debate has its id: the app may stop between the two entries.
        var (_, verdict) = await ApprovedRunAsync();
        await _log.OpenAsync("deb-20261019-0000ff", verdict);

        AssertNotReady(await CheckAsync(), "deb-20261019-0000ff", "no ha terminado");
        await AssertNothingDeliveredAsync();
    }

    [Fact]
    public async Task A_challenge_whose_revision_never_opened_blocks_the_delivery_until_it_is_challenged_again()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var never = await _log.ChallengeAsync(Run, verdict, open: false);

        AssertNotReady(await CheckAsync(), never, "no llegó a abrirse");

        // A new challenge of the same verdict, revised and closed, is the latest word on it.
        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task A_challenge_whose_revision_the_user_declined_to_open_was_withdrawn_but_a_no_of_the_system_is_no_decision()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var declined = await _log.ChallengeAsync(Run, verdict, open: false);
        await _log.DeclineOpeningAsync(declined, new Actor(ActorKind.User, "usuario"));

        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);

        // The gate denied in its own name (the question expired): nobody decided, and the challenge stands.
        var expired = await _log.ChallengeAsync(Run, verdict, open: false);
        await _log.DeclineOpeningAsync(expired, new Actor(ActorKind.System, "agent-studio"));

        AssertNotReady(await CheckAsync(), expired, "no llegó a abrirse");

        // Challenged again and declined by the person: the latest word, before which the expired one gives way.
        var again = await _log.ChallengeAsync(Run, verdict, open: false);
        await _log.DeclineOpeningAsync(again, new Actor(ActorKind.User, "usuario"));

        Assert.True((await CheckAsync()).Ready);
    }

    [Theory]
    [InlineData(VerdictDecision.ChangesRequested, "pide cambios")]
    [InlineData(VerdictDecision.Rejected, "rechazado")]
    public async Task A_revision_that_withdraws_the_approval_blocks_the_delivery(VerdictDecision decision, string said)
    {
        var (_, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, decision);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        AssertNotReady(await CheckAsync(), debate, said, "retira la aprobación");
        await AssertNothingDeliveredAsync();

        // Accepting it changes nothing: there is no approval to accept.
        await _log.AcceptAsync(Run.Value);
        AssertNotReady(await CheckAsync(), "retira la aprobación");
    }

    [Fact]
    public async Task A_revision_that_turns_towards_an_approval_blocks_until_the_user_accepts_it_and_then_is_delivered()
    {
        var (commit, verdict) = await EscalatedRunAsync();
        Assert.False((await CheckAsync()).Ready);
        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        AssertNotReady(await CheckAsync(), debate, revision, "la aceptas expresamente");
        await AssertNothingDeliveredAsync();

        await _log.AcceptAsync(Run.Value);

        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
        Assert.Contains("aceptaste", readiness.Reason, StringComparison.Ordinal);
        var delivered = await Service().DeliverAsync(Project, Run, DeliveryMode.FastForwardOnly, Ct);
        Assert.True(delivered.Delivered, delivered.Message);
        Assert.Equal(commit, _repo.UserHead);
    }

    [Fact]
    public async Task The_acceptance_may_be_recorded_in_the_revision_debate_as_well()
    {
        var (commit, verdict) = await EscalatedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        await _log.AcceptAsync(debate);

        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    public static TheoryData<string> AcceptancesThatDoNotCount() =>
        ["asked-before-the-revision-closed", "denied", "granted-by-the-system", "of-another-run", "another-action"];

    [Theory]
    [MemberData(nameof(AcceptancesThatDoNotCount))]
    public async Task Only_an_explicit_yes_of_a_person_asked_after_the_revision_closed_accepts_it(string acceptance)
    {
        var (_, verdict) = await EscalatedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        switch (acceptance)
        {
            case "asked-before-the-revision-closed":
                await _log.AskAcceptanceAsync(Run.Value);
                await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);
                await _log.AnswerAcceptanceAsync(Run.Value, approved: true);
                break;
            case "denied":
                await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);
                await _log.AcceptAsync(Run.Value, approved: false);
                break;
            case "granted-by-the-system":
                await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);
                await _log.AcceptAsync(Run.Value, actor: new Actor(ActorKind.System, "agent-studio"));
                break;
            case "of-another-run":
                await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);
                await _log.AcceptAsync("20261019-ffffff");
                break;
            default:
                await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);
                await _log.AcceptAsync(Run.Value, action: DeliveryService.ReplanDismissalAction);
                break;
        }

        AssertNotReady(await CheckAsync(), "la aceptas expresamente");
    }

    [Fact]
    public async Task A_revision_that_needs_a_person_blocks_and_the_users_own_decision_is_not_a_judges_approval()
    {
        var (_, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var invalid = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved, valid: false);
        await _log.CloseAsync(debate, DebateOutcome.EscalatedToUser, JudgingPanel.Invalid, invalid);

        AssertNotReady(await CheckAsync(), debate, "necesita a una persona");

        // «Decidir yo» closes another revision: a decision of a person is not the approval of an independent judge.
        var decided = await _log.ChallengeAsync(Run, verdict);
        var unresolved = await _log.ReviseAsync(Run, verdict, VerdictDecision.Unresolved);
        await _log.CloseAsync(decided, DebateOutcome.Resolved, JudgingPanel.User, unresolved);

        AssertNotReady(await CheckAsync(), decided, "la decidiste tú");
        await AssertNothingDeliveredAsync();
    }

    [Fact]
    public async Task An_aborted_revision_leaves_the_challenge_open_and_another_revision_can_settle_it()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var aborted = await _log.ChallengeAsync(Run, verdict);
        await _log.CloseAsync(aborted, DebateOutcome.Aborted, DebateClosedPayload.AbortedRule);

        AssertNotReady(await CheckAsync(), aborted, "se abortó");

        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task Challenging_the_original_again_does_not_undo_a_revision_that_withdrew_the_approval()
    {
        var (_, verdict) = await ApprovedRunAsync();
        var first = await _log.ChallengeAsync(Run, verdict);
        var withdrawn = await _log.ReviseAsync(Run, verdict, VerdictDecision.Rejected);
        await _log.CloseAsync(first, DebateOutcome.Resolved, JudgingPanel.Single, withdrawn);
        var second = await _log.ChallengeAsync(Run, verdict);
        var confirmed = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(second, DebateOutcome.Resolved, JudgingPanel.Single, confirmed);

        AssertNotReady(await CheckAsync(), first, "retira la aprobación");
    }

    [Fact]
    public async Task A_challenge_declined_after_a_revision_withdrew_the_approval_does_not_undo_it()
    {
        var (_, verdict) = await ApprovedRunAsync();
        var first = await _log.ChallengeAsync(Run, verdict);
        var withdrawn = await _log.ReviseAsync(Run, verdict, VerdictDecision.Rejected);
        await _log.CloseAsync(first, DebateOutcome.Resolved, JudgingPanel.Single, withdrawn);
        var declined = await _log.ChallengeAsync(Run, verdict, open: false);
        await _log.DeclineOpeningAsync(declined, new Actor(ActorKind.User, "usuario"));

        AssertNotReady(await CheckAsync(), first, "retira la aprobación");
    }

    [Fact]
    public async Task A_withdrawn_approval_challenged_in_turn_counts_again_only_once_the_user_accepts_the_new_revision()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var first = await _log.ChallengeAsync(Run, verdict);
        var withdrawn = await _log.ReviseAsync(Run, verdict, VerdictDecision.ChangesRequested);
        await _log.CloseAsync(first, DebateOutcome.Resolved, JudgingPanel.Single, withdrawn);
        var second = await _log.ChallengeAsync(Run, withdrawn);
        var approved = await _log.ReviseAsync(Run, withdrawn, VerdictDecision.Approved);
        await _log.CloseAsync(second, DebateOutcome.Resolved, JudgingPanel.Single, approved);

        AssertNotReady(await CheckAsync(), second, "la aceptas expresamente");

        await _log.AcceptAsync(Run.Value);
        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task A_revision_whose_copy_is_the_runs_integration_counts()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict, open: false);
        await _log.OpenAsync(debate, verdict, snapshot: commit.ToUpperInvariant());
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        var readiness = await CheckAsync();

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task A_revision_judged_by_a_panel_counts_when_both_verdicts_check_out()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var claude = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        var codex = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Agree, claude, codex);

        var readiness = await CheckAsync();

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task A_correction_inside_the_revision_supersedes_its_first_attempt_and_still_reaches_the_challenged_verdict()
    {
        var (commit, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var attempt = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved, valid: false);
        var corrected = await _records.VerdictAsync(Run, VerdictDecision.Approved, valid: true, supersedes: attempt);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, corrected);

        var readiness = await CheckAsync();

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    public static TheoryData<string> RevisionsThatDoNotCheckOut() =>
        [
            "verdict-not-in-the-log", "verdict-not-in-the-store", "store-says-invalid", "announced-invalid", "other-requirements",
            "does-not-supersede-the-challenged", "debate-challenges-another-verdict", "debate-of-other-requirements",
            "debate-of-another-commit", "panel-disagrees", "no-verdicts", "closed-twice",
        ];

    [Theory]
    [MemberData(nameof(RevisionsThatDoNotCheckOut))]
    public async Task A_revision_that_does_not_check_out_against_the_log_and_the_store_blocks_the_delivery(string problem)
    {
        var (_, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict, open: !problem.StartsWith("debate-", StringComparison.Ordinal));
        string[] verdicts;
        switch (problem)
        {
            case "debate-of-other-requirements":
                await _log.OpenAsync(debate, verdict, requirements: "otros-requisitos");
                verdicts = [await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved)];
                break;
            case "debate-of-another-commit":
                await _log.OpenAsync(debate, verdict, snapshot: _repo.Commit(_repo.Base));
                verdicts = [await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved)];
                break;
            case "closed-twice":
                verdicts = [await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved)];
                await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, verdicts);
                break;
            case "verdict-not-in-the-log":
                verdicts = [await _records.StoreVerdictAsync(Run, VerdictDecision.Approved, valid: true, supersedes: verdict)];
                break;
            case "verdict-not-in-the-store":
                var announcedOnly = Guid.NewGuid().ToString("N");
                await _records.AnnounceVerdictAsync(Run, announcedOnly, VerdictDecision.Approved, valid: true, supersedes: verdict);
                verdicts = [announcedOnly];
                break;
            case "store-says-invalid":
                var stored = await _records.StoreVerdictAsync(Run, VerdictDecision.Approved, valid: false, supersedes: verdict);
                await _records.AnnounceVerdictAsync(Run, stored, VerdictDecision.Approved, valid: true, supersedes: verdict);
                verdicts = [stored];
                break;
            case "announced-invalid":
                verdicts = [await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved, valid: false)];
                break;
            case "other-requirements":
                verdicts = [await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved, requirements: "otros-requisitos")];
                break;
            case "does-not-supersede-the-challenged":
                verdicts = [await _records.VerdictAsync(Run, VerdictDecision.Approved, valid: true)];
                break;
            case "debate-challenges-another-verdict":
                await _log.OpenAsync(debate, challenges: Guid.NewGuid().ToString("N"));
                verdicts = [await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved)];
                break;
            case "panel-disagrees":
                verdicts = [await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved), await _log.ReviseAsync(Run, verdict, VerdictDecision.Rejected)];
                break;
            default:
                verdicts = [];
                break;
        }

        await _log.CloseAsync(debate, DebateOutcome.Resolved, verdicts.Length > 1 ? JudgingPanel.Agree : JudgingPanel.Single, verdicts);

        AssertNotReady(await CheckAsync(), debate, "no cuadra");
        await AssertNothingDeliveredAsync();
    }

    [Fact]
    public async Task A_verdict_superseded_without_a_challenge_still_blocks_as_in_phase_3()
    {
        var (_, verdict) = await ApprovedRunAsync();
        await _records.VerdictAsync(Run, VerdictDecision.Approved, supersedes: verdict);

        AssertNotReady(await CheckAsync(), verdict, "sustituido");
    }

    [Fact]
    public async Task A_verdict_the_store_says_supersedes_the_challenged_one_but_the_log_never_announced_blocks()
    {
        var (_, verdict) = await ApprovedRunAsync();
        var debate = await _log.ChallengeAsync(Run, verdict);
        var revision = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);
        await _records.StoreVerdictAsync(Run, VerdictDecision.Rejected, valid: true, supersedes: verdict);

        AssertNotReady(await CheckAsync(), verdict, "no anuncia");
    }

    [Fact]
    public async Task A_revision_that_needs_acceptance_is_not_turned_into_a_confirmation_by_another_challenge()
    {
        // Escalated (no judge approved), revised towards an approval and not accepted, then challenged and confirmed: the
        // approval was never accepted, so the new revision needs the acceptance too.
        var (commit, verdict) = await EscalatedRunAsync();
        var first = await _log.ChallengeAsync(Run, verdict);
        var turned = await _log.ReviseAsync(Run, verdict, VerdictDecision.Approved);
        await _log.CloseAsync(first, DebateOutcome.Resolved, JudgingPanel.Single, turned);
        var second = await _log.ChallengeAsync(Run, turned);
        var confirmed = await _log.ReviseAsync(Run, turned, VerdictDecision.Approved);
        await _log.CloseAsync(second, DebateOutcome.Resolved, JudgingPanel.Single, confirmed);

        AssertNotReady(await CheckAsync(), "la aceptas expresamente");

        await _log.AcceptAsync(Run.Value);
        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task A_challenge_of_another_runs_verdict_does_not_touch_this_delivery()
    {
        var (commit, _) = await ApprovedRunAsync();
        var other = new RunId("20261019-ffffff");
        var elsewhere = await _records.VerdictAsync(other);
        await _log.ChallengeAsync(other, elsewhere, open: true);

        var readiness = await CheckAsync();

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task A_pending_revision_of_any_verdict_of_the_run_blocks_its_delivery()
    {
        // The review's first attempt, corrected by the judge itself: the run ended with the correction, but its user
        // disputes the review, and the revision is still going on.
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit);
        var attempt = await _records.VerdictAsync(Run, VerdictDecision.Approved, valid: false);
        var corrected = await _records.VerdictAsync(Run, VerdictDecision.Approved, supersedes: attempt);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, commit, corrected);
        Assert.True((await CheckAsync()).Ready);

        var debate = await _log.ChallengeAsync(Run, attempt, open: true);

        AssertNotReady(await CheckAsync(), debate, "no ha terminado");
    }

    [Theory]
    [InlineData(VerdictDecision.Rejected, false)]
    [InlineData(VerdictDecision.ChangesRequested, false)]
    [InlineData(VerdictDecision.Approved, true)]
    public async Task A_revision_of_the_attempt_the_judge_corrected_is_about_the_same_work_and_may_not_withdraw_the_approval(VerdictDecision decision, bool ready)
    {
        var commit = BranchAhead();
        await _records.IntegratedAsync(Run, commit);
        var attempt = await _records.VerdictAsync(Run, VerdictDecision.Approved, valid: false);
        var corrected = await _records.VerdictAsync(Run, VerdictDecision.Approved, supersedes: attempt);
        await _records.FinishedAsync(Run, RunPhase.Succeeded, commit, corrected);
        var debate = await _log.ChallengeAsync(Run, attempt);
        var revision = await _log.ReviseAsync(Run, attempt, decision);
        await _log.CloseAsync(debate, DebateOutcome.Resolved, JudgingPanel.Single, revision);

        var readiness = await CheckAsync();

        if (ready)
        {
            // An approval of the same work: the run's own verdict still decides, and nothing has to be accepted.
            Assert.True(readiness.Ready, readiness.Reason);
            Assert.Equal(commit, readiness.Commit);
        }
        else
        {
            AssertNotReady(readiness, debate, attempt, "retira la aprobación");
        }
    }

    // ---- D-35: a request to replan, until it is dismissed or followed ----

    [Fact]
    public async Task A_request_to_replan_blocks_the_delivery_until_the_user_dismisses_it()
    {
        var (commit, _) = await ApprovedRunAsync();
        await _log.RequestReplanAsync(Run, "orchestrator.review-omitted", "orchestrator.approval-missing");

        AssertNotReady(await CheckAsync(), "replanificar", "orchestrator.review-omitted", "orchestrator.approval-missing", "auditor");
        await AssertNothingDeliveredAsync();

        await _log.DismissReplanAsync(Run);

        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public async Task A_request_to_replan_is_attended_by_a_follow_up_run_started_after_it()
    {
        var (commit, _) = await ApprovedRunAsync();
        await _log.FollowUpAsync(Run, new RunId("20261019-0f0f0f"));
        await _log.RequestReplanAsync(Run, "orchestrator.integration-unverified");

        // A follow-up that started before the request did not answer it.
        AssertNotReady(await CheckAsync(), "replanificar");

        await _log.FollowUpAsync(Run, new RunId("20261019-1f1f1f"));

        var readiness = await CheckAsync();
        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    public static TheoryData<string> DismissalsThatDoNotCount() => ["before-the-request", "denied", "of-another-run", "by-the-system", "asked-before-the-request"];

    [Theory]
    [MemberData(nameof(DismissalsThatDoNotCount))]
    public async Task Only_a_person_who_said_yes_to_dismissing_after_the_request_dismisses_it(string dismissal)
    {
        await ApprovedRunAsync();
        switch (dismissal)
        {
            case "before-the-request":
                await _log.DismissReplanAsync(Run);
                await _log.RequestReplanAsync(Run, "orchestrator.permissions");
                break;
            case "denied":
                await _log.RequestReplanAsync(Run, "orchestrator.permissions");
                await _log.DismissReplanAsync(Run, approved: false);
                break;
            case "of-another-run":
                await _log.RequestReplanAsync(Run, "orchestrator.permissions");
                await _log.DismissReplanAsync(new RunId("20261019-ffffff"));
                break;
            case "by-the-system":
                await _log.RequestReplanAsync(Run, "orchestrator.permissions");
                await _log.DismissReplanAsync(Run, actor: new Actor(ActorKind.System, "agent-studio"));
                break;
            default:
                await _log.AskDismissalAsync(Run);
                await _log.RequestReplanAsync(Run, "orchestrator.permissions");
                await _log.AnswerDismissalAsync(Run);
                break;
        }

        AssertNotReady(await CheckAsync(), "replanificar");
    }

    [Fact]
    public async Task A_new_request_after_a_dismissal_blocks_again()
    {
        await ApprovedRunAsync();
        await _log.RequestReplanAsync(Run, "orchestrator.permissions");
        await _log.DismissReplanAsync(Run);
        Assert.True((await CheckAsync()).Ready);

        await _log.RequestReplanAsync(Run, "orchestrator.dependency-order", judge: true);

        AssertNotReady(await CheckAsync(), "replanificar", "orchestrator.dependency-order", "juez");
    }

    [Fact]
    public async Task A_request_to_replan_another_run_does_not_touch_this_delivery()
    {
        var (commit, _) = await ApprovedRunAsync();
        await _log.RequestReplanAsync(new RunId("20261019-ffffff"), "orchestrator.permissions");

        var readiness = await CheckAsync();

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(commit, readiness.Commit);
    }

    [Fact]
    public void The_actions_the_delivery_reads_are_the_ones_of_docs_15()
    {
        // D-35 and D-40 name them; the writers (the challenge, the monitor) cannot see this module and repeat them.
        Assert.Equal("verdict.revision.accept", DeliveryService.RevisionAcceptanceAction);
        Assert.Equal("replan.dismiss", DeliveryService.ReplanDismissalAction);
        Assert.Equal("debate.open", DeliveryService.DebateOpenAction);
        Assert.Equal("deliver-integration", DeliveryService.ApprovalAction);
    }
}

/// <summary>
/// What the challenge of a verdict, its revision debate, the acceptance of a revision and the auditor leave in the log, as
/// docs/15 D-35 to D-40 and docs/04 section 17 say: <c>VerdictChallenged</c> in the run of the verdict; the revision debate
/// in its own run (<c>DebateOpened</c> naming the challenged verdict, <c>DebateClosed</c> naming its verdicts); the verdicts
/// of the revision stored and announced in the run of the original with <c>Supersedes</c> = the challenged one; the
/// acceptance and the dismissal through the approval gate (request, then the person's answer).
/// </summary>
internal sealed class ChallengeRecords(DeliveryRecords records)
{
    private static readonly Actor Person = new(ActorKind.User, "usuario");
    private static readonly Actor Auditor = new(ActorKind.Auditor, "auditor-orquestador");
    private int _debates;

    /// <summary>Records the challenge and, unless told otherwise, opens its revision debate; returns the debate's id.</summary>
    public async Task<string> ChallengeAsync(RunId run, string verdict, bool open = true)
    {
        var debate = $"deb-20261019-{++_debates:x6}";
        await records.RecordAsync(
            DecisionKind.VerdictChallenged,
            new VerdictChallengedPayload(verdict, "usuario", new EvidenceId("razones-" + debate), new string('c', 64), debate) { RunId = run.Value },
            Person);
        if (open)
        {
            await OpenAsync(debate, verdict);
        }

        return debate;
    }

    public Task OpenAsync(string debate, string challenges, string requirements = "hash-de-requisitos", string? snapshot = null) =>
        records.RecordAsync(
            DecisionKind.DebateOpened,
            new DebateOpenedPayload(
                DebateMode.Adversarial,
                new string('d', 64),
                new string('e', 64),
                requirements,
                new EvidenceId("pregunta-" + debate),
                new string('f', 64),
                DebateSubjectKind.Verdict,
                challenges,
                snapshot,
                [
                    new DebateParticipantEntry(new AgentId("arquitecto"), DebateRole.Proponent, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku", "P1"),
                    new DebateParticipantEntry(new AgentId("critico"), DebateRole.Critic, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet", "P2"),
                ],
                [new DebateJudgeEntry(new AgentId("juez-revision"), ProviderId.CodexCli, VendorId.OpenAI, null)],
                DebateJudgingKind.IndependentJudge,
                [VendorId.Anthropic],
                [],
                4,
                1800,
                400_000,
                10,
                challenges) { RunId = debate });

    /// <summary>A verdict of the revision, kept as <c>VerdictKeeper</c> keeps it: in the run of the original, superseding it.</summary>
    public async Task<string> ReviseAsync(RunId run, string challenged, VerdictDecision decision, bool valid = true, string? requirements = null)
    {
        if (requirements is null)
        {
            return await records.VerdictAsync(run, decision, valid, supersedes: challenged);
        }

        var id = await records.StoreVerdictAsync(run, decision, valid, challenged);
        await records.RecordAsync(
            DecisionKind.VerdictIssued,
            new VerdictIssuedPayload(id, new TaskId("juez-juez-revision"), decision, valid, [], requirements, records.Head, challenged, null) { RunId = run.Value },
            new Actor(ActorKind.Judge, "juez-revision"));
        return id;
    }

    /// <summary>The approval to open the revision (<c>debate.open</c>, in the debate) asked and denied by <paramref name="actor"/>.</summary>
    public async Task DeclineOpeningAsync(string debate, Actor actor)
    {
        await AskAsync(debate, DeliveryService.DebateOpenAction);
        await records.RecordAsync(
            DecisionKind.ApprovalDenied,
            new ApprovalAnsweredPayload(DeliveryService.DebateOpenAction, false, actor.Id, "No abrir la revisión.", null) { RunId = debate },
            actor);
    }

    public Task CloseAsync(string debate, DebateOutcome outcome, string rule, params string[] verdicts) =>
        records.RecordAsync(DecisionKind.DebateClosed, new DebateClosedPayload(outcome, verdicts, rule, null, "Cierre del debate.") { RunId = debate });

    /// <summary>«Aceptar la revisión» through the gate: the request, then the answer.</summary>
    public async Task AcceptAsync(string run, bool approved = true, Actor? actor = null, string action = DeliveryService.RevisionAcceptanceAction)
    {
        await AskAsync(run, action);
        await AnswerAsync(run, action, approved, actor);
    }

    public Task AskAcceptanceAsync(string run) => AskAsync(run, DeliveryService.RevisionAcceptanceAction);

    public Task AnswerAcceptanceAsync(string run, bool approved) => AnswerAsync(run, DeliveryService.RevisionAcceptanceAction, approved, null);

    public Task RequestReplanAsync(RunId run, params string[] findings) => RequestReplanAsync(run, false, findings);

    public Task RequestReplanAsync(RunId run, string finding, bool judge) => RequestReplanAsync(run, judge, [finding]);

    public async Task DismissReplanAsync(RunId run, bool approved = true, Actor? actor = null)
    {
        await AskAsync(run.Value, DeliveryService.ReplanDismissalAction);
        await AnswerAsync(run.Value, DeliveryService.ReplanDismissalAction, approved, actor);
    }

    public Task AskDismissalAsync(RunId run) => AskAsync(run.Value, DeliveryService.ReplanDismissalAction);

    public Task AnswerDismissalAsync(RunId run) => AnswerAsync(run.Value, DeliveryService.ReplanDismissalAction, true, null);

    /// <summary>A follow-up run of <paramref name="follows"/> starts (<c>RunStarted.Follows</c>).</summary>
    public Task FollowUpAsync(RunId follows, RunId run) =>
        records.RecordAsync(
            DecisionKind.RunStarted,
            new RunStartedPayload(new string('1', 64), "hash-de-requisitos", new string('2', 40), "refs/heads/main", new string('3', 64), [], follows.Value) { RunId = run.Value });

    private Task RequestReplanAsync(RunId run, bool judge, IReadOnlyList<string> findings) =>
        records.RecordAsync(
            DecisionKind.ReplanRequested,
            new ReplanRequestedPayload(findings, [new TaskId("T1")], "Run has findings of high severity.", judge ? "veredicto-de-la-revision" : null) { RunId = run.Value },
            judge ? new Actor(ActorKind.Judge, "juez-revision") : Auditor);

    private Task AskAsync(string run, string action) =>
        records.RecordAsync(
            DecisionKind.ApprovalRequested,
            new ApprovalRequestedPayload(action, "Aprobación pedida.", RiskLevel.Critical, null, []) { RunId = run });

    private Task AnswerAsync(string run, string action, bool approved, Actor? actor) =>
        records.RecordAsync(
            approved ? DecisionKind.ApprovalGranted : DecisionKind.ApprovalDenied,
            new ApprovalAnsweredPayload(action, approved, actor?.Id ?? "usuario", null, null) { RunId = run },
            actor ?? Person);
}
