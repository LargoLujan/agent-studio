using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

/// <summary>
/// One read of the quota of a provider for everyone who asks at the same time (docs/18 P37): the engine, when a limit cut an
/// attempt of Codex and it asks when the window resets, and the screens, once a run of Codex ended, may ask together. The
/// app-server is launched once, and every caller gets its answer; a caller that gives up only stops waiting, it never cancels
/// the read of the others (the reader has its own time limit). A read asked after the last one ended is a new read. The read
/// runs on the thread pool: whoever asks from the UI thread never runs its synchronous part there (the settings it reads, the
/// lookup of the executable, the start of the process).
/// </summary>
public sealed class SharedQuotaReader : IProviderQuotaReader
{
    private readonly Lock _gate = new();
    private Task<QuotaReadResult>? _reading;

    /// <param name="inner">The reader that really reads (the app-server of Codex).</param>
    public SharedQuotaReader(IProviderQuotaReader inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>The reader that really reads.</summary>
    public IProviderQuotaReader Inner { get; }

    public ProviderId Provider => Inner.Provider;

    public Task<QuotaReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        Task<QuotaReadResult> reading;
        lock (_gate)
        {
            if (_reading is not { IsCompleted: false } running)
            {
                running = Task.Run(() => Inner.ReadAsync(CancellationToken.None), CancellationToken.None);
                _reading = running;
            }

            reading = running;
        }

        return reading.WaitAsync(cancellationToken);
    }
}
