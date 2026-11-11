using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/16 D-29, D-30 and F9-3, ADR-0039: explicit links are entries of the log, many to many and read in both directions; what is inferred
/// is a suggestion that only counts once a person confirms it.
/// </summary>
public sealed class TraceTests
{
    private static readonly string CommitA = new('a', 40);
    private static readonly string CommitB = new('b', 40);

    private static CancellationToken Ct => WorkWorld.Ct;

    private static readonly TraceRef Req = TraceRef.Requirement("R1");

    [Fact]
    public async Task A_link_is_recorded_once_read_in_both_directions_and_the_same_link_again_writes_nothing()
    {
        using var world = await WorkWorld.OpenAsync();

        var link = await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitA), Req, TraceAssertedBy.User, "cumple R1", "manuel", Ct);
        var again = await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitA), Req, TraceAssertedBy.User, "otro motivo", "manuel", Ct);

        Assert.Equal(link.Id, again.Id);
        Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.TraceLinkRecorded);
        Assert.Equal([link.Id], (await world.Trace.LinksAroundAsync(world.Project, Req, Ct)).Select(l => l.Id));
        Assert.Equal([link.Id], (await world.Trace.LinksAroundAsync(world.Project, TraceRef.Commit(CommitA), Ct)).Select(l => l.Id));
        Assert.Empty(await world.Trace.LinksAroundAsync(world.Project, TraceRef.Commit(CommitB), Ct));
    }

    [Fact]
    public async Task Links_are_many_to_many()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitA), Req, TraceAssertedBy.User, "a", "manuel", Ct);
        await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitB), Req, TraceAssertedBy.User, "b", "manuel", Ct);
        await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitA), TraceRef.Requirement("R2"), TraceAssertedBy.User, "c", "manuel", Ct);

        Assert.Equal(2, (await world.Trace.LinksAroundAsync(world.Project, Req, Ct)).Count);
        Assert.Equal(2, (await world.Trace.LinksAroundAsync(world.Project, TraceRef.Commit(CommitA), Ct)).Count);
    }

    [Fact]
    public async Task Retracting_withdraws_the_link_but_it_stays_in_the_log_and_it_can_be_recorded_again()
    {
        using var world = await WorkWorld.OpenAsync();
        var link = await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitA), Req, TraceAssertedBy.User, "cumple R1", "manuel", Ct);

        await world.Trace.RetractAsync(world.Project, link.Id, "manuel", "me equivoqué", Ct);

        Assert.Empty(await world.Trace.LinksAroundAsync(world.Project, Req, Ct));
        Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.TraceLinkRecorded);
        await Assert.ThrowsAsync<DomainRuleViolationException>(() => world.Trace.RetractAsync(world.Project, link.Id, "manuel", "otra vez", Ct));
        await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitA), Req, TraceAssertedBy.User, "mejor sí", "manuel", Ct);
        Assert.Single(await world.Trace.LinksAroundAsync(world.Project, Req, Ct));
    }

    [Fact]
    public async Task A_link_asserted_by_the_system_is_decided_by_agent_studio_and_one_by_a_person_by_that_person()
    {
        using var world = await WorkWorld.OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => world.Trace.RecordAsync(world.Project, TraceRef.Run("20261111-a1b2c3"), Req, TraceAssertedBy.System, "x", "manuel", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Trace.RecordAsync(world.Project, TraceRef.Run("20261111-a1b2c3"), Req, TraceAssertedBy.User, "x", MilestoneService.SystemName, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Trace.RecordAsync(world.Project, TraceRef.Run("20261111-a1b2c3"), TraceRef.Run("20261111-a1b2c3"), TraceAssertedBy.User, "a sí misma", "manuel", Ct));
        var link = await world.Trace.RecordAsync(world.Project, TraceRef.Run("20261111-a1b2c3"), Req, TraceAssertedBy.System, "lanzada desde el hito", MilestoneService.SystemName, Ct);

        var entry = Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.TraceLinkRecorded);
        Assert.Equal(new Actor(ActorKind.System, MilestoneService.SystemName), entry.Actor);
        Assert.Equal(TraceAssertedBy.System, link.AssertedBy);
    }

    [Fact]
    public async Task The_link_of_a_milestone_with_its_run_is_read_by_the_trace_without_being_recorded_twice()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.MentionUnitAsync(WorkWorld.Run1);
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), MilestoneService.SystemName, Ct);

        var around = await world.Trace.LinksAroundAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        var link = Assert.Single(around);
        Assert.Equal((TraceRef.Milestone(view.Header.Id.Value), TraceAssertedBy.System), (link.From, link.AssertedBy));
        Assert.DoesNotContain((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.TraceLinkRecorded);
    }

    [Fact]
    public async Task Confirming_a_suggestion_records_a_link_asserted_by_the_person_with_its_origin()
    {
        using var world = await WorkWorld.OpenAsync();
        var suggestion = new TraceSuggestion(TraceRef.Commit(CommitA), Req, TraceSuggestionSource.CommitMessageMention, "Cumple R1");

        var link = await world.Trace.ConfirmAsync(world.Project, suggestion, "manuel", Ct);

        Assert.Equal(TraceAssertedBy.User, link.AssertedBy);
        var entry = Assert.Single((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.TraceLinkRecorded);
        Assert.Contains("CommitMessageMention", entry.PayloadJson, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => world.Trace.ConfirmAsync(world.Project, suggestion, MilestoneService.SystemName, Ct));
    }

    // ---- suggestions ----

    private sealed class FakeHistory(IReadOnlyList<CommitNode> commits, IReadOnlyList<BranchSummary>? branches = null) : IRepositoryHistoryReader
    {
        public Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult(branches ?? []);

        public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken) => Task.FromResult(commits);
    }

    private static CommitNode Commit(string id, string message) =>
        new(id, [], T0, message.Split('\n')[0], "Agent Studio", [], message);

    private static TraceSuggester Suggester(WorkWorld world, params CommitNode[] commits) => new(world.Instance.Recorder, new FakeHistory(commits));

    [Fact]
    public async Task The_trailers_of_a_commit_of_the_system_suggest_its_attempt_only_when_the_log_knows_the_run()
    {
        using var world = await WorkWorld.OpenAsync();
        var message = CommitTrailers.Append("T1: Task T1 (intento 1)", new RunId(WorkWorld.Run1), new TaskId("T1"), 1, new AgentId("ejecutor"), "sonnet");
        var suggester = Suggester(world, Commit(CommitA, message));
        var attempt = TraceRef.Attempt(WorkWorld.Run1, "T1", 1);

        Assert.Empty(await suggester.SuggestAsync(world.Project, TraceRef.Commit(CommitA), Ct));
        await world.MentionUnitAsync(WorkWorld.Run1);
        var suggestions = await suggester.SuggestAsync(world.Project, TraceRef.Commit(CommitA), Ct);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal((TraceRef.Commit(CommitA), attempt, TraceSuggestionSource.CommitTrailer), (suggestion.From, suggestion.To, suggestion.Source));
        Assert.Single(await suggester.SuggestAsync(world.Project, attempt, Ct));
        Assert.Empty(await suggester.SuggestAsync(world.Project, TraceRef.Commit(CommitB), Ct));
        Assert.DoesNotContain((await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct)).Entries, e => e.Kind == DecisionKind.TraceLinkRecorded);
    }

    [Fact]
    public async Task A_message_that_mentions_a_known_run_or_milestone_suggests_a_link_and_one_that_mentions_an_unknown_one_does_not()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.MentionUnitAsync(WorkWorld.Run1);
        var suggester = Suggester(world, Commit(CommitA, $"Arregla el cálculo ({WorkWorld.Run1}, {view.Header.Id.Value}) y 20260101-ffffff y mil-20260101-zzzzzz"));

        var suggestions = await suggester.SuggestAsync(world.Project, TraceRef.Commit(CommitA), Ct);

        Assert.Equal(
            [TraceRef.Run(WorkWorld.Run1), TraceRef.Milestone(view.Header.Id.Value)],
            suggestions.Select(s => s.To));
        Assert.All(suggestions, s => Assert.Equal(TraceSuggestionSource.CommitMessageMention, s.Source));
    }

    [Fact]
    public async Task A_confirmed_suggestion_and_one_a_person_retracted_are_not_offered_again()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.MentionUnitAsync(WorkWorld.Run1);
        await world.MentionUnitAsync("20261111-bbbbbb");
        var suggester = Suggester(world, Commit(CommitA, $"Cierra {WorkWorld.Run1}"), Commit(CommitB, "Cierra 20261111-bbbbbb"));

        var first = await suggester.SuggestAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);
        var second = await suggester.SuggestAsync(world.Project, TraceRef.Run("20261111-bbbbbb"), Ct);
        Assert.Single(first);
        Assert.Single(second);

        await world.Trace.ConfirmAsync(world.Project, first[0], "manuel", Ct);
        var retracted = await world.Trace.ConfirmAsync(world.Project, second[0], "manuel", Ct);
        await world.Trace.RetractAsync(world.Project, retracted.Id, "manuel", "no es eso", Ct);

        Assert.Empty(await suggester.SuggestAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct));
        Assert.Empty(await suggester.SuggestAsync(world.Project, TraceRef.Run("20261111-bbbbbb"), Ct));
    }

    [Fact]
    public async Task The_branch_of_a_run_suggests_its_commit_and_a_commit_that_is_not_a_full_id_is_skipped()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.MentionUnitAsync(WorkWorld.Run1);
        var history = new FakeHistory(
            [Commit("abc1234", $"Cierra {WorkWorld.Run1}")],
            [new BranchSummary($"agentstudio/run-{WorkWorld.Run1}", CommitB, false, BranchKind.Run), new BranchSummary("agentstudio/run-20260101-ffffff", CommitA, false, BranchKind.Run), new BranchSummary("main", CommitA, true, BranchKind.Local)]);
        var suggester = new TraceSuggester(world.Instance.Recorder, history);

        var suggestions = await suggester.SuggestAsync(world.Project, TraceRef.Run(WorkWorld.Run1), Ct);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal((TraceRef.Commit(CommitB), TraceSuggestionSource.RunBranchName), (suggestion.To, suggestion.Source));
    }
}
