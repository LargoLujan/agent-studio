using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Domain.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Hosting;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>Publishing in stretches (docs/21 D-18, F10-4, F10-5 and F10-6): git and the log with the project taken and never the platform's CLI; nothing duplicated after a cut.</summary>
public sealed class PublicationServiceTests
{
    private static readonly RemoteBranch Main = HostingP0Data.Main;

    private static PublicationRequest Request() =>
        new(PublicationRig.Project, HostingP0Data.Run, Main, new PullRequestText("Agent Studio · `x`", "body"), false);

    private static async Task<RemoteWorkStatus> StatusAsync(PublicationRig rig, RemoteWorkTicket ticket) =>
        (await rig.BoxAsync()).StatusOf(ticket.Id)!;

    [Fact]
    public async Task PublishingRunsTheStretchOfGitInOneTakeAndTheCliOutsideIt()
    {
        await using var rig = new PublicationRig();

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.Done, (await StatusAsync(rig, ticket)).State);
        var events = rig.Trace.Events;
        var inspect = events.LastIndexOf("git:inspect");
        var take = events.LastIndexOf("take", inspect);
        var release = events.IndexOf("release", inspect);
        var between = events.Skip(take).Take(release - take + 1).ToList();
        Assert.Equal(["take", "git:identify", "git:inspect", "git:push", "git:read", "record:HostingOperationPerformed", "release"], between);

        // The platform runs between: after the stretch of git, never inside a take (the provider double fails when the project is taken).
        Assert.True(events.IndexOf("gh:find") > release);
        Assert.True(events.IndexOf("gh:open") > events.IndexOf("gh:find"));
    }

    [Fact]
    public async Task EveryOperationLeavesItsEntryWithWhatAllowedIt()
    {
        await using var rig = new PublicationRig();

        await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        var operations = RemoteLogReading.ReadEntries(rig.Log.Entries).Operations;
        Assert.Equal([HostingOperationKind.PushRunBranch, HostingOperationKind.OpenPullRequest], operations.Select(o => o.Payload.Operation));
        Assert.All(operations, o =>
        {
            Assert.Equal(HostingOutcome.Ok, o.Payload.Outcome);
            Assert.Null(o.Payload.GrantId);
            Assert.NotNull(o.Payload.ApprovalSequence);
            Assert.Equal(DecisionPayload.NoRun, o.Payload.RunId);
        });
        Assert.NotNull(operations[1].Payload.TextEvidence);
        Assert.Equal(2, rig.Approvals.Requests.Count);
        Assert.Equal([HostingActions.PublishAction, HostingActions.PullRequestWriteAction], rig.Approvals.Requests.Select(r => r.Action));
    }

    [Fact]
    public async Task WithTheProjectBusyNothingIsPublishedAndNothingIsLaunchedOrAsked()
    {
        await using var rig = new PublicationRig();
        using var run = rig.Activity.TryEnter("p1", ProjectActivityKind.Run);

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);

        var status = await StatusAsync(rig, ticket);
        Assert.Equal(RemoteWorkState.Failed, status.State);
        Assert.Equal(PublicationCodes.ProjectBusy, status.Message!.Code);
        Assert.Equal(0, rig.Trace.Count("git:"));
        Assert.Equal(0, rig.Trace.Count("gh:"));
        Assert.Empty(rig.Log.Entries);
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task ThePreviewAndTheInspectionSayBusyWithoutLaunchingAnything()
    {
        await using var rig = new PublicationRig();
        using var audit = rig.Activity.TryEnter("p1", ProjectActivityKind.Audit);

        var preview = await rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, null, CancellationToken.None);
        var inspection = await rig.Service.InspectAsync(PublicationRig.Project, CancellationToken.None);

        Assert.False(preview.Ready);
        Assert.Equal(PublicationCodes.ProjectBusy, preview.NotReadyReason!.Code);
        Assert.Null(inspection);
        Assert.Equal(0, rig.Trace.Count("git:"));
        Assert.Equal(0, rig.Trace.Count("gh:"));
    }

    [Fact]
    public async Task ThePreviewReadsGitInOneTakeAndThePlatformOutsideIt()
    {
        await using var rig = new PublicationRig();

        var preview = await rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, null, CancellationToken.None);

        Assert.True(preview.Ready);
        Assert.Equal(HostingP0Data.Repository, preview.Repository);
        Assert.Equal(HostingP0Data.Head, preview.Commit);
        Assert.Equal(Main, preview.BaseBranch);
        Assert.True(preview.Rules!.Readable);
        Assert.Equal(1, rig.Activity.Takes);
    }

    [Fact]
    public async Task AfterACutTheRetryAsksWhatExistsBeforeItRepeatsAndOpensNothingTwice()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Found = new HostingResult<PullRequestRef?>(HostingOutcome.Ok, HostingP0Data.PullRequest);

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.Done, (await StatusAsync(rig, ticket)).State);
        Assert.Equal(0, rig.Trace.Count("gh:open"));
        Assert.Equal(1, rig.Trace.Count("gh:update"));
    }

    [Fact]
    public async Task WithoutConnectionTheRequestWaitsAndEveryRetryPassesTheGateAgain()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Found = new HostingResult<PullRequestRef?>(HostingOutcome.Unreachable);

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        var status = await StatusAsync(rig, ticket);
        Assert.Equal(RemoteWorkState.PendingSync, status.State);
        Assert.Equal(PendingReason.Offline, status.Reason);
        Assert.NotNull(status.RetryAt);
        Assert.Contains(rig.Sync.Pending("p1"), p => p.Ticket.Id == ticket.Id);
        Assert.Equal(1, rig.Trace.Count("git:push"));

        // The verdict is challenged meanwhile: the retry passes the whole gate again and stops.
        rig.Delivery.Ready = false;
        await rig.AdvanceAsync(TimeSpan.FromMinutes(1));

        var after = await StatusAsync(rig, ticket);
        Assert.Equal(RemoteWorkState.Failed, after.State);
        Assert.Equal(PublicationCodes.VerdictNotApproved, after.Message!.Code);
        Assert.Equal(0, rig.Trace.Count("gh:open"));
        Assert.Equal(1, rig.Trace.Count("git:push"));
    }

    [Fact]
    public async Task ARetryAfterTheCutGoesOnFromTheStageAndDoesNotPushAgain()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Found = new HostingResult<PullRequestRef?>(HostingOutcome.Unreachable);
        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        rig.Provider.Found = new HostingResult<PullRequestRef?>(HostingOutcome.Ok, null);

        await rig.AdvanceAsync(TimeSpan.FromMinutes(1));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.Done, (await StatusAsync(rig, ticket)).State);
        Assert.Equal(1, rig.Trace.Count("git:push"));
        Assert.Equal(1, rig.Trace.Count("gh:open"));
        Assert.Equal(1, rig.Log.Count(DecisionKind.HostingOperationPerformed) - 1);
    }

    [Fact]
    public async Task ANoteThatFindsTheProjectBusyIsParkedWhileThePlatformPartGoesOn()
    {
        await using var rig = new PublicationRig();

        // The courtesy second after the stretch of git leaves the project closed for the notes: they park and the flow does not wait.
        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.Done, (await StatusAsync(rig, ticket)).State);
        Assert.Equal(1, rig.Log.Count(DecisionKind.HostingOperationPerformed));
        Assert.True((await rig.BoxAsync()).ParkedCount >= 1);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, rig.Log.Count(DecisionKind.HostingOperationPerformed));
        Assert.Equal(0, (await rig.BoxAsync()).ParkedCount);
    }

    [Fact]
    public async Task APushThatTheRemoteDoesNotConfirmIsNotTakenAsDone()
    {
        await using var rig = new PublicationRig();
        rig.Git.PushOutcome = RemotePushOutcome.NotConfirmed;
        rig.Git.RemoteHead = "9999999999999999999999999999999999999999";

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.PendingSync, (await StatusAsync(rig, ticket)).State);
        Assert.Equal(0, rig.Trace.Count("gh:open"));
        var recorded = RemoteLogReading.ReadEntries(rig.Log.Entries).Operations.Single().Payload;
        Assert.Equal(HostingOutcome.Unreachable, recorded.Outcome);
    }

    [Fact]
    public async Task AStandingGrantPublishesWithoutAskingAndNamesTheGrantInTheEntry()
    {
        await using var rig = new PublicationRig();
        foreach (var @class in new[] { HostingOperationClass.PublishRunBranch, HostingOperationClass.PullRequestWrite })
        {
            rig.Log.Add(
                DecisionKind.HostingPolicyGranted, new Actor(ActorKind.User, "person"), "grant",
                HostingPolicyGrantedPayload.Granting(new HostingPolicyGrant("g-" + @class, @class, HostingP0Data.Repository, null, "person", HostingP0Data.At)));
        }

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.Done, (await StatusAsync(rig, ticket)).State);
        Assert.Empty(rig.Approvals.Requests);
        Assert.All(RemoteLogReading.ReadEntries(rig.Log.Entries).Operations, o => Assert.StartsWith("g-", o.Payload.GrantId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedGateStopsThePublicationBeforeAnythingRuns()
    {
        await using var rig = new PublicationRig();
        rig.Delivery.Ready = false;

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);

        var status = await StatusAsync(rig, ticket);
        Assert.Equal(RemoteWorkState.Failed, status.State);
        Assert.Equal(0, rig.Trace.Count("git:"));
        Assert.Equal(0, rig.Trace.Count("gh:"));
    }

    [Fact]
    public void NoFileOfThePublicationNamesTheWriteGateOfWorkNorStartsAProcess()
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentStudio.Orchestration", "Publication"));
        Assert.True(Directory.Exists(folder), folder);
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("WorkWrite" + "Gate", text, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Diagnostics.Process", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IProcessRunner", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ProcessStartInfo", text, StringComparison.Ordinal);
        }
    }
}
