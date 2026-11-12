using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Challenges;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/15 D-38 to D-40 on the board: a challenged verdict is set aside until its revision settles it. The board reads the challenge with
/// <see cref="ChallengeReader"/>, which repeats <c>ChallengeLog.StatusOf</c> (the Debate module is out of reach of Orchestration): for the same
/// log both must give the same status, and the card must follow it.
/// </summary>
public sealed class ChallengedVerdictBoardTests
{
    private const string Debate = "deb-20261111-aaaaaa";

    private static CancellationToken Ct => WorkWorld.Ct;

    public enum Closing
    {
        NotClosed,
        Judge,
        User,
        Aborted,
    }

    /// <summary>A run whose original verdict was challenged, and what the revision did.</summary>
    private static async Task<(WorkWorld World, MilestoneId Milestone)> ScenarioAsync(VerdictDecision original, Closing closing, VerdictDecision revised = VerdictDecision.Approved, bool accepted = false, string? opensChallenging = "v1", bool revisionStored = true)
    {
        var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
        await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
        await world.VerdictAsync(WorkWorld.Run1, original);
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.VerdictChallenged, new VerdictChallengedPayload("v1", "manuel", new EvidenceId("motivos"), new string('e', 64), Debate) { RunId = WorkWorld.Run1 });
        await world.RecordAsync(Debate, DecisionKind.DebateOpened, Opened(opensChallenging));
        if (closing != Closing.NotClosed)
        {
            string[] verdicts = closing == Closing.Aborted ? [] : ["v2"];
            if (closing != Closing.Aborted)
            {
                await world.VerdictAsync(Debate, revised, id: "v2", store: revisionStored);
            }

            await world.RecordAsync(
                Debate,
                DecisionKind.DebateClosed,
                new DebateClosedPayload(closing == Closing.Aborted ? DebateOutcome.Aborted : DebateOutcome.Resolved, verdicts, closing == Closing.User ? JudgingPanel.User : "judge", null, "cerrado") { RunId = Debate });
            if (accepted)
            {
                await world.RecordAsync(Debate, DecisionKind.ApprovalRequested, new ApprovalRequestedPayload(ChallengeReader.RevisionAcceptanceAction, "Aceptas", RiskLevel.Critical, null, []) { RunId = Debate });
                await world.RecordAsUserAsync(DecisionKind.ApprovalGranted, new ApprovalAnsweredPayload(ChallengeReader.RevisionAcceptanceAction, true, "manuel", null, null) { RunId = Debate });
            }
        }

        return (world, view.Header.Id);
    }

    private static DebateOpenedPayload Opened(string? challenges) => new(
        DebateMode.Adversarial,
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        new EvidenceId("pregunta"),
        new string('4', 64),
        DebateSubjectKind.Verdict,
        "v1",
        null,
        [],
        [],
        DebateJudgingKind.IndependentJudge,
        [],
        [],
        3,
        600,
        null,
        8,
        challenges) { RunId = Debate };

    private static async Task<BoardColumn> R1Async(WorkWorld world, MilestoneId milestone) =>
        (await world.Board.ProjectAsync(world.Project, milestone, Ct)).Requirements.Single(c => c.Id == "R1").Column;

    /// <summary>The status the Debate module gives to the verdict "v1" of the scenario, and the one the board's reader gives.</summary>
    private static async Task<(ChallengeStatus Debate, ChallengeStatus Board)> StatusesAsync(WorkWorld world)
    {
        var log = (await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries;
        var all = await world.Instance.Verdicts.ListAsync(world.ProjectId, null, Ct);
        var byId = all.ToDictionary(v => v.Id, StringComparer.Ordinal);
        return (ChallengeLog.StatusOf(log, byId["v1"]), ChallengeReader.Read(log, byId["v1"], byId).Status);
    }

    public static TheoryData<VerdictDecision, Closing, VerdictDecision, bool, ChallengeStatus, BoardColumn> Cases => new()
    {
        { VerdictDecision.Approved, Closing.NotClosed, VerdictDecision.Approved, false, ChallengeStatus.Pending, BoardColumn.InReview },
        { VerdictDecision.Approved, Closing.Aborted, VerdictDecision.Approved, false, ChallengeStatus.Pending, BoardColumn.InReview },
        { VerdictDecision.Approved, Closing.Judge, VerdictDecision.Approved, false, ChallengeStatus.Confirmed, BoardColumn.Done },
        { VerdictDecision.Approved, Closing.Judge, VerdictDecision.Rejected, false, ChallengeStatus.Revoked, BoardColumn.Returned },
        { VerdictDecision.Approved, Closing.Judge, VerdictDecision.ChangesRequested, false, ChallengeStatus.Revoked, BoardColumn.Returned },

        // «Decidir yo»: the entry carries the verdicts of the judge, not what the person decided, so the card waits in review.
        { VerdictDecision.Approved, Closing.User, VerdictDecision.Approved, false, ChallengeStatus.DecidedByUser, BoardColumn.InReview },
        { VerdictDecision.Approved, Closing.User, VerdictDecision.Rejected, false, ChallengeStatus.DecidedByUser, BoardColumn.InReview },

        // A revision that turns towards an approval waits for the person, and leaves review when they accept.
        { VerdictDecision.ChangesRequested, Closing.Judge, VerdictDecision.Approved, false, ChallengeStatus.AwaitingAcceptance, BoardColumn.InReview },
        { VerdictDecision.ChangesRequested, Closing.Judge, VerdictDecision.Approved, true, ChallengeStatus.Accepted, BoardColumn.Done },
        { VerdictDecision.ChangesRequested, Closing.Judge, VerdictDecision.Rejected, false, ChallengeStatus.Confirmed, BoardColumn.Returned },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_board_and_ChallengeLog_give_the_same_status_for_the_same_log_and_the_card_follows_it(VerdictDecision original, Closing closing, VerdictDecision revised, bool accepted, ChallengeStatus expectedStatus, BoardColumn expectedColumn)
    {
        var (world, milestone) = await ScenarioAsync(original, closing, revised, accepted);
        using (world)
        {
            var (debate, board) = await StatusesAsync(world);

            Assert.Equal(expectedStatus, debate);
            Assert.Equal(debate, board);
            Assert.Equal(expectedColumn, await R1Async(world, milestone));
        }
    }

    [Fact]
    public async Task A_person_who_decides_a_challenged_approval_themselves_never_leaves_the_card_done()
    {
        // The judge of the revision said Approved; the person decided otherwise. The entry cannot say so: the card waits for a person's look.
        var (world, milestone) = await ScenarioAsync(VerdictDecision.Approved, Closing.User, VerdictDecision.Approved);
        using (world)
        {
            var card = (await world.Board.ProjectAsync(world.Project, milestone, Ct)).Requirements.Single(c => c.Id == "R1");

            Assert.Equal((BoardColumn.InReview, BoardCardReasons.Challenged), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task A_revision_debate_that_does_not_challenge_this_verdict_leaves_the_challenge_pending()
    {
        var (world, milestone) = await ScenarioAsync(VerdictDecision.Approved, Closing.Judge, VerdictDecision.Approved, opensChallenging: "otro-veredicto");
        using (world)
        {
            var (debate, board) = await StatusesAsync(world);

            Assert.Equal((ChallengeStatus.Pending, ChallengeStatus.Pending), (debate, board));
            Assert.Equal(BoardColumn.InReview, await R1Async(world, milestone));
        }
    }

    [Fact]
    public async Task A_revision_whose_verdict_is_not_in_the_store_does_not_settle_the_challenge()
    {
        var (world, milestone) = await ScenarioAsync(VerdictDecision.Approved, Closing.Judge, VerdictDecision.Approved, revisionStored: false);
        using (world)
        {
            Assert.Equal(BoardColumn.InReview, await R1Async(world, milestone));
        }
    }
}
