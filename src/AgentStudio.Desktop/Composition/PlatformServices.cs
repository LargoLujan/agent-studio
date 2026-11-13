using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Debate;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Setup;
using AgentStudio.Desktop.Skills;
using AgentStudio.Infrastructure;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Memory;
using AgentStudio.Observability;
using AgentStudio.Observability.Analysis;
using AgentStudio.Observability.Estimates;
using AgentStudio.Observability.Logging;
using AgentStudio.Observability.Models;
using AgentStudio.Observability.Quota;
using AgentStudio.Orchestration;
using Engine = AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Providers;
using AgentStudio.Providers.Cli;
using AgentStudio.Security;
using AgentStudio.Security.Approvals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Composition;

internal static class PlatformServices
{
    /// <summary>Host configuration key of the file log's minimum level (a <see cref="LogLevel"/> name). Information when missing or invalid.</summary>
    internal const string FileLogMinLevelKey = "Logging:File:MinLevel";

    /// <summary>
    /// Registers the real implementations behind the Application ports: the platform (processes, prompt journal, agent
    /// providers, redaction) and, one line per module, the Phase 3 engine (docs/13 section 10, «Integración»): git,
    /// persistence, skills, verification, integration, the independent review, the approval gate and the orchestrator;
    /// and the Phase 4 modules (docs/14 section 9, «Integración»): the usage projection with the quota record, the
    /// project memory with its context packer and export, the Codex quota reader (it reads only while the saved setting
    /// says the user opted in, D-15) and the assisted skill recommendation with its read-only MCP server next to the app;
    /// and the Phase 12 index (docs/17 §9.2, «Integración»; <see cref="SemanticIndexServices"/>): the semantic index with
    /// its store and its local model, the installer of the model and the planner's tool over the same MCP server.
    /// The engine works in <see cref="LocalDataPaths.Orchestration"/>; git is located with the executable locator at
    /// first use and gets the empty hooks folder under the worktree root (both inside the git layer). The skills catalog
    /// is the <c>catalog.json</c> of <paramref name="skillsIndexPath"/>, or none, and the global activation decisions live
    /// with the settings. The quota record and the folders of the assisted recommendation live under
    /// <see cref="AppFolders.Local"/>.
    /// </summary>
    internal static IServiceCollection AddPlatformServices(this IServiceCollection services, AppFolders folders, string? skillsIndexPath)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var paths = folders.Local.Orchestration;

        // ADR-0018 (addendum of 2026-11-12): no program is resolved from the open project nor from the worktrees of the runs. The
        // shell tells this list which project opens; the locator and the runners (AddAgentStudioInfrastructure) read it.
        services.AddSingleton(new OpenProjectRoots([paths.WorktreeRoot]));
        services.AddSingleton<IUntrustedRoots>(provider => provider.GetRequiredService<OpenProjectRoots>());
        services.AddSingleton(new NeutralWorkingFolderOptions(Path.Combine(folders.Local.BaseDirectory, "doctor", "cwd")));
        return services
            .AddAgentStudioInfrastructure()
            .AddAgentStudioMemory()
            .AddAgentStudioProjectMemory()
            .AddAgentStudioProviders()
            .AddCodexQuotaReader(provider => () => CodexQuotaReadEnabled(provider))
            .AddAgentStudioSecurity()
            .AddSingleton(paths)
            .AddAgentStudioGit(new GitWorktreeOptions(paths.WorktreeRoot))
            .AddAgentStudioPersistence()
            .AddAgentStudioObservability(new QuotaLogOptions { FilePath = Path.Combine(folders.Local.BaseDirectory, "usage", "quota.jsonl") })
            .AddModelServices()
            .AddAgentStudioSkills(SkillCatalogFor(skillsIndexPath), new SkillActivationStoreOptions(Path.Combine(folders.Roaming.BaseDirectory, "skill-activations.jsonl")))
            .AddAgentStudioSkillsDoctor(Path.Combine(folders.Local.BaseDirectory, "skills-doctor"), Path.Combine(folders.Local.BaseDirectory, "vault"))
            .AddSkillProvisioner(paths)
            .AddAgentStudioSkillRecommendation(Path.Combine(folders.Local.BaseDirectory, "skill-recommendations"), SkillsScreenServices.DefaultServerExecutable)
            .AddSemanticIndexServices(folders, SkillsScreenServices.DefaultServerExecutable)
            .AddAgentStudioVerification()
            .AddAgentStudioIntegration()
            .AddAgentStudioJudging()
            .AddAgentStudioApprovals()
            .AddEngineOnItsClock()
            .AddAgentStudioOrchestration()
            .AddPreparationServices()
            .AddAgentStudioSetup(Path.Combine(folders.Local.BaseDirectory, "setup"))
            .AddDoctorServices(folders)
            .AddSingleton<IVerificationProfileSource, FileVerificationProfileSource>()
            .AddDebateServices();
    }

    /// <summary>
    /// Phase 13 (docs/17 D-22, D-23, D-32 and D-34): the catalog of models the app ships, what the verified log of a project
    /// teaches of each model, the estimate of what an attempt would spend (the engine prices the options of a question with
    /// it, an optional port of its constructor) and the analysis of consumption, composed with the same catalog so that
    /// <c>model.oversized</c> can speak.
    /// </summary>
    private static IServiceCollection AddModelServices(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => new ConsumptionAnalysisOptions { Catalog = provider.GetRequiredService<IModelCatalogSource>().Current.Catalog });
        return services
            .AddAgentStudioModelCatalog()
            .AddAgentStudioModelLearning()
            .AddAgentStudioUsageEstimator()
            .AddAgentStudioConsumptionAnalysis();
    }

    /// <summary>
    /// Phase 13 (docs/17 D-20 and D-44): «Revisar el orden» and the chat of preparation, single for the app. They launch
    /// auxiliary agents (<see cref="IAuxiliaryAgentRunner"/>, which the screen of skills registers) and share the one register
    /// of the OKs to spend quota with the recommendation of skills and the analysis of consumption with an agent, which take it
    /// when it is registered.
    /// </summary>
    private static IServiceCollection AddPreparationServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IQuotaConsentLedger, QuotaConsentLedger>();
        services.TryAddSingleton<IOrderReview, OrderReviewService>();
        services.TryAddSingleton<IPreparationChat, PreparationChatService>();
        return services;
    }

    /// <summary>
    /// The engine on its own clock (<see cref="EngineClock"/>, F8-5): the orchestrator the module registers, built with the
    /// awake clock instead of the composition's, so only the engine (its runs, its detection of silence and its waits for
    /// quota) leaves the time asleep out. It is registered before the module, which keeps it; a registration made earlier
    /// still wins. The manual resume of the screen and the consented resume of the scheduler share this one orchestrator.
    /// </summary>
    private static IServiceCollection AddEngineOnItsClock(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => EngineClock.For(provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IOrchestrator>(provider =>
            ActivatorUtilities.CreateInstance<Engine.Orchestrator>(provider, provider.GetRequiredService<EngineClock>().Time));
        return services;
    }

    /// <summary>
    /// The Phase 5 modules (docs/15 section 9, «Integración»): the arena of debates and the audits (of the orchestrator, of
    /// the whole project, and the challenge of a verdict), with what only the composition root can give them: the trusted
    /// verification profile of a project, adapted from the desktop's source, and the fixed requirements of the modes that
    /// have them.
    /// </summary>
    private static IServiceCollection AddDebateServices(this IServiceCollection services) =>
        services
            .AddSingleton(provider => new VerificationProfilePorts(provider.GetRequiredService<IVerificationProfileSource>()))
            .AddSingleton<IDebateTestProfiles>(provider => provider.GetRequiredService<VerificationProfilePorts>())
            .AddSingleton<IAuditVerificationProfiles>(provider => provider.GetRequiredService<VerificationProfilePorts>())
            .AddSingleton(provider => DebateModeRequirementSource.For(provider.GetService<TimeProvider>() ?? TimeProvider.System))
            .AddAgentStudioDebateArena()
            .AddAgentStudioAudits();

    /// <summary>The opt-in of the Codex quota read, as saved now (a change in «Ajustes» applies to the next read); off without settings.</summary>
    private static bool CodexQuotaReadEnabled(IServiceProvider provider) =>
        provider.GetService<IAppSettingsStore>()?.Load().CodexQuotaReadEnabled == true;

    /// <summary>The catalog of the skills index rooted at <paramref name="indexRoot"/> (its <c>catalog.json</c>); none without an index.</summary>
    internal static SkillCatalogOptions SkillCatalogFor(string? indexRoot) =>
        new(string.IsNullOrWhiteSpace(indexRoot) ? null : Path.Combine(indexRoot, "catalog.json"));

    /// <summary>
    /// Adds the structured file logger: JSON lines in <c>%LOCALAPPDATA%\AgentStudio\logs\agentstudio-YYYYMMDD.log</c>,
    /// with secrets redacted and prompts reduced to a length and a hash. The minimum level is Information unless the
    /// host configuration says otherwise. <c>Logging:File:MinLevel</c> (for example the environment variable
    /// <c>Logging__File__MinLevel=Debug</c>) sets it explicitly and wins over every other setting, in both directions.
    /// Without it the framework's own rules decide, so <c>Logging:LogLevel</c> and <c>Logging:File:LogLevel</c> work as
    /// for any provider and the default stays Information. The logger needs the redactor of
    /// <see cref="AddPlatformServices"/>; the host disposes the logger when it shuts down, which writes what is still
    /// queued.
    /// </summary>
    internal static ILoggingBuilder AddFileLogging(this ILoggingBuilder logging, IConfiguration configuration)
    {
        var configured = configuration[FileLogMinLevelKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            // No explicit level: the provider must not add a floor of its own, or it would shadow the framework's rules.
            return logging.AddAgentStudioFileLogging(options => options.MinLevel = LogLevel.Trace);
        }

        // An explicit level also needs a filter for the provider, which is what lets it go below the framework default.
        var minLevel = FileLoggerOptions.ParseLevel(configured);
        return logging
            .AddAgentStudioFileLogging(options => options.MinLevel = minLevel)
            .AddFilter<FileLoggerProvider>(null, minLevel);
    }

    /// <summary>
    /// The skills of each agent run (F3-9): the catalog and the activations decide what is on, and the materializer of the
    /// providers puts it in a session folder under <see cref="OrchestrationPaths.SkillSessionRoot"/>.
    /// </summary>
    private static IServiceCollection AddSkillProvisioner(this IServiceCollection services, OrchestrationPaths paths) =>
        services
            .AddSingleton(new SkillProvisioningOptions(paths.SkillSessionRoot))
            .AddSingleton<ISkillProvisioner, SkillProvisioner>();
}
