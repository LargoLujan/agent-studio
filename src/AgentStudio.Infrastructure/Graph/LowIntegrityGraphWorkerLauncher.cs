using AgentStudio.Application.Execution;
using AgentStudio.Application.Graph;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Graph;

/// <summary>
/// Launches <c>AgentStudio.GraphWorker</c> at low integrity, as the isolated verification launches its steps (ADR-0017, docs/20 D-18): the executable by its
/// absolute path, next to the app, never searched for; the working folder in the isolated area and not in the copy; <c>TEMP</c> in the area; no standard input.
/// It reads the standard output by bytes with its cap and the standard error bounded while the process writes (<see cref="BoundedProcessReaders"/>), kills the
/// whole tree past the cap or after <see cref="GraphWorkerProtocol.MaxRuntime"/>, and returns what it read.
/// </summary>
/// <param name="workerPath">The executable of the process; the one next to the app when null. Only a test gives its own.</param>
public sealed class LowIntegrityGraphWorkerLauncher(string? workerPath = null) : IGraphWorkerLauncher
{
    public const string ExecutableName = "AgentStudio.GraphWorker.exe";

    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public async Task<GraphWorkerLaunch> LaunchAsync(GraphWorkerRequest request, string workingDirectory, string tempDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var executable = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, ExecutableName));
        if (!File.Exists(executable))
        {
            throw new GraphAnalysisException("The analysis process is not next to the app.");
        }

        var spec = new ProcessSpec(
            executable,
            request.ToArguments(),
            workingDirectory,
            new Dictionary<string, string?>
            {
                ["TEMP"] = tempDirectory,
                ["TMP"] = tempDirectory,
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
            Timeout: null,
            StandardInput: null,
            Isolation: ProcessIsolation.LowIntegrity);

        using var process = ProcessLaunch.Start(spec, new ExecutableSearch(roots: null, pathVariable: null));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tooLong = false;
        void OnExceeded()
        {
            tooLong = true;
            process.KillTree();
            linked.Cancel();
        }

        var output = BoundedProcessReaders.ReadOutputAsync(process.StandardOutput, GraphWorkerProtocol.MaxOutputBytes, OnExceeded, linked.Token);
        var error = BoundedProcessReaders.ReadErrorAsync(process.StandardError, BoundedProcessReaders.MaxErrorBytes, OnExceeded, linked.Token);
        linked.CancelAfter(GraphWorkerProtocol.MaxRuntime);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.KillTree();
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            timedOut = !tooLong;
        }

        if (timedOut || tooLong)
        {
            using var grace = new CancellationTokenSource(DrainGrace);
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The tree was killed; a surviving grandchild holding the pipes is the job object's to remove when the process is disposed.
            }

            return new GraphWorkerLaunch(-1, null, null, tooLong, timedOut);
        }

        var summary = await error.ConfigureAwait(false);
        return new GraphWorkerLaunch(process.ExitCode, (await output.ConfigureAwait(false)).Text, summary.LastProgress, false, false);
    }
}
