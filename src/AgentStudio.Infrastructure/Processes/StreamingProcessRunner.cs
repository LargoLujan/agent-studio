using System.Text;
using System.Threading.Channels;
using AgentStudio.Application.Execution;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>
/// Starts processes whose output is consumed line by line. Same start rules as <see cref="ProcessRunner"/>, including
/// <see cref="ProcessSpec.Isolation"/>: an isolated process streams exactly like one without isolation.
/// A line longer than the limit (8 M characters by default) is discarded without buffering it and replaced by a
/// marker line on the standard error stream, so a runaway output cannot exhaust memory.
/// </summary>
/// <param name="maxLineLength">Longest line, in characters, that is delivered.</param>
/// <param name="roots">The folders an executable named without a path is never resolved from (see <see cref="ExecutableSearch"/>).</param>
/// <param name="pathVariable">The PATH to resolve those names on; the one of the process when null.</param>
public sealed class StreamingProcessRunner(
    int maxLineLength = StreamingProcessRunner.DefaultMaxLineLength,
    IUntrustedRoots? roots = null,
    Func<string?>? pathVariable = null) : IStreamingProcessRunner
{
    /// <summary>Default line limit: 8 M characters.</summary>
    public const int DefaultMaxLineLength = 8 * 1024 * 1024;

    private readonly int _maxLineLength = maxLineLength > 0 ? maxLineLength : throw new ArgumentOutOfRangeException(nameof(maxLineLength));
    private readonly ExecutableSearch _search = new(roots, pathVariable);

    public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // The token only governs the start; once the process runs, KillAsync/DisposeAsync/Timeout end it.
        cancellationToken.ThrowIfCancellationRequested();
        var process = ProcessLaunch.Start(spec, _search);
        return Task.FromResult<IProcessSession>(new ProcessSession(process, spec, _maxLineLength));
    }

    private sealed class ProcessSession : IProcessSession
    {
        private const char Cr = (char)13;
        private const char Lf = (char)10;

        private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

        private readonly IStartedProcess _process;
        private readonly Channel<ProcessOutputLine> _channel;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<int> _exited;
        private readonly int _maxLineLength;
        private int _killRequested;
        private int _disposed;

        public ProcessSession(IStartedProcess process, ProcessSpec spec, int maxLineLength)
        {
            _maxLineLength = maxLineLength;
            _process = process;
            ProcessId = process.Id;

            // Unbounded on purpose: a slow consumer must never block the process or the pipe readers (a blocked
            // reader would stall the child on a full pipe). Memory grows with the unread backlog only.
            _channel = Channel.CreateUnbounded<ProcessOutputLine>(new UnboundedChannelOptions { SingleReader = true });

            var pumps = new[]
            {
                PumpAsync(process.StandardOutput, ProcessStream.StandardOutput),
                PumpAsync(process.StandardError, ProcessStream.StandardError),
            };
            // Kept open when asked (ProcessSpec.KeepStandardInputOpen): it closes with the process when the session ends it.
            var stdin = spec.StandardInput is null ? Task.CompletedTask : ProcessLaunch.WriteStandardInputAsync(process, spec.StandardInput, close: !spec.KeepStandardInputOpen);

            if (spec.Timeout is { } timeout)
            {
                _stop.CancelAfter(timeout);
            }

            _exited = MonitorAsync(pumps, stdin);
        }

        public int ProcessId { get; }

        public IAsyncEnumerable<ProcessOutputLine> Output => _channel.Reader.ReadAllAsync();

        public Task<int> Exited => _exited;

        public async Task KillAsync()
        {
            Kill();
            await WaitExitedAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Kill();
            await WaitExitedAsync().ConfigureAwait(false);
            _stop.Dispose();
            _process.Dispose();
        }

        private void Kill()
        {
            if (!_process.HasExited)
            {
                Interlocked.Exchange(ref _killRequested, 1);
            }

            _process.KillTree();
        }

        // Bounded wait: a surviving grandchild holding the pipes must not hang callers.
        private async Task WaitExitedAsync() =>
            await Task.WhenAny(_exited, Task.Delay(KillWait)).ConfigureAwait(false);

        private async Task<int> MonitorAsync(Task[] pumps, Task stdin)
        {
            try
            {
                try
                {
                    await _process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Timeout elapsed.
                    Kill();
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                // A grandchild can keep the pipes open after the process exits: do not wait for them forever.
                try
                {
                    await Task.WhenAll(pumps.Append(stdin)).WaitAsync(DrainGrace).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }

                return Volatile.Read(ref _killRequested) != 0 ? -1 : _process.ExitCode;
            }
            finally
            {
                _channel.Writer.TryComplete();
            }
        }

        private async Task PumpAsync(StreamReader reader, ProcessStream stream)
        {
            try
            {
                var buffer = new char[8192];
                var line = new StringBuilder();
                long discarded = 0;
                var overflow = false;
                var skipLineFeed = false;

                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    var chunk = buffer.AsMemory(0, read);
                    while (chunk.Length > 0)
                    {
                        if (skipLineFeed)
                        {
                            skipLineFeed = false;
                            if (chunk.Span[0] == Lf)
                            {
                                chunk = chunk[1..];
                                continue;
                            }
                        }

                        var end = chunk.Span.IndexOfAny(Cr, Lf);
                        var piece = end < 0 ? chunk : chunk[..end];
                        if (overflow)
                        {
                            discarded += piece.Length;
                        }
                        else if (line.Length + piece.Length > _maxLineLength)
                        {
                            // Drop what was buffered: only a marker is delivered for an oversized line.
                            overflow = true;
                            discarded = line.Length + piece.Length;
                            line.Clear();
                            line.Capacity = 16;
                        }
                        else
                        {
                            line.Append(piece.Span);
                        }

                        if (end < 0)
                        {
                            break;
                        }

                        skipLineFeed = chunk.Span[end] == Cr;
                        chunk = chunk[(end + 1)..];
                        Emit(stream, line, ref overflow, ref discarded);
                    }
                }

                if (line.Length > 0 || overflow)
                {
                    Emit(stream, line, ref overflow, ref discarded);
                }
            }
            catch (IOException)
            {
                // The pipe broke because the tree was killed.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Emit(ProcessStream stream, StringBuilder line, ref bool overflow, ref long discarded)
        {
            if (overflow)
            {
                var marker = $"[agent-studio] A {(stream == ProcessStream.StandardOutput ? "stdout" : "stderr")} line longer than {_maxLineLength} characters was discarded ({discarded} characters).";
                _channel.Writer.TryWrite(new ProcessOutputLine(ProcessStream.StandardError, marker, DateTimeOffset.UtcNow));
                overflow = false;
                discarded = 0;
            }
            else
            {
                _channel.Writer.TryWrite(new ProcessOutputLine(stream, line.ToString(), DateTimeOffset.UtcNow));
            }

            line.Clear();
        }
    }
}
