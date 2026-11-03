using System.Collections.Concurrent;
using AgentStudio.Application.Projects;
using AgentStudio.Observability.Context;

namespace AgentStudio.Desktop.Observability;

/// <summary>
/// The context windows each project learned (docs/14 D-10, P36), with the base table of this version under them
/// (<see cref="ModelWindowTable"/>): what the monitors judge the context of an attempt with while its CLI has not reported
/// the window. Each project's log is read through <see cref="IProjectLogReader"/> (read only, without the lock, a link
/// refused), kept per project and read again after a run, whose attempts may have reported new windows.
/// <list type="bullet">
/// <item>The read never runs on the thread that asks (the UI): the reader checks the folders, opens SQLite and verifies
/// the whole chain before its first real asynchronous step, so it goes to the thread pool.</item>
/// <item>One read at a time per project: whoever asks while one is going waits for it and then shares the one read that
/// follows it, which sees what was written meanwhile; no burst of reads of the same log.</item>
/// <item>Until a project's log is read, the base table alone. A read that fails, or a log that is not trusted (it does
/// not verify, or no anchor recognises it), leaves what was known: it never takes away what an earlier read learned.</item>
/// </list>
/// </summary>
public sealed class ContextWindowCatalogs
{
    private readonly IProjectLogReader? _logs;
    private readonly ConcurrentDictionary<string, ModelWindowCatalog> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastStarted = new(StringComparer.OrdinalIgnoreCase);
    private long _requests;

    /// <param name="logs">Reads the log of a project; without it every project has the base table alone.</param>
    public ContextWindowCatalogs(IProjectLogReader? logs)
    {
        _logs = logs;
    }

    /// <summary>The windows of the project known now: the last read, or the base table alone.</summary>
    public ModelWindowCatalog Current(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return _known.TryGetValue(Key(projectRoot), out var catalog) ? catalog : ModelWindowCatalog.Baseline;
    }

    /// <summary>
    /// Reads the log of the project again, on the thread pool, and keeps its windows with the base table under them: the
    /// windows of a read that started after this request, which may be one asked by someone else meanwhile. A log that
    /// cannot be read, or is not trusted, leaves what was known (the base table, the first time): the bars never wait for it.
    /// </summary>
    public async Task<ModelWindowCatalog> RefreshAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (_logs is not { } logs)
        {
            return ModelWindowCatalog.Baseline;
        }

        var key = Key(projectRoot);
        var request = Interlocked.Increment(ref _requests);
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A read that started after this request has already been made: what it learned is as fresh as this asks.
            if (_lastStarted.TryGetValue(key, out var started) && started >= request)
            {
                return Current(projectRoot);
            }

            _lastStarted[key] = Interlocked.Read(ref _requests);
            return await Task.Run(() => ReadAsync(logs, key, projectRoot, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ModelWindowCatalog> ReadAsync(IProjectLogReader logs, string key, string projectRoot, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await logs.ReadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsTrusted)
            {
                // A log that does not verify teaches nothing, and takes away nothing either.
                return Current(projectRoot);
            }

            var catalog = ModelWindowCatalog.FromLog(snapshot).WithBaseline();
            _known[key] = catalog;
            return catalog;
        }
#pragma warning disable CA1031 // The log is behind a port: whatever it throws leaves the windows known, and the bars never fail for it.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Current(projectRoot);
        }
    }

    private static string Key(string projectRoot) => projectRoot.TrimEnd('\\', '/');
}
