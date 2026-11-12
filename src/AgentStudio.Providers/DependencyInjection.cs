using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Cli.Codex.AppServer;
using AgentStudio.Providers.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Providers;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentStudioProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProviderProbe>(sp => new ClaudeCodeProbe(
            sp.GetRequiredService<IExecutableLocator>(), sp.GetRequiredService<IProcessRunner>()) { WorkingDirectory = NeutralFolder(sp) });
        services.AddSingleton<IProviderProbe>(sp => new CodexCliProbe(
            sp.GetRequiredService<IExecutableLocator>(), sp.GetRequiredService<IProcessRunner>()) { WorkingDirectory = NeutralFolder(sp) });
        services.AddSingleton<IProviderProbe>(sp => new GeminiCliProbe(
            sp.GetRequiredService<IExecutableLocator>(), sp.GetRequiredService<IProcessRunner>()) { WorkingDirectory = NeutralFolder(sp) });
        services.AddSingleton<ProviderDiagnostics>();

        // Which CLI keeps an executor's writes in its worktree at the version installed now (ADR-0018): --version only.
        services.AddSingleton<IWriteConfinementProbe>(sp => new CliWriteConfinementProbe(
            sp.GetRequiredService<IExecutableLocator>(), sp.GetRequiredService<IProcessRunner>()) { WorkingDirectory = NeutralFolder(sp) });

        // Agent providers are only reachable composed with the project's journal (criterion F2-9): the adapters are
        // never registered as IAgentProvider. Needs an IPromptJournalFactory (AddAgentStudioMemory) and a TimeProvider.
        services.AddSingleton<IProjectAgentProviders, ProjectAgentProviders>();

        // The skills of each agent run (F3-9), materialized without touching the user's CLI configuration.
        services.AddSingleton<Application.Skills.ISkillMaterializer>(_ => new Skills.SkillMaterializer());
        return services;
    }

    /// <summary>
    /// Registers the reader of the Codex quota (docs/14 D-15) behind <see cref="IProviderQuotaReader"/>: the app-server's
    /// <c>account/rateLimits/read</c>, only with the user's opt-in. <paramref name="optedIn"/> gives the function that reads
    /// that setting (it lives in the desktop settings), asked on every read; without it nothing is looked up nor launched.
    /// The engine and the screens share it, and reads asked at the same time are one (<see cref="SharedQuotaReader"/>, docs/18
    /// P37), off the thread that asks. Needs an <see cref="IExecutableLocator"/>, both process runners and a
    /// <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddCodexQuotaReader(this IServiceCollection services, Func<IServiceProvider, Func<bool>> optedIn)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optedIn);
        services.AddSingleton<IProviderQuotaReader>(sp => new SharedQuotaReader(new CodexAppServerQuotaReader(
            sp.GetRequiredService<IExecutableLocator>(),
            sp.GetRequiredService<IStreamingProcessRunner>(),
            sp.GetRequiredService<IProcessRunner>(),
            optedIn(sp),
            sp.GetRequiredService<TimeProvider>()) { WorkingDirectory = NeutralFolder(sp) }));
        return services;
    }

    // The neutral folder of the probes (ADR-0018): the composition's, or none, which means the system folder.
    private static string? NeutralFolder(IServiceProvider provider) => provider.GetService<NeutralWorkingFolderOptions>()?.Path;
}
