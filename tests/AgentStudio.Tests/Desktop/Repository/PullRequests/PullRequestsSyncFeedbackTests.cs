using AgentStudio.Application.Hosting;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository.PullRequests;

namespace AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;

/// <summary>P85 and P90: the mailbox supplies the click's count; a normal empty list needs no warning colour.</summary>
public sealed class PullRequestsSyncFeedbackTests
{
    [Theory]
    [InlineData(0, "Hosting_Sync_Empty")]
    [InlineData(2, "Hosting_Sync_Asked")]
    public async Task TheMailboxCountDeterminesTheClickMessageEvenWhenTheProjectionIsEmpty(int followed, string key)
    {
        var world = new PrWorld();
        world.Sync.FollowedCount = followed;
        world.Sync.HealthNow = new RemoteSyncHealth(null, null, null);
        await world.Model.SetProjectAsync(PrData.Project);
        var lastSync = world.Model.LastSyncText;

        await world.Model.SyncNowCommand.ExecuteAsync(null);

        Assert.Equal(Texts.Get(key), world.Model.Message);
        Assert.Null(world.Model.Error);
        Assert.Equal(lastSync, world.Model.LastSyncText);
        Assert.Null(world.Sync.HealthNow.LastGoodSync);
        Assert.Null(world.Sync.HealthNow.LastOutcome);
    }

    [Fact]
    public async Task AnEmptyReadyListIsNeutralAndAnUnavailableRemoteIsAWarning()
    {
        var ready = new PrWorld();
        await ready.Model.SetProjectAsync(PrData.Project);
        Assert.True(ready.Model.HasGuide);
        Assert.False(ready.Model.GuideIsWarning);

        var unavailable = new PrWorld(withRemote: false);
        await unavailable.Model.SetProjectAsync(PrData.Project);
        Assert.True(unavailable.Model.HasGuide);
        Assert.True(unavailable.Model.GuideIsWarning);
    }

    [Fact]
    public async Task TheGuideToneIsNotifiedWhenTheSessionRequiresAttention()
    {
        var world = new PrWorld();
        await world.Model.SetProjectAsync(PrData.Project);
        var notified = false;
        world.Model.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(PullRequestsViewModel.GuideIsWarning);
        world.Sync.HealthNow = new RemoteSyncHealth(null, HostingOutcome.SignInNeeded, null);

        await world.Model.ReloadAsync();

        Assert.True(notified);
        Assert.True(world.Model.GuideIsWarning);
    }
}
