using AgentStudio.Application.Documents;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Domain.Hosting;
using Microsoft.Extensions.DependencyInjection;
using AgentStudio.Application.Decisions;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>The registration of the publication resolves without a cycle, and the texts of the pull request carry their data in code spans (docs/21 D-19).</summary>
public sealed class PublicationCompositionAndTextsTests
{
    [Fact]
    public async Task TheRegistrationResolvesWithValidationAndTheGateAndTheMailboxAreTheSameInstances()
    {
        await using var rig = new PublicationRig();
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionRecorder>(rig.Log);
        services.AddSingleton<IProjectActivity>(rig.Activity);
        services.AddSingleton<IHostingProviders>(rig.Providers);
        services.AddSingleton<IRemoteSyncStore>(rig.Store);
        services.AddSingleton<IApprovalGate>(rig.Approvals);
        services.AddSingleton<IDeliveryService>(rig.Delivery);
        services.AddSingleton<IVerdictStore>(new RigVerdicts());
        services.AddSingleton<IRemoteGitRunner>(rig.Git);
        services.AddSingleton<IEvidenceStore>(new RigEvidence());
        services.AddSingleton<IRepositoryHistoryReader>(rig.History);
        services.AddSingleton<IRepositoryAnnotator>(rig.Annotator);
        services.AddSingleton<IUserTextRenderer>(new RigTexts());
        services.AddSingleton<TimeProvider>(rig.Time);
        services.AddAgentStudioPublication();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Same(provider.GetRequiredService<IHostingPolicyGate>(), provider.GetRequiredService<IHostingUseGate>());
        Assert.Same(provider.GetRequiredService<IRemoteSync>(), provider.GetRequiredService<RemoteSyncService>());
        Assert.NotNull(provider.GetRequiredService<IPublicationService>());
    }

    [Fact]
    public void ADatumLosesItsBackticksAndLineBreaksBeforeItIsFenced()
    {
        Assert.Equal("`abc`", PullRequestTextComposer.Fence("a`b`c"));
        Assert.Equal("`xy`", PullRequestTextComposer.Fence("x\r\ny"));
        Assert.Equal("`@someone Closes #12`", PullRequestTextComposer.Fence("@someone Closes #12"));
        Assert.Equal("`-`", PullRequestTextComposer.Fence(null));
    }

    [Fact]
    public async Task TheTitleAndTheBodyAreMadeOfTheTemplatesAndTheDataIsFenced()
    {
        await using var rig = new PublicationRig();
        var composer = new PullRequestTextComposer(new RigTexts(), null, new RigVerdicts(), new RigEvidence());

        var text = await composer.ComposeAsync(PublicationRig.Project, HostingP0Data.Run, HostingP0Data.Head, CancellationToken.None);

        Assert.Equal($"hosting.pr.title:`{HostingP0Data.Run.Value}`", text.Title);
        Assert.Contains($"hosting.pr.body-run:`{HostingP0Data.Run.Value}`,`{HostingP0Data.Head}`", text.Body, StringComparison.Ordinal);
        Assert.EndsWith("hosting.pr.body-footer:", text.Body, StringComparison.Ordinal);
        Assert.True(text.Title.Length <= PullRequestTextComposer.MaxTitle);
        Assert.True(text.Body.Length <= PullRequestTextComposer.MaxBody);
    }

    [Fact]
    public void TheMarkOfTheCommentIsTheFirstLineByWhichItIsFoundAgain()
    {
        Assert.Equal($"<!-- agent-studio:verdict run={HostingP0Data.Run.Value} -->", VerdictNoticeBuilder.MarkOf(HostingP0Data.Run));
    }
}
