using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Tests.Orchestration.WorkTracking;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>An inference of the system that stops being offered once the link it proposes is in force, as the real suggester does.</summary>
internal sealed class OneSuggestion(ITraceLinkService links, TraceSuggestion suggestion) : ITraceSuggester
{
    public async Task<IReadOnlyList<TraceSuggestion>> SuggestAsync(ProjectContext project, TraceRef node, CancellationToken cancellationToken)
    {
        var around = await links.LinksAroundAsync(project, node, cancellationToken);
        return around.Any(l => l.From == suggestion.From && l.To == suggestion.To) ? [] : [suggestion];
    }
}

/// <summary>docs/16 D-29, D-30 and D-18 on the screen: links are confirmed from a suggestion and retired with a reason; the backlog takes a version for every change and never deletes.</summary>
public sealed class WorkLinksAndBacklogViewModelTests
{
    private static readonly string Commit = new('a', 40);

    private static CancellationToken Ct => WorkTestWorld.Ct;

    private static TraceSuggestion Suggestion => new(TraceRef.Commit(Commit), TraceRef.Requirement("R1"), TraceSuggestionSource.CommitMessageMention, "Refs R1");

    [Fact]
    public async Task A_suggestion_counts_only_when_the_person_confirms_it_and_a_link_is_retired_with_a_reason_and_stays_in_the_log()
    {
        using var test = await WorkTestWorld.OpenAsync((world, backend) => backend with { Suggestions = new OneSuggestion(world.Trace, Suggestion) });
        test.Document.Context.LookAt(TraceRef.Requirement("R1"));
        await test.Document.Links.ReloadAsync();
        var links = test.Document.Links;

        // Offered, and nothing is a link yet.
        Assert.Single(links.Suggestions);
        Assert.Empty(links.Links);
        Assert.Empty(await test.World.Trace.LinksAroundAsync(test.World.Project, TraceRef.Requirement("R1"), Ct));

        await links.Suggestions[0].ConfirmCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();

        var link = Assert.Single(links.Links);
        Assert.Empty(links.Suggestions);
        Assert.Equal(TraceAssertedBy.User, link.Link.AssertedBy);
        Assert.Contains("Refs R1", link.Reason, StringComparison.Ordinal);

        // A link is not retired without saying why.
        await link.RetractCommand.ExecuteAsync(null);
        Assert.Equal(Texts.Get("Work_Link_NeedsReason"), links.Error);
        Assert.Single(await test.World.Trace.LinksAroundAsync(test.World.Project, TraceRef.Requirement("R1"), Ct));

        links.RetractReason = "Era otra versión";
        await link.RetractCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();
        Assert.Empty(links.Links);
        Assert.Empty(await test.World.Trace.LinksAroundAsync(test.World.Project, TraceRef.Requirement("R1"), Ct));
    }

    [Fact]
    public async Task The_backlog_takes_a_new_version_for_each_change_and_a_retired_item_leaves_the_list_but_not_its_history()
    {
        using var test = await WorkTestWorld.OpenAsync();
        var backlog = test.Document.Backlog;
        backlog.Statement = "Calc multiplica";
        backlog.AddCriterionCommand.Execute(null);
        backlog.Criteria[0].Description = "Multiply(2,3) da 6";
        await backlog.SaveCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();

        var row = Assert.Single(backlog.Items);
        Assert.Equal((1, "Calc multiplica", Texts.Get("Work_Priority_Mandatory")), (row.Entry.Versions, row.Statement, row.PriorityText));
        Assert.Equal(Texts.Format("Work_Backlog_Version", 1), row.VersionText);

        backlog.SelectedItem = row;
        Assert.True(backlog.IsEditing);
        Assert.Equal(Texts.Get("Work_Backlog_SaveVersion"), backlog.SaveLabel);
        backlog.SelectedPriority = BacklogSection.Priorities.Single(p => p.Value == RequirementPriority.Desirable);
        backlog.Statement = "Calc multiplica y divide";
        await backlog.SaveCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();

        var changed = Assert.Single(backlog.Items);
        Assert.Equal((2, "Calc multiplica y divide"), (changed.Entry.Versions, changed.Statement));
        var history = await test.World.Backlog.HistoryAsync(test.World.Project, changed.Id, Ct);
        Assert.Equal([1, 2], history.Select(h => h.Version));
        Assert.Equal("Calc multiplica", history[0].Statement);

        backlog.SelectedItem = changed;
        await backlog.RetireCommand.ExecuteAsync(null);
        await test.Document.ReloadAsync();
        Assert.Empty(backlog.Items);
        Assert.Equal(2, (await test.World.Backlog.HistoryAsync(test.World.Project, changed.Id, Ct)).Count);
    }
}
