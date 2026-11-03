using AgentStudio.Application.Decisions;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Security.Approvals;

public sealed class ApprovalsCompositionTests
{
    private sealed class YesPrompt : IApprovalPrompt
    {
        public int Asked { get; private set; }

        public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(new ApprovalDecision(true, "manuel", T0, null));
        }
    }

    private static (ServiceCollection Services, YesPrompt Prompt, InMemoryDecisionLogStore Store) Platform()
    {
        var store = new InMemoryDecisionLogStore();
        var clock = new FakeTimeProvider(T0);
        var prompt = new YesPrompt();
        var services = new ServiceCollection();
        services.AddSingleton<IApprovalPrompt>(prompt);
        services.AddSingleton<IDecisionRecorder>(new DecisionRecorder(store, clock));
        services.AddSingleton<TimeProvider>(clock);
        return (services, prompt, store);
    }

    [Fact]
    public async Task The_gate_from_the_container_asks_the_desktop_prompt_and_records_both_entries()
    {
        var (services, prompt, store) = Platform();
        using var container = services.AddAgentStudioApprovals().BuildServiceProvider();

        var gate = container.GetRequiredService<IApprovalGate>();
        var decision = await gate.RequestAsync(new ApprovalRequest("deliver", "Entregar en main", RiskLevel.Critical, null, [], "p1", "r1"), TestContext.Current.CancellationToken);

        Assert.True(decision.Approved);
        Assert.Equal(1, prompt.Asked);
        Assert.Equal([DecisionKind.ApprovalRequested, DecisionKind.ApprovalGranted], store.Of("p1").Select(e => e.Kind));
        Assert.Same(gate, container.GetRequiredService<IApprovalGate>());
    }

    [Fact]
    public void Options_registered_earlier_are_kept()
    {
        var (services, _, _) = Platform();
        var options = new ApprovalGateOptions(TimeSpan.FromMinutes(5));
        services.AddSingleton(options);

        using var container = services.AddAgentStudioApprovals(new ApprovalGateOptions(TimeSpan.FromHours(1))).BuildServiceProvider();

        Assert.Same(options, container.GetRequiredService<ApprovalGateOptions>());
    }
}
