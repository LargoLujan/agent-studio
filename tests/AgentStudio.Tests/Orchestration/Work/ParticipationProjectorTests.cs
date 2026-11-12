using AgentStudio.Application.Persistence;
using AgentStudio.Application.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/16 D-32 and F9-5, ADR-0039, docs/04 §23.3: the six roles are never confused; a role with no source is «no consta» and is never deduced; a review that
/// was not made is never attributed; a log that does not verify is not read.
/// </summary>
public sealed class ParticipationProjectorTests
{
    private static readonly string CommitA = new('a', 40);

    private static CancellationToken Ct => WorkWorld.Ct;

    private static ParticipationProjector Projector(WorkWorld world) => new(world.Instance.Recorder, world.Instance.Verdicts, world.Instance.Plans);

    private static TraceRef T1 => TraceRef.Task(WorkWorld.Run1, "T1");

    private static ParticipationEntry Role(ParticipationView view, ParticipationRole role) => Assert.Single(view.Entries, e => e.Role == role);

    private static async Task<WorkWorld> RunningAsync()
    {
        var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();
        return world;
    }

    [Fact]
    public async Task A_run_with_nothing_recorded_has_the_six_roles_as_no_consta()
    {
        using var world = await RunningAsync();

        var view = await Projector(world).ProjectAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        Assert.Equal(Enum.GetValues<ParticipationRole>(), view.Entries.Select(e => e.Role));
        Assert.All(view.Entries, e => Assert.False(e.IsRecorded));
    }

    [Fact]
    public async Task Each_role_comes_from_its_own_source_in_the_log()
    {
        using var world = await RunningAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.LaunchAsync(WorkWorld.Run1, "T1", 2);
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 2, EvidenceKind.CodeDiff, "diff1");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved);
        await world.RecordAsUserAsync(DecisionKind.ApprovalGranted, new ApprovalAnsweredPayload("integrate.deliver", true, "manuel", null, null) { RunId = WorkWorld.Run1 });
        await world.DeliverAsync(WorkWorld.Run1, CommitA);

        var view = await Projector(world).ProjectAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        var producer = Role(view, ParticipationRole.ProducingAgent);
        Assert.Equal(("ejecutor", ParticipationSources.Attempt), (producer.Who, producer.Source));
        Assert.Contains("2 intento(s)", producer.Detail);
        var author = Role(view, ParticipationRole.GitAuthorAndCommitter);
        Assert.Equal(("Agent Studio", ParticipationSources.Commit), (author.Who, author.Source));
        Assert.False(Role(view, ParticipationRole.PublishingAccount).IsRecorded);
        var reviewer = Role(view, ParticipationRole.Reviewer);
        Assert.Equal(("judge", ParticipationSources.Verdict), (reviewer.Who, reviewer.Source));
        Assert.Contains("Approved", reviewer.Detail);
        var approver = Role(view, ParticipationRole.Approver);
        Assert.Equal(("manuel", ParticipationSources.Approval, "integrate.deliver"), (approver.Who, approver.Source, approver.Detail));
        var integrator = Role(view, ParticipationRole.Integrator);
        Assert.Equal(ParticipationSources.LocalDelivery, integrator.Source);
        Assert.Contains(CommitA, integrator.Detail);
    }

    [Fact]
    public async Task A_delivery_without_a_commit_or_with_a_conflict_a_verdict_not_valid_or_not_in_the_store_and_a_denial_attribute_nothing()
    {
        using var world = await RunningAsync();
        await world.DeliverAsync(WorkWorld.Run1, conflict: true);
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, validInLog: false, validInStore: false, id: "bad");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, id: "lost", store: false);
        await world.RecordAsUserAsync(DecisionKind.ApprovalDenied, new ApprovalAnsweredPayload("integrate.deliver", false, "manuel", null, null) { RunId = WorkWorld.Run1 });

        var view = await Projector(world).ProjectAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        Assert.All(view.Entries, e => Assert.False(e.IsRecorded));
    }

    [Fact]
    public async Task A_verdict_whose_decision_differs_from_the_stored_one_is_not_attributed()
    {
        using var world = await RunningAsync();
        await world.Instance.Verdicts.AppendAsync(world.ProjectId, new StoredVerdict("v1", ValidVerdict(Dossier(), VerdictDecision.Rejected), WorkWorld.Run1, "T7", AuditScope.CurrentPlan, true, [], null), Ct);
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.VerdictIssued, new VerdictIssuedPayload("v1", null, VerdictDecision.Approved, true, [], "h", "g", null, null) { RunId = WorkWorld.Run1 });

        var view = await Projector(world).ProjectAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        Assert.False(Role(view, ParticipationRole.Reviewer).IsRecorded);
    }

    [Fact]
    public async Task A_task_has_its_own_agents_and_what_has_no_task_counts_for_all_of_them()
    {
        using var world = await RunningAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1, agent: "ejecutor");
        await world.LaunchAsync(WorkWorld.Run1, "T2", 1, agent: "ejecutor2");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved);
        var projector = Projector(world);

        var first = await projector.ProjectAsync(world.Project, T1, Ct);
        var second = await projector.ProjectAsync(world.Project, TraceRef.Task(WorkWorld.Run1, "T2"), Ct);
        var run = await projector.ProjectAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        Assert.Equal("ejecutor", Role(first, ParticipationRole.ProducingAgent).Who);
        Assert.Equal("ejecutor2", Role(second, ParticipationRole.ProducingAgent).Who);
        Assert.Equal(["ejecutor", "ejecutor2"], run.Entries.Where(e => e.Role == ParticipationRole.ProducingAgent).Select(e => e.Who).Order());
        Assert.True(Role(first, ParticipationRole.Reviewer).IsRecorded);
        Assert.True(Role(second, ParticipationRole.Reviewer).IsRecorded);
    }

    [Fact]
    public async Task The_verdict_issued_on_the_review_task_counts_for_the_tasks_whose_requirements_it_assessed_and_for_no_other()
    {
        // The engine issues the verdict of a run on its review task (T7); the chain hangs it from the tasks it judged and the participation must agree with it.
        using var world = await RunningAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7", assessing: ["R1"]);
        var projector = Projector(world);

        var first = await projector.ProjectAsync(world.Project, T1, Ct);
        var second = await projector.ProjectAsync(world.Project, TraceRef.Task(WorkWorld.Run1, "T2"), Ct);
        var run = await projector.ProjectAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        Assert.Equal(("judge", ParticipationSources.Verdict), (Role(first, ParticipationRole.Reviewer).Who, Role(first, ParticipationRole.Reviewer).Source));
        Assert.False(Role(second, ParticipationRole.Reviewer).IsRecorded);
        Assert.True(Role(run, ParticipationRole.Reviewer).IsRecorded);
    }

    [Fact]
    public async Task A_verdict_with_no_assessments_counts_for_every_task_and_one_issued_on_another_task_only_for_that_one()
    {
        using var world = await RunningAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7", assessing: [], id: "whole");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, task: "T2", id: "of-t2");
        var projector = Projector(world);

        var first = await projector.ProjectAsync(world.Project, T1, Ct);
        var second = await projector.ProjectAsync(world.Project, TraceRef.Task(WorkWorld.Run1, "T2"), Ct);

        // The details differ by the decision, so each verdict is its own entry.
        Assert.Contains("Approved", Assert.Single(first.Entries, e => e.Role == ParticipationRole.Reviewer).Detail);
        Assert.Equal(["Approved", "Rejected"], second.Entries.Where(e => e.Role == ParticipationRole.Reviewer).Select(e => e.Detail!.Split(": ")[1]).Order());
    }

    [Fact]
    public async Task A_milestone_gathers_the_runs_linked_to_it_and_the_ones_that_follow()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await world.StartRunAsync("20261112-d4e5f6", follows: WorkWorld.Run1);
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1, agent: "ejecutor");
        await world.LaunchAsync("20261112-d4e5f6", "T1", 1, agent: "ejecutor2");
        await world.StartRunAsync("20261113-aaaaaa");
        await world.LaunchAsync("20261113-aaaaaa", "T1", 1, agent: "ejecutorG");

        var participation = await Projector(world).ProjectAsync(world.Project, TraceRef.Milestone(view.Header.Id.Value), Ct);

        Assert.Equal(["ejecutor", "ejecutor2"], participation.Entries.Where(e => e.Role == ParticipationRole.ProducingAgent).Select(e => e.Who).Order());
    }

    [Fact]
    public async Task Only_a_task_a_run_or_a_milestone_can_be_asked_and_a_broken_log_is_not_read()
    {
        using var world = await RunningAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Projector(world).ProjectAsync(world.Project, TraceRef.Commit(CommitA), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Projector(world).ProjectAsync(world.Project, new TraceRef(TraceNodeKind.Task, "sin-barra"), Ct));
        var broken = new ParticipationProjector(new BrokenLogRecorder(world.Instance.Recorder), world.Instance.Verdicts, world.Instance.Plans);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => broken.ProjectAsync(world.Project, T1, Ct));
    }
}
