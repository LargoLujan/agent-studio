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

    private Task<IReadOnlyList<ProviderProbeResult>>? _inFlight;
    private IReadOnlyList<ProviderProbeResult>? _last;
    private long _lastAt;

    /// <summary>
    /// Runs every probe. A call that arrives while another run is in progress joins it instead of launching every program again (the summary of the top bar and the panel of
    /// Auto Doctor ask at start-up at the same time). With <paramref name="reuseWithin"/>, results that are younger than that are given back as they are.
    /// </summary>
    public Task<IReadOnlyList<ProviderProbeResult>> RunAsync(CancellationToken cancellationToken) => RunAsync(TimeSpan.Zero, cancellationToken);

    public async Task<IReadOnlyList<ProviderProbeResult>> RunAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
    {
        if (reuseWithin > TimeSpan.Zero && _last is { } last && Environment.TickCount64 - Volatile.Read(ref _lastAt) < reuseWithin.TotalMilliseconds)
        {
            return last;
        }

        var current = Volatile.Read(ref _inFlight);
        if (current is null)
        {
            // The shared run does not depend on the token of whoever started it: each probe has its own time limit, and the others may have joined.
            var started = new TaskCompletionSource<IReadOnlyList<ProviderProbeResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            current = Interlocked.CompareExchange(ref _inFlight, started.Task, null);
            if (current is null)
            {
                _ = RunAllAsync(started);
                current = started.Task;
            }
        }

        return await current.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAllAsync(TaskCompletionSource<IReadOnlyList<ProviderProbeResult>> started)
    {
        try
        {
            var results = await Task.WhenAll(_probes.Select(p => ProbeSafelyAsync(p, CancellationToken.None))).ConfigureAwait(false);
            _last = results;
            Volatile.Write(ref _lastAt, Environment.TickCount64);
            started.SetResult(results);
        }
#pragma warning disable CA1031 // Whoever joined gets the failure; the next run starts clean.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            started.SetException(ex);
        }
        finally
        {
            Volatile.Write(ref _inFlight, null);
        }
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
