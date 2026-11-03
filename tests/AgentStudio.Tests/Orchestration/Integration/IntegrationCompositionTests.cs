using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Orchestration.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Orchestration.Integration;

public sealed class IntegrationCompositionTests
{
    private static readonly RunId Run = new("20261016-a1b2c3");

    private static ServiceCollection WithPorts(FakeWorktreeManager worktrees, DeliveryRecords? records = null)
    {
        var log = records ?? new DeliveryRecords("proyecto-1");
        var services = new ServiceCollection();
        services.AddSingleton<IStreamingProcessRunner>(new ScriptedProcessRunner());
        services.AddSingleton<IEvidenceStore>(new InMemoryEvidenceStore());
        services.AddSingleton<IWorktreeManager>(worktrees);
        services.AddSingleton<IApprovalGate>(new RecordingApprovalGate());
        services.AddSingleton<IDecisionRecorder>(log.Recorder);
        services.AddSingleton<IVerdictStore>(log.Verdicts);
        services.AddAgentStudioVerification();
        return services;
    }

    [Fact]
    public void Integration_delivery_and_cleanup_are_composed_from_the_application_ports()
    {
        using var provider = WithPorts(new FakeWorktreeManager(new FakeRepository()))
            .AddAgentStudioIntegration()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<Integrator>(provider.GetRequiredService<IIntegrator>());
        Assert.IsType<DeliveryService>(provider.GetRequiredService<IDeliveryService>());
        Assert.IsType<WorkspaceCleanup>(provider.GetRequiredService<IWorkspaceCleanup>());
        Assert.Same(provider.GetRequiredService<IIntegrator>(), provider.GetRequiredService<IIntegrator>());
    }

    [Fact]
    public async Task The_composed_services_reach_git_only_through_the_registered_worktree_manager()
    {
        // The log says the run succeeded with an approved integration, but the run's branch does not exist.
        var repo = new FakeRepository();
        var worktrees = new FakeWorktreeManager(repo);
        var records = new DeliveryRecords("proyecto-1");
        await records.SucceededAsync(Run, repo.Commit(repo.Base));
        using var provider = WithPorts(worktrees, records).AddAgentStudioIntegration().BuildServiceProvider();

        var result = await provider.GetRequiredService<IDeliveryService>()
            .DeliverAsync(new ProjectContext("proyecto-1", repo.Root), Run, DeliveryMode.FastForwardOnly, TestContext.Current.CancellationToken);

        Assert.False(result.Delivered);
        Assert.Equal(["resolve " + IntegrationNames.BranchRef(Run)], worktrees.Calls);
        Assert.Equal(1, worktrees.Inspections);
    }

    [Fact]
    public async Task The_composed_delivery_reads_the_decision_log_and_the_verdicts_of_the_container()
    {
        var repo = new FakeRepository();
        var worktrees = new FakeWorktreeManager(repo);
        var records = new DeliveryRecords("proyecto-1");
        var integration = repo.Commit(repo.Base);
        await records.SucceededAsync(Run, integration);
        using var provider = WithPorts(worktrees, records).AddAgentStudioIntegration().BuildServiceProvider();

        var readiness = await provider.GetRequiredService<IDeliveryService>()
            .CheckAsync(new ProjectContext("proyecto-1", repo.Root), Run, TestContext.Current.CancellationToken);

        Assert.True(readiness.Ready, readiness.Reason);
        Assert.Equal(integration, readiness.Commit);
    }
}
