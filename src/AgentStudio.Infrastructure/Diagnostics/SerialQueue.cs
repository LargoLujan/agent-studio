using System.Threading.Channels;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>
/// Runs jobs one at a time, in the order they arrive, with a single consumer of a channel and no lock (the door asks one tool after another:
/// docs/20 D-02, «de una en una»). A job whose caller cancelled before its turn is not started.
/// </summary>
public sealed class SerialQueue : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _jobs = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _consumer;

    public SerialQueue() => _consumer = Task.Run(ConsumeAsync);

    /// <summary>Queues <paramref name="work"/> and returns what it returns; it runs when the ones before it ended.</summary>
    /// <exception cref="InvalidOperationException">The queue was disposed.</exception>
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _jobs.Writer.TryWrite(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                done.SetCanceled(cancellationToken);
                return;
            }

            try
            {
                done.SetResult(await work(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException ex)
            {
                done.SetCanceled(ex.CancellationToken);
            }
#pragma warning disable CA1031 // The failure goes to whoever asked, never to the consumer, which must go on with the next job.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.SetException(ex);
            }
        });
        if (!queued)
        {
            throw new InvalidOperationException("The queue of the door for tools was disposed.");
        }

        return done.Task;
    }

    private async Task ConsumeAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await job().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }
}
