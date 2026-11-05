using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop.Memory;

/// <summary>A refresh of the index of a project ended: the project, and what the refresh did (or why it stopped).</summary>
public sealed class SemanticIndexRefreshedEventArgs(ProjectContext project, SemanticIndexRefresh refresh) : EventArgs
{
    public ProjectContext Project { get; } = project ?? throw new ArgumentNullException(nameof(project));

    public SemanticIndexRefresh Refresh { get; } = refresh ?? throw new ArgumentNullException(nameof(refresh));
}

/// <summary>
/// Keeps the semantic index of the projects up to date in the background (docs/17 D-04; ADR-0047, decision 2;
/// <see cref="ISemanticIndex.RefreshAsync"/>): after each derivation of the memory, when a project is opened and once the
/// model was downloaded with the person's OK.
/// <list type="bullet">
/// <item><b>Never two at once for a project:</b> each project has a queue with a single consumer (races are solved with a
/// queue before a lock). A request that arrives while a refresh runs waits for it and is served by one more refresh after
/// it, merged with every other request that arrived meanwhile: a burst of derivations costs one more refresh, not one
/// each.</item>
/// <item><b>Out of the UI thread and at low priority, as far as .NET lets it:</b> the consumer runs on the thread pool and
/// nothing waits for it (a search never waits for a refresh; the store of the index serves both in turns). The embedding
/// runs on the two threads of the model (<c>OnnxEmbedderSource.IntraOpThreads</c>), which leave the processor to the
/// interface; the threads themselves are not given a lower priority, because the embedder runs them on the pool.</item>
/// <item><b>It never fails its caller:</b> a refresh that throws ends its requests with a refresh that says why
/// (<see cref="SemanticIndexRefresh.Reason"/>), and so does one stopped by the end of the app. A follower of
/// <see cref="Refreshed"/> that throws is traced and skipped: the others still hear it, and the consumer goes on.</item>
/// <item><b>The model is opened ahead</b> (<see cref="WarmUpAsync"/>) when its files are already in place and verify, so
/// the first search does not pay its load (about 2.5 s); without them nothing is opened and nothing is downloaded (F12-6):
/// only the installer downloads, and only after the person's OK in «Memoria».</item>
/// </list>
/// Without an index (a composition without it) nothing is done.
/// </summary>
public sealed class SemanticIndexUpkeep : IDisposable
{
    /// <summary>Why a refresh did not run: the app is closing.</summary>
    public const string StoppedReason = "La actualización del índice se detuvo al cerrar Agent Studio.";

    private readonly ISemanticIndex? _index;
    private readonly IEmbedderSource? _model;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ProjectQueue> _queues = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly System.Threading.Lock _gate = new();
    private Task<bool>? _warmUp;
    private bool _disposed;

    /// <param name="index">The index of the projects; null when it was not composed (nothing is done).</param>
    /// <param name="model">The local model, opened ahead when its files verify; null: never opened ahead.</param>
    /// <param name="logger">Where a follower of <see cref="Refreshed"/> that failed is traced; null: the debug output only.</param>
    public SemanticIndexUpkeep(ISemanticIndex? index, IEmbedderSource? model, ILogger<SemanticIndexUpkeep>? logger = null)
    {
        _index = index;
        _model = model;
        _logger = logger;
    }

    /// <summary>Whether there is an index to keep.</summary>
    public bool IsAvailable => _index is not null;

    /// <summary>A refresh ended, on a thread of the pool: whoever shows its result goes to the UI thread on their own.</summary>
    public event EventHandler<SemanticIndexRefreshedEventArgs>? Refreshed;

    /// <summary>Why a refresh failed, in Spanish.</summary>
    public static string FailedReason(string problem) =>
        $"No se pudo actualizar el índice semántico ({problem.TrimEnd().TrimEnd('.')}): la búsqueda sigue con lo que ya tenía.";

    /// <summary>
    /// Asks for a refresh of the index of <paramref name="project"/>, in the background. The task ends with the refresh that
    /// served the request (one that may have merged it with others) and never faults; null at once without an index.
    /// </summary>
    public Task<SemanticIndexRefresh?> RefreshAsync(ProjectContext project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_index is not { } index)
        {
            return Task.FromResult<SemanticIndexRefresh?>(null);
        }

        var request = new Request(project, new TaskCompletionSource<SemanticIndexRefresh?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult<SemanticIndexRefresh?>(new SemanticIndexRefresh(0, 0, 0, 0, StoppedReason));
            }

            var queue = _queues.GetOrAdd(project.ProjectId, _ => new ProjectQueue());
            queue.Requests.Writer.TryWrite(request); // unbounded: it always takes it
            queue.Consumer ??= Task.Run(() => ConsumeAsync(index, queue, _stopping.Token));
        }

        return request.Done.Task;
    }

    /// <summary>
    /// Opens the local model in the background when its files are already in place and verify (F12-6: it never downloads),
    /// so the first search does not pay its load. True when the model is open; false without it, or when it does not load.
    /// One opening at a time; once the model is open, the same answer.
    /// </summary>
    public Task<bool> WarmUpAsync()
    {
        if (_model is not { } model || _index is null)
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(false);
            }

            // A warm-up going on, or one that opened the model, is the answer; a failed one is tried again (the person may
            // have downloaded the model since).
            if (_warmUp is { } going && (!going.IsCompleted || going is { IsCompletedSuccessfully: true, Result: true }))
            {
                return going;
            }

            var stopping = _stopping.Token;
            return _warmUp = Task.Run(() => OpenAsync(model, stopping));
        }
    }

    /// <summary>
    /// Follows the «Memoria» document: a download that ended with the model in place refreshes the index of the project that
    /// was open (or opens the model, without one), and every refresh that ends reads again the state of the model and of the
    /// index on the screen (<see cref="MemoryViewModel.IndexRefreshedAsync"/>).
    /// </summary>
    public void Follow(MemoryViewModel memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        memory.ModelInstalled += (_, e) =>
        {
            if (e.Project is { } project)
            {
                _ = RefreshAsync(project);
            }
            else
            {
                _ = WarmUpAsync();
            }
        };
        Refreshed += (_, _) => Observe(memory.IndexRefreshedAsync());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // The consumers stop at their next await and end what still waits with StoppedReason; nobody waits for them here,
        // so closing the app is never held by a long first embedding.
        _stopping.Cancel();
    }

    private static async Task<bool> OpenAsync(IEmbedderSource model, CancellationToken stopping)
    {
        try
        {
            if (!model.Check().IsReady)
            {
                return false;
            }

            var opening = await model.OpenAsync(stopping).ConfigureAwait(false);
            return opening.Embedder is not null;
        }
#pragma warning disable CA1031 // Opening ahead is a help, never a condition: the first search opens the model, or says why not.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>The single consumer of one project: everything waiting becomes one refresh, then it waits for more.</summary>
    private async Task ConsumeAsync(ISemanticIndex index, ProjectQueue queue, CancellationToken stopping)
    {
        var reader = queue.Requests.Reader;
        var stopped = new SemanticIndexRefresh(0, 0, 0, 0, StoppedReason);
        try
        {
            while (await reader.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                var batch = new List<Request>();
                while (reader.TryRead(out var request))
                {
                    batch.Add(request);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                // The newest context of the project: the requests of one queue are all of the same project.
                var project = batch[^1].Project;
                SemanticIndexRefresh refresh;
                try
                {
                    refresh = await index.RefreshAsync(project, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    refresh = stopped;
                }
#pragma warning disable CA1031 // The index is behind a port: a failure ends the requests with its reason, never the consumer.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    refresh = new SemanticIndexRefresh(0, 0, 0, 0, FailedReason(ex.Message));
                }

                batch.ForEach(r => r.Done.TrySetResult(refresh));
                if (!stopping.IsCancellationRequested)
                {
                    Announce(new SemanticIndexRefreshedEventArgs(project, refresh));
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The app is closing.
        }

        while (reader.TryRead(out var left))
        {
            left.Done.TrySetResult(stopped);
        }
    }

    /// <summary>
    /// Tells every follower of <see cref="Refreshed"/> that a refresh ended, one by one: one that throws is traced and skipped,
    /// so the others still hear it and the consumer of the project goes on with the next requests.
    /// </summary>
    private void Announce(SemanticIndexRefreshedEventArgs refreshed)
    {
        foreach (var follower in Refreshed?.GetInvocationList().OfType<EventHandler<SemanticIndexRefreshedEventArgs>>() ?? [])
        {
            try
            {
                follower(this, refreshed);
            }
#pragma warning disable CA1031 // A follower only shows the result: its failure must never stop the refreshes of the project.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Debug.WriteLine($"Semantic index upkeep: a follower of the refresh failed: {ex.Message}");
                _logger?.LogWarning(ex, "A follower of the refresh of the semantic index of project {ProjectId} failed.", refreshed.Project.ProjectId);
            }
        }
    }

    /// <summary>Lets a task of the screen fail without its exception going unobserved: the screen shows its own errors.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed record Request(ProjectContext Project, TaskCompletionSource<SemanticIndexRefresh?> Done);

    private sealed class ProjectQueue
    {
        public Channel<Request> Requests { get; } = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>The single consumer, started with the first request; written under the gate of the upkeep.</summary>
        public Task? Consumer { get; set; }
    }
}
