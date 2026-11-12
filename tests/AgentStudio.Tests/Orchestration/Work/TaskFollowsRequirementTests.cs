using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// The card of a task never contradicts the card of a requirement it covers (docs/16 D-19, F9-2): an approved verdict with a requirement not satisfied leaves the requirement
/// returned (or in review) and so its task, which is not «Hecho». The destination of the requirement is the one the owner has yet to decide (docs/18); the task only follows it.
/// </summary>
public sealed class TaskFollowsRequirementTests
{
    private static CancellationToken Ct => WorkWorld.Ct;

    public static TheoryData<AssessmentStatus, BoardColumn> Assessments => new()
    {
        { AssessmentStatus.Satisfied, BoardColumn.Done },
        { AssessmentStatus.NotSatisfied, BoardColumn.Returned },
        { AssessmentStatus.NotVerifiable, BoardColumn.InReview },
    };

    [Theory]
    [MemberData(nameof(Assessments))]
    public async Task An_approved_verdict_with_the_requirement_of_the_task_not_satisfied_does_not_leave_the_task_done(AssessmentStatus assessed, BoardColumn column)
    {
        var world = await WorkWorld.OpenAsync();
        using (world)
        {
            var view = await world.OpenMilestoneAsync();
            await world.StartRunAsync();
            await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, statuses: new Dictionary<string, AssessmentStatus> { ["R1"] = assessed });

            var board = await world.Board.ProjectAsync(world.Project, view.Header.Id, Ct);

            Assert.Equal(column, board.Requirements.Single(c => c.Id == "R1").Column);
            Assert.Equal(column, board.Tasks.Single(c => c.Id == "T1").Column);
        }
    }

    [Fact]
    public async Task A_requirement_not_satisfied_leaves_alone_the_task_that_does_not_cover_it()
    {
        var world = await WorkWorld.OpenAsync();
        using (world)
        {
            var view = await world.OpenMilestoneAsync();
            await world.StartRunAsync();
            await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, statuses: new Dictionary<string, AssessmentStatus> { ["R2"] = AssessmentStatus.NotSatisfied });

            var board = await world.Board.ProjectAsync(world.Project, view.Header.Id, Ct);

            Assert.Equal(BoardColumn.Done, board.Requirements.Single(c => c.Id == "R1").Column);
            Assert.Equal(BoardColumn.Done, board.Tasks.Single(c => c.Id == "T1").Column);
            Assert.Equal(BoardColumn.Returned, board.Requirements.Single(c => c.Id == "R2").Column);
            Assert.Equal(BoardColumn.Returned, board.Tasks.Single(c => c.Id == "T2").Column);
        }
    }

    [Fact]
    public void The_rule_of_a_task_card_gives_the_worst_of_what_the_verdict_says_of_its_requirements()
    {
        static BoardColumn? Column(params AssessmentStatus?[] assessed) =>
            BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Completed, VerdictDecision.Approved, assessed));

        Assert.Equal(BoardColumn.Done, Column(AssessmentStatus.Satisfied, AssessmentStatus.Satisfied));
        Assert.Equal(BoardColumn.Returned, Column(AssessmentStatus.Satisfied, AssessmentStatus.NotSatisfied));
        Assert.Equal(BoardColumn.InReview, Column(AssessmentStatus.Satisfied, AssessmentStatus.NotVerifiable));
        Assert.Equal(BoardColumn.InReview, Column(new AssessmentStatus?[] { null }));
        Assert.Equal(BoardColumn.Done, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Completed, VerdictDecision.Approved, [])));

        // Only an approval is softened: a verdict that returns the work returns the task whatever it assessed, and nothing else reaches Done.
        Assert.Equal(BoardColumn.Returned, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Completed, VerdictDecision.Rejected, [AssessmentStatus.Satisfied])));
        Assert.NotEqual(BoardColumn.Done, BoardRules.ColumnOf(new TaskBoardFacts(WorkTaskStatus.Running, VerdictDecision.Approved, [AssessmentStatus.Satisfied])));
    }
}
