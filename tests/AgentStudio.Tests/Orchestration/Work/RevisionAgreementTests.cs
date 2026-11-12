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
    }

    public static TheoryData<Scenario, bool, BoardColumn> Cases => new()
    {
        { Scenario.Unchallenged, true, BoardColumn.Done },
        { Scenario.RevisionOpen, false, BoardColumn.InReview },
        { Scenario.RevisionConfirms, true, BoardColumn.Done },
        { Scenario.RevisionWithdraws, false, BoardColumn.Returned },
        { Scenario.DecidedByUser, false, BoardColumn.InReview },
        { Scenario.RevisionAborted, false, BoardColumn.InReview },
        { Scenario.TurnsTowardsApprovalUnaccepted, false, BoardColumn.InReview },
        { Scenario.TurnsTowardsApprovalAccepted, true, BoardColumn.Done },

        // A person who declined to open the revision never started it: the challenge counts for nothing.
        { Scenario.DeclinedByPerson, true, BoardColumn.Done },
        { Scenario.JudgedThenDeclined, true, BoardColumn.Done },
        { Scenario.NotOpenedThenDeclined, true, BoardColumn.Done },

        // The challenge of a verdict that is not the one the run ended with: its revision never has to be accepted.
        { Scenario.OtherVerdictRevisionApproves, true, BoardColumn.Done },

        // The delivery is stricter than the board here: the card follows the last verdict (the one the run ended with, which nobody challenged) and the delivery also stops
        // for a revision of an attempt the judge corrected that withdrew its approval. The board never shows more done than the delivery lets through the other way round.
        { Scenario.OtherVerdictRevisionWithdraws, false, BoardColumn.Done },
    };

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
        var closingDecision = scenario is Scenario.TurnsTowardsApprovalUnaccepted or Scenario.TurnsTowardsApprovalAccepted ? VerdictDecision.ChangesRequested : VerdictDecision.Approved;
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
        }

        return (world, view.Header.Id);
    }

    [Theory]
    [InlineData(Scenario.DeclinedByPerson, "v1", ChallengeStatus.Pending, ChallengeStatus.None)]
    [InlineData(Scenario.OtherVerdictRevisionApproves, "v0", ChallengeStatus.AwaitingAcceptance, ChallengeStatus.Confirmed)]
    public async Task The_two_readers_of_a_challenge_differ_only_in_the_two_cases_where_the_board_follows_the_delivery(Scenario scenario, string verdict, ChallengeStatus debateSays, ChallengeStatus boardSays)
    {
        var (world, _) = await ArrangeAsync(scenario);
        using (world)
        {
            var log = (await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries;
            var byId = (await world.Instance.Verdicts.ListAsync(world.ProjectId, null, Ct)).ToDictionary(v => v.Id, StringComparer.Ordinal);

            Assert.Equal(debateSays, ChallengeLog.StatusOf(log, byId[verdict]));
            Assert.Equal(boardSays, ChallengeReader.Read(log, byId[verdict], byId).Status);
        }
    }

    private static DeliveryService DeliveryOf(WorkWorld world) =>
        new(new FakeWorktreeManager(new FakeRepository()), new RecordingApprovalGate(), world.Instance.Recorder, world.Instance.Verdicts);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_board_and_the_delivery_agree_on_what_a_challenge_leaves_of_the_verdict(Scenario scenario, bool deliverable, BoardColumn requirement)
    {
        var (world, milestone) = await ArrangeAsync(scenario);
        using (world)
        {
            var readiness = await DeliveryOf(world).CheckAsync(world.Project, new RunId(WorkWorld.Run1), Ct);
            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);
            var r1 = board.Requirements.Single(c => c.Id == "R1").Column;
            var t1 = board.Tasks.Single(c => c.Id == "T1").Column;

            Assert.Equal(deliverable, readiness.Ready);
            Assert.Equal(requirement, r1);
            Assert.Equal(r1, t1);

            // The two guarantees, whatever the scenario: nothing is delivered that the board has in review, and nothing stays in review that the delivery lets through.
            Assert.False(readiness.Ready && r1 == BoardColumn.InReview);
            Assert.False(readiness.Ready && r1 != BoardColumn.Done);
        }
    }
}
