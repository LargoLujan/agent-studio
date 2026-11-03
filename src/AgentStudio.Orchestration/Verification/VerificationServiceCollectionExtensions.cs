using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Orchestration.Verification;

public static class VerificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IVerificationRunner"/> (<see cref="VerificationRunner"/>). It needs an
    /// <see cref="IStreamingProcessRunner"/> (<c>AddAgentStudioInfrastructure</c>) and an <see cref="IEvidenceStore"/>
    /// (the persistence package); a <see cref="TimeProvider"/> registered earlier is used, otherwise the system clock.
    /// Isolated steps (ADR-0017) run in the work areas of the <see cref="IIsolatedWorkAreaFactory"/>
    /// (<c>AddAgentStudioInfrastructure</c> registers it), on a copy of the commit the git layer
    /// (<see cref="IWorktreeManager"/>, <c>AddAgentStudioGit</c>) takes from the project's repository; without either the
    /// runner refuses them instead of running them without isolation. A runner registered earlier is kept, so tests and
    /// hosts can substitute their own. The profile file needs no registration: <see cref="VerificationProfileFile"/> is static.
    /// </summary>
    public static IServiceCollection AddAgentStudioVerification(this IServiceCollection services, VerificationRunnerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IVerificationRunner>(provider => new VerificationRunner(
            provider.GetRequiredService<IStreamingProcessRunner>(),
            provider.GetRequiredService<IEvidenceStore>(),
            options,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IIsolatedWorkAreaFactory>(),
            provider.GetService<IWorktreeManager>()));
        return services;
    }
}
