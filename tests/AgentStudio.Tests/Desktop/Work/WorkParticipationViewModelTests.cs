using AgentStudio.Application.Decisions;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Tracing;
using AgentStudio.Tests.Orchestration.WorkTracking;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>
/// docs/16 D-32 and F9-5 on the screen: the six roles of a task, a run or a milestone are shown each with its own source, and a role no source
/// gives says «No consta» instead of taking the name of another.
/// </summary>
public sealed class WorkParticipationViewModelTests
{
    private static async Task<WorkTestWorld> RunningAsync()
    {
        var test = await WorkTestWorld.OpenAsync();
        await test.World.StartRunAsync();
        return test;
    }

    private static ParticipationRowViewModel Role(WorkTestWorld test, ParticipationRole role) => test.Document.Participation.Roles.Single(r => r.Entry.Role == role);

    [Fact]
    public async Task A_run_with_nothing_recorded_shows_the_six_roles_as_no_consta()
    {
        using var test = await RunningAsync();
        test.Document.Context.LookAt(TraceRef.Run(WorkWorld.Run1));
        await test.Document.Participation.ReloadAsync();

        var roles = test.Document.Participation.Roles;
        Assert.Equal(Enum.GetValues<ParticipationRole>(), roles.Select(r => r.Entry.Role));
        Assert.All(roles, r =>
        {
            Assert.False(r.IsRecorded);
            Assert.Equal(Texts.Get("Work_Participation_NotRecorded"), r.WhoText);
            Assert.False(r.HasSource);
        });
    }

    [Fact]
    public async Task Each_role_shows_who_took_it_and_where_that_comes_from_and_the_rest_stay_no_consta()
    {
        using var test = await RunningAsync();
        var world = test.World;
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved);
        await world.RecordAsUserAsync(DecisionKind.ApprovalGranted, new ApprovalAnsweredPayload("integrate.deliver", true, "manuel", null, null) { RunId = WorkWorld.Run1 });
        test.Document.Context.LookAt(TraceRef.Run(WorkWorld.Run1));
        await test.Document.Participation.ReloadAsync();

        var producer = Role(test, ParticipationRole.ProducingAgent);
        Assert.Equal("ejecutor", producer.WhoText);
        Assert.Equal(Texts.Format("Work_Participation_Source", Texts.Get("Work_Source_Attempt")), producer.SourceText);
        Assert.Equal("judge", Role(test, ParticipationRole.Reviewer).WhoText);
        Assert.Equal("manuel", Role(test, ParticipationRole.Approver).WhoText);

        // Nobody published and nothing was integrated: those roles are not filled from the others.
        Assert.Equal(Texts.Get("Work_Participation_NotRecorded"), Role(test, ParticipationRole.PublishingAccount).WhoText);
        Assert.Equal(Texts.Get("Work_Participation_NotRecorded"), Role(test, ParticipationRole.Integrator).WhoText);
    }

    [Fact]
    public async Task The_subjects_of_a_node_are_its_task_its_run_and_the_milestone_selected()
    {
        using var test = await RunningAsync();
        var view = await test.World.OpenMilestoneAsync();
        await test.Document.ReloadAsync();
        test.Document.Milestones.SelectedItem = test.Document.Milestones.Items.Single();

        test.Document.Context.LookAt(TraceRef.Attempt(WorkWorld.Run1, "T1", 1));
        await test.Document.Participation.ReloadAsync();

        Assert.Equal(
            [TraceRef.Task(WorkWorld.Run1, "T1"), TraceRef.Run(WorkWorld.Run1), TraceRef.Milestone(view.Header.Id.Value)],
            test.Document.Participation.Subjects.Select(s => s.Value));
        test.Document.Participation.SelectedSubject = test.Document.Participation.Subjects[2];
        await Task.Delay(100, WorkTestWorld.Ct);
        Assert.Equal(6, test.Document.Participation.Roles.Count);
    }

    [Fact]
    public async Task Every_role_and_source_the_projector_can_give_has_a_text()
    {
        foreach (var role in Enum.GetValues<ParticipationRole>())
        {
            Assert.False(WorkTexts.Role(role).StartsWith('['), role.ToString());
            Assert.True(Texts.Current.Has("Work_Role_" + role), role.ToString());
        }

        foreach (var source in new[]
        {
            ParticipationSources.Attempt, ParticipationSources.Commit, ParticipationSources.Verdict, ParticipationSources.Approval,
            ParticipationSources.LocalDelivery, ParticipationSources.SkippedReview,
        })
        {
            Assert.DoesNotContain("participation.", WorkTexts.Source(source), StringComparison.Ordinal);
        }

        Assert.Null(WorkTexts.Source(null));
        await Task.CompletedTask;
    }
}
