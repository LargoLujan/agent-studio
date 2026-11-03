using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory;
using AgentStudio.Memory.Derivation;
using AgentStudio.Security;
using AgentStudio.Tests.Infrastructure.Persistence;
using AgentStudio.Tests.Memory.Store;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// The derivation as the composition root registers it (<see cref="DependencyInjection.AddAgentStudioProjectMemory"/>):
/// behind <see cref="IMemoryDerivation"/>, over the persistence, the memory and the activity of the orchestrator.
/// </summary>
public sealed class MemoryDerivationCompositionTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_derivation_is_one_service_behind_its_port_and_derives_a_real_project()
    {
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(_anchors));
        services.AddAgentStudioSecurity();
        services.AddAgentStudioProjectMemory();
        services.AddSingleton<IRunActivity>(new FakeRunActivity());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);

        var derivation = provider.GetRequiredService<IMemoryDerivation>();
        var result = await derivation.DeriveAsync(project, Ct);

        Assert.IsType<MemoryDerivation>(derivation);
        Assert.Same(derivation, provider.GetRequiredService<IMemoryDerivation>());
        Assert.True(result.Completed);
        Assert.Empty(result.Recorded);
    }
}
