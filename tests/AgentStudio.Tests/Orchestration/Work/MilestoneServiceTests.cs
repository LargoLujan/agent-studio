using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Application.Work;
using AgentStudio.Orchestration.Work;
using AgentStudio.Tests.Domain.Orchestration;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/16 D-17 and F9-1, ADR-0038: a milestone opens by freezing its requirements, storing its header and anchoring its hash; it is read
/// from the row checked against the log; and every change is an entry the domain accepted first.
/// </summary>
public sealed class MilestoneServiceTests
{
    private static CancellationToken Ct => WorkWorld.Ct;

    [Fact]
    public async Task Opening_stores_the_header_and_anchors_its_hash_in_the_log_in_that_order()
    {
        using var world = await WorkWorld.OpenAsync();

        var view = await world.OpenMilestoneAsync(budget: new WorkBudget(1_000_000, 20));

        Assert.StartsWith("mil-", view.Header.Id.Value, StringComparison.Ordinal);
        Assert.Equal(MilestoneStatus.Open, view.Status);
        Assert.Empty(view.Problems);
        Assert.Equal([PlanFixtures.Requirements().ContentHash], view.RequirementSetHistory);
        var row = await world.Registry.FindAsync(world.ProjectId, view.Header.Id, Ct);
        Assert.Equal(MilestoneHeaderHash.Of(row!), MilestoneHeaderHash.Of(view.Header));
        var log = (await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries;
        var opened = Assert.Single(log, e => e.Kind == DecisionKind.MilestoneOpened);
        var payload = Assert.IsType<MilestoneOpenedPayload>(DecisionPayloadSerializer.Deserialize(opened.Kind, opened.PayloadJson));
        Assert.Equal(MilestoneHeaderHash.Of(row!), payload.HeaderHash);
        Assert.Equal(view.Header.Id.Value, payload.RunId);
        Assert.Equal(new Actor(ActorKind.User, "manuel"), opened.Actor);
        Assert.NotNull(await world.Instance.Requirements.FindAsync(world.ProjectId, view.Header.RequirementSetHash, Ct));
    }

    [Fact]
    public async Task A_milestone_is_read_back_by_id_and_listed_in_the_order_it_was_opened()
    {
        using var world = await WorkWorld.OpenAsync();
        var first = await world.OpenMilestoneAsync("Uno");
        var second = await world.OpenMilestoneAsync("Dos");

        var listed = await world.Milestones.ListAsync(world.Project, Ct);

        Assert.Equal([first.Header.Id, second.Header.Id], listed.Select(m => m.Header.Id));
        Assert.Equal("Dos", (await world.Milestones.GetAsync(world.Project, second.Header.Id, Ct))!.Header.Name);
        Assert.Null(await world.Milestones.GetAsync(world.Project, new MilestoneId("mil-20260101-000000"), Ct));
    }

    [Fact]
    public async Task A_row_nobody_announced_is_half_open_and_opening_again_completes_it()
    {
        using var world = await WorkWorld.OpenAsync();
        var requirements = PlanFixtures.Requirements();
        await world.Instance.Requirements.SaveAsync(world.ProjectId, requirements, Ct);
        var orphan = new MilestoneHeader(new MilestoneId("mil-20261111-aaaaaa"), "Primera entrega", "Que multiplique.", requirements.ContentHash, null, world.Instance.Clock.GetUtcNow());
        await world.Registry.CreateAsync(world.ProjectId, orphan, Ct);

        Assert.Empty(await world.Milestones.ListAsync(world.Project, Ct));
        Assert.Null(await world.Milestones.GetAsync(world.Project, orphan.Id, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Milestones.LinkAsync(world.Project, orphan.Id, new MilestoneTarget(MilestoneTargetKind.Run, "x"), "manuel", Ct));

        var view = await world.OpenMilestoneAsync("Primera entrega", "Que multiplique.");

        Assert.Equal(orphan.Id, view.Header.Id);
        Assert.Single(await world.Registry.ListAsync(world.ProjectId, Ct));
        Assert.Single(await world.Milestones.ListAsync(world.Project, Ct));
    }

    [Fact]
    public async Task A_header_rewritten_behind_the_stores_back_with_its_own_hash_is_shown_with_its_problem_and_nothing_is_written_to_it()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        var forged = view.Header with { Objective = "Haz otra cosa" };
        using (var connection = world.Instance.Connect())
        {
            AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData.Exec(connection, "DROP TRIGGER milestones_no_update");
            AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData.Exec(connection, $"UPDATE milestones SET objective = 'Haz otra cosa', header_hash = '{MilestoneHeaderHash.Of(forged)}'");
        }

        var found = await world.Milestones.GetAsync(world.Project, view.Header.Id, Ct);

        Assert.NotNull(found);
        Assert.Contains(found.Problems, p => p.StartsWith(MilestoneService.HeaderNotAnchoredProblem, StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, "x"), "manuel", Ct));
    }

    [Fact]
    public async Task Revising_the_requirements_keeps_the_earlier_set_in_the_history_and_refuses_the_same_set()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        var other = FrozenRequirementSetOf("R9", "Another requirement");

        var revised = await world.Milestones.ReviseRequirementsAsync(world.Project, view.Header.Id, other, "manuel", "cambia el alcance", Ct);

        Assert.Equal(other.ContentHash, revised.CurrentRequirementSetHash);
        Assert.Equal([view.Header.RequirementSetHash, other.ContentHash], revised.RequirementSetHistory);
        Assert.NotNull(await world.Instance.Requirements.FindAsync(world.ProjectId, other.ContentHash, Ct));
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Milestones.ReviseRequirementsAsync(world.Project, view.Header.Id, other, "manuel", null, Ct));
        Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.MilestoneRequirementsRevised);
    }

    [Fact]
    public async Task A_run_is_linked_once_and_only_when_the_log_knows_it()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        var target = new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1);

        await Assert.ThrowsAsync<ArgumentException>(() => world.Milestones.LinkAsync(world.Project, view.Header.Id, target, "manuel", Ct));
        await world.MentionUnitAsync(WorkWorld.Run1);
        var linked = await world.Milestones.LinkAsync(world.Project, view.Header.Id, target, "manuel", Ct);

        Assert.Equal([target], linked.Targets);
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Milestones.LinkAsync(world.Project, view.Header.Id, target, "manuel", Ct));
        var entry = Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.MilestoneLinked);
        Assert.Equal(ActorKind.User, entry.Actor.Kind);
    }

    [Fact]
    public async Task The_system_links_a_run_as_agent_studio()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.MentionUnitAsync(WorkWorld.Run1);

        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), MilestoneService.SystemName, Ct);

        var entry = Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.MilestoneLinked);
        Assert.Equal(new Actor(ActorKind.System, MilestoneService.SystemName), entry.Actor);
    }

    [Fact]
    public async Task An_open_milestone_is_not_reopened()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();

        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Milestones.ReopenAsync(world.Project, view.Header.Id, MilestoneReopenReason.ByPerson, null, "manuel", null, Ct));
    }

    [Fact]
    public async Task A_milestone_closed_in_the_log_is_read_closed_with_its_report_and_reopening_keeps_it()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.MentionUnitAsync(WorkWorld.Run1);
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await world.RecordAsync(view.Header.Id.Value, DecisionKind.MilestoneClosed, MilestoneClosedPayload.Closing(view.Header.Id, 1, "aud-1", new EvidenceId("informe"), new string('d', 64), new string('a', 40), "manuel"));

        var closed = await world.Milestones.GetAsync(world.Project, view.Header.Id, Ct);

        Assert.Equal(MilestoneStatus.Closed, closed!.Status);
        var report = Assert.Single(closed.Reports);
        Assert.Equal((1, "aud-1", new EvidenceId("informe")), (report.Revision, report.Audit, report.Report));
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Debate, "deb-x"), "manuel", Ct));

        var reopened = await world.Milestones.ReopenAsync(world.Project, view.Header.Id, MilestoneReopenReason.ByPerson, null, "manuel", "me lo repienso", Ct);

        Assert.Equal(MilestoneStatus.Open, reopened.Status);
        Assert.Single(reopened.Reports);
        await Assert.ThrowsAsync<ArgumentException>(() => world.Milestones.ReopenAsync(world.Project, view.Header.Id, MilestoneReopenReason.Revert, new EvidenceId("no-existe"), "manuel", null, Ct));
    }

    [Fact]
    public async Task Each_report_is_matched_with_its_own_closing_entry_across_a_close_a_reopening_and_a_new_close()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.MentionUnitAsync(WorkWorld.Run1);
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        var id = view.Header.Id;
        await world.RecordAsync(id.Value, DecisionKind.MilestoneClosed, MilestoneClosedPayload.Closing(id, 1, "aud-1", new EvidenceId("informe-1"), new string('d', 64), null, "manuel"));
        await world.Milestones.ReopenAsync(world.Project, id, MilestoneReopenReason.ByPerson, null, "manuel", null, Ct);
        await world.RecordAsync(id.Value, DecisionKind.MilestoneClosed, MilestoneClosedPayload.Closing(id, 2, "aud-2", new EvidenceId("informe-2"), new string('c', 64), null, "manuel"));

        var closed = await world.Milestones.GetAsync(world.Project, id, Ct);

        Assert.Empty(closed!.Problems);
        Assert.Equal([(1, "aud-1", "informe-1"), (2, "aud-2", "informe-2")], closed.Reports.Select(r => (r.Revision, r.Audit, r.Report.Value)));
    }

    [Fact]
    public async Task A_write_waits_until_the_project_is_idle()
    {
        using var world = await WorkWorld.OpenAsync(TimeProvider.System);
        world.Activity.Busy = true;

        var opening = world.OpenMilestoneAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);

        Assert.False(opening.IsCompleted);
        Assert.DoesNotContain((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.MilestoneOpened);
        world.Activity.Busy = false;
        var view = await opening.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(MilestoneStatus.Open, view.Status);
    }

    private static AgentStudio.Domain.Requirements.FrozenRequirementSet FrozenRequirementSetOf(string id, string statement) =>
        AgentStudio.Domain.Requirements.FrozenRequirementSet.Freeze(
            [new AgentStudio.Domain.Requirements.Requirement(new RequirementId(id), statement, AgentStudio.Domain.Requirements.RequirementPriority.Mandatory, [new AgentStudio.Domain.Requirements.AcceptanceCriterion("c1", "pasa", AgentStudio.Domain.Requirements.VerificationMethod.AutomatedTest)])],
            AgentStudio.Tests.Domain.TestData.T0);
}
