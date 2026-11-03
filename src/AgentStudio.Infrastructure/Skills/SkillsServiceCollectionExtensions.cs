using AgentStudio.Application.Skills;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Skills;

public static class SkillsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the skills catalog over the user's index, the store of activation decisions (docs/13 section 3.6) and
    /// the reader of the local health of a skill (docs/14 D-26, no network). <see cref="SkillCatalog"/> is also
    /// registered as itself, for whoever shows its warnings. Project and agent decisions live in the project database, so
    /// this needs <c>AddAgentStudioPersistence</c> (it shares its <see cref="ProjectDatabases"/>). Global decisions go to
    /// <paramref name="activations"/>, by default <c>%APPDATA%\AgentStudio\skill-activations.jsonl</c>.
    /// <c>ISkillProvisioner</c> is not registered here: it needs the session folder of the run
    /// (<c>OrchestrationPaths.SkillSessionRoot</c>), which the composition root decides. Neither is the assisted
    /// recommendation: it needs the folders of its runs and the path of the MCP server, also the composition root's.
    /// </summary>
    public static IServiceCollection AddAgentStudioSkills(this IServiceCollection services, SkillCatalogOptions catalog, SkillActivationStoreOptions? activations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ProjectDatabases>();
        services.TryAddSingleton(catalog);
        services.TryAddSingleton(activations ?? SkillActivationStoreOptions.ForCurrentUser());
        services.TryAddSingleton<ISkillCatalogImporter, SkillsIndexCatalogImporter>();
        services.TryAddSingleton<SkillCatalog>();
        services.TryAddSingleton<ISkillCatalog>(sp => sp.GetRequiredService<SkillCatalog>());
        services.TryAddSingleton<ISkillActivationStore, SkillActivationStore>();
        services.TryAddSingleton<ISkillHealthReader, SkillHealthReader>();
        return services;
    }
}
