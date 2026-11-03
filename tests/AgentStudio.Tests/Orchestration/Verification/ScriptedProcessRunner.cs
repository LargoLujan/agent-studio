using System.Threading.Channels;
using AgentStudio.Application.Execution;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>What a scripted process does once it is started.</summary>
/// <param name="ExitCode">The exit code, unless the process hangs (then it only ends when killed, with -1).</param>
/// <param name="Lines">The output lines, delivered in this order.</param>
/// <param name="Hang">The process keeps running until it is killed or disposed.</param>
/// <param name="OnStart">A side effect at start, such as writing a test report where the real tool would.</param>
/// <param name="StartFailure">Thrown by <see cref="IStreamingProcessRunner.StartAsync"/> instead of starting.</param>
internal sealed record ProcessScript(
    int ExitCode = 0,
    IReadOnlyList<ProcessOutputLine>? Lines = null,
    bool Hang = false,
    Action<ProcessSpec>? OnStart = null,
    Exception? StartFailure = null)
{
    public static ProcessScript Exit(int exitCode, params string[] standardOutput) =>
        new(exitCode, standardOutput.Select(Out).ToList());

    public static ProcessScript Hanging(params string[] standardOutput) =>
        new(Lines: standardOutput.Select(Out).ToList(), Hang: true);

    public static ProcessOutputLine Out(string text) => new(ProcessStream.StandardOutput, text, DateTimeOffset.UtcNow);

    public static ProcessOutputLine Err(string text) => new(ProcessStream.StandardError, text, DateTimeOffset.UtcNow);
}

/// <summary>
/// An <see cref="IStreamingProcessRunner"/> that starts nothing: each start takes the next <see cref="ProcessScript"/>
/// (or asks a function for one) and records the <see cref="ProcessSpec"/> it was given. Its sessions behave like the
/// real ones: after a kill the exit code is -1, and disposing a session that still runs kills it.
/// </summary>
internal sealed class ScriptedProcessRunner : IStreamingProcessRunner
{
    private readonly Func<ProcessSpec, ProcessScript> _next;
    private readonly List<ProcessSpec> _started = [];
    private readonly List<ScriptedSession> _sessions = [];
    private readonly TaskCompletionSource _firstStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();

    public ScriptedProcessRunner(Func<ProcessSpec, ProcessScript> next) => _next = next;

    public ScriptedProcessRunner(params ProcessScript[] scripts)
    {
        var queue = new Queue<ProcessScript>(scripts);
        _next = _ => queue.Count > 0 ? queue.Dequeue() : throw new InvalidOperationException("No script left for another process.");
    }

    public IReadOnlyList<ProcessSpec> Started
    {
        get
        {
            lock (_gate)
            {
                return [.. _started];
            }
        }
    }

    public IReadOnlyList<ScriptedSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>Completes when the first process has been started (or failed to start).</summary>
    public Task FirstStart => _firstStart.Task;

    public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var script = _next(spec);
        lock (_gate)
        {
            _started.Add(spec);
        }

        try
        {
            if (script.StartFailure is not null)
            {
                throw script.StartFailure;
            }

            script.OnStart?.Invoke(spec);
            var session = new ScriptedSession(script);
            lock (_gate)
            {
                _sessions.Add(session);
            }

            return Task.FromResult<IProcessSession>(session);
        }
        finally
        {
            _firstStart.TrySetResult();
        }
    }
}

internal sealed class ScriptedSession : IProcessSession
{
    private readonly Channel<ProcessOutputLine> _channel = Channel.CreateUnbounded<ProcessOutputLine>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedSession(ProcessScript script)
    {
        foreach (var line in script.Lines ?? [])
        {
            _channel.Writer.TryWrite(line);
        }

        if (!script.Hang)
        {
            _channel.Writer.TryComplete();
            _exited.TrySetResult(script.ExitCode);
        }
    }

    public int ProcessId => 4242;

    public IAsyncEnumerable<ProcessOutputLine> Output => _channel.Reader.ReadAllAsync();

    public Task<int> Exited => _exited.Task;

    /// <summary>True when the tree was killed while the process was still running.</summary>
    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public Task KillAsync()
    {
        Kill();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Kill();
        return ValueTask.CompletedTask;
    }

    private void Kill()
    {
        if (!_exited.Task.IsCompleted)
        {
            Killed = true;
        }

        _channel.Writer.TryComplete();
        _exited.TrySetResult(-1);
    }
}
