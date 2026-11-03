using System.Windows.Threading;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.ViewModels;
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
            .AddSingleton<IOrchestratorDocumentFactory>(provider => new OrchestratorDocumentFactory(
                provider.GetRequiredService<OrchestrationSession>(),
                provider.GetRequiredService<ICanvasLayoutStore>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetService<ReinforcedConfirmation>()));
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
