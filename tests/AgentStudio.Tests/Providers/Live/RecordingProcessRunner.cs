using System.Runtime.CompilerServices;
using AgentStudio.Application.Execution;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>
/// Test decorator over a real <see cref="IStreamingProcessRunner"/>. It changes nothing about what is launched; it records
/// the <see cref="ProcessSpec"/>, every output line exactly as the adapter receives it, and when each process was
/// requested and observed to exit. A live test can then inspect the raw stream (for example the <c>system/init</c> line)
/// and prove whether two CLI processes overlapped, without trusting the adapter's own bookkeeping.
/// </summary>
internal sealed class RecordingProcessRunner(IStreamingProcessRunner inner) : IStreamingProcessRunner
{
    private readonly Lock _gate = new();
    private readonly List<RecordedProcess> _processes = [];

    /// <summary>The processes started through this runner, in start order.</summary>
    public IReadOnlyList<RecordedProcess> Processes
    {
        get
        {
            lock (_gate)
            {
                return [.. _processes];
            }
        }
    }

    public async Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        // Conservative on purpose: the request time is taken BEFORE launching and the exit time AFTER the exit is
        // observed, so "next.RequestedAt >= previous.ExitedAt" is stricter than "the processes never overlapped".
        var requestedAt = DateTimeOffset.UtcNow;
        var session = await inner.StartAsync(spec, cancellationToken).ConfigureAwait(false);
        var record = new RecordedProcess(spec, session.ProcessId, requestedAt, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _processes.Add(record);
        }

        return new RecordingSession(session, record);
    }

    private sealed class RecordingSession : IProcessSession
    {
        private readonly IProcessSession _inner;
        private readonly RecordedProcess _record;

        public RecordingSession(IProcessSession inner, RecordedProcess record)
        {
            _inner = inner;
            _record = record;
            Exited = ObserveExitAsync();
        }

        public int ProcessId => _inner.ProcessId;

        public IAsyncEnumerable<ProcessOutputLine> Output => RecordAsync();

        // The adapter only sees the exit after the timestamp has been taken.
        public Task<int> Exited { get; }

        public Task KillAsync() => _inner.KillAsync();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private async IAsyncEnumerable<ProcessOutputLine> RecordAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var line in _inner.Output.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                _record.AddLine(line);
                yield return line;
            }
        }

        private async Task<int> ObserveExitAsync()
        {
            var exitCode = await _inner.Exited.ConfigureAwait(false);
            _record.MarkExited(exitCode, DateTimeOffset.UtcNow);
            return exitCode;
        }
    }
}

/// <summary>One process observed by <see cref="RecordingProcessRunner"/>.</summary>
internal sealed class RecordedProcess(ProcessSpec spec, int processId, DateTimeOffset requestedAt, DateTimeOffset startedAt)
{
    private readonly Lock _gate = new();
    private readonly List<ProcessOutputLine> _lines = [];
    private DateTimeOffset? _exitedAt;
    private int? _exitCode;

    public ProcessSpec Spec { get; } = spec;

    public int ProcessId { get; } = processId;

    /// <summary>Taken just before the launch was requested, so never later than the real spawn.</summary>
    public DateTimeOffset RequestedAt { get; } = requestedAt;

    /// <summary>Taken right after the launch returned.</summary>
    public DateTimeOffset StartedAt { get; } = startedAt;

    /// <summary>Taken when the runner reported the exit (never earlier than the real exit); null while it runs.</summary>
    public DateTimeOffset? ExitedAt
    {
        get
        {
            lock (_gate)
            {
                return _exitedAt;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _exitCode;
            }
        }
    }

    public IReadOnlyList<string> StandardOutput => LinesOf(ProcessStream.StandardOutput);

    public IReadOnlyList<string> StandardError => LinesOf(ProcessStream.StandardError);

    /// <summary>True when the two processes were alive at the same time (a process that has not exited counts as alive).</summary>
    public bool OverlapsWith(RecordedProcess other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var thisEnd = ExitedAt ?? DateTimeOffset.MaxValue;
        var otherEnd = other.ExitedAt ?? DateTimeOffset.MaxValue;
        return RequestedAt < otherEnd && other.RequestedAt < thisEnd;
    }

    internal void AddLine(ProcessOutputLine line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    internal void MarkExited(int exitCode, DateTimeOffset at)
    {
        lock (_gate)
        {
            _exitCode = exitCode;
            _exitedAt = at;
        }
    }

    private List<string> LinesOf(ProcessStream stream)
    {
        lock (_gate)
        {
            return [.. _lines.Where(l => l.Stream == stream).Select(l => l.Text)];
        }
    }
}
