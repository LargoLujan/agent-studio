using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentStudioInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IStreamingProcessRunner, StreamingProcessRunner>();

        // The untrusted roots (the open project, ADR-0018) are the composition root's: without them nothing is excluded but the relative
        // entries of the PATH. The runners resolve bare program names with the same roots.
        services.AddSingleton<IExecutableLocator>(provider => new ExecutableLocator(roots: provider.GetService<IUntrustedRoots>()));

        // The Low work areas of isolated verification (ADR-0017), under %USERPROFILE%\AppData\LocalLow\AgentStudio\verify.
        // The copy of the commit in each one is written by the git layer (AddAgentStudioGit).
        services.AddSingleton<IIsolatedWorkAreaFactory>(_ => new LowIntegrityWorkAreaFactory());
        return services;
    }
}
