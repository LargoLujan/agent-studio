using System.Diagnostics;
using System.Text;
using AgentStudio.Application.Execution;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>
/// Runs real OS processes. Arguments always go through <see cref="ProcessStartInfo.ArgumentList"/>, or through the same
/// quoting rules for an isolated process (<see cref="ProcessSpec.Isolation"/>).
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.KeepStandardInputOpen)
        {
            throw new ArgumentException("Standard input can be kept open only in a streaming session: here the process would wait for its timeout.", nameof(spec));
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = ProcessLaunch.Start(spec);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var pumps = new[]
        {
            PumpAsync(process.StandardOutput, stdout),
            PumpAsync(process.StandardError, stderr),
        };
        var stdin = spec.StandardInput is null ? Task.CompletedTask : ProcessLaunch.WriteStandardInputAsync(process, spec.StandardInput);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (spec.Timeout is { } timeout)
        {
            linked.CancelAfter(timeout);
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // A grandchild can keep the pipes open after the process exits, so draining must honour the timeout too.
            await Task.WhenAll(pumps).WaitAsync(linked.Token).ConfigureAwait(false);
            await stdin.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.KillTree();
            await WaitAfterKillAsync(process, pumps, stdin).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }
#pragma warning disable CA1031 // The process tree is killed and the exception is rethrown.
        catch
#pragma warning restore CA1031
        {
            process.KillTree();
            throw;
        }

        stopwatch.Stop();
        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            Snapshot(stdout),
            Snapshot(stderr),
            stopwatch.Elapsed,
            timedOut);
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder target)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            lock (target)
            {
                target.Append(buffer, 0, read);
            }
        }
    }

    private static async Task WaitAfterKillAsync(IStartedProcess process, Task[] pumps, Task stdin)
    {
        using var grace = new CancellationTokenSource(DrainGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            await Task.WhenAll(pumps.Append(stdin)).WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A surviving grandchild still holds the pipes: return what was captured.
        }
    }

    private static string Snapshot(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString();
        }
    }
}
