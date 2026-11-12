using AgentStudio.Application.Graph;
using AgentStudio.Domain.Graph;

namespace AgentStudio.GraphWorker;

/// <summary>
/// The analysis behind the process (docs/20 D-16 to D-18). WP-G implements it with the readers of <c>.slnx</c>, <c>.sln</c> and <c>.csproj</c> and the
/// syntax parser of Roslyn; the skeleton of the P0 has none (<see cref="UnavailableGraphAnalysis"/>). It opens files only under the root of the
/// request, never follows a link and tells the runner each file it starts on.
/// </summary>
internal interface IGraphAnalysis
{
    /// <param name="request">The root of the copy, its commit and the files to skip.</param>
    /// <param name="fileStarted">Called with the relative path of each file before it is opened: the runner writes it as a progress line, so a crash names its file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="GraphAnalysisUnavailableException">This build of the process has no analysis.</exception>
    Task<GraphBuild> AnalyzeAsync(GraphWorkerRequest request, Action<string> fileStarted, CancellationToken ct);
}

/// <summary>This build of the process cannot analyze anything yet: it ends with <see cref="GraphWorkerExitCodes.AnalysisUnavailable"/>.</summary>
internal sealed class GraphAnalysisUnavailableException(string message) : Exception(message);

/// <summary>
/// The skeleton of the P0: it fixes the protocol and the host contract and analyzes nothing. It never returns a graph, not even an empty one:
/// something that looks like a model would be taken for one (docs/20 §9.2). WP-G replaces it.
/// </summary>
internal sealed class UnavailableGraphAnalysis : IGraphAnalysis
{
    public Task<GraphBuild> AnalyzeAsync(GraphWorkerRequest request, Action<string> fileStarted, CancellationToken ct) =>
        throw new GraphAnalysisUnavailableException("The analysis is not implemented in this build of the process.");
}

/// <summary>
/// What the process does, apart from the real console so it can be driven over in-memory streams (as the tests of the MCP server do): reads the
/// arguments as <see cref="GraphWorkerRequest"/>, checks the root, runs the analysis and writes the model, and only the model, to the standard
/// output. Progress and diagnostics go to the standard error, and a diagnostic never carries a path or text of the repository.
/// </summary>
internal static class GraphWorkerRunner
{
    internal const string Usage =
        $"Usage: AgentStudio.GraphWorker {GraphWorkerProtocol.RootArgument} <absolute path of the copy of the commit> "
        + $"{GraphWorkerProtocol.CommitArgument} <full commit id> [{GraphWorkerProtocol.SkipArgument} <relative path>]...";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter standardOutput,
        TextWriter standardError,
        IGraphAnalysis analysis,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(analysis);

        if (!GraphWorkerRequest.TryParse(args, out var request))
        {
            await standardError.WriteLineAsync(Usage).ConfigureAwait(false);
            return GraphWorkerExitCodes.UsageError;
        }

        if (!Directory.Exists(request.Root))
        {
            await standardError.WriteLineAsync("The root of the copy is not a folder.").ConfigureAwait(false);
            return GraphWorkerExitCodes.RootError;
        }

        try
        {
            var build = await analysis.AnalyzeAsync(request, path => ReportProgress(standardError, path), ct).ConfigureAwait(false);
            if (!string.Equals(build.Graph.Commit, request.Commit, StringComparison.Ordinal))
            {
                await standardError.WriteLineAsync("The analysis described another commit than the one asked.").ConfigureAwait(false);
                return GraphWorkerExitCodes.Failed;
            }

            await standardOutput.WriteAsync(CodeGraphJson.Serialize(build.Graph, build.Files)).ConfigureAwait(false);
            await standardOutput.FlushAsync(ct).ConfigureAwait(false);
            return GraphWorkerExitCodes.Completed;
        }
        catch (GraphAnalysisUnavailableException ex)
        {
            await standardError.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return GraphWorkerExitCodes.AnalysisUnavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The process reports every failure with an exit code and a message of its own: the type of the exception only, never its text, which may hold a path.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await standardError.WriteLineAsync($"The analysis failed: {ex.GetType().Name}.").ConfigureAwait(false);
            return GraphWorkerExitCodes.Failed;
        }
    }

    private static void ReportProgress(TextWriter standardError, string relativePath)
    {
        standardError.WriteLine(GraphWorkerProtocol.ProgressLine(relativePath));
        standardError.Flush();
    }
}
