using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Desktop.Controls.Terminal;

public static class TerminalServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ConPTY launcher, the shell locator and the IDE's single <see cref="TerminalViewModel"/>. Needs an
    /// <see cref="IExecutableLocator"/> (registered by <c>AddAgentStudioInfrastructure</c>). Resolve the view model on
    /// the UI thread so it captures the right synchronization context.
    /// </summary>
    public static IServiceCollection AddTerminalServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ITerminalLauncher, ConPtyTerminalLauncher>();
        services.AddSingleton<IShellLocator>(provider => new ShellLocator(provider.GetRequiredService<IExecutableLocator>()));
        services.AddSingleton(provider => new TerminalViewModel(
            provider.GetRequiredService<ITerminalLauncher>(),
            provider.GetRequiredService<IShellLocator>()));
        return services;
    }
}
