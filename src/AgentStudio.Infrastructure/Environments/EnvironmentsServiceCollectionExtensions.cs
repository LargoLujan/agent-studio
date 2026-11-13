using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Environments;

public static class EnvironmentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers what WP-E contributes (docs/20 §9.4): the reader of the declared environment, the source of its checks for Auto Doctor (it needs an
    /// <see cref="IInstalledToolInventory"/>, which WP-A registers) and the reader of the versions of the tools of a verification step, which
    /// <c>AddAgentStudioVerification</c> gives to the runner when it is registered. A registration made earlier is kept. It is a method of its own so the one
    /// of the infrastructure does not change; the composition root calls it.
    /// </summary>
    public static IServiceCollection AddAgentStudioEnvironments(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDeclaredEnvironmentReader, DeclaredEnvironmentReader>();
        services.TryAddSingleton<IStepToolVersionReader>(provider => new StepToolVersionReader(provider.GetService<IUntrustedRoots>()));
        services.AddSingleton<IDoctorCheckSource>(provider => new DeclaredEnvironmentCheckSource(
            provider.GetRequiredService<IDeclaredEnvironmentReader>(), provider.GetRequiredService<IInstalledToolInventory>()));
        return services;
    }
}
