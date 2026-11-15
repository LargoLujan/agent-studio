using System.Text.Json;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Integration.Phase9;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase10;

/// <summary>
/// F10-8 with the app composed for real (docs/21 §9, docs/18 P75): a milestone with its requirements frozen and a real run of the engine linked to it is published to a bare folder, merged on a platform that acts on real git and
/// observed by the mailbox, and then the board of «Trabajo» and the report of the milestone are read from the verified log. The card passes to «Integrado» by the pull request (and says which one) when what was merged is the commit
/// the judge saw; it stays in «Hecho», with its reason, when the head that was merged is another one; and the report of the close says «integrado» and, only when the platform reports a deployment, «desplegado». Nothing here writes a SHA.
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class RemoteBoardJourneyLocalToolsTests
{
    private static readonly RemoteBranch Main = RemoteBranch.TryParse("main")!;
    // The run covers the mandatory requirement, which the scripted judge approves; RQ-02 (desirable) is not covered by it and stays out of the story.
    private static readonly string[] Covered = ["RQ-01"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>T1 covers the mandatory requirement and its trusted step checks it; I1 integrates it and R1 is the review: the run ends with the approval of its judge.</summary>
    private static PlanDefinition CoveringPlan() => Plan(
        Work("T1", scope: ["src/"]).Verified("check").Covering("RQ-01"),
        Node("I1", WorkTaskKind.Integration).After(On("T1")) with { VerificationSteps = ["check"] },
        Node("R1", WorkTaskKind.Review).After(On("I1")));

    /// <summary>A milestone with its requirements frozen and the run of the engine linked to it.</summary>
    private static async Task<(RunResult Run, MilestoneId Milestone)> RunInMilestoneAsync(Phase10World world)
    {
        var run = await Phase9Runs.RunAsync(world.App, CoveringPlan(), Phase5Runs.Profile());
        Assert.NotNull(run.VerdictId);
        Assert.NotNull(run.IntegrationCommit);
        var opened = await Phase9Runs.OpenMilestoneAsync(world.App);
        await Phase9Runs.LinkRunAsync(world.App, opened.Header.Id, run.Id);
        return (run, opened.Header.Id);
    }

    private static async Task<PullRequestRef> PublishAsync(Phase10World world, RunResult run)
    {
        var preview = await world.Publication.PreviewAsync(world.Project, run.Id, Main, Ct);
        Assert.True(preview.Ready, preview.NotReadyReason?.Code);
        world.Clock.Advance(TimeSpan.FromSeconds(2)); // the courtesy after the preview let go of the project
        var status = await RemoteJourneyLocalToolsTests.PublishAsync(world, new PublicationRequest(world.Project, run.Id, Main, preview.ProposedText, false));
        Assert.True(status is { State: RemoteWorkState.Done }, RemoteJourneyLocalToolsTests.Why(world, status));
        var found = await world.Platform.FindPullRequestAsync(Phase10World.Repository, RemoteBranch.ForRun(run.Id), Ct);
        return Assert.IsType<PullRequestRef>(found.Value);
    }

    /// <summary>The mailbox follows the pull request and observes it by asking the platform, which reads the folder; the log gets the observation.</summary>
    private static async Task<PullRequestView> ObserveAsync(Phase10World world, PullRequestRef pull, RunId run, Func<PullRequestView, bool> ready)
    {
        await world.Sync.FollowAsync(world.Project, pull, run, Ct);
        await world.Sync.SyncNowAsync(world.Project, Ct);
        return await RemoteJourneyLocalToolsTests.ViewAsync(world, pull, ready);
    }

    /// <summary>The person pulls what was merged into the repository (what a person does after a merge on the platform): the audit of the close verifies HEAD, which must hold the work. The log learns nothing of it.</summary>
    private static async Task TakeTheMergeAsync(Phase10World world)
    {
        world.LocalGit.Run(world.App.Repo.Root, "pull", "-q", "--ff-only", world.LocalGit.Address, "main");
        await world.App.Get<IVerificationProfileSource>().SaveAsync(world.Project, Phase5Runs.Profile(), Ct);
    }

    private static Task<MilestoneBoard> BoardAsync(Phase10World world, MilestoneId milestone) =>
        world.App.Get<IBoardProjector>().ProjectAsync(world.Project, milestone, Ct);

    private static async Task<BoardCardViewModel> CardOnScreenAsync(Phase10World world, string requirement)
    {
        var document = world.App.Get<WorkDocumentViewModel>();
        await document.SetProjectAsync(world.App.Repo.Root);
        document.Milestones.SelectedItem = document.Milestones.Items.Single();
        await document.Board.ReloadAsync();
        return document.Board.Columns.SelectMany(c => c.Cards).Single(c => c.Card.Kind == BoardCardKind.Requirement && c.Card.Id == requirement);
    }

    private static async Task<JsonDocument> CloseAsync(Phase10World world, MilestoneId milestone, int revision)
    {
        var closed = await world.App.Get<IMilestoneCloser>().CloseAsync(new CloseMilestoneRequest(world.Project, milestone, "manuel", Phase5Roster.CodexJudge, [VendorId.Anthropic]), null, Ct);
        Assert.Equal(MilestoneStatus.Closed, closed.Status);
        var report = closed.Reports.Single(r => r.Revision == revision);
        return JsonDocument.Parse(await Phase5Checks.TextAsync(world.App.App,report.Report, report.ReportSha256));
    }

    private static JsonElement RequirementOf(JsonDocument report, string id) =>
        report.RootElement.GetProperty("requirements").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

    [Fact]
    public async Task A_requirement_is_integrated_by_the_pull_request_that_merged_the_commit_the_judge_saw_and_the_report_says_deployed_only_when_the_platform_reports_a_deployment()
    {
        await using var world = await Phase10World.OpenAsync();
        var (run, milestone) = await RunInMilestoneAsync(world);

        // Before anything is published the approval of the judge leaves both requirements in «Hecho»: nothing integrated them.
        var before = await BoardAsync(world, milestone);
        Assert.All(Covered,id => Assert.Equal(BoardColumn.Done, before.Requirements.Single(c => c.Id == id).Column));

        // Published, merged on the platform with the commit that was published, and observed.
        var pull = await PublishAsync(world, run);
        world.Platform.AdvanceBase();
        var merged = await world.Platform.MergeAsync(pull, MergeMethod.Squash, run.IntegrationCommit!, Ct);
        Assert.True(merged.Value!.Merged);
        var view = await ObserveAsync(world, pull, run.Id, v => v.Snapshot?.State == PullRequestState.Merged);
        Assert.True(view.HeadIsJudgedCommit, "the head that was merged is the commit the judge saw");

        // The board: «Integrado», by that pull request, with no reason of a deviation.
        var board = await BoardAsync(world, milestone);
        Assert.All(Covered,id =>
        {
            var card = board.Requirements.Single(c => c.Id == id);
            Assert.Equal(BoardColumn.Integrated, card.Column);
            Assert.Equal(pull.TraceId, card.IntegratedByPullRequest);
            Assert.Null(card.Reason);
        });

        // The screen says which pull request integrated it.
        var onScreen = await CardOnScreenAsync(world, "RQ-01");
        Assert.Equal(BoardColumn.Integrated, onScreen.Card.Column);
        Assert.True(onScreen.HasIntegratedBy);
        Assert.Contains("#" + pull.Number, onScreen.IntegratedByText, StringComparison.Ordinal);
        Assert.False(onScreen.HasUnjudgedReason);

        // The report of the close says integrated, by that pull request, and no «desplegado»: the platform has not reported a deployment.
        await TakeTheMergeAsync(world);
        using (var first = await CloseAsync(world, milestone, 1))
        {
            Assert.All(Covered,id =>
            {
                Assert.True(RequirementOf(first, id).GetProperty("integrated").GetBoolean());
                Assert.False(RequirementOf(first, id).TryGetProperty("deployed", out _), "There is no «no desplegado».");
            });
            var listed = Assert.Single(first.RootElement.GetProperty("remote").GetProperty("pullRequests").EnumerateArray());
            Assert.Equal((pull.TraceId, "Merged", false), (listed.GetProperty("id").GetString(), listed.GetProperty("state").GetString(), listed.GetProperty("deployed").GetBoolean()));
            Assert.Equal(2, first.RootElement.GetProperty("notIncluded").GetArrayLength());
        }

        // The platform reports a deployment of what the merge left: the mailbox observes it, and the next close says «desplegado» for the requirements that pull request carried.
        await world.App.Get<IMilestoneService>().ReopenAsync(world.Project, milestone, MilestoneReopenReason.ByPerson, null, "manuel", "Para recoger el despliegue.", Ct);
        world.Platform.ReportDeployment(pull);
        await world.Sync.SyncNowAsync(world.Project, Ct);
        view = await RemoteJourneyLocalToolsTests.ViewAsync(world, pull, v => v.Snapshot?.Deployment is not null);
        Assert.Equal(DeploymentState.Succeeded, view.Snapshot!.Deployment!.State);
        using var second = await CloseAsync(world, milestone, 2);
        Assert.All(Covered,id => Assert.True(RequirementOf(second, id).GetProperty("deployed").GetBoolean()));
        Assert.True(Assert.Single(second.RootElement.GetProperty("remote").GetProperty("pullRequests").EnumerateArray()).GetProperty("deployed").GetBoolean());
        Assert.Equal(1, second.RootElement.GetProperty("notIncluded").GetArrayLength());
        Assert.True((await world.LogAsync()).Verification.IsValid);
    }

    [Fact]
    public async Task A_requirement_stays_done_with_its_reason_when_the_head_that_was_merged_is_not_the_commit_the_judge_saw_and_the_report_does_not_count_it_as_integrated()
    {
        await using var world = await Phase10World.OpenAsync();
        var (run, milestone) = await RunInMilestoneAsync(world);

        // Somebody pushes to the branch of the pull request after the judge saw the commit, and the platform merges that head.
        var pull = await PublishAsync(world, run);
        var otherHead = world.Platform.PushToHead(pull);
        Assert.NotEqual(run.IntegrationCommit, otherHead);
        var merged = await world.Platform.MergeAsync(pull, MergeMethod.Squash, otherHead, Ct);
        Assert.True(merged.Value!.Merged);
        var view = await ObserveAsync(world, pull, run.Id, v => v.Snapshot?.State == PullRequestState.Merged);
        Assert.Equal(otherHead, view.Snapshot!.HeadCommit);
        Assert.False(view.HeadIsJudgedCommit, "what was merged is not what the judge saw");

        // The board: «Hecho» with the reason, and no pull request named as the one that integrated it.
        var board = await BoardAsync(world, milestone);
        Assert.All(Covered,id =>
        {
            var card = board.Requirements.Single(c => c.Id == id);
            Assert.Equal(BoardColumn.Done, card.Column);
            Assert.Equal(BoardCardReasons.MergedWithUnjudgedChanges, card.Reason);
            Assert.Null(card.IntegratedByPullRequest);
        });
        var onScreen = await CardOnScreenAsync(world, "RQ-01");
        Assert.Equal(BoardColumn.Done, onScreen.Card.Column);
        Assert.True(onScreen.HasUnjudgedReason);
        Assert.False(onScreen.HasIntegratedBy);
        Assert.False(string.IsNullOrWhiteSpace(onScreen.ReasonText));

        // The report: not integrated, the deviation named, and no «desplegado» (the platform said nothing of a deployment).
        await TakeTheMergeAsync(world);
        using var report =await CloseAsync(world, milestone, 1);
        Assert.All(Covered,id =>
        {
            Assert.False(RequirementOf(report, id).GetProperty("integrated").GetBoolean());
            Assert.True(RequirementOf(report, id).GetProperty("mergedWithUnjudgedChanges").GetBoolean());
            Assert.False(RequirementOf(report, id).TryGetProperty("deployed", out _));
        });
        var deviations = report.RootElement.GetProperty("deviations").EnumerateArray().Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.Contains("requirement.merged-unjudged-changes", deviations);
        Assert.False(Assert.Single(report.RootElement.GetProperty("remote").GetProperty("pullRequests").EnumerateArray()).GetProperty("deployed").GetBoolean());
    }
}
