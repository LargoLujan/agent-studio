using System.IO;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using AgentStudio.Infrastructure.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Skills;

/// <summary>
/// The composition of the Skills screen (docs/14 F4-7 and F4-8), one line each for the composition root:
/// <see cref="AddAgentStudioSkillsScreen"/> next to the other screens and <see cref="AddAgentStudioSkillRecommendation"/>
/// next to the platform services.
/// </summary>
public static class SkillsScreenServices
{
    /// <summary>Where the MCP server is expected by default: next to the app, where the integration copies it (docs/04 section 16.9).</summary>
    public static string DefaultServerExecutable => Path.Combine(AppContext.BaseDirectory, "AgentStudio.SkillsMcp.exe");

    /// <summary>
    /// The screen: its <see cref="SkillsBackend"/> over whatever ports are registered (the catalog with its warnings
    /// through <see cref="SkillCatalog"/>, the decisions, the roster, the local health, the recommendation, the workspace
    /// and the approval gate, which records the consent) and the factory of «Skills» documents. It registers no port of its
    /// own: without them the screen says what is missing.
    /// </summary>
    public static IServiceCollection AddAgentStudioSkillsScreen(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The doctor of skills (docs/20 D-13 to D-15): the permission lives in the settings of the app, and Windows Hello is the barrier the approvals use.
        // They replace the safe defaults of AddAgentStudioSkillsDoctor (never allowed, every confirmation a no), whichever of the two is called first.
        services.TryAddSingleton<ISkillsDoctorNetworkSetting>(provider =>
            provider.GetService<IAppSettingsStore>() is { } store ? new AppSettingsSkillsDoctorSetting(store) : new NoSkillsDoctorNetworkSetting());
        services.Replace(ServiceDescriptor.Singleton<ISkillsDoctorPolicy>(provider => provider.GetRequiredService<ISkillsDoctorNetworkSetting>()));
        services.Replace(ServiceDescriptor.Singleton<ISkillsDoctorPresence>(provider =>
            provider.GetService<ReinforcedConfirmation>() is { } confirmation
                ? new HelloSkillsDoctorPresence(confirmation)
                : new NoSkillsDoctorPresence()));
        services.TryAddSingleton(provider => new SkillsBackend(
            provider.GetService<SkillCatalog>() is { } catalog ? catalog.ListWithReportAsync : null,
            provider.GetService<ISkillActivationStore>(),
            provider.GetService<IAgentRoster>(),
            provider.GetService<ISkillHealthReader>(),
            provider.GetService<ISkillRecommendationService>(),
            provider.GetService<IProjectWorkspace>(),
            provider.GetService<IApprovalGate>(),
            provider.GetService<ISkillHealthBatchChecker>(),
            provider.GetService<ISkillsDoctorTokenManager>(),
            provider.GetService<ISkillsDoctorNetworkSetting>() is { } setting and not NoSkillsDoctorNetworkSetting ? setting : null,
            provider.GetService<ISkillScriptReviews>(),
            provider.GetService<ISkillScriptsInspector>()));
        services.TryAddSingleton<ISkillsDocumentFactory>(provider => new SkillsDocumentFactory(
            provider.GetRequiredService<SkillsBackend>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }

    /// <summary>
    /// The assisted recommendation (D-29 and D-30): the folders of its runs under <paramref name="foldersRoot"/> (by
    /// default <c>%LOCALAPPDATA%\AgentStudio\skill-recommendations</c>), the absolute path of the MCP server, the auxiliary
    /// runner (kept when the composition root already registered one) and the service. It needs the skills module
    /// (<c>AddAgentStudioSkills</c>), the persistence (decision log, evidence, roster) and the providers.
    /// </summary>
    public static IServiceCollection AddAgentStudioSkillRecommendation(this IServiceCollection services, string foldersRoot, string serverExecutable)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(foldersRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExecutable);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISkillRecommendationFolders>(new SkillRecommendationFolders(foldersRoot));
        services.TryAddSingleton(new SkillRecommendationOptions(serverExecutable));
        services.TryAddSingleton<IAuxiliaryAgentRunner, AuxiliaryAgentRunner>();
        services.TryAddSingleton<ISkillRecommendationService, SkillRecommendationService>();
        return services;
    }
}

/// <summary>Creates the «Skills» documents over one backend.</summary>
public sealed class SkillsDocumentFactory : ISkillsDocumentFactory
{
    private readonly SkillsBackend _backend;
    private readonly TimeProvider _time;

    public SkillsDocumentFactory(SkillsBackend backend, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(time);
        _backend = backend;
        _time = time;
    }

    public SkillsDocumentViewModel Create(string? projectRoot, Func<string, Task>? openFile = null) => new(projectRoot, _backend, _time, openFile: openFile);
}
