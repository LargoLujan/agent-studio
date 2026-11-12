using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/16 D-44 and F9-9, docs/04 §23.3: the annotation of the commits and the activity of «Repositorio y cambios» come from the verified log; the trailers of a
/// message annotate only an attempt the log has, and a log that does not verify is not read.
/// </summary>
public sealed class RepositoryViewTests
{
    private static readonly string CommitA = new('a', 40);
    private static readonly string CommitB = new('b', 40);
    private static readonly string CommitC = new('c', 40);

    private static CancellationToken Ct => WorkWorld.Ct;

    private sealed class FakeHistory : IRepositoryHistoryReader
    {
        public Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BranchSummary>>([]);

        public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CommitNode>>([]);
    }

    private static RepositoryAnnotator Annotator(WorkWorld world, IDecisionRecorder? recorder = null) =>
        new(recorder ?? world.Instance.Recorder, new TraceSuggester(recorder ?? world.Instance.Recorder, new FakeHistory()));

    private static CommitNode Commit(string id, string subject, string? message) => new(id, [], T0, subject, "Alguien", [], message);

    private static string Trailers(string message, string run = WorkWorld.Run1, string agent = "ejecutor", string model = "sonnet", int attempt = 1) =>
        CommitTrailers.Append(message, new RunId(run), new TaskId("T1"), attempt, new AgentId(agent), model);

    [Fact]
    public async Task A_commit_whose_trailers_name_an_attempt_of_the_log_is_annotated_with_the_agent_and_model_of_the_log()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1, agent: "ejecutor", model: "sonnet");
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.CodeDiff, "diff1", CommitA);
        var message = Trailers("T1: hace algo\n\nUn cuerpo largo.", agent: "falso", model: "inventado");

        var annotated = await Annotator(world).AnnotateAsync(world.Project, [Commit(CommitA, "T1: hace algo", message)], Ct);

        var annotation = Assert.Single(annotated).Annotation;
        Assert.NotNull(annotation);
        Assert.Equal((WorkWorld.Run1, "T1", 1, "ejecutor", "sonnet"), (annotation.Run, annotation.Task, annotation.Attempt, annotation.Agent?.Value, annotation.Model));
    }

    [Fact]
    public async Task The_trailers_in_the_last_paragraph_count_only_with_the_whole_message_and_not_with_the_subject()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.CodeDiff, "diff1", CommitA);
        var message = Trailers("T1: hace algo\n\nPárrafo uno.\n\nPárrafo dos.");

        var annotated = await Annotator(world).AnnotateAsync(world.Project, [Commit(CommitA, "T1: hace algo", message), Commit(CommitB, "T1: hace algo", null)], Ct);

        Assert.Equal(WorkWorld.Run1, annotated[0].Annotation?.Run);
        Assert.Null(annotated[1].Annotation);
    }

    [Fact]
    public async Task Trailers_the_log_does_not_back_annotate_nothing_as_a_commit_of_the_system()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);

        var annotated = await Annotator(world).AnnotateAsync(
            world.Project,
            [
                Commit(CommitA, "x", Trailers("x", attempt: 2)),
                Commit(CommitB, "x", Trailers("x", run: "20261201-ffffff")),
            ],
            Ct);

        Assert.All(annotated, a => Assert.Null(a.Annotation?.Run));
        Assert.All(annotated, a => Assert.Null(a.Annotation?.Agent));
    }

    [Fact]
    public async Task Trailers_copied_onto_another_commit_than_the_one_the_log_registered_for_the_attempt_are_not_an_annotation_of_it()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.CodeDiff, "diff1", CommitA);
        var message = Trailers("T1: hace algo");

        var annotated = await Annotator(world).AnnotateAsync(world.Project, [Commit(CommitA, "T1: hace algo", message), Commit(CommitB, "T1: hace algo (cherry-pick)", message)], Ct);

        Assert.Equal(WorkWorld.Run1, annotated[0].Annotation?.Run);
        Assert.Null(annotated[1].Annotation?.Run);
        Assert.Null(annotated[1].Annotation?.Agent);

        // At most it is offered as a suggestion for a person to confirm.
        Assert.Contains(annotated[1].Annotation!.Suggestions, s => s.To == TraceRef.Attempt(WorkWorld.Run1, "T1", 1));
    }

    [Fact]
    public async Task A_commit_with_a_link_in_force_carries_it_and_a_commit_with_nothing_has_no_annotation()
    {
        using var world = await WorkWorld.OpenAsync();
        var link = await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitA), TraceRef.Requirement("R1"), TraceAssertedBy.User, "cumple", "manuel", Ct);

        var annotated = await Annotator(world).AnnotateAsync(world.Project, [Commit(CommitA, "x", "x"), Commit(CommitB, "y", "y")], Ct);

        Assert.Equal([link.Id], annotated[0].Annotation!.Links.Select(l => l.Id));
        Assert.Null(annotated[0].Annotation!.Run);
        Assert.Null(annotated[1].Annotation);
    }

    [Fact]
    public async Task A_mention_of_a_run_the_log_has_is_a_suggestion_in_the_annotation()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();

        var annotated = await Annotator(world).AnnotateAsync(world.Project, [Commit(CommitA, "x", $"Sigue a {WorkWorld.Run1}")], Ct);

        var suggestion = Assert.Single(annotated[0].Annotation!.Suggestions);
        Assert.Equal(TraceRef.Run(WorkWorld.Run1), suggestion.To);
    }

    [Fact]
    public async Task Annotating_with_a_broken_log_fails()
    {
        using var world = await WorkWorld.OpenAsync();

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Annotator(world, new BrokenLogRecorder(world.Instance.Recorder)).AnnotateAsync(world.Project, [Commit(CommitA, "x", "x")], Ct));
    }

    private static Task Integrate(WorkWorld world, string run, string? task, string? commit, bool conflict = false, bool delivery = false) =>
        world.RecordAsync(run, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(task is null ? null : new TaskId(task), "main", [], commit, conflict ? ["a.txt"] : [], delivery) { RunId = run });

    [Fact]
    public async Task The_activity_lists_integrations_deliveries_and_cleaning_with_their_real_result_oldest_first()
    {
        using var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();
        await Integrate(world, WorkWorld.Run1, "T1", CommitA);
        await Integrate(world, WorkWorld.Run1, "T2", null, conflict: true);
        await Integrate(world, WorkWorld.Run1, "T3", null);
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.WorktreeRemoved, new WorktreeRemovedPayload("w", "p", false) { RunId = WorkWorld.Run1 });
        await Integrate(world, WorkWorld.Run1, null, CommitB, delivery: true);

        var activity = await new RepositoryActivityReader(world.Instance.Recorder).ListAsync(world.Project, null, Ct);

        Assert.Equal(
            [
                (RepositoryActivityKind.Integration, "T1", true, null),
                (RepositoryActivityKind.Integration, "T2", false, RepositoryActivityReader.ConflictCode),
                (RepositoryActivityKind.Integration, "T3", false, RepositoryActivityReader.FailedCode),
                (RepositoryActivityKind.WorktreeRemoved, null, true, null),
                (RepositoryActivityKind.Delivery, null, true, null),
            ],
            activity.Select(a => (a.Kind, a.Task, a.Succeeded, a.ErrorCode)));
        Assert.Equal(CommitB, activity[^1].Commit);
        Assert.Equal(activity.Select(a => a.Sequence).Order(), activity.Select(a => a.Sequence));
    }

    [Fact]
    public async Task The_activity_filters_by_run_task_milestone_agent_provider_model_and_time()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        const string other = "20261112-d4e5f6";
        await world.StartRunAsync();
        await world.StartRunAsync(other);
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1, agent: "ejecutor", model: "sonnet");
        await world.LaunchAsync(other, "T1", 1, agent: "ejecutor2", model: "opus");
        await Integrate(world, WorkWorld.Run1, "T1", CommitA);
        world.Instance.Clock.Advance(TimeSpan.FromHours(1));
        var cut = world.Instance.Clock.GetUtcNow();
        world.Instance.Clock.Advance(TimeSpan.FromHours(1));
        await Integrate(world, other, "T1", CommitB);
        await Integrate(world, other, null, CommitC, delivery: true);
        var reader = new RepositoryActivityReader(world.Instance.Recorder);

        async Task<List<string?>> Commits(RepositoryActivityFilter filter) => [.. (await reader.ListAsync(world.Project, filter, Ct)).Select(a => a.Commit)];

        Assert.Equal([CommitA], await Commits(new RepositoryActivityFilter(Run: WorkWorld.Run1)));
        Assert.Equal([CommitA], await Commits(new RepositoryActivityFilter(Milestone: view.Header.Id)));
        Assert.Equal([CommitA, CommitB], await Commits(new RepositoryActivityFilter(Task: "T1")));
        Assert.Equal([CommitB, CommitC], await Commits(new RepositoryActivityFilter(Agent: new AgentId("ejecutor2"))));
        Assert.Equal([CommitB, CommitC], await Commits(new RepositoryActivityFilter(Model: "opus")));
        Assert.Equal([CommitA, CommitB, CommitC], await Commits(new RepositoryActivityFilter(Provider: ProviderId.ClaudeCode)));
        Assert.Equal([CommitB, CommitC], await Commits(new RepositoryActivityFilter(From: cut)));
        Assert.Equal([CommitA], await Commits(new RepositoryActivityFilter(To: cut)));
        Assert.Empty(await Commits(new RepositoryActivityFilter(Agent: new AgentId("nadie"))));
    }

    [Fact]
    public async Task Listing_the_activity_with_a_broken_log_fails()
    {
        using var world = await WorkWorld.OpenAsync();

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => new RepositoryActivityReader(new BrokenLogRecorder(world.Instance.Recorder)).ListAsync(world.Project, null, Ct));
    }
}
