using System.Globalization;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;

namespace AgentStudio.Infrastructure.Graph;

/// <summary>How one launch of the analysis process went (docs/20 D-18).</summary>
/// <param name="ExitCode">The exit code of the process; any code that is not one of <see cref="GraphWorkerExitCodes"/> is a crash.</param>
/// <param name="Output">The standard output, read up to its cap; null when the cap was passed or the process was killed.</param>
/// <param name="LastProgress">The last file the process said it started on, when it was a safe path.</param>
/// <param name="OutputTooLong">The standard output or error passed its cap and the process was killed.</param>
/// <param name="TimedOut">The process ran past <see cref="GraphWorkerProtocol.MaxRuntime"/> and was killed.</param>
public sealed record GraphWorkerLaunch(int ExitCode, string? Output, string? LastProgress, bool OutputTooLong, bool TimedOut);

/// <summary>Launches the analysis process once (docs/20 D-18). The real one starts it at low integrity; the tests of the loop that relaunches it give their own.</summary>
public interface IGraphWorkerLauncher
{
    /// <param name="request">The arguments: the root of the copy, its commit and the files to skip.</param>
    /// <param name="workingDirectory">A folder of the isolated area, not the copy.</param>
    /// <param name="tempDirectory">The temporary folder of the area (<c>TEMP</c> and <c>TMP</c>).</param>
    Task<GraphWorkerLaunch> LaunchAsync(GraphWorkerRequest request, string workingDirectory, string tempDirectory, CancellationToken ct);
}

/// <summary>
/// The host of the analysis process (docs/20 D-18, ADR-0062): launches it over the copy of a commit and reads what it says as data that is not trusted. If the
/// process dies (a stack that is exhausted cannot be caught) it names the file it was on in its standard error, and the host launches it again skipping that
/// file, up to <see cref="GraphWorkerProtocol.MaxRelaunches"/> times; the file is then a <c>graph.file-skipped</c> gap and its project partial. A process that
/// ends with one of its own codes, runs too long or writes too much is a failed analysis: it is not relaunched.
/// </summary>
public sealed class GraphWorkerHost(IGraphWorkerLauncher launcher) : ICodeGraphBuilder
{
    public async Task<GraphBuild> BuildAsync(IRepositorySnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var area = Path.GetDirectoryName(snapshot.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? snapshot.Root;
        var temp = Path.Combine(area, "tmp");
        if (!Directory.Exists(temp))
        {
            temp = area;
        }

        var skipped = new List<string>();
        var withoutSources = false;
        for (var launchNumber = 0; ; launchNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var request = new GraphWorkerRequest(snapshot.Root, snapshot.Commit, skipped, withoutSources);
            var launch = await launcher.LaunchAsync(request, area, temp, ct).ConfigureAwait(false);

            if (launch.TimedOut)
            {
                throw new GraphAnalysisException("The analysis ran longer than it is allowed to.");
            }

            if (launch.OutputTooLong)
            {
                throw new GraphAnalysisException("The analysis wrote more than it is allowed to.");
            }

            // A launch that ends well is kept, however many launches it took to get there.
            if (launch.ExitCode == GraphWorkerExitCodes.Completed)
            {
                return Parse(launch.Output, snapshot.Commit);
            }

            if (launch.ExitCode is GraphWorkerExitCodes.UsageError or GraphWorkerExitCodes.RootError or GraphWorkerExitCodes.AnalysisUnavailable or GraphWorkerExitCodes.Failed)
            {
                throw new GraphAnalysisException(string.Create(CultureInfo.InvariantCulture, $"The analysis ended with its code {launch.ExitCode}."));
            }

            // Any other end is a crash. The file it was on is skipped and the process is launched again, up to MaxRelaunches times.
            var canSkip = launchNumber < GraphWorkerProtocol.MaxRelaunches && launch.LastProgress is not null
                && !skipped.Contains(launch.LastProgress, StringComparer.OrdinalIgnoreCase) && skipped.Count < GraphWorkerProtocol.MaxSkipped;
            if (canSkip)
            {
                skipped.Add(launch.LastProgress!);
                continue;
            }

            // Nothing more can be skipped: one last launch reads no source file at all, so the graph is partial with every project marked.
            if (withoutSources)
            {
                throw new GraphAnalysisException(string.Create(CultureInfo.InvariantCulture, $"The analysis process died with {launchNumber + 1} launches, also without reading sources."));
            }

            withoutSources = true;
        }
    }

    private static GraphBuild Parse(string? output, string commit)
    {
        if (string.IsNullOrEmpty(output))
        {
            throw new GraphAnalysisException("The analysis ended well and wrote no model.");
        }

        try
        {
            var build = CodeGraphJson.ParseBuild(output);
            return string.Equals(build.Graph.Commit, commit, StringComparison.Ordinal)
                ? build
                : throw new GraphAnalysisException("The analysis described another commit than the one asked.");
        }
        catch (InvalidDataException)
        {
            // The text of the process is data that is not trusted: nothing of it, not even the message of the exception, goes on.
            throw new GraphAnalysisException("The analysis wrote something that is not a model.");
        }
    }
}
