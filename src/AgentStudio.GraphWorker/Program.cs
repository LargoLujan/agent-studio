using System.Text;

namespace AgentStudio.GraphWorker;

/// <summary>
/// Entry point of the analysis process of Auto Graph (docs/20 D-18, ADR-0062). The host launches it at low integrity over the copy of a commit
/// (<see cref="AgentStudio.Application.Graph.GraphWorkerProtocol"/>) and reads its standard output, which is the model or nothing; progress and
/// diagnostics go to the standard error. There is no host: no configuration file, no environment section, no logger and no network.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // UTF-8 without a byte-order mark on both streams, whatever the code page of the console: the host reads the model as UTF-8.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };
        await using var error = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await GraphWorkerRunner.RunAsync(args, output, error, new SolutionGraphAnalysis(), cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AgentStudio.Application.Graph.GraphWorkerExitCodes.Failed;
        }
    }
}
