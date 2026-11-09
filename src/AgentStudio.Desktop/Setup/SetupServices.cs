using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Models;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Setup;

/// <summary>Creates the view models of the configurator over one service.</summary>
public sealed class SetupViewModelFactory(ProjectSetupService service)
{
    /// <summary>A configurator that has not read any project yet: <see cref="SetupViewModel.LoadAsync"/> does.</summary>
    public SetupViewModel Create() => new(service);
}

/// <summary>
/// The composition of the configurator of a project (docs/17 §4.6, ADR-0055), one line for the composition root:
/// <see cref="AddAgentStudioSetup"/>. It registers the adapters of its ports and the service over whatever ports are registered
/// (the roster, the decision log, the brief, the skills, the catalog of models and what the log teaches of them, the requirements
/// and the confirmation of Windows Hello). What is registered earlier is kept, so tests put doubles first. A port that is missing
/// is not faked: the step that needs it proposes nothing, and what needs Windows Hello is denied without it.
/// </summary>
public static class SetupServices
{
    /// <param name="optionsFolder">Where the options of each project live; by default <see cref="LocalProjectOptionsStore.DefaultFolder"/>.</param>
    public static IServiceCollection AddAgentStudioSetup(this IServiceCollection services, string? optionsFolder = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISetupRepositoryReader>(provider => new FileSetupRepositoryReader(provider.GetService<IWorktreeManager>()));
        services.TryAddSingleton<ISetupVerificationProfiles>(provider => new StackSetupVerificationProfiles(provider.GetRequiredService<IProjectStorageFiles>(), provider.GetService<IExecutableLocator>() ?? new ExecutableLocator()));
        services.TryAddSingleton<IProjectOptionsStore>(_ => new LocalProjectOptionsStore(optionsFolder ?? LocalProjectOptionsStore.DefaultFolder));
        services.TryAddSingleton(provider => new ProjectSetupService(
            provider.GetRequiredService<ISetupRepositoryReader>(),
            provider.GetRequiredService<IAgentRoster>(),
            provider.GetRequiredService<ISetupVerificationProfiles>(),
            provider.GetRequiredService<IProjectOptionsStore>(),
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetService<IProjectBrief>(),
            provider.GetService<ISkillCatalog>(),
            provider.GetService<ISkillActivationStore>(),
            provider.GetService<IRequirementStore>(),
            provider.GetService<IModelCatalogSource>(),
            provider.GetService<IModelLearning>(),
            provider.GetService<ISetupPresenceGate>() ?? (provider.GetService<ReinforcedConfirmation>() is { } confirmation ? new HelloSetupPresenceGate(confirmation) : null)));
        services.TryAddSingleton<SetupViewModelFactory>();

        // Which projects were already offered the configurator on the welcome tab: next to the options, nothing in the project.
        services.TryAddSingleton(_ => new SetupSuggestions(optionsFolder ?? LocalProjectOptionsStore.DefaultFolder));
        return services;
    }
}
