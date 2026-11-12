using AgentStudio.Application.Prompts;
using System.Windows.Threading;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Panels;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Agents;
using AgentStudio.Observability.Analysis;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

internal static class OrchestrationUiServices
{
    /// <summary>
    /// Registers the orchestrator screen (docs/13 section 10, WP6): the approval prompt behind <see cref="IApprovalPrompt"/>
    /// with its WPF dialog, the canvas layout store, the session over whatever backend ports are registered and the
    /// factory of «Orquestador» documents. It registers no engine, store or judge of its own: the composition root does
    /// (<see cref="PlatformServices.AddPlatformServices"/>), and without them the screen says «Motor no disponible» and
    /// lists what is missing. The layout store writes through the policy of the project's storage
    /// (<see cref="IProjectStorageFiles"/>), registered here unless the composition root already did. With Phase 8 (docs/16
    /// D-13, ADR-0037) the prompt and «Aprobar plan» get the reinforced confirmation of Windows Hello, and the prompt waits on
    /// the lock of the interface, when <see cref="LockServices.AddWindowsHelloAndLock"/> registered them; the session reads
    /// the run headers (whether a run has the consent to resume by itself). The engine also reads a previous run back from its
    /// log when it can (<see cref="IRunInspector"/>), so «Ver» opens it in the monitor, and the evidence store gives the
    /// monitor the error line of a failed verification step.
    /// </summary>
    internal static IServiceCollection AddOrchestrationUiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IProjectStorageFiles, ProjectStorageFiles>();
        return services
            .AddSingleton<IApprovalDialog, WpfApprovalDialog>()
            .AddSingleton<IApprovalPrompt>(provider => new ApprovalPromptService(
                provider.GetRequiredService<IApprovalDialog>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                UiContext(),
                provider.GetService<ReinforcedConfirmation>(),
                provider.GetService<IInterfaceLock>(),
                ProjectNames(provider.GetService<ProjectDatabases>())))
            .AddSingleton<ICanvasLayoutStore, FileCanvasLayoutStore>()
            .AddSingleton(provider => new OrchestrationBackend(
                provider.GetService<IOrchestrator>(),
                provider.GetService<IProjectWorkspace>(),
                provider.GetService<IRequirementStore>(),
                provider.GetService<IAgentRoster>(),
                provider.GetService<IVerificationProfileSource>(),
                provider.GetService<IPlanStore>(),
                provider.GetService<IDeliveryService>(),
                provider.GetService<IWriteConfinementProbe>(),
                provider.GetService<IDecisionRecorder>(),
                provider.GetService<IDecisionLogOrigin>(),
                provider.GetService<IApprovalPrompt>(),
                provider.GetService<IRunRegistry>(),
                provider.GetService<IOrchestrator>() as IRunInspector,
                provider.GetService<IEvidenceStore>()))
            .AddSingleton<OrchestrationSession>()
            .AddSingleton(provider => new HeadTestsServices(
                provider.GetService<IVerificationRunner>(),
                provider.GetService<IVerificationProfileSource>(),
                provider.GetService<IWorktreeManager>(),
                provider.GetService<IProjectWorkspace>(),
                provider.GetService<IProjectActivity>()))
            .AddSingleton(provider => new ReduceMotionSetting(provider.GetRequiredService<IAppSettingsStore>()))
            .AddSingleton(provider => new MonitorServices(
                provider.GetService<IDecisionRecorder>() is { } decisions ? new DecisionLogContextPartsSource(decisions) : null,
                provider.GetService<QuotaViewModel>(),
                provider.GetService<IConsumptionAnalyzer>(),
                provider.GetService<IAssistedConsumptionAnalysis>(),
                AssistedEstimate(provider.GetService<IAgentRoster>(), provider.GetService<IUsageEstimator>()),
                provider.GetService<IInterfaceLock>(),
                liveContext: provider.GetService<IRunRegistry>() is { } runs && provider.GetService<IPlanStore>() is { } plans ? new RegistryRunLiveContextSource(runs, plans) : null))
            .AddSingleton<IOrchestratorDocumentFactory>(provider =>
            {
                // Resolved once, here: the documents keep the delegate for as long as they live, and a canvas asks for it each time an
                // animation would start.
                var motion = provider.GetRequiredService<ReduceMotionSetting>();
                return new OrchestratorDocumentFactory(
                    provider.GetRequiredService<OrchestrationSession>(),
                    provider.GetRequiredService<ICanvasLayoutStore>(),
                    provider.GetService<TimeProvider>() ?? TimeProvider.System,
                    provider.GetService<ReinforcedConfirmation>(),
                    BuilderServicesOf(provider),
                    () => motion.Current,
                    provider.GetRequiredService<MonitorServices>(),
                    provider.GetService<IProjectOptionsStore>());
            });
    }

    /// <summary>
    /// What the visual builder reads (docs/04 section 21.8): «Revisar el orden», the chat of preparation and the register of the OKs of
    /// quota, the catalog of models and what the log taught of them, the estimate, the quota of the account and the brief of the
    /// project, and the state of the repository read through the hardened git layer. The live state is not here: each document
    /// takes it from its own monitor.
    /// </summary>
    internal static BuilderServices BuilderServicesOf(IServiceProvider provider) => new(
        provider.GetService<IOrderReview>(),
        provider.GetService<IPreparationChat>(),
        provider.GetService<IQuotaConsentLedger>(),
        provider.GetService<IModelCatalogSource>(),
        provider.GetService<IModelLearning>(),
        provider.GetService<IUsageEstimator>(),
        provider.GetService<IWorktreeManager>() is { } worktrees ? new WorktreeRepositoryStateReader(worktrees, provider.GetService<IRepositoryRemoteReader>()) : null,
        Live: null,
        Quota: provider.GetService<IQuotaObservations>(),
        Brief: provider.GetService<IProjectBrief>(),
        ContextPacker: provider.GetService<IContextPacker>(),
        ProjectFiles: (root, cancellationToken) => Task.Run(() => ProjectFileCounter.Count(root, ProjectFileCounter.DefaultMaxFiles, cancellationToken), cancellationToken),
        SkillCharacters: SkillPresentationSource.For(provider.GetService<ISkillCatalog>(), provider.GetService<ISkillActivationStore>()),
        Language: provider.GetService<IResponseLanguage>());

    /// <summary>
    /// What the analysis of consumption with an agent would spend, shown before the OK (docs/17 D-35): the estimate of one attempt of
    /// the analyst on the program of the judge of the template, the vendor that did not produce the work. Unknown, with its reason, when
    /// the template has no judge. Null when the estimator or the template are not composed: the OK then says there is no estimate.
    /// </summary>
    internal static Func<ProjectContext, CancellationToken, Task<UsageEstimate>>? AssistedEstimate(IAgentRoster? roster, IUsageEstimator? estimator)
    {
        if (roster is null || estimator is null)
        {
            return null;
        }

        return async (project, cancellationToken) =>
        {
            var agents = await roster.ListAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
            var judge = agents.FirstOrDefault(a => a.Role == AgentRole.Judge);
            return judge is null
                ? UsageEstimate.Unknown(0, "There is no judge in the template to say which program would analyse.")
                : await estimator.EstimateAsync(
                    new UsageEstimateRequest(project, AssistedConsumptionAnalysisService.AnalystId, judge.Model.Provider, judge.Model.Model, AgentRunPurpose.ConsumptionAnalysis, Effort: judge.Effort),
                    cancellationToken).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// The name an approval question gives a project (V25): the folder of a project this instance has open, as the window
    /// and the welcome name it; null for any other, and then the question names none. Never its id.
    /// </summary>
    internal static Func<string, string?>? ProjectNames(ProjectDatabases? databases) =>
        databases is null ? null : id => databases.Find(id) is { } open ? RecentProjectItem.From(open.RootPath).Name : null;

    /// <summary>
    /// The UI thread's context, whatever thread resolves the prompt (the approval gate may be built off the UI thread):
    /// the dialog must be shown on the dispatcher.
    /// </summary>
    private static DispatcherSynchronizationContext? UiContext() =>
        System.Windows.Application.Current?.Dispatcher is { } dispatcher ? new DispatcherSynchronizationContext(dispatcher) : null;
}
