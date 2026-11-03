using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Orchestration.Integration;

public static class IntegrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IIntegrator"/>, <see cref="IDeliveryService"/> and <see cref="IWorkspaceCleanup"/>. Every git
    /// operation they make goes through <see cref="IWorktreeManager"/> (<c>AddAgentStudioGit</c>), the hardened git
    /// layer; they also need <see cref="IVerificationRunner"/> (<c>AddAgentStudioVerification</c>),
    /// <see cref="IEvidenceStore"/>, <see cref="IApprovalGate"/>, and, for the delivery, the decision log
    /// (<see cref="IDecisionRecorder"/>) and the <see cref="IVerdictStore"/>. Anything registered earlier is kept.
    /// </summary>
    public static IServiceCollection AddAgentStudioIntegration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IIntegrator>(provider => new Integrator(
            provider.GetRequiredService<IWorktreeManager>(),
            provider.GetRequiredService<IVerificationRunner>(),
            provider.GetRequiredService<IEvidenceStore>()));
        services.TryAddSingleton<IDeliveryService>(provider => new DeliveryService(
            provider.GetRequiredService<IWorktreeManager>(),
            provider.GetRequiredService<IApprovalGate>(),
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IVerdictStore>()));
        services.TryAddSingleton<IWorkspaceCleanup>(provider => new WorkspaceCleanup(
            provider.GetRequiredService<IWorktreeManager>(),
            provider.GetRequiredService<IApprovalGate>()));
        return services;
    }
}
