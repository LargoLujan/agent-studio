using AgentStudio.Application.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>Registration of the door for tools and of the sources of Auto Doctor that read this computer (docs/20 D-02 and D-03).</summary>
public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// The door (<see cref="IDoctorToolRunner"/>), the inventory (<see cref="IInstalledToolInventory"/>, which is also the scope of a run), the probe of the project
    /// and the sources of «Herramientas» and «Contenedores». It needs <c>IExecutableLocator</c> and <c>IProcessRunner</c> (<c>AddAgentStudioInfrastructure</c>).
    /// <paramref name="localBase"/> is <c>%LOCALAPPDATA%\AgentStudio</c>: the folders of the door live under it, in <c>doctor</c>.
    /// </summary>
    public static IServiceCollection AddAgentStudioDoctorTools(this IServiceCollection services, string localBase)
    {
        ArgumentNullException.ThrowIfNull(services);
        var folders = DoctorFolders.Under(localBase);
        services.TryAddSingleton(folders);
        services.TryAddSingleton<IDoctorToolRunner>(provider => new DoctorToolRunner(
            provider.GetRequiredService<AgentStudio.Application.Execution.IExecutableLocator>(),
            provider.GetRequiredService<AgentStudio.Application.Execution.IProcessRunner>(),
            provider.GetRequiredService<DoctorFolders>()));
        services.TryAddSingleton<IWebView2Probe, WebView2RegistryProbe>();
        services.TryAddSingleton<ToolSnapshotProvider>();
        services.TryAddSingleton<IInstalledToolInventory>(provider => provider.GetRequiredService<ToolSnapshotProvider>());
        services.AddSingleton<IDoctorRunScope>(provider => provider.GetRequiredService<ToolSnapshotProvider>());
        services.TryAddSingleton<IProjectEnvironmentProbe>(_ => new ProjectEnvironmentProbe());
        services.AddSingleton<IDoctorCheckSource, ToolsCheckSource>();
        services.AddSingleton<IDoctorCheckSource, ContainersCheckSource>();
        return services;
    }
}
