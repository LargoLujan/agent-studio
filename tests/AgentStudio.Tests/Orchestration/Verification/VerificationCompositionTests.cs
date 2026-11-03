using AgentStudio.Application.Execution;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Verification;
using AgentStudio.Orchestration.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Orchestration.Verification;

public sealed class VerificationCompositionTests
{
    private static ServiceCollection WithPorts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStreamingProcessRunner>(new ScriptedProcessRunner());
        services.AddSingleton<IEvidenceStore>(new InMemoryEvidenceStore());
        return services;
    }

    [Fact]
    public void The_runner_is_composed_from_the_process_and_evidence_ports()
    {
        using var provider = WithPorts().AddAgentStudioVerification().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<VerificationRunner>(provider.GetRequiredService<IVerificationRunner>());
        Assert.Same(provider.GetRequiredService<IVerificationRunner>(), provider.GetRequiredService<IVerificationRunner>());
    }

    [Fact]
    public void A_runner_registered_earlier_is_kept()
    {
        var services = WithPorts();
        var mine = new VerificationRunner(new ScriptedProcessRunner(), new InMemoryEvidenceStore());
        services.AddSingleton<IVerificationRunner>(mine);

        using var provider = services.AddAgentStudioVerification().BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IVerificationRunner>());
    }
}
