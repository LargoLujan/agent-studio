using System.IO;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Observability.Analysis;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Observability.Analysis;

/// <summary>ADR-0052: the composition gets both analyses of consumption from one call, once, and the analysis with an agent is one instance.</summary>
public sealed class ConsumptionAnalysisRegistrationTests
{
    [Fact]
    public void Both_analyses_are_registered_once_and_the_one_with_an_agent_is_a_single_instance()
    {
        var services = new ServiceCollection()
            .AddSingleton<IDecisionRecorder, Unused>()
            .AddSingleton<IEvidenceStore, Unused>()
            .AddSingleton<IAgentRoster, Unused>()
            .AddSingleton<IAuxiliaryAgentRunner, Unused>();

        services.AddAgentStudioConsumptionAnalysis().AddAgentStudioConsumptionAnalysis();
        using var provider = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(IConsumptionAnalyzer));
        Assert.Single(services, d => d.ServiceType == typeof(IAssistedConsumptionAnalysis));
        Assert.IsType<ConsumptionAnalyzer>(provider.GetRequiredService<IConsumptionAnalyzer>());
        Assert.Same(provider.GetRequiredService<IAssistedConsumptionAnalysis>(), provider.GetRequiredService<IAssistedConsumptionAnalysis>());
    }

    private sealed class Unused : IDecisionRecorder, IEvidenceStore, IAgentRoster, IAuxiliaryAgentRunner
    {
        public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken) => throw new NotSupportedException();

        Task<IReadOnlyList<AgentDefinition>> IAgentRoster.ListAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string projectId, AgentId agent, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuxiliaryRunResult> RunAsync(ProjectContext project, AuxiliaryRunRequest request, IProgress<AgentEvent>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
