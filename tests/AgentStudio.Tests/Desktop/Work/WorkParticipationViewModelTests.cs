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

    /// <summary>A projector that answers each call only when the test lets it, and says in the answer which subject it was asked about.</summary>
    private sealed class GatedParticipation : IParticipationProjector
    {
        public List<(TraceRef Subject, TaskCompletionSource<bool> Gate)> Calls { get; } = [];

        public async Task<ParticipationView> ProjectAsync(AgentStudio.Application.Projects.ProjectContext project, TraceRef subject, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Calls)
            {
                Calls.Add((subject, gate));
            }

            await gate.Task.WaitAsync(cancellationToken);
            return new ParticipationView(subject, [new ParticipationEntry(ParticipationRole.Reviewer, subject.Id, null, ParticipationSources.Verdict)]);
        }

        public async Task WaitForCallsAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (Calls)
                {
                    if (Calls.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10, cancellationToken);
            }
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public async Task Two_overlapping_reads_show_the_one_asked_last_and_its_error_whichever_ends_last(bool theFirstEndsLast, bool theFirstFails, bool theSecondFails)
    {
        var gated = new GatedParticipation();
        using var withTimeout = CancellationTokenSource.CreateLinkedTokenSource(WorkTestWorld.Ct);
        withTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var test = await WorkTestWorld.OpenAsync((_, backend) => backend with { Participation = gated });
        await test.World.StartRunAsync();

        // The focus changes (the task is selected: first read) and the person then selects the run (second read) before the first one answers.
        test.Document.Context.LookAt(TraceRef.Attempt(WorkWorld.Run1, "T1", 1));
        await gated.WaitForCallsAsync(1, withTimeout.Token);
        var participation = test.Document.Participation;
        participation.SelectedSubject = participation.Subjects.Single(s => s.Value.Kind == TraceNodeKind.Run);
        await gated.WaitForCallsAsync(2, withTimeout.Token);
        var (first, second) = (gated.Calls[0], gated.Calls[1]);
        Assert.Equal(TraceNodeKind.Task, first.Subject.Kind);
        Assert.Equal(TraceNodeKind.Run, second.Subject.Kind);

        void End(TaskCompletionSource<bool> gate, bool fails)
        {
            if (fails)
            {
                gate.SetException(new InvalidOperationException("la lectura falló"));
            }
            else
            {
                gate.SetResult(true);
            }
        }

        if (theFirstEndsLast)
        {
            End(second.Gate, theSecondFails);
            await Task.Delay(100, withTimeout.Token);
            End(first.Gate, theFirstFails);
        }
        else
        {
            End(first.Gate, theFirstFails);
            await Task.Delay(100, withTimeout.Token);
            End(second.Gate, theSecondFails);
        }

        await Task.Delay(200, withTimeout.Token);

        // What the read asked last says is what is shown, its roles or its error, and what an older one that ended later found changes nothing.
        Assert.Equal(theSecondFails, participation.HasError);
        if (theSecondFails)
        {
            Assert.Empty(participation.Roles);
        }
        else
        {
            Assert.Equal(second.Subject.Id, Assert.Single(participation.Roles).Entry.Who);
        }
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
