using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Backlog;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/16 D-18 and F9-2, ADR-0038: the backlog is announced before its rows, reads only the rows the log announced with the same hash,
/// and fails closed on what does not match.
/// </summary>
public sealed class BacklogServiceTests
{
    private static CancellationToken Ct => WorkWorld.Ct;

    private static BacklogDraft Draft(string statement = "Que el informe se exporte a PDF", RequirementPriority priority = RequirementPriority.Mandatory) =>
        new(statement, priority, [new AcceptanceCriterion("C1", "Se abre sin avisos", VerificationMethod.HumanReview)]);

    [Fact]
    public async Task Adding_announces_the_version_first_and_stamps_the_row_with_the_time_of_the_announcement()
    {
        using var world = await WorkWorld.OpenAsync();
        world.Instance.Clock.Advance(TimeSpan.FromMinutes(3));

        var item = await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);

        Assert.Equal(1, item.Version);
        var log = (await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries;
        var entry = Assert.Single(log, e => e.Kind == DecisionKind.BacklogItemRecorded);
        var payload = Assert.IsType<BacklogItemRecordedPayload>(DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson));
        Assert.Equal(item.Sha256(), payload.Sha256);
        Assert.Equal(DecisionPayload.NoRun, payload.RunId);
        Assert.Equal(entry.RecordedAt, item.CreatedAt);
        Assert.Equal(item.Sha256(), (await world.BacklogStore.FindAsync(world.ProjectId, item.Id, 1, Ct))!.Sha256());
        Assert.DoesNotContain("PDF", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revising_writes_a_new_version_with_all_its_content_and_the_history_keeps_every_one()
    {
        using var world = await WorkWorld.OpenAsync();
        var item = await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);

        var second = await world.Backlog.ReviseAsync(world.Project, item.Id, Draft("Que el informe se exporte a PDF o a HTML", RequirementPriority.Desirable), "manuel", Ct);

        Assert.Equal(2, second.Version);
        var listed = Assert.Single(await world.Backlog.ListAsync(world.Project, Ct));
        Assert.Equal((second.Statement, 2), (listed.Item.Statement, listed.Versions));
        Assert.Equal([1, 2], (await world.Backlog.HistoryAsync(world.Project, item.Id, Ct)).Select(i => i.Version));
        Assert.Equal(RequirementPriority.Mandatory, (await world.Backlog.HistoryAsync(world.Project, item.Id, Ct))[0].Priority);
    }

    [Fact]
    public async Task Retiring_takes_the_item_out_of_the_list_but_not_out_of_the_history_and_it_takes_no_new_version()
    {
        using var world = await WorkWorld.OpenAsync();
        var item = await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);
        var other = await world.Backlog.AddAsync(world.Project, Draft("Otra cosa"), "manuel", Ct);

        await world.Backlog.RetireAsync(world.Project, item.Id, "manuel", "ya no hace falta", Ct);

        Assert.Equal([other.Id], (await world.Backlog.ListAsync(world.Project, Ct)).Select(e => e.Item.Id));
        Assert.Single(await world.Backlog.HistoryAsync(world.Project, item.Id, Ct));
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Backlog.ReviseAsync(world.Project, item.Id, Draft("Cambio"), "manuel", Ct));
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Backlog.RetireAsync(world.Project, item.Id, "manuel", null, Ct));
    }

    [Fact]
    public async Task The_order_is_a_decision_of_the_person_and_the_items_it_does_not_list_follow_in_the_order_they_were_added()
    {
        using var world = await WorkWorld.OpenAsync();
        var a = await world.Backlog.AddAsync(world.Project, Draft("A"), "manuel", Ct);
        var b = await world.Backlog.AddAsync(world.Project, Draft("B"), "manuel", Ct);
        var c = await world.Backlog.AddAsync(world.Project, Draft("C"), "manuel", Ct);

        await world.Backlog.ReorderAsync(world.Project, [c.Id], "manuel", Ct);

        Assert.Equal([c.Id, a.Id, b.Id], (await world.Backlog.ListAsync(world.Project, Ct)).Select(e => e.Item.Id));
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Backlog.ReorderAsync(world.Project, [a.Id, a.Id], "manuel", Ct));
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Backlog.ReorderAsync(world.Project, [new BacklogItemId("bk-20260101-000000")], "manuel", Ct));
    }

    [Fact]
    public async Task An_invalid_draft_is_refused_before_anything_is_written()
    {
        using var world = await WorkWorld.OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => world.Backlog.AddAsync(world.Project, Draft("   "), "manuel", Ct));

        Assert.Empty((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries);
    }

    [Fact]
    public async Task An_announcement_without_its_row_is_not_an_item_and_writing_the_same_version_again_completes_it()
    {
        using var world = await WorkWorld.OpenAsync();
        var first = await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);
        var second = first with { Version = 2, Statement = "Que el informe se exporte a PDF o a HTML" };

        // The app died between the announcement and the row.
        await world.Instance.Recorder.RecordAsync(world.ProjectId, new Actor(ActorKind.User, "manuel"), DecisionKind.BacklogItemRecorded, "anuncio", BacklogItemRecordedPayload.Announcing(second), null, Ct);

        Assert.Equal(1, Assert.Single(await world.Backlog.ListAsync(world.Project, Ct)).Versions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Backlog.ReviseAsync(world.Project, first.Id, Draft("Otro contenido"), "manuel", Ct));
        var completed = await world.Backlog.ReviseAsync(world.Project, first.Id, new BacklogDraft(second.Statement, second.Priority, second.Criteria), "manuel", Ct);

        Assert.Equal(2, completed.Version);
        Assert.Equal(2, Assert.Single(await world.Backlog.ListAsync(world.Project, Ct)).Versions);
        Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.BacklogItemRecorded && e.PayloadJson.Contains("\"version\":2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adding_the_same_thing_again_after_an_announcement_lost_its_row_reuses_the_announced_id()
    {
        using var world = await WorkWorld.OpenAsync();
        var lost = new BacklogItem(new BacklogItemId("bk-20261111-abcdef"), 1, "Que el informe se exporte a PDF", RequirementPriority.Mandatory, Draft().Criteria, world.Instance.Clock.GetUtcNow());
        await world.Instance.Recorder.RecordAsync(world.ProjectId, new Actor(ActorKind.User, "manuel"), DecisionKind.BacklogItemRecorded, "anuncio", BacklogItemRecordedPayload.Announcing(lost), null, Ct);
        Assert.Empty(await world.Backlog.ListAsync(world.Project, Ct));

        var item = await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);

        Assert.Equal(lost.Id, item.Id);
        Assert.Single(await world.Backlog.ListAsync(world.Project, Ct));
        Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.BacklogItemRecorded);

        // Other content is another item, with its own id.
        var other = await world.Backlog.AddAsync(world.Project, Draft("Otra cosa"), "manuel", Ct);
        Assert.NotEqual(lost.Id, other.Id);
    }

    [Fact]
    public async Task A_row_nobody_announced_is_not_read()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);
        await world.BacklogStore.AppendAsync(world.ProjectId, new BacklogItem(new BacklogItemId("bk-20261111-ffffff"), 1, "Colado", RequirementPriority.Mandatory, [], world.Instance.Clock.GetUtcNow()), Ct);

        var items = await world.Backlog.ListAsync(world.Project, Ct);

        Assert.Equal("Que el informe se exporte a PDF", Assert.Single(items).Item.Statement);
    }

    [Fact]
    public async Task A_row_rewritten_with_its_own_hash_does_not_match_the_announcement_and_the_backlog_is_not_read()
    {
        using var world = await WorkWorld.OpenAsync();
        var item = await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);
        var forged = item with { Statement = "Haz otra cosa" };
        using (var connection = world.Instance.Connect())
        {
            Exec(connection, "DROP TRIGGER backlog_items_no_update");
            Exec(connection, $"UPDATE backlog_items SET statement = 'Haz otra cosa', content_sha256 = '{forged.Sha256()}'");
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Backlog.ListAsync(world.Project, Ct));

        Assert.Contains(item.Id.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_changed_without_its_hash_is_invalid_data()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.Backlog.AddAsync(world.Project, Draft(), "manuel", Ct);
        using (var connection = world.Instance.Connect())
        {
            Exec(connection, "DROP TRIGGER backlog_items_no_update");
            Exec(connection, "UPDATE backlog_items SET statement = 'Haz otra cosa'");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => world.Backlog.ListAsync(world.Project, Ct));
    }

    [Fact]
    public async Task The_board_shows_the_items_in_force_as_cards_of_the_backlog_column()
    {
        using var world = await WorkWorld.OpenAsync();
        var a = await world.Backlog.AddAsync(world.Project, Draft("A"), "manuel", Ct);
        var b = await world.Backlog.AddAsync(world.Project, Draft("B"), "manuel", Ct);
        await world.Backlog.RetireAsync(world.Project, a.Id, "manuel", null, Ct);

        var cards = await world.Board.BacklogAsync(world.Project, Ct);

        var card = Assert.Single(cards);
        Assert.Equal((BoardCardKind.Requirement, b.Id.Value, "B", AgentStudio.Domain.Work.Milestones.BoardColumn.Backlog), (card.Kind, card.Id, card.Title, card.Column));
    }
}
