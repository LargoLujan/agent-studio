using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;

/// <summary>P89: local names are candidates only; a verified start in this project's log makes a run visible.</summary>
public sealed class PullRequestsRegisteredRunsTests
{
    private const string Existing = "20261113-aa";
    private const string New = "20261115-bb";

    [Fact]
    public async Task OnlyLocalBranchesWhoseRunsStartedInThisProjectsVerifiedLogAreOffered()
    {
        var world = new PrWorld();
        world.RunLog.Start("another-project", "foreign");
        world.RunLog.Logs[PrData.Project.ProjectId].Append(PrData.Now, new Actor(ActorKind.System, "orchestrator"), DecisionKind.RunFinished, "finished",
            DecisionPayloadSerializer.Serialize(new RunFinishedPayload(RunPhase.Succeeded, "done", null, null) { RunId = "not-started" }));
        world.Model.SetBranches(["main", "agentstudio/run-" + Existing, "agentstudio/run-" + Existing, "agentstudio/run-live1", "agentstudio/run-foreign", "agentstudio/run-not-started"]);

        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal([Existing], world.Model.PublishableRuns.Select(r => r.Run));
    }

    [Fact]
    public async Task ACompositionWithoutTheVerifiedLogKeepsTheListReadOnlyForUnpublishedRuns()
    {
        var world = new PrWorld();
        using var model = new PullRequestsViewModel(world.Providers, world.Projector, world.Sync, world.Publication, world.Gate, null, PrData.Clock());
        model.SetBranches(["agentstudio/run-" + Existing]);

        await model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.Ready, model.Status);
        Assert.Empty(model.PublishableRuns);
    }

    [Fact]
    public async Task ALogThatDoesNotVerifyCannotOfferABranchOrBeShownAsAnEmptyReadyList()
    {
        var world = new PrWorld();
        world.RunLog.Verifies = false;
        world.Model.SetBranches(["agentstudio/run-" + Existing]);

        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.Unavailable, world.Model.Status);
        Assert.Equal(Texts.Get("Hosting_Pr_Unreadable"), world.Model.GuideText);
        Assert.Empty(world.Model.PublishableRuns);
        Assert.Equal(0, world.Projector.Reads);
    }

    [Fact]
    public async Task AnUnreadableRunStartFailsClosed()
    {
        var world = new PrWorld();
        world.RunLog.Logs[PrData.Project.ProjectId].Append(PrData.Now, new Actor(ActorKind.System, "orchestrator"), DecisionKind.RunStarted, "broken", "{");
        world.Model.SetBranches(["agentstudio/run-" + Existing]);

        await world.Model.SetProjectAsync(PrData.Project);

        Assert.Equal(PullRequestsStatus.Unavailable, world.Model.Status);
        Assert.Empty(world.Model.PublishableRuns);
        Assert.Equal(0, world.Projector.Reads);
    }

    [Fact]
    public async Task ARefreshRemovesPreviouslyRegisteredRunsWhileTheNextLogReadIsPending()
    {
        var world = new PrWorld();
        world.Model.SetBranches(["agentstudio/run-" + Existing]);
        await world.Model.SetProjectAsync(PrData.Project);
        Assert.Single(world.Model.PublishableRuns);
        var answer = new TaskCompletionSource<DecisionLogState>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunLog.Read = (_, _) => answer.Task;

        var reading = world.Model.ReloadAsync();
        Assert.Empty(world.Model.PublishableRuns);
        world.Model.SetBranches(["agentstudio/run-" + Existing]);
        Assert.Empty(world.Model.PublishableRuns);
        answer.SetResult(new DecisionLogState([], DecisionLogVerification.Valid));
        await reading;

        Assert.Empty(world.Model.PublishableRuns);
        Assert.Equal(PullRequestsStatus.Ready, world.Model.Status);
    }

    [Fact]
    public async Task ALateLogReadCannotReplaceTheRunsOfANewerRead()
    {
        var world = new PrWorld();
        var old = await world.RunLog.LoadAsync(PrData.Project.ProjectId, CancellationToken.None);
        var answer = new TaskCompletionSource<DecisionLogState>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunLog.Read = (_, _) => answer.Task; // Deliberately ignores cancellation.
        world.Model.SetBranches(["agentstudio/run-" + Existing, "agentstudio/run-" + New]);
        var slow = world.Model.SetProjectAsync(PrData.Project);
        Assert.Empty(world.Model.PublishableRuns);
        world.RunLog.Read = null;
        world.RunLog.Logs.Clear();
        world.RunLog.Start(PrData.Project.ProjectId, New);
        await world.Model.ReloadAsync();

        answer.SetResult(old);
        await slow;

        Assert.Equal([New], world.Model.PublishableRuns.Select(r => r.Run));
        Assert.Equal(1, world.Projector.Reads);
    }

    [Fact]
    public async Task ALateLogReadOfThePreviousProjectCannotOfferItsRunsInTheCurrentProject()
    {
        var world = new PrWorld();
        var old = await world.RunLog.LoadAsync(PrData.Project.ProjectId, CancellationToken.None);
        var answer = new TaskCompletionSource<DecisionLogState>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunLog.Read = (_, _) => answer.Task;
        world.Model.SetBranches(["agentstudio/run-" + Existing, "agentstudio/run-" + New]);
        var slow = world.Model.SetProjectAsync(PrData.Project);
        var current = new ProjectContext("p-2", @"C:\work\other");
        world.RunLog.Start(current.ProjectId, New);
        world.RunLog.Read = null;
        await world.Model.SetProjectAsync(current);

        answer.SetResult(old);
        await slow;

        Assert.Equal([New], world.Model.PublishableRuns.Select(r => r.Run));
        Assert.Equal(1, world.Projector.Reads);
    }

    [Fact]
    public async Task DisposingTheSectionDiscardsALogAnswerThatIgnoresItsCancellation()
    {
        var world = new PrWorld();
        var log = await world.RunLog.LoadAsync(PrData.Project.ProjectId, CancellationToken.None);
        var answer = new TaskCompletionSource<DecisionLogState>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunLog.Read = (_, _) => answer.Task;
        world.Model.SetBranches(["agentstudio/run-" + Existing]);
        var reading = world.Model.SetProjectAsync(PrData.Project);

        world.Model.Dispose();
        answer.SetResult(log);
        await reading;

        Assert.Empty(world.Model.PublishableRuns);
        Assert.Equal(0, world.Projector.Reads);
    }
}
