using AgentStudio.Application.Help;
using AgentStudio.Application.Models;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Help;

/// <summary>
/// The composition of the help (docs/17 D-55 and D-56, ADR-0056), one line for the composition root: <see cref="AddAgentStudioHelp"/>.
/// It registers the corpus the app ships, its local search, the closed list of actions, the agent of the help over the auxiliary
/// runner and the register of OKs that the whole app shares, and the «Ayuda» document. What is registered earlier is kept, so tests
/// put doubles first. The agent is composed only when the auxiliary runner is: without it «Preguntar a un agente» says it is not
/// available, and the local help works all the same.
/// </summary>
public static class HelpServices
{
    public static IServiceCollection AddAgentStudioHelp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IHelpCorpus>(_ => HelpCorpus.FromAssembly());
        services.TryAddSingleton<IHelpSearch>(provider => new HelpSearch(provider.GetRequiredService<IHelpCorpus>()));
        services.TryAddSingleton<IHelpActionCatalog, HelpActionCatalog>();
        services.TryAddSingleton(provider => new HelpBackend(
            provider.GetRequiredService<IHelpCorpus>(),
            provider.GetRequiredService<IHelpSearch>(),
            provider.GetRequiredService<IHelpActionCatalog>(),
            provider.GetService<IHelpAgent>() ?? AgentOf(provider),
            provider.GetService<IUsageEstimator>(),
            provider.GetService<IProjectWorkspace>(),
            provider.GetService<ISecretRedactor>()));
        services.TryAddSingleton(provider => new HelpDocumentViewModel(
            provider.GetRequiredService<HelpBackend>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }

    /// <summary>The agent of the help over the services the app composed; null when the auxiliary runner, the redactor or the register of OKs is missing.</summary>
    private static IHelpAgent? AgentOf(IServiceProvider provider)
    {
        if (provider.GetService<IAuxiliaryAgentRunner>() is not { } runner
            || provider.GetService<ISecretRedactor>() is not { } redactor
            || provider.GetService<IQuotaConsentLedger>() is not { } ledger)
        {
            return null;
        }

        return new HelpAgentService(
            runner,
            redactor,
            ledger,
            provider.GetRequiredService<IHelpCorpus>(),
            provider.GetRequiredService<IHelpSearch>(),
            provider.GetRequiredService<IHelpActionCatalog>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<IModelCatalogSource>());
    }
}
