using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Diagnostics;

/// <summary>
/// First slice of Auto Doctor (master prompt, section 12): checks every registered provider tool in parallel.
/// A probe that throws is reported as an error for that provider instead of breaking the whole diagnosis.
/// </summary>
public sealed class ProviderDiagnostics
{
    private readonly IReadOnlyList<IProviderProbe> _probes;

    public ProviderDiagnostics(IEnumerable<IProviderProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToList();
    }

    public async Task<IReadOnlyList<ProviderProbeResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(_probes.Select(p => ProbeSafelyAsync(p, cancellationToken))).ConfigureAwait(false);
        return results;
    }

    private static async Task<ProviderProbeResult> ProbeSafelyAsync(IProviderProbe probe, CancellationToken cancellationToken)
    {
        try
        {
            return await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One faulty probe must not hide the state of the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ProviderProbeResult(
                probe.Provider,
                probe.DisplayName,
                InstallationState.Error,
                ExecutablePath: null,
                Version: null,
                AuthenticationState.Unknown,
                AuthenticationMethod: null,
                [$"La comprobación falló: {ex.Message}"]);
        }
    }

    public IReadOnlyList<ProviderId> Providers => _probes.Select(p => p.Provider).ToList();
}
