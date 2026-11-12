using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Integration.Phase5;

namespace AgentStudio.Tests.Integration.Phase9;

/// <summary>
/// The world of the first scenario of F9-10 (docs/16 §4.6, «Integración»): the app composed as it composes itself, and over a temporary
/// repository, with scripted CLIs, three runs of the real engine that end: the first reviewed by its judge and delivered to the user's branch,
/// the second with no review, and a follow-up of the first (also with no review). A milestone with its requirements frozen has the first two
/// linked; the follow-up belongs to it through the run it follows. It is built once and read by every test of the class.
/// </summary>
public sealed class MilestoneWorld : IAsyncLifetime
{
    internal AppWorld World { get; private set; } = null!;

    public MilestoneView Milestone { get; private set; } = null!;

    public RunResult Reviewed { get; private set; } = null!;

    public RunResult Unreviewed { get; private set; } = null!;

    public RunResult FollowUp { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        World = await AppWorld.OpenAsync(clis: new Phase5Clis { Executor = Phase9Runs.Executor });
        Reviewed = await Phase9Runs.RunAsync(World, Phase5Runs.Reviewed(), Phase5Runs.Profile());
        Assert.NotNull(Reviewed.VerdictId);
        var delivered = await World.Get<IDeliveryService>().DeliverAsync(World.App.Project, Reviewed.Id, DeliveryMode.FastForwardOnly, ct);
        Assert.True(delivered.Delivered, delivered.Message);
        Unreviewed = await Phase9Runs.RunAsync(World, Phase9Runs.Unreviewed("T2"), Phase9Runs.NoChecks);
        FollowUp = await Phase9Runs.RunAsync(World, Phase9Runs.Unreviewed("T3"), Phase9Runs.NoChecks, Reviewed.Id, "Un seguimiento de la primera ejecución.");

        var opened = await Phase9Runs.OpenMilestoneAsync(World);
        await Phase9Runs.LinkRunAsync(World, opened.Header.Id, Reviewed.Id);
        Milestone = await Phase9Runs.LinkRunAsync(World, opened.Header.Id, Unreviewed.Id);
    }

    public async ValueTask DisposeAsync()
    {
        if (World is not null)
        {
            await World.DisposeAsync();
        }
    }
}

/// <summary>
/// F9-1, F9-2, F9-4, F9-5, F9-6 and F9-9 on the same milestone with two runs and a follow-up, read from the verified log by the services the
/// app composes and by the view models of «Trabajo» and «Repositorio y cambios».
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class MilestoneWithTwoRunsTests(MilestoneWorld fixture) : IClassFixture<MilestoneWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AppWorld World => fixture.World;

    private MilestoneId Id => fixture.Milestone.Header.Id;

    private async Task<WorkDocumentViewModel> OpenDocumentAsync()
    {
        var document = World.Get<WorkDocumentViewModel>();
        await document.SetProjectAsync(World.Repo.Root);
        document.Milestones.SelectedItem = document.Milestones.Items.Single();
        await document.Board.ReloadAsync();
        return document;
    }

    [Fact]
    public async Task F9_1_the_state_of_the_milestone_is_replayed_from_the_verified_log_with_its_two_runs_and_the_follow_up_that_belongs_to_it()
    {
        var log = await World.App.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);

        // The state, replayed by a fresh read of the log: open, the two runs linked, nothing wrong with the record.
        var view = await World.Get<IMilestoneService>().GetAsync(World.App.Project, Id, Ct);
        Assert.NotNull(view);
        Assert.Equal(MilestoneStatus.Open, view.Status);
        Assert.Empty(view.Problems);
        Assert.Equal(
            new[] { fixture.Reviewed.Id.Value, fixture.Unreviewed.Id.Value }.Order(StringComparer.Ordinal),
            view.Targets.Where(t => t.Kind == MilestoneTargetKind.Run).Select(t => t.Id).Order(StringComparer.Ordinal));
        Assert.Equal(view.Header.RequirementSetHash, view.CurrentRequirementSetHash);

        // The header is anchored in the log: the hash it announced is the one of the header the store gives.
        var opened = Assert.Single(await World.App.PayloadsAsync<MilestoneOpenedPayload>(Id.Value)).Payload;
        Assert.Equal(AgentStudio.Application.Work.MilestoneHeaderHash.Of(view.Header), opened.HeaderHash);
        Assert.Equal(2, (await World.App.PayloadsAsync<MilestoneLinkedPayload>(Id.Value)).Count);

        // The follow-up is not linked, and belongs to the milestone through the run it follows.
        Assert.DoesNotContain(view.Targets, t => t.Id == fixture.FollowUp.Id.Value);
        var membership = MilestoneMembership.Of(log.Entries);
        Assert.Equal([Id.Value], membership[fixture.FollowUp.Id.Value]);
        Assert.Equal([Id.Value], membership[fixture.Reviewed.Id.Value]);
        var listed = Assert.Single(await World.Get<IMilestoneService>().ListAsync(World.App.Project, Ct));
        Assert.Equal(view.Targets, listed.Targets);
    }

    [Fact]
    public async Task F9_2_the_board_comes_from_that_log_and_no_card_reaches_done_or_integrated_without_the_approved_verdict_of_a_judge()
    {
        var board = await World.Get<IBoardProjector>().ProjectAsync(World.App.Project, Id, Ct);
        Assert.Equal((await World.App.LoadLogAsync()).Entries.Count, board.LogEntries);

        // Every card in «Hecho» or «Integrado» has a verdict the store keeps as valid and approved, of its own run.
        var verdicts = World.Get<IVerdictStore>();
        var finished = board.Requirements.Concat(board.Tasks).Where(c => c.Column is BoardColumn.Done or BoardColumn.Integrated).ToList();
        Assert.NotEmpty(finished);
        foreach (var card in finished)
        {
            var kept = await verdicts.ListAsync(World.App.Project.ProjectId, card.Run!, Ct);
            Assert.Contains(kept, v => v.Valid && v.Verdict.Decision == VerdictDecision.Approved);
        }

        // The reviewed run, with its approval, is done; the run nobody reviewed is in review, however many tasks it completed.
        Assert.All(["T1", "I1", "R1"], id => Assert.Equal(BoardColumn.Done, board.Tasks.Single(c => c.Id == id).Column));
        Assert.All(["T2", "T3"], id => Assert.Equal(BoardColumn.InReview, board.Tasks.Single(c => c.Id == id).Column));
        Assert.Equal(fixture.Unreviewed.Id.Value, board.Tasks.Single(c => c.Id == "T2").Run);
        Assert.Equal(fixture.FollowUp.Id.Value, board.Tasks.Single(c => c.Id == "T3").Run);

        // The requirement the later runs cover is not done on the strength of the first run's approval, and the one nobody covers waits.
        Assert.DoesNotContain(board.Requirements, c => c.Column is BoardColumn.Done or BoardColumn.Integrated);

        // The screen draws exactly that, and has no way to move a card.
        var document = await OpenDocumentAsync();
        var drawn = document.Board.Columns.SelectMany(c => c.Cards).Select(c => (c.Card.Kind, c.Card.Id, c.Card.Column)).Order().ToList();
        Assert.Equal(board.Requirements.Concat(board.Tasks).Select(c => (c.Kind, c.Id, c.Column)).Order().ToList(), drawn);
    }

    [Fact]
    public async Task F9_4_the_chain_is_walked_from_the_requirement_to_the_delivery_in_the_view_model_of_trabajo()
    {
        var document = await OpenDocumentAsync();
        var card = document.Board.Columns.SelectMany(c => c.Cards).First(c => c.Card.Kind == BoardCardKind.Requirement && c.Card.Id == "RQ-01");
        card.ViewChainCommand.Execute(null);
        await document.Chain.ReloadAsync();

        Assert.Same(document.Chain, document.SelectedSection);
        Assert.Equal(TraceRef.Requirement("RQ-01"), document.Context.Focus);
        var stages = document.Chain.Steps.Select(s => s.Step.Stage).ToList();
        Assert.Equal(stages.Order(), stages); // in the order of the chain
        foreach (var stage in new[] { TraceChainStage.Requirement, TraceChainStage.Milestone, TraceChainStage.Task, TraceChainStage.Attempt, TraceChainStage.Change, TraceChainStage.Verification, TraceChainStage.Verdict, TraceChainStage.Delivery })
        {
            Assert.Contains(stage, stages);
        }

        // The nodes are those of the real runs: the task of the reviewed run, its verdict and the commit that was delivered to main.
        Assert.Contains(document.Chain.Steps, s => s.Step.Node == TraceRef.Task(fixture.Reviewed.Id.Value, "T1"));
        Assert.Contains(document.Chain.Steps, s => s.Step.Node == TraceRef.Verdict(fixture.Reviewed.VerdictId!));
        Assert.Contains(document.Chain.Steps, s => s.Step.Stage == TraceChainStage.Delivery && s.Step.Node == TraceRef.Commit(fixture.Reviewed.IntegrationCommit!));

        // From the screen, the verdict opens with its decision and a saved proof opens with its text.
        document.Chain.Steps.Single(s => s.Step.Stage == TraceChainStage.Verdict).OpenCommand.Execute(null);
        await WaitAsync(() => document.Chain.HasOpenedVerdict);
        Assert.Equal(AgentStudio.Desktop.Debate.VerdictTexts.Decision(VerdictDecision.Approved), document.Chain.OpenedVerdict!.Decision);
        document.Chain.Steps.First(s => s.Step.Stage == TraceChainStage.Verification && s.CanOpen).OpenCommand.Execute(null);
        await WaitAsync(() => document.Chain.HasOpenedProof);
        Assert.False(string.IsNullOrWhiteSpace(document.Chain.OpenedProof!.Text));
    }

    [Fact]
    public async Task F9_5_the_participation_of_a_run_gives_each_role_its_own_source_and_the_role_with_no_source_says_no_consta()
    {
        var document = await OpenDocumentAsync();
        var none = Texts.Get("Work_Participation_NotRecorded");

        document.Context.LookAt(TraceRef.Run(fixture.Reviewed.Id.Value));
        await document.Participation.ReloadAsync();
        Assert.Equal(Enum.GetValues<ParticipationRole>(), document.Participation.Roles.Select(r => r.Entry.Role));
        Assert.Equal("ejecutor", Role(document, ParticipationRole.ProducingAgent).WhoText);
        Assert.Equal("juez", Role(document, ParticipationRole.Reviewer).WhoText);
        Assert.Equal(ScriptedPerson, Role(document, ParticipationRole.Approver).WhoText);
        Assert.Equal(none, Role(document, ParticipationRole.PublishingAccount).WhoText); // nobody published: no remote is involved

        // The run nobody reviewed has no reviewer, and is not given the name of another role.
        document.Context.LookAt(TraceRef.Run(fixture.Unreviewed.Id.Value));
        await document.Participation.ReloadAsync();
        Assert.Equal("ejecutor", Role(document, ParticipationRole.ProducingAgent).WhoText);
        Assert.Equal(none, Role(document, ParticipationRole.Reviewer).WhoText);
        Assert.Equal(none, Role(document, ParticipationRole.Integrator).WhoText);
        Assert.False(Role(document, ParticipationRole.Reviewer).IsRecorded);
    }

    private const string ScriptedPerson = "usuario";

    private static ParticipationRowViewModel Role(WorkDocumentViewModel document, ParticipationRole role) => document.Participation.Roles.Single(r => r.Entry.Role == role);

    [Fact]
    public async Task F9_6_the_consumption_by_milestone_adds_up_to_the_total_without_counting_an_attempt_twice()
    {
        var usage = await World.Get<IUsageReport>().QueryAsync(
            [World.Repo.Root],
            new UsageQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), TimeZoneInfo.Utc, [UsageDimension.Milestone]),
            Ct);

        // Four attempts: the three executors (1 000 tokens of input and 100 of output each) and the judge of the first run (2 000 and 200).
        Assert.Equal(4, usage.Attempts.Count);
        Assert.All(usage.Attempts, a => Assert.Equal([Id.Value], a.Milestones)); // the follow-up counts through the run it follows
        var group = Assert.Single(usage.Groups, g => g.Key.Any(k => k.Value == Id.Value));
        Assert.DoesNotContain(usage.Groups, g => g.Key.Any(k => k.Value == AgentStudio.Observability.Usage.UsageAggregator.NoMilestone));
        Assert.Equal(0, group.SharedAttempts);
        Assert.Equal(4, group.Totals.Attempts);
        Assert.Equal(5_000L, group.Totals.InputTokens.Sum);
        Assert.Equal(500L, group.Totals.OutputTokens.Sum);
        Assert.Equal(usage.Attempts.Sum(a => a.Own.InputTokens.Value!.Value), group.Totals.InputTokens.Sum);

        // The groups of the axis add up to the total.
        var total = usage.Groups.Where(g => !g.Key.Any(k => k.Value == Id.Value)).Single();
        Assert.Equal((group.Totals.Attempts, group.Totals.InputTokens.Sum), (total.Totals.Attempts, total.Totals.InputTokens.Sum));
    }

    [Fact]
    public async Task F9_9_the_commits_the_system_made_are_annotated_in_repositorio_y_cambios_with_the_run_the_task_the_attempt_and_the_agent()
    {
        var repository = World.Get<RepositoryViewModel>();
        await repository.SetProjectAsync(World.Repo.Root);
        Assert.Null(repository.RefusalText);

        var made = repository.Commits.Where(c => c.IsSystem).ToList();
        Assert.NotEmpty(made);
        var attempt = Assert.Single(made, c => c.Origin.Any(o => o.ToString()!.Contains(fixture.Reviewed.Id.Value, StringComparison.Ordinal)));
        var origin = string.Join(" ", attempt.Origin.Select(o => o.ToString()));
        Assert.Contains("T1", origin, StringComparison.Ordinal);
        Assert.Contains("ejecutor", origin, StringComparison.Ordinal);

        // The commit of the user's repository, which the log knows nothing about, is not claimed by the system.
        Assert.Contains(repository.Commits, c => !c.IsSystem && c.Subject == "base");
        Assert.DoesNotContain(repository.Commits.Where(c => c.Subject == "base"), c => c.IsSystem);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition(), "The node did not open.");
    }
}
