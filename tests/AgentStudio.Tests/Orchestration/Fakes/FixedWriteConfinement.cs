using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>The confinement probe of the CLIs with a fixed answer: <paramref name="confined"/> are the CLIs whose installed version was measured.</summary>
internal sealed class FixedWriteConfinement(params ProviderId[] confined) : IWriteConfinementProbe
{
    public Task<bool> IsWriteConfinedAsync(ProviderId provider, CancellationToken cancellationToken) =>
        Task.FromResult(confined.Contains(provider));
}
