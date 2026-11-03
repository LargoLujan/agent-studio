using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Memory.Context;
using AgentStudio.Memory.Export;
using AgentStudio.Memory.Journal;
using AgentStudio.Memory.Store;
using AgentStudio.Memory.Summary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Memory;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the factory of per-project Markdown daily prompt journals. They write through an
    /// <see cref="IProjectStorageFiles"/>, which the composition root registers (<c>AddAgentStudioPersistence</c>).
    /// </summary>
    public static IServiceCollection AddAgentStudioMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPromptJournalFactory, MarkdownDailyJournalFactory>();
        return services;
    }

    /// <summary>
    /// Registers the memory of the projects (docs/14 F4-5, F4-6 and F4-9): one <see cref="ProjectMemory"/> as
    /// <see cref="IProjectMemory"/> (one instance, so every memory write of a project goes through the same gate), its
    /// Markdown export (<see cref="IMemoryExporter"/>), the context packer (<see cref="IContextPacker"/>), the daily
    /// summary (<see cref="IDailySummaryService"/>), the derivation of the memory from the log and the journal
    /// (<see cref="IMemoryDerivation"/>, D-18 and docs/17 D-02) and the trust of the journal (<see cref="IJournalTrust"/>). They need the persistence (<c>AddAgentStudioPersistence</c>: the
    /// decision recorder, the memory store and the checked storage), the redactor (<c>AddAgentStudioSecurity</c>) and the
    /// <see cref="Application.Orchestration.IRunActivity"/> of the orchestrator, which this method does not register: with
    /// no activity the container does not build, so no memory write ever runs as if no run could be going on. The system
    /// clock is registered unless a <see cref="TimeProvider"/> already is, and anything registered before for these ports
    /// is kept.
    /// </summary>
    public static IServiceCollection AddAgentStudioProjectMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ProjectMemory>();
        services.TryAddSingleton<IProjectMemory>(provider => provider.GetRequiredService<ProjectMemory>());
        services.TryAddSingleton<IMemoryExporter, MemoryMarkdownExporter>();
        services.TryAddSingleton<IContextPacker, ContextPacker>();
        services.TryAddSingleton<IDailySummaryService, DailySummaryService>();
        services.TryAddSingleton<IMemoryDerivation, Derivation.MemoryDerivation>();
        services.TryAddSingleton<IJournalTrust, Derivation.JournalTrust>();
        return services;
    }

    /// <summary>
    /// Registers the Markdown daily prompt journal for the project rooted at <paramref name="projectRoot"/>, written
    /// through the registered <see cref="IProjectStorageFiles"/>.
    /// </summary>
    public static IServiceCollection AddAgentStudioMemory(this IServiceCollection services, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPromptJournal>(sp => new MarkdownDailyJournal(
            projectRoot,
            sp.GetRequiredService<IProjectStorageFiles>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
