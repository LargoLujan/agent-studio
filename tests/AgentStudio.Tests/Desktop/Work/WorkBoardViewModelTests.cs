using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Orchestration.WorkTracking;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>
/// docs/16 F9-2 and D-19 on the screen: the board is the one <c>BoardProjector</c> reads from the verified log, drawn column by column,
/// and no card is in «Hecho» or «Integrado» without an approved verdict. The tests run on a real project and its real projector: a double
/// would prove nothing about the rule.
/// </summary>
public sealed class WorkBoardViewModelTests
{
    private static CancellationToken Ct => WorkTestWorld.Ct;

    /// <summary>A milestone open with a run of the fixture plan (T1 covers R1, T2 covers R2) linked to it, selected on the screen.</summary>
    private static async Task<(WorkTestWorld World, MilestoneId Milestone)> LinkedAsync()
    {
        var test = await WorkTestWorld.OpenAsync();
        var view = await test.World.OpenMilestoneAsync();
        await test.World.StartRunAsync();
        await test.World.Milestones.LinkAsync(test.World.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await test.Document.ReloadAsync();
        test.Document.Milestones.SelectedItem = test.Document.Milestones.Items.Single();
        await test.Document.Board.ReloadAsync();
        return (test, view.Header.Id);
    }

    private static BoardColumnViewModel Column(WorkTestWorld test, BoardColumn column) => test.Document.Board.Columns.Single(c => c.Column == column);

    private static IReadOnlyList<string> Titles(BoardColumnViewModel column, BoardCardKind kind) =>
        [.. column.Cards.Where(c => c.Card.Kind == kind).Select(c => c.Card.Id)];

    [Fact]
    public async Task The_board_has_the_seven_columns_in_order_and_a_new_milestone_has_everything_planned()
    {
        var (test, _) = await LinkedAsync();
        using (test)
        {
            Assert.Equal(Enum.GetValues<BoardColumn>(), test.Document.Board.Columns.Select(c => c.Column));
            Assert.Equal(["R1", "R2"], Titles(Column(test, BoardColumn.Planned), BoardCardKind.Requirement));
            Assert.Empty(Column(test, BoardColumn.Done).Cards);
            Assert.Empty(Column(test, BoardColumn.Integrated).Cards);
            Assert.Equal(Texts.Get("Work_Column_Planned"), Column(test, BoardColumn.Planned).Title);
            Assert.NotNull(test.Document.Board.ReadText);
        }
    }

    [Fact]
    public async Task No_card_reaches_done_or_integrated_without_an_approved_verdict_and_the_screen_never_moves_one()
    {
        var (test, _) = await LinkedAsync();
        using (test)
        {
            var world = test.World;

            // The task is completed and the run ended, delivered, with no verdict at all: in review, never done.
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.DeliverAsync(WorkWorld.Run1);
            await test.Document.Board.ReloadAsync();
            AssertNothingDone(test);
            Assert.Contains("R1", Titles(Column(test, BoardColumn.InReview), BoardCardKind.Requirement));

            // Every verdict that is not an approval leaves it out of done and integrated too.
            foreach (var (decision, id) in new[] { (VerdictDecision.ChangesRequested, "v-changes"), (VerdictDecision.EscalateToHuman, "v-escalated"), (VerdictDecision.Unresolved, "v-unresolved"), (VerdictDecision.Rejected, "v-rejected") })
            {
                await world.VerdictAsync(WorkWorld.Run1, decision, id: id, task: "T1");
                await test.Document.Board.ReloadAsync();
                AssertNothingDone(test);
            }

            // A verdict the store does not keep as valid is no verdict for the board.
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, validInStore: false, id: "v-invalid", task: "T1");
            await test.Document.Board.ReloadAsync();
            AssertNothingDone(test);
        }
    }

    private static void AssertNothingDone(WorkTestWorld test)
    {
        Assert.DoesNotContain(Column(test, BoardColumn.Done).Cards, c => c.Card.Kind == BoardCardKind.Requirement);
        Assert.DoesNotContain(Column(test, BoardColumn.Integrated).Cards, c => c.Card.Kind == BoardCardKind.Requirement);
    }

    [Fact]
    public async Task An_approved_and_valid_verdict_puts_the_requirement_in_done_and_a_delivery_after_it_in_integrated()
    {
        var (test, _) = await LinkedAsync();
        using (test)
        {
            var world = test.World;
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T1");
            await test.Document.Board.ReloadAsync();

            var done = Column(test, BoardColumn.Done);
            Assert.NotEmpty(done.Cards);
            Assert.All(done.Cards.Where(c => c.Card.Kind == BoardCardKind.Requirement), c => Assert.Equal(BoardColumn.Done, c.Card.Column));
            Assert.DoesNotContain(Column(test, BoardColumn.Integrated).Cards, c => c.Card.Kind == BoardCardKind.Requirement);

            await world.DeliverAsync(WorkWorld.Run1);
            await test.Document.Board.ReloadAsync();

            Assert.Contains(Column(test, BoardColumn.Integrated).Cards, c => c.Card.Kind == BoardCardKind.Requirement);
        }
    }

    [Fact]
    public async Task A_task_that_waits_for_the_usage_limit_says_why_in_words()
    {
        var (test, _) = await LinkedAsync();
        using (test)
        {
            await test.World.TransitionAsync(WorkWorld.Run1, "T1", WorkTaskStatus.Pending, WorkTaskStatus.Running);
            await test.World.TransitionAsync(WorkWorld.Run1, "T1", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "RateLimitedUntil(2026-11-12T10:00:00.0000000+00:00)");
            await test.Document.Board.ReloadAsync();

            var card = Column(test, BoardColumn.InProgress).Cards.Single(c => c.Card.Kind == BoardCardKind.Task && c.Card.Id == "T1");
            Assert.Equal(Texts.Get("Work_Reason_WaitingForQuota"), card.ReasonText);
            Assert.True(card.HasReason);
            Assert.DoesNotContain("quota", card.ReasonText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_card_in_review_because_the_verdict_was_challenged_says_why()
    {
        const string debate = "deb-20261111-aaaaaa";
        var (test, _) = await LinkedAsync();
        using (test)
        {
            var world = test.World;
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T1");
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.VerdictChallenged, new VerdictChallengedPayload("v1", "manuel", new EvidenceId("motivos"), new string('e', 64), debate) { RunId = WorkWorld.Run1 });
            await world.RecordAsync(debate, DecisionKind.DebateOpened, new DebateOpenedPayload(
                DebateMode.Adversarial, new string('1', 64), new string('2', 64), new string('3', 64), new EvidenceId("pregunta"), new string('4', 64),
                DebateSubjectKind.Verdict, "v1", null, [], [], DebateJudgingKind.IndependentJudge, [], [], 3, 600, null, 8, "v1") { RunId = debate });
            await test.Document.Board.ReloadAsync();

            var card = Column(test, BoardColumn.InReview).Cards.Single(c => c.Card.Kind == BoardCardKind.Requirement && c.Card.Id == "R1");
            Assert.Equal(Texts.Get("Work_Reason_Challenged"), card.ReasonText);
            AssertNothingDone(test);
        }
    }

    [Fact]
    public async Task Every_reason_code_the_board_can_give_has_a_text()
    {
        foreach (var code in new[]
        {
            BoardCardReasons.AwaitingApproval, BoardCardReasons.WaitingForQuota, BoardCardReasons.AwaitingAnswer, BoardCardReasons.Blocked,
            BoardCardReasons.Challenged, BoardCardReasons.EscalatedToPerson, BoardCardReasons.Unresolved, BoardCardReasons.RunFailed,
        })
        {
            var text = WorkTexts.Reason(code);
            Assert.NotNull(text);
            Assert.DoesNotContain("board.", text, StringComparison.Ordinal);
            Assert.DoesNotContain('[', text);
        }

        Assert.Null(WorkTexts.Reason(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_rules_the_screen_shows_are_the_ones_the_code_has()
    {
        var (test, _) = await LinkedAsync();
        using (test)
        {
            Assert.Equal(5, test.Document.Board.Rules.Count);
            Assert.All(test.Document.Board.Rules, rule => Assert.False(rule.StartsWith('[')));
        }
    }
}
