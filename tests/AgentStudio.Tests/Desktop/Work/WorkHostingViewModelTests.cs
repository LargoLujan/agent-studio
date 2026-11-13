using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>A board the test fixes: the cards the projection would give for a milestone.</summary>
internal sealed class FixedBoard(IReadOnlyList<BoardCard> requirements) : IBoardProjector
{
    public Task<MilestoneBoard> ProjectAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken) =>
        Task.FromResult(new MilestoneBoard(milestone, 1, new string('0', 64), requirements, []));

    public Task<IReadOnlyList<BoardCard>> BacklogAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BoardCard>>([]);
}

/// <summary>
/// F10-8 on the screens of «Trabajo» (docs/21 sections 7.4 and 9): «integrado» also by pull request and which one, the reason «fusionado con cambios que el juez no vio», the chain
/// with the pull request, the merge, the deployment and the revert, and «desplegado» in the milestone only when there is data. The projections are the ones of WP4 and have their own
/// tests: here a port gives the data and the screen says what it is given.
/// </summary>
public sealed class WorkHostingViewModelTests
{
    private static readonly string Pr12 = PrData.Pr(12).TraceId;

    private static CancellationToken Ct => WorkTestWorld.Ct;

    private static async Task<WorkTestWorld> WithBoardAsync(IReadOnlyList<BoardCard> cards, IPullRequestProjector? projector = null)
    {
        var test = await WorkTestWorld.OpenAsync((_, backend) => backend with { Board = new FixedBoard(cards), PullRequests = projector });
        await test.World.OpenMilestoneAsync();
        await test.Document.ReloadAsync();
        test.Document.Milestones.SelectedItem = test.Document.Milestones.Items.Single();
        await test.Document.Milestones.IntegrationRead;
        await test.Document.Board.ReloadAsync();
        return test;
    }

    private static BoardCard Card(string id, BoardColumn column, string? reason = null, string? carrier = null) =>
        new(BoardCardKind.Requirement, id, "Title " + id, column, "20261112-a1b2c3", reason, carrier);

    private static BoardCardViewModel CardOf(WorkTestWorld test, string id) =>
        test.Document.Board.Columns.SelectMany(c => c.Cards).Single(c => c.Card.Id == id);

    // ---- Board ----

    [Fact]
    public async Task A_requirement_integrated_by_a_pull_request_says_which_one_and_one_integrated_by_the_delivery_says_nothing_of_it()
    {
        using var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated, null, Pr12), Card("R2", BoardColumn.Integrated)]);

        Assert.Equal(Texts.Format("Hosting_Board_IntegratedBy", 12, "acme/calc"), CardOf(test, "R1").IntegratedByText);
        Assert.True(CardOf(test, "R1").HasIntegratedBy);
        Assert.Null(CardOf(test, "R2").IntegratedByText);
        Assert.False(CardOf(test, "R2").HasIntegratedBy);
    }

    [Fact]
    public async Task A_card_in_done_with_the_reason_merged_with_changes_the_judge_did_not_see_says_it_and_has_its_info()
    {
        using var test = await WithBoardAsync([Card("R1", BoardColumn.Done, BoardCardReasons.MergedWithUnjudgedChanges), Card("R2", BoardColumn.InReview, BoardCardReasons.Challenged)]);

        var card = CardOf(test, "R1");
        Assert.Equal(Texts.Get("Text_board_merged_unjudged_changes"), card.ReasonText);
        Assert.True(card.HasUnjudgedReason);
        Assert.Null(card.IntegratedByText);

        // The reasons of Phase 9 are said as they were, and carry no such info.
        Assert.Equal(Texts.Get("Work_Reason_Challenged"), CardOf(test, "R2").ReasonText);
        Assert.False(CardOf(test, "R2").HasUnjudgedReason);
    }

    // ---- Chain ----

    [Fact]
    public async Task The_chain_says_the_pull_request_the_merge_with_its_mode_the_deployment_and_the_revert_marked_as_deduced()
    {
        var chain = new FixedChain
        {
            Steps =
            [
                new(TraceChainStage.Delivery, TraceRef.Commit(PrData.Sha('1')), null, false),
                new(TraceChainStage.Publication, TraceRef.PullRequest(PrData.Pr(12)), null, false),
                new(TraceChainStage.RemoteMerge, TraceRef.Commit(PrData.Sha('2')), RemoteTraceTitles.MergeMode(RemoteMergeMode.Squash), false),
                new(TraceChainStage.Deployment, TraceRef.Commit(PrData.Sha('2')), RemoteTraceTitles.Deployed, false),
                new(TraceChainStage.RemoteMerge, TraceRef.Commit(PrData.Sha('3')), RemoteTraceTitles.RevertedBy, true),
                new(TraceChainStage.RemoteMerge, TraceRef.Commit(PrData.Sha('4')), RemoteTraceTitles.MergeMode(RemoteMergeMode.SquashOrRebase), true),
            ],
        };
        using var test = await WorkTestWorld.OpenAsync((_, backend) => backend with { Chain = chain });
        test.Document.Context.LookAt(TraceRef.Requirement("R1"));
        await test.Document.Chain.ReloadAsync();
        var steps = test.Document.Chain.Steps;

        Assert.Equal(Texts.Format("Hosting_Chain_PullRequest", 12), steps[1].Title);
        Assert.Equal(Texts.Get("Work_Stage_Publication"), steps[1].StageText);
        Assert.Equal(Texts.Get("Text_remote_merge_squash"), steps[2].Title);
        Assert.Null(steps[2].SuggestedText);
        Assert.Equal(Texts.Get("Text_remote_deployed"), steps[3].Title);
        Assert.Equal(Texts.Get("Text_remote_reverted_by"), steps[4].Title);
        Assert.Equal(Texts.Get("Hosting_Chain_Deduced"), steps[4].SuggestedText);
        Assert.Equal(Texts.Get("Text_remote_merge_squash_or_rebase"), steps[5].Title);
        Assert.Equal(Texts.Get("Hosting_Chain_Deduced"), steps[5].SuggestedText);
        Assert.Equal(Texts.Get("Work_Stage_Deployment"), steps[3].StageText);
    }

    [Fact]
    public async Task A_step_of_phase_9_keeps_its_own_words()
    {
        var chain = new FixedChain { Steps = [new(TraceChainStage.Task, TraceRef.Task("20261112-a1b2c3", "T1"), "Write the multiply", true)] };
        using var test = await WorkTestWorld.OpenAsync((_, backend) => backend with { Chain = chain });
        test.Document.Context.LookAt(TraceRef.Requirement("R1"));
        await test.Document.Chain.ReloadAsync();

        Assert.Equal("Write the multiply", test.Document.Chain.Steps[0].Title);
        Assert.Equal(Texts.Get("Work_Chain_Suggested"), test.Document.Chain.Steps[0].SuggestedText);
    }

    // ---- Milestone ----

    private static PullRequestView Viewing(RemoteSnapshot snapshot) => PrData.View(snapshot);

    [Fact]
    public async Task The_milestone_says_how_many_requirements_are_integrated_and_how_many_by_pull_request()
    {
        using var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated, null, Pr12), Card("R2", BoardColumn.Integrated), Card("R3", BoardColumn.Done)]);
        var milestones = test.Document.Milestones;

        Assert.Equal(Texts.Format("Hosting_Milestone_IntegratedBoth", 2, 3, 1), milestones.IntegrationText);
        Assert.True(milestones.HasIntegration);
        Assert.False(milestones.HasDeployment);
        Assert.Equal(string.Empty, milestones.DeploymentText);
    }

    [Fact]
    public async Task Integrated_only_by_the_delivery_does_not_mention_pull_requests()
    {
        using var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated), Card("R2", BoardColumn.Planned)]);

        Assert.Equal(Texts.Format("Hosting_Milestone_Integrated", 1, 2), test.Document.Milestones.IntegrationText);
    }

    [Fact]
    public async Task Deployed_appears_only_when_the_platform_reported_a_deployment_of_the_pull_request_that_integrated()
    {
        var deployed = PrData.Snapshot(12, PullRequestState.Merged, results: [PrData.Sha('f')], deployment: new RemoteDeployment(7, PrData.Sha('f'), DeploymentState.Succeeded));
        var projector = new FakeProjector();
        projector.Views.Add(Viewing(deployed));
        using (var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated, null, Pr12), Card("R2", BoardColumn.Integrated, null, PrData.Pr(99).TraceId)], projector))
        {
            Assert.True(test.Document.Milestones.HasDeployment);
            Assert.Equal(Texts.Format("Hosting_Milestone_Deployed", 1, 1), test.Document.Milestones.DeploymentText);
        }

        // The pull request is merged and integrated, but the platform reported no deployment: there is no «not deployed» to say.
        var none = new FakeProjector();
        none.Views.Add(Viewing(PrData.Snapshot(12, PullRequestState.Merged, results: [PrData.Sha('f')])));
        using (var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated, null, Pr12)], none))
        {
            Assert.False(test.Document.Milestones.HasDeployment);
            Assert.Equal(string.Empty, test.Document.Milestones.DeploymentText);
            Assert.True(test.Document.Milestones.HasIntegration);
        }

        // Without the port of the pull requests the state does not exist at all.
        using (var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated, null, Pr12)]))
        {
            Assert.False(test.Document.Milestones.HasDeployment);
        }
    }

    [Fact]
    public async Task A_deployment_that_failed_is_not_said_as_deployed_and_is_counted_apart()
    {
        var failed = PrData.Snapshot(12, PullRequestState.Merged, results: [PrData.Sha('f')], deployment: new RemoteDeployment(7, PrData.Sha('f'), DeploymentState.Failed));
        var projector = new FakeProjector();
        projector.Views.Add(Viewing(failed));
        using var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated, null, Pr12)], projector);

        Assert.Equal(Texts.Format("Hosting_Milestone_Deployed", 0, 1), test.Document.Milestones.DeploymentText);
    }

    [Fact]
    public async Task A_projection_that_fails_leaves_the_lines_out_and_never_a_default()
    {
        var projector = new FakeProjector { Failure = new InvalidOperationException("log") };
        using var test = await WithBoardAsync([Card("R1", BoardColumn.Integrated, null, Pr12)], projector);

        Assert.False(test.Document.Milestones.HasDeployment);
        Assert.Equal(Texts.Format("Hosting_Milestone_IntegratedBoth", 1, 1, 1), test.Document.Milestones.IntegrationText);
        _ = Ct;
    }

    [Fact]
    public async Task Without_a_milestone_selected_there_is_nothing_to_say_of_integration()
    {
        using var test = await WorkTestWorld.OpenAsync((_, backend) => backend with { Board = new FixedBoard([Card("R1", BoardColumn.Integrated)]) });

        Assert.False(test.Document.Milestones.HasIntegration);
        Assert.False(test.Document.Milestones.HasDeployment);
    }
}
