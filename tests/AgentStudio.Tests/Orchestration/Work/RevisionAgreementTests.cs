using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Challenges;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Work;
using AgentStudio.Tests.Orchestration.Integration;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// The board and the delivery read the challenges of the verdicts of a run from the same log and must not contradict each other (docs/04 §23.6, ADR-0039): nothing can be
/// delivered that the board has in review, and nothing stays in review for ever that the delivery would let through. Each scenario is written to the log of a real project
/// as the product writes it, and then both read it: <see cref="DeliveryService.CheckAsync"/> and <see cref="BoardProjector"/>.
/// </summary>
public sealed class RevisionAgreementTests
{
    private const string Debate1 = "deb-20261111-aaaaaa";
    private const string Debate2 = "deb-20261111-bbbbbb";
    private const string Debate3 = "deb-20261111-cccccc";

    private static CancellationToken Ct => WorkWorld.Ct;

    public enum Scenario
    {
        Unchallenged,
        RevisionOpen,
        RevisionConfirms,
        RevisionWithdraws,
        DecidedByUser,
        RevisionAborted,
        TurnsTowardsApprovalUnaccepted,
        TurnsTowardsApprovalAccepted,
        DeclinedByPerson,
        JudgedThenDeclined,
        NotOpenedThenDeclined,
        OtherVerdictRevisionApproves,
        OtherVerdictRevisionWithdraws,
        TwoChallengesOfTheSameVerdict,
        RevisionVerdictChallengedAndWithdrawn,
        SecondRevisionConfirms,
        SecondRevisionOpen,
        SecondRevisionAborted,
        SecondRevisionDeclined,
        SecondRevisionTurnsTowardsApprovalUnaccepted,
        SecondRevisionTurnsTowardsApprovalAccepted,
        ThreeLevelChainConfirms,
        ThreeLevelChainWithdrawn,
        RevisionAbortedThenChallengedAgain,
        RejectedOriginalRevisionAccepted,
        RejectedOriginalSecondRevisionWithdraws,
        RejectedOriginalSecondRevisionConfirms,
        RejectedOriginalSecondRevisionUnaccepted,
    }

    /// <summary>
    /// Scenario, whether the delivery lets the run through, the column of the card of R1 (and of its task T1), the verdict whose challenge is read and the status that all three
    /// readers (the delivery by what it blocks, the board and <c>ChallengeLog.StatusOf</c>, the one that tells a person whether they may challenge again or accept) give it.
    /// </summary>
    private static readonly (Scenario Scenario, bool Deliverable, BoardColumn Column, string Verdict, ChallengeStatus Status)[] Table =
    [
        (Scenario.Unchallenged, true, BoardColumn.Done, "v1", ChallengeStatus.None),
        (Scenario.RevisionOpen, false, BoardColumn.InReview, "v1", ChallengeStatus.Pending),
        (Scenario.RevisionConfirms, true, BoardColumn.Done, "v1", ChallengeStatus.Confirmed),
        (Scenario.RevisionWithdraws, false, BoardColumn.Returned, "v1", ChallengeStatus.Revoked),
        (Scenario.DecidedByUser, false, BoardColumn.InReview, "v1", ChallengeStatus.DecidedByUser),

        // Nobody judged an aborted revision: it counts for nothing and the verdict can be challenged again, but while it is the latest word the delivery stays blocked.
        (Scenario.RevisionAborted, false, BoardColumn.InReview, "v1", ChallengeStatus.Aborted),
        (Scenario.TurnsTowardsApprovalUnaccepted, false, BoardColumn.InReview, "v1", ChallengeStatus.AwaitingAcceptance),
        (Scenario.TurnsTowardsApprovalAccepted, true, BoardColumn.Done, "v1", ChallengeStatus.Accepted),

        // A person who declined to open the revision never started it: the challenge counts for nothing, and the verdict can be challenged again.
        (Scenario.DeclinedByPerson, true, BoardColumn.Done, "v1", ChallengeStatus.None),
        (Scenario.JudgedThenDeclined, true, BoardColumn.Done, "v1", ChallengeStatus.Confirmed),
        (Scenario.NotOpenedThenDeclined, true, BoardColumn.Done, "v1", ChallengeStatus.None),

        // The challenge of a verdict that is not the one the run ended with: its revision never has to be accepted (it is read as if that verdict were an approval).
        (Scenario.OtherVerdictRevisionApproves, true, BoardColumn.Done, "v0", ChallengeStatus.Confirmed),

        // The revision of an attempt the judge corrected withdrew its approval: the work is the same, so the delivery stops and the card goes back with it.
        (Scenario.OtherVerdictRevisionWithdraws, false, BoardColumn.Returned, "v0", ChallengeStatus.Revoked),

        // The same verdict challenged twice, the two revisions confirming it.
        (Scenario.TwoChallengesOfTheSameVerdict, true, BoardColumn.Done, "v1", ChallengeStatus.Confirmed),

        // A verdict of a revision (v2, which confirmed v1) challenged in its turn: its own revision withdraws it. The board walks the whole chain of challenges, as the delivery does.
        (Scenario.RevisionVerdictChallengedAndWithdrawn, false, BoardColumn.Returned, "v2", ChallengeStatus.Revoked),

        // The chain of revisions of revisions.
        (Scenario.SecondRevisionConfirms, true, BoardColumn.Done, "v2", ChallengeStatus.Confirmed),
        (Scenario.SecondRevisionOpen, false, BoardColumn.InReview, "v2", ChallengeStatus.Pending),
        (Scenario.SecondRevisionAborted, false, BoardColumn.InReview, "v2", ChallengeStatus.Aborted),
        (Scenario.SecondRevisionDeclined, true, BoardColumn.Done, "v2", ChallengeStatus.None),
        (Scenario.SecondRevisionTurnsTowardsApprovalUnaccepted, false, BoardColumn.InReview, "v2", ChallengeStatus.AwaitingAcceptance),
        (Scenario.SecondRevisionTurnsTowardsApprovalAccepted, true, BoardColumn.Done, "v2", ChallengeStatus.Accepted),
        (Scenario.ThreeLevelChainConfirms, true, BoardColumn.Done, "v3", ChallengeStatus.Confirmed),
        (Scenario.ThreeLevelChainWithdrawn, false, BoardColumn.Returned, "v3", ChallengeStatus.Revoked),
        (Scenario.RevisionAbortedThenChallengedAgain, true, BoardColumn.Done, "v1", ChallengeStatus.Confirmed),

        // The same from an original that was rejected: its revision turns it towards an approval, which a person accepts, and the chain goes on from there.
        (Scenario.RejectedOriginalRevisionAccepted, true, BoardColumn.Done, "v1", ChallengeStatus.Accepted),
        (Scenario.RejectedOriginalSecondRevisionWithdraws, false, BoardColumn.Returned, "v2", ChallengeStatus.Revoked),
        (Scenario.RejectedOriginalSecondRevisionConfirms, true, BoardColumn.Done, "v2", ChallengeStatus.Confirmed),

        // The approval of v2 was not accepted yet, so that the confirmation of v3 does not count either: the person accepts the revision of v1 (the one that awaits it).
        (Scenario.RejectedOriginalSecondRevisionUnaccepted, false, BoardColumn.InReview, "v2", ChallengeStatus.Confirmed),
    ];

    public static TheoryData<Scenario, bool, BoardColumn, string, ChallengeStatus> Cases
    {
        get
        {
            var data = new TheoryData<Scenario, bool, BoardColumn, string, ChallengeStatus>();
            foreach (var row in Table)
            {
                data.Add(row.Scenario, row.Deliverable, row.Column, row.Verdict, row.Status);
            }

            return data;
        }
    }

    public static TheoryData<Scenario, string, ChallengeStatus> StatusCases
    {
        get
        {
            var data = new TheoryData<Scenario, string, ChallengeStatus>();
            foreach (var row in Table)
            {
                data.Add(row.Scenario, row.Verdict, row.Status);
            }

            return data;
        }
    }

    private static async Task ChallengeAsync(WorkWorld world, string verdict, string debate, bool open = true)
    {
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.VerdictChallenged, new VerdictChallengedPayload(verdict, "manuel", new EvidenceId("motivos-" + debate), new string('e', 64), debate) { RunId = WorkWorld.Run1 });
        if (open)
        {
            await world.RecordAsync(debate, DecisionKind.DebateOpened, new DebateOpenedPayload(
                DebateMode.Adversarial, new string('1', 64), new string('2', 64), Dossier().Requirements.ContentHash, new EvidenceId("pregunta-" + debate), new string('4', 64),
                DebateSubjectKind.Verdict, verdict, null, [], [], DebateJudgingKind.IndependentJudge, [], [], 3, 600, null, 8, verdict) { RunId = debate });
        }
    }

    private static Task CloseAsync(WorkWorld world, string debate, DebateOutcome outcome, string rule, params string[] verdicts) =>
        world.RecordAsync(debate, DecisionKind.DebateClosed, new DebateClosedPayload(outcome, verdicts, rule, null, "cerrado") { RunId = debate });

    /// <summary>A challenge of <paramref name="verdict"/> whose revision a judge settled with <paramref name="decision"/> in the verdict <paramref name="id"/>.</summary>
    private static async Task ReviseAsync(WorkWorld world, string verdict, string debate, VerdictDecision decision, string id)
    {
        await ChallengeAsync(world, verdict, debate);
        await world.RevisionVerdictAsync(WorkWorld.Run1, decision, verdict, id: id);
        await CloseAsync(world, debate, DebateOutcome.Resolved, JudgingPanel.Single, id);
    }

    private static async Task DeclineAsync(WorkWorld world, string debate) =>
        await world.RecordAsUserAsync(DecisionKind.ApprovalDenied, new ApprovalAnsweredPayload(DeliveryService.DebateOpenAction, false, "manuel", "No abrir la revisión.", null) { RunId = debate });

    private static async Task AcceptAsync(WorkWorld world, string debate)
    {
        await world.RecordAsync(debate, DecisionKind.ApprovalRequested, new ApprovalRequestedPayload(ChallengeReader.RevisionAcceptanceAction, "Aceptas", RiskLevel.Critical, null, []) { RunId = debate });
        await world.RecordAsUserAsync(DecisionKind.ApprovalGranted, new ApprovalAnsweredPayload(ChallengeReader.RevisionAcceptanceAction, true, "manuel", null, null) { RunId = debate });
    }

    /// <summary>
    /// A run that finished succeeded with everything the delivery asks of the verdict it ended with (its integration, the worktree of its review, the head its dossier named),
    /// and then what the scenario says happened to its verdicts.
    /// </summary>
    private static async Task<(WorkWorld World, MilestoneId Milestone)> ArrangeAsync(Scenario scenario)
    {
        var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
        var commit = new string('c', 40);
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(Tid("T3"), IntegrationNames.BranchName(new RunId(WorkWorld.Run1)), [], commit, [], false) { RunId = WorkWorld.Run1 });
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.WorktreeCreated, new WorktreeCreatedPayload(Tid("T7"), "T7", @"C:\wt\run\T7", "refs/agentstudio/run/T7", commit) { RunId = WorkWorld.Run1 });
        var head = (await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries[^1].Hash;

        var otherVerdict = scenario is Scenario.OtherVerdictRevisionApproves or Scenario.OtherVerdictRevisionWithdraws;
        var closingDecision = scenario switch
        {
            Scenario.TurnsTowardsApprovalUnaccepted or Scenario.TurnsTowardsApprovalAccepted => VerdictDecision.ChangesRequested,
            Scenario.RejectedOriginalRevisionAccepted or Scenario.RejectedOriginalSecondRevisionWithdraws or Scenario.RejectedOriginalSecondRevisionConfirms
                or Scenario.RejectedOriginalSecondRevisionUnaccepted => VerdictDecision.Rejected,
            _ => VerdictDecision.Approved,
        };
        if (otherVerdict)
        {
            // The first attempt of the judge, which it corrected: the correction is the verdict the run ends with.
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, id: "v0", task: "T7", head: head);
            await world.VerdictAsync(WorkWorld.Run1, closingDecision, task: "T7", head: head, supersedes: "v0");
        }
        else
        {
            await world.VerdictAsync(WorkWorld.Run1, closingDecision, task: "T7", head: head);
        }

        await world.RecordAsync(WorkWorld.Run1, DecisionKind.RunFinished, new RunFinishedPayload(RunPhase.Succeeded, "fin", commit, "v1") { RunId = WorkWorld.Run1 });

        switch (scenario)
        {
            case Scenario.Unchallenged:
                break;
            case Scenario.RevisionOpen:
                await ChallengeAsync(world, "v1", Debate1);
                break;
            case Scenario.RevisionConfirms:
                await ChallengeAsync(world, "v1", Debate1);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, "v1");
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v2");
                break;
            case Scenario.RevisionWithdraws:
                await ChallengeAsync(world, "v1", Debate1);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, "v1");
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v2");
                break;
            case Scenario.DecidedByUser:
                await ChallengeAsync(world, "v1", Debate1);
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.User);
                break;
            case Scenario.RevisionAborted:
                await ChallengeAsync(world, "v1", Debate1);
                await CloseAsync(world, Debate1, DebateOutcome.Aborted, DebateClosedPayload.AbortedRule);
                break;
            case Scenario.TurnsTowardsApprovalUnaccepted:
            case Scenario.TurnsTowardsApprovalAccepted:
                await ChallengeAsync(world, "v1", Debate1);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, "v1");
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v2");
                if (scenario == Scenario.TurnsTowardsApprovalAccepted)
                {
                    await AcceptAsync(world, Debate1);
                }

                break;
            case Scenario.DeclinedByPerson:
                await ChallengeAsync(world, "v1", Debate1, open: false);
                await DeclineAsync(world, Debate1);
                break;
            case Scenario.JudgedThenDeclined:
                await ChallengeAsync(world, "v1", Debate1);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, "v1");
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v2");
                await ChallengeAsync(world, "v1", Debate2, open: false);
                await DeclineAsync(world, Debate2);
                break;
            case Scenario.NotOpenedThenDeclined:
                await ChallengeAsync(world, "v1", Debate1, open: false);
                await ChallengeAsync(world, "v1", Debate2, open: false);
                await DeclineAsync(world, Debate2);
                break;
            case Scenario.OtherVerdictRevisionApproves:
            case Scenario.OtherVerdictRevisionWithdraws:
                await ChallengeAsync(world, "v0", Debate1);
                await world.RevisionVerdictAsync(WorkWorld.Run1, scenario == Scenario.OtherVerdictRevisionApproves ? VerdictDecision.Approved : VerdictDecision.Rejected, "v0");
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v2");
                break;
            case Scenario.TwoChallengesOfTheSameVerdict:
                await ChallengeAsync(world, "v1", Debate1);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, "v1");
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v2");
                await ChallengeAsync(world, "v1", Debate2);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, "v1", id: "v3");
                await CloseAsync(world, Debate2, DebateOutcome.Resolved, JudgingPanel.Single, "v3");
                break;
            case Scenario.RevisionVerdictChallengedAndWithdrawn:
                await ChallengeAsync(world, "v1", Debate1);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, "v1");
                await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v2");
                await ChallengeAsync(world, "v2", Debate2);
                await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, "v2", id: "v3");
                await CloseAsync(world, Debate2, DebateOutcome.Resolved, JudgingPanel.Single, "v3");
                break;
            case Scenario.SecondRevisionConfirms:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await ReviseAsync(world, "v2", Debate2, VerdictDecision.Approved, "v3");
                break;
            case Scenario.SecondRevisionOpen:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await ChallengeAsync(world, "v2", Debate2);
                break;
            case Scenario.SecondRevisionAborted:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await ChallengeAsync(world, "v2", Debate2);
                await CloseAsync(world, Debate2, DebateOutcome.Aborted, DebateClosedPayload.AbortedRule);
                break;
            case Scenario.SecondRevisionDeclined:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await ChallengeAsync(world, "v2", Debate2, open: false);
                await DeclineAsync(world, Debate2);
                break;
            case Scenario.SecondRevisionTurnsTowardsApprovalUnaccepted:
            case Scenario.SecondRevisionTurnsTowardsApprovalAccepted:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Rejected, "v2");
                await ReviseAsync(world, "v2", Debate2, VerdictDecision.Approved, "v3");
                if (scenario == Scenario.SecondRevisionTurnsTowardsApprovalAccepted)
                {
                    await AcceptAsync(world, Debate2);
                }

                break;
            case Scenario.ThreeLevelChainConfirms:
            case Scenario.ThreeLevelChainWithdrawn:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await ReviseAsync(world, "v2", Debate2, VerdictDecision.Approved, "v3");
                await ReviseAsync(world, "v3", Debate3, scenario == Scenario.ThreeLevelChainConfirms ? VerdictDecision.Approved : VerdictDecision.Rejected, "v4");
                break;
            case Scenario.RevisionAbortedThenChallengedAgain:
                await ChallengeAsync(world, "v1", Debate1);
                await CloseAsync(world, Debate1, DebateOutcome.Aborted, DebateClosedPayload.AbortedRule);
                await ReviseAsync(world, "v1", Debate2, VerdictDecision.Approved, "v2");
                break;
            case Scenario.RejectedOriginalRevisionAccepted:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await AcceptAsync(world, Debate1);
                break;
            case Scenario.RejectedOriginalSecondRevisionWithdraws:
            case Scenario.RejectedOriginalSecondRevisionConfirms:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await AcceptAsync(world, Debate1);
                await ReviseAsync(world, "v2", Debate2, scenario == Scenario.RejectedOriginalSecondRevisionConfirms ? VerdictDecision.Approved : VerdictDecision.Rejected, "v3");
                break;
            case Scenario.RejectedOriginalSecondRevisionUnaccepted:
                await ReviseAsync(world, "v1", Debate1, VerdictDecision.Approved, "v2");
                await ReviseAsync(world, "v2", Debate2, VerdictDecision.Approved, "v3");
                break;
        }

        return (world, view.Header.Id);
    }

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task The_debate_screen_and_the_board_read_the_same_status_of_a_challenge(Scenario scenario, string verdict, ChallengeStatus status)
    {
        var (world, _) = await ArrangeAsync(scenario);
        using (world)
        {
            var log = (await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries;
            var byId = (await world.Instance.Verdicts.ListAsync(world.ProjectId, null, Ct)).ToDictionary(v => v.Id, StringComparer.Ordinal);

            // ChallengeLog.StatusOf decides whether a person may challenge again (anything but Pending) and whether a revision awaits their acceptance.
            Assert.Equal(status, ChallengeLog.StatusOf(log, byId[verdict], byId));
            Assert.Equal(status, ChallengeReader.Read(log, byId[verdict], byId).Status);
        }
    }
    [Fact]
    public async Task A_revision_that_names_the_verdict_it_revises_fails_closed_and_ends()
    {
        var (world, milestone) = await ArrangeAsync(Scenario.Unchallenged);
        using (world)
        {
            // A store that was tampered with: the revision of v1 says that v1 itself is its verdict, so the chain would never end.
            await ChallengeAsync(world, "v1", Debate1);
            await CloseAsync(world, Debate1, DebateOutcome.Resolved, JudgingPanel.Single, "v1");

            var readiness = await DeliveryOf(world).CheckAsync(world.Project, new RunId(WorkWorld.Run1), Ct);
            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.False(readiness.Ready);
            Assert.Equal(BoardColumn.InReview, board.Requirements.Single(c => c.Id == "R1").Column);
        }
    }

    [Fact]
    public async Task A_chain_of_revisions_longer_than_the_delivery_checks_is_in_review_on_the_board_and_blocks_the_delivery()
    {
        var (world, milestone) = await ArrangeAsync(Scenario.Unchallenged);
        using (world)
        {
            var verdict = "v1";
            for (var level = 2; level <= 20; level++)
            {
                var next = "v" + level;
                await ReviseAsync(world, verdict, $"deb-20261111-{level:x6}", VerdictDecision.Approved, next);
                verdict = next;
            }

            var readiness = await DeliveryOf(world).CheckAsync(world.Project, new RunId(WorkWorld.Run1), Ct);
            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.False(readiness.Ready);
            Assert.Equal(BoardColumn.InReview, board.Requirements.Single(c => c.Id == "R1").Column);
        }
    }

    private static DeliveryService DeliveryOf(WorkWorld world) =>
        new(new FakeWorktreeManager(new FakeRepository()), new RecordingApprovalGate(), world.Instance.Recorder, world.Instance.Verdicts);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_board_and_the_delivery_agree_on_what_a_challenge_leaves_of_the_verdict(Scenario scenario, bool deliverable, BoardColumn requirement, string verdict, ChallengeStatus status)
    {
        var (world, milestone) = await ArrangeAsync(scenario);
        using (world)
        {
            var readiness = await DeliveryOf(world).CheckAsync(world.Project, new RunId(WorkWorld.Run1), Ct);
            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);
            var r1 = board.Requirements.Single(c => c.Id == "R1").Column;
            var t1 = board.Tasks.Single(c => c.Id == "T1").Column;

            // The one that tells a person whether they may challenge again or accept the revision says what the delivery and the board act on.
            var log = (await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries;
            var byId = (await world.Instance.Verdicts.ListAsync(world.ProjectId, null, Ct)).ToDictionary(v => v.Id, StringComparer.Ordinal);
            Assert.Equal(status, ChallengeLog.StatusOf(log, byId[verdict], byId));

            Assert.Equal(deliverable, readiness.Ready);
            Assert.Equal(requirement, r1);
            Assert.Equal(r1, t1);

            // The two guarantees, whatever the scenario: nothing is delivered that the board has in review, and nothing stays in review that the delivery lets through.
            Assert.False(readiness.Ready && r1 == BoardColumn.InReview);
            Assert.False(readiness.Ready && status is ChallengeStatus.Pending or ChallengeStatus.AwaitingAcceptance or ChallengeStatus.DecidedByUser or ChallengeStatus.Revoked or ChallengeStatus.Aborted);
            Assert.False(status is ChallengeStatus.Pending or ChallengeStatus.AwaitingAcceptance or ChallengeStatus.DecidedByUser or ChallengeStatus.Aborted && r1 == BoardColumn.Done);
            Assert.False(readiness.Ready && r1 != BoardColumn.Done);
        }
    }
}
