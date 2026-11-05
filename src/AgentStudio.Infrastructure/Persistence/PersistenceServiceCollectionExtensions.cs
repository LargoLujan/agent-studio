using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the project workspace and the SQLite stores behind the Application ports (docs/13 sections 3.2 and
    /// 3.6), the files other modules write in a project's <c>.agentstudio</c> (<see cref="IProjectStorageFiles"/>: the
    /// prompt journal, the canvas layout, the verification profile and the Markdown of the memory, under the policy of the
    /// storage) and their checked read (<see cref="IProjectStorageReader"/>), the rows of the memory
    /// (<see cref="IMemoryStore"/>), the read-only log of another project (<see cref="IProjectLogReader"/>), and
    /// <see cref="DecisionRecorder"/> (implemented in Application) as the single writer of every decision log,
    /// which is also the <see cref="IDecisionLogOrigin"/> that accepts a history no anchor recognises.
    /// All are singletons that share one <see cref="ProjectDatabases"/>; the host disposes it when it shuts down, which
    /// releases the open projects. The system clock is registered unless a <see cref="TimeProvider"/> already is, and
    /// anything registered before for one of these ports is kept. Since Phase 5 the stores include the immutable headers
    /// of the debates (<see cref="IDebateRegistry"/>) and of the whole-project audits (<see cref="IProjectAuditRegistry"/>).
    /// </summary>
    /// <param name="anchors">
    /// Where the recorder anchors the head of each log, outside the projects (ADR-0010, addendum). The composition root
    /// passes it; without it, <see cref="DecisionLogAnchorOptions.ForCurrentUser"/>.
    /// </param>
    public static IServiceCollection AddAgentStudioPersistence(this IServiceCollection services, DecisionLogAnchorOptions? anchors = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ProjectDatabases>();
        services.TryAddSingleton<IProjectClosedSignal>(provider => provider.GetRequiredService<ProjectDatabases>());
        services.TryAddSingleton<IProjectWorkspace, ProjectWorkspace>();
        services.TryAddSingleton<IProjectStorageFiles, ProjectStorageFiles>();
        services.TryAddSingleton<IProjectStorageReader, ProjectStorageFiles>();
        services.TryAddSingleton<IMemoryStore, SqliteMemoryStore>();
        services.TryAddSingleton<IProjectLogReader, ReadOnlyProjectLogReader>();
        services.TryAddSingleton<IDecisionLogStore, SqliteDecisionLogStore>();
        services.TryAddSingleton<IEvidenceStore, SqliteEvidenceStore>();
        services.TryAddSingleton<IRequirementStore, SqliteRequirementStore>();
        services.TryAddSingleton<IPlanStore, SqlitePlanStore>();
        services.TryAddSingleton<IRunRegistry, SqliteRunRegistry>();
        services.TryAddSingleton<IDebateRegistry, SqliteDebateRegistry>();
        services.TryAddSingleton<IProjectAuditRegistry, SqliteProjectAuditRegistry>();
        services.TryAddSingleton<IVerdictStore, SqliteVerdictStore>();
        services.TryAddSingleton<IAgentRoster, SqliteAgentRoster>();
        services.TryAddSingleton(anchors ?? DecisionLogAnchorOptions.ForCurrentUser());
        services.TryAddSingleton<IDecisionLogAnchorStore, FileDecisionLogAnchorStore>();
        services.TryAddSingleton<IDecisionRecorder>(provider =>
        {
            var recorder = new DecisionRecorder(
                provider.GetRequiredService<IDecisionLogStore>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IDecisionLogAnchorStore>());

            // A closed project keeps nothing in memory: the log the recorder verified for it is forgotten.
            provider.GetRequiredService<ProjectDatabases>().Closed += recorder.Forget;
            return recorder;
        });

        // The same writer: accepting a history appends to the log, under the lock of the one recorder of each project.
        services.TryAddSingleton<IDecisionLogOrigin>(provider => provider.GetRequiredService<IDecisionRecorder>() as IDecisionLogOrigin
            ?? throw new InvalidOperationException("The registered IDecisionRecorder cannot accept a history as a new origin (IDecisionLogOrigin): register one that can."));
        return services;
    }
}
