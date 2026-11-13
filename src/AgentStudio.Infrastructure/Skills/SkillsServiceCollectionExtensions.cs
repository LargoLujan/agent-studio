using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Security;
using AgentStudio.Infrastructure.Skills.GitHub;
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
        services.TryAddSingleton<ISkillScriptsInspector, SkillScriptsInspector>();
        services.TryAddSingleton<ISkillScriptReviews>(sp => new SkillScriptReviewStore(new SkillScriptReviewOptions(
            Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<SkillActivationStoreOptions>().GlobalActivationsFile)!, "skill-script-reviews.json"))));
        services.TryAddSingleton<ISkillHealthReader, SkillHealthReader>();
        return services;
    }

    /// <summary>
    /// The doctor of skills against GitHub (docs/20 D-13 to D-15, ADR-0063), one line for the composition root. It registers the client, the cache of
    /// <c>ETag</c>s, the vault of the token (DPAPI, only on Windows), the secrets and the checker. Two things are the composition root's, and each has a
    /// safe default that asks nothing:
    /// <list type="bullet">
    /// <item><see cref="ISkillsDoctorPolicy"/>, the permission of Settings: without one, the network is never allowed;</item>
    /// <item><see cref="ISkillsDoctorPresence"/>, Windows Hello: without one, every confirmation is a no, so the token is never read.</item>
    /// </list>
    /// The folders are injectable: <paramref name="cacheFolder"/> (by default <c>%LOCALAPPDATA%\AgentStudio\skills-doctor</c>) and <paramref name="vaultFolder"/>
    /// (<c>%LOCALAPPDATA%\AgentStudio\vault</c>).
    /// </summary>
    public static IServiceCollection AddAgentStudioSkillsDoctor(this IServiceCollection services, string? cacheFolder = null, string? vaultFolder = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISkillsDoctorPolicy, NeverAllowedSkillsDoctorPolicy>();
        services.TryAddSingleton<ISkillsDoctorPresence, DeniedSkillsDoctorPresence>();
        services.TryAddSingleton(new GitHubResponseCache(cacheFolder ?? GitHubResponseCache.DefaultFolder()));
        services.TryAddSingleton(sp => new GitHubApiClient(time: sp.GetRequiredService<TimeProvider>()));
        if (OperatingSystem.IsWindows())
        {
            var vault = new DpapiCredentialVault(vaultFolder ?? DpapiCredentialVault.DefaultFolder());
            services.TryAddSingleton<ICredentialVault>(vault);
            services.TryAddSingleton<ICredentialVaultProbe>(vault);
            services.TryAddSingleton(sp => new SkillsDoctorSecrets(
                sp.GetRequiredService<ICredentialVault>(), sp.GetRequiredService<ICredentialVaultProbe>(), sp.GetRequiredService<ISkillsDoctorPresence>()));
            services.TryAddSingleton<ISkillsDoctorSecrets>(sp => sp.GetRequiredService<SkillsDoctorSecrets>());
            services.TryAddSingleton<ISkillsDoctorTokenManager>(sp => sp.GetRequiredService<SkillsDoctorSecrets>());
        }

        services.TryAddSingleton<ISkillHealthBatchChecker>(sp => new GitHubSkillHealthChecker(
            sp.GetRequiredService<ISkillsDoctorPolicy>(),
            sp.GetService<ISkillsDoctorSecrets>() ?? new NoSkillsDoctorSecrets(),
            sp.GetRequiredService<GitHubApiClient>(),
            sp.GetRequiredService<GitHubResponseCache>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    private sealed class NeverAllowedSkillsDoctorPolicy : ISkillsDoctorPolicy
    {
        public bool NetworkAllowed => false;
    }

    private sealed class DeniedSkillsDoctorPresence : ISkillsDoctorPresence
    {
        public Task<bool> ConfirmAsync(SkillsDoctorPresenceReason reason, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class NoSkillsDoctorSecrets : ISkillsDoctorSecrets
    {
        public Task<SkillsDoctorTokenUse> UseTokenAsync(CancellationToken ct) => Task.FromResult(SkillsDoctorTokenUse.None);
    }
}
