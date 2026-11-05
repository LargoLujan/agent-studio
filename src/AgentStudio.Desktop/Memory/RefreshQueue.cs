using System.Threading.Channels;

namespace AgentStudio.Desktop.Memory;

/// <summary>What one refresh of «Memoria» reads again. Each is read only when it may have changed, never on every action.</summary>
[Flags]
public enum MemoryRefresh
{
    None = 0,

    /// <summary>
    /// The state of the model and of the index of the project. It checks the files of the model (their SHA-256), so it is
    /// read when the project opens, after a download and after a refresh of the index; never on a search or a review.
    /// </summary>
    Status = 1,

    /// <summary>The trust of the journal and its days (it reads the journal entries): when the project opens, after a decision about the journal and when the memory changed.</summary>
    Journal = 2,

    /// <summary>The list or the search on screen.</summary>
    List = 4,

    All = Status | Journal | List,
}

/// <summary>
/// The refreshes of «Memoria», one at a time: a queue with a single consumer (races are solved with a queue before a lock).
/// A request that arrives while a refresh runs waits for it, and is served by one more refresh after it, merged with every
/// other request that arrived meanwhile (what they read together): two refreshes never interleave, and a burst of requests
/// costs one more refresh, not one each. Each request gets a task that ends with the refresh that served it, or with its
/// error. The consumer runs on <c>ui</c> when it is given (a refresh changes what the screen binds to), or on the context
/// of the first request.
/// </summary>
internal sealed class RefreshQueue
{
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<MemoryRefresh, Task> _refresh;
    private readonly SynchronizationContext? _ui;
    private int _started;

    /// <param name="refresh">One refresh of what the merged requests ask for.</param>
    /// <param name="ui">The context of the UI thread, where the consumer runs; null takes the one of the first request.</param>
    public RefreshQueue(Func<MemoryRefresh, Task> refresh, SynchronizationContext? ui)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        _refresh = refresh;
        _ui = ui;
    }

    /// <summary>Asks for a refresh of <paramref name="scope"/>; the task ends when a refresh that covered it ends.</summary>
    public Task RequestAsync(MemoryRefresh scope)
    {
        var request = new Request(scope, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _requests.Writer.TryWrite(request); // unbounded: it always takes it
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            if (_ui is { } ui && !ReferenceEquals(SynchronizationContext.Current, ui))
            {
                ui.Post(static state => _ = ((RefreshQueue)state!).ConsumeAsync(), this);
            }
            else
            {
                _ = ConsumeAsync();
            }
        }

        return request.Done.Task;
    }

    /// <summary>The single consumer: everything waiting becomes one refresh, then it waits for more.</summary>
    private async Task ConsumeAsync()
    {
        while (await _requests.Reader.WaitToReadAsync().ConfigureAwait(true))
        {
            var batch = new List<Request>();
            var scope = MemoryRefresh.None;
            while (_requests.Reader.TryRead(out var request))
            {
                batch.Add(request);
                scope |= request.Scope;
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                await _refresh(scope).ConfigureAwait(true);
                batch.ForEach(r => r.Done.TrySetResult());
            }
#pragma warning disable CA1031 // The error of a refresh goes to every request it served; the consumer goes on with the next ones.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                batch.ForEach(r => r.Done.TrySetException(ex));
            }
        }
    }

    private sealed record Request(MemoryRefresh Scope, TaskCompletionSource Done);
}
