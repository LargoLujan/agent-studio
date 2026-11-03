using System.Collections.Frozen;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Orchestration.Verification.Reports;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// Runs the steps of the trusted verification profile (docs/13 sections 4.5 and 5, ADR-0016) through
/// <see cref="IStreamingProcessRunner"/>, which kills the whole process tree and ties it to a Job Object, and captures the
/// full output of every step as evidence whose hash the <see cref="IEvidenceStore"/> computes. By default a step is
/// isolated (ADR-0017): it runs at Low integrity on a copy of the commit it verifies, so the code it builds and tests
/// cannot write the user's files.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The executable and the arguments come only from the step, that is, from the trusted profile: nothing a plan or
/// an agent wrote reaches a command line, and nothing is written to standard input.</item>
/// <item><b>Isolation.</b> A step with <see cref="ProcessIsolation.LowIntegrity"/> (the default) runs in the work area
/// of the run (<see cref="IIsolatedWorkAreaFactory"/>): a copy of the tree of <see cref="EvidenceScope.SubjectCommit"/> in
/// folders labelled Low, with <see cref="ProcessIsolation.LowIntegrity"/> and <see cref="IsolatedStepEnvironment"/>. The
/// copy is made by the hardened git layer (<see cref="IWorktreeManager.ArchiveCommitAsync"/>) from the objects of the
/// project's repository, <see cref="EvidenceScope.RepositoryRoot"/>: never through the <c>.git</c> of the working
/// directory, which an agent can rewrite to name an object store of its own. All the steps of one run share that area, so
/// a test step finds what the build step produced, and it is deleted when the run ends. The working directory is not read
/// for it. When the area cannot be prepared (no factory or git layer was given, the scope names no commit or no
/// repository, the copy failed) the step is not run and its evidence says why: it never falls back to running without
/// isolation. Isolated runs go one at a time, because all Low processes of the user share one identity and could write
/// into each other's areas: a gate orders the runs of this process, and the work area factory holds a lock of the user for
/// as long as an area exists, which orders those of every process (the gate is always taken first).</item>
/// <item>A step with <see cref="ProcessIsolation.None"/> (only the profile can say so, step by step) runs as before: in
/// the working directory, with <see cref="StepEnvironment"/> and the rights of the user. Its evidence title ends in
/// <see cref="WithoutIsolationTitleSuffix"/> and its header says so.</item>
/// <item>Every step runs without MSBuild nodes, build servers or compiler servers that outlive it, and with the console in
/// English (it is captured, never parsed).</item>
/// <item>The timeout of a step counts from the moment it is started. When it elapses the process tree is killed and the
/// result says <see cref="VerificationResult.TimedOut"/>, with exit code -1.</item>
/// <item>A test step is judged by its TRX or JUnit report, never by its console. A missing or unreadable report fails the
/// step and is recorded with the summary 0 passed, 0 failed, 0 skipped; a run without a passed test is never a success.
/// Without isolation the report path is cleared before the step starts, so a report left there earlier never counts. An
/// isolated step's report must be inside the fresh <see cref="ResultsFolderToken"/> folder of the step, a folder that did
/// not exist before it, so nothing is cleared: any other path of the area may be a link an earlier step left, and the
/// runner has the rights of the user. Otherwise the step is not run. The report is read only as a regular file reached
/// without links (above the area too), once the processes of the step have ended.</item>
/// <item><see cref="ResultsFolderToken"/>, in an argument or in the report path, stands for a fresh, empty folder outside
/// the working directory: inside the work area for an isolated step, in the scratch folder otherwise. Reports never land in
/// a worktree (where the next system commit would pick them up) and parallel runs never share one.</item>
/// <item>The output of every step is written to a file in the scratch folder, never in the work area, so no process of
/// the step can touch the evidence before it is captured.</item>
/// <item>The steps run in order and the run stops at the first one that does not succeed: a test step with
/// <c>--no-build</c> after a failed build would test stale binaries. A shorter list of results than of steps means the
/// rest did not run. A step that requires others (<see cref="VerificationStep.Requires"/>) needs them before it in the
/// list, with its isolation (<see cref="VerificationRules.CheckSteps"/>): otherwise nothing runs, so a test never runs on a
/// copy its build did not reach. <see cref="VerificationProfile.StepsToRun(IEnumerable{string})"/> gives such a list.</item>
/// <item>Cancelling kills the running step and throws <see cref="OperationCanceledException"/>; nothing is captured for it.</item>
/// <item>Every evidence is bound to the commit the scope names (<see cref="EvidenceScope.SubjectCommit"/>), which for an
/// isolated step is exactly the commit that was copied and verified. The judge only lets a test run of the commit under
/// review support an approval.</item>
/// </list>
/// </remarks>
public sealed class VerificationRunner : IVerificationRunner
{
    /// <summary>Stands for a fresh, empty folder outside the working directory, in arguments and report paths.</summary>
    public const string ResultsFolderToken = "{results}";

    /// <summary>The <see cref="EvidenceDraft.ProducedBy"/> of every capture: the system itself.</summary>
    public const string EvidenceProducer = "agent-studio";

    public const string EvidenceMediaType = "text/plain; charset=utf-8";

    /// <summary>The end of the evidence title of a step the profile runs without isolation.</summary>
    public const string WithoutIsolationTitleSuffix = " · sin aislamiento";

    /// <summary>The largest report read from an isolated work area (1 GiB).</summary>
    public const long MaxIsolatedReportBytes = 1L << 30;

    private const string OutputPrefix = "stdout| ";
    private const string ErrorPrefix = "stderr| ";

    // After a kill the session completes its output quickly; this only bounds a pathological wait (real time on purpose).
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // The gate of every runner whose options name none: one isolated run at a time in this process (ADR-0017). Across
    // processes, the work area factory holds the lock of the user for as long as an area exists.
    private static readonly SemaphoreSlim SharedIsolationGate = new(1, 1);

    private readonly IStreamingProcessRunner _processes;
    private readonly IEvidenceStore _evidence;
    private readonly VerificationRunnerOptions _options;
    private readonly TimeProvider _time;
    private readonly IIsolatedWorkAreaFactory? _isolation;
    private readonly IWorktreeManager? _git;
    private readonly SemaphoreSlim _isolationGate;

    /// <param name="isolation">
    /// Where isolated steps run. Without it an isolated step is never run (and never run without isolation): only steps the
    /// profile marks <see cref="ProcessIsolation.None"/> can run.
    /// </param>
    /// <param name="git">
    /// The hardened git layer, which writes the copy of the commit into each work area from the project's repository
    /// (<see cref="IWorktreeManager.ArchiveCommitAsync"/>). Without it an isolated step is never run either.
    /// </param>
    public VerificationRunner(
        IStreamingProcessRunner processes,
        IEvidenceStore evidence,
        VerificationRunnerOptions? options = null,
        TimeProvider? timeProvider = null,
        IIsolatedWorkAreaFactory? isolation = null,
        IWorktreeManager? git = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(evidence);

        _processes = processes;
        _evidence = evidence;
        _options = options ?? VerificationRunnerOptions.Default;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ScratchRoot, nameof(options));
        _time = timeProvider ?? TimeProvider.System;
        _isolation = isolation;
        _git = git;
        _isolationGate = _options.IsolationGate ?? SharedIsolationGate;
    }

    /// <summary>
    /// The environment of every step: <c>MSBUILDDISABLENODEREUSE=1</c>, <c>DOTNET_CLI_USE_MSBUILD_SERVER=0</c> and
    /// <c>UseSharedCompilation=false</c>, so no node keeps files of the worktree locked, and
    /// <c>DOTNET_CLI_UI_LANGUAGE=en</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> StepEnvironment { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["UseSharedCompilation"] = "false",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The environment of an isolated step (spike of 2026-10-16): <see cref="StepEnvironment"/>, plus every folder the .NET
    /// SDK, NuGet and the test host write to moved into the Low work area of the run (<c>NUGET_PACKAGES</c>,
    /// <c>NUGET_HTTP_CACHE_PATH</c>, <c>NUGET_PLUGINS_CACHE_PATH</c>, <c>DOTNET_CLI_HOME</c>, <c>TEMP</c>, <c>TMP</c>), and
    /// the first run of the SDK quiet: no telemetry, no banner, no change to the user's PATH and no development certificate.
    /// The NuGet cache is that of the run alone, so no run can plant a package for the next one.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> IsolatedStepEnvironment(IIsolatedWorkArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        return new Dictionary<string, string?>(StepEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = area.NuGetPackagesDirectory,
            ["NUGET_HTTP_CACHE_PATH"] = area.NuGetHttpCacheDirectory,
            ["NUGET_PLUGINS_CACHE_PATH"] = area.NuGetPluginsCacheDirectory,
            ["DOTNET_CLI_HOME"] = area.CliHomeDirectory,
            ["TEMP"] = area.TempDirectory,
            ["TMP"] = area.TempDirectory,
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <exception cref="ArgumentException">A step breaks <see cref="VerificationRules"/>, or comes before a step it requires; nothing was run.</exception>
    /// <exception cref="DirectoryNotFoundException">The working directory does not exist; nothing was run.</exception>
    public async Task<IReadOnlyList<VerificationResult>> RunAsync(
        string workingDirectory,
        IReadOnlyList<VerificationStep> steps,
        EvidenceScope scope,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ProjectId, nameof(scope));

        var problems = VerificationRules.CheckSteps(steps);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The verification steps are not valid: " + string.Join(" ", problems), nameof(steps));
        }

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The working directory '{directory}' does not exist.");
        }

        var results = new List<VerificationResult>(steps.Count);
        var isolated = new IsolatedRun(this, scope.RepositoryRoot, scope.SubjectCommit);
        await using (isolated.ConfigureAwait(false))
        {
            foreach (var step in steps)
            {
                var place = step.Isolation == ProcessIsolation.LowIntegrity
                    ? await isolated.EnsureAsync(cancellationToken).ConfigureAwait(false)
                    : null;
                var result = await RunStepAsync(directory, step, scope, place, output, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (!result.Succeeded)
                {
                    break;
                }
            }
        }

        return results;
    }

    private async Task<VerificationResult> RunStepAsync(
        string directory,
        VerificationStep step,
        EvidenceScope scope,
        IsolatedPlace? place,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        // The output of the step stays in the scratch folder (Medium), never in the work area its processes can write.
        var scratch = Path.Combine(_options.ScratchRoot, Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(scratch);
        try
        {
            var area = place?.Area;
            var workingDirectory = area?.SourceDirectory ?? directory;
            var problem = place?.Problem;
            var resultsFolder = Path.Combine(scratch, "results");
            if (problem is null && (step.Arguments.Any(UsesResultsFolder) || (step.ReportPath is not null && UsesResultsFolder(step.ReportPath))))
            {
                try
                {
                    resultsFolder = area is null ? Directory.CreateDirectory(resultsFolder).FullName : area.CreateResultsDirectory();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problem = $"The results folder of the step could not be created, so the step was not run: {ex.Message}";
                }
            }

            var arguments = step.Arguments.Select(a => a.Replace(ResultsFolderToken, resultsFolder, StringComparison.Ordinal)).ToArray();
            var reportPath = step.ReportPath is null
                ? null
                : Path.GetFullPath(step.ReportPath.Replace(ResultsFolderToken, resultsFolder, StringComparison.Ordinal), workingDirectory);

            // Any other path of the area, in the copy above all, may be a link an earlier step left: the runner, with the
            // rights of the user, never clears or reads it. The results folder is new, so there is nothing to clear.
            if (problem is null && area is not null && reportPath is not null && !IsInside(reportPath, resultsFolder))
            {
                problem = $"The report path '{reportPath}' is not inside the fresh results folder of the step, so the step was not run: an isolated step writes its report in {ResultsFolderToken}, a new folder of its work area, because any other path there may be a link an earlier step left.";
            }

            var log = new FileStream(
                Path.Combine(scratch, "output.log"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await using (log.ConfigureAwait(false))
            {
                var writer = new StreamWriter(log, Utf8NoBom, 64 * 1024, leaveOpen: true) { NewLine = "\n" };
                await using (writer.ConfigureAwait(false))
                {
                    await WriteHeaderAsync(writer, step, arguments, directory, workingDirectory, scope.RepositoryRoot, place).ConfigureAwait(false);

                    var outcome = (problem is null ? (StepOutcome?)null : StepOutcome.NotStarted(problem))
                        ?? (area is null ? ClearReport(reportPath) : null)
                        ?? await ExecuteAsync(step, arguments, workingDirectory, area, writer, output, cancellationToken).ConfigureAwait(false);

                    TestRunSummary? summary = null;
                    string? reportProblem = null;
                    if (step.Report != TestReportFormat.None)
                    {
                        (summary, reportProblem) = outcome.Started
                            ? ReadReport(step.Report, reportPath!, area, outcome.ExitCode)
                            : (new TestRunSummary(0, 0, 0, outcome.ExitCode), null);
                    }

                    var succeeded = outcome.Started
                        && !outcome.TimedOut
                        && (step.Report == TestReportFormat.None ? outcome.ExitCode == 0 : reportProblem is null && summary!.Succeeded);

                    await WriteFooterAsync(writer, step, reportPath, outcome, summary, reportProblem, succeeded).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                    log.Position = 0;
                    var title = step.Isolation == ProcessIsolation.None
                        ? $"{step.Title} ({step.Id}){WithoutIsolationTitleSuffix}"
                        : $"{step.Title} ({step.Id})";
                    var draft = new EvidenceDraft(
                        step.Kind,
                        EvidenceOrigin.CapturedBySystem,
                        title,
                        EvidenceProducer,
                        summary,
                        RunId: scope.Run?.Value,
                        TaskId: scope.Task?.Value,
                        Attempt: scope.Attempt,
                        MediaType: EvidenceMediaType,
                        SubjectCommit: scope.SubjectCommit);
                    var evidence = await _evidence.CaptureAsync(scope.ProjectId, draft, log, cancellationToken).ConfigureAwait(false);
                    return new VerificationResult(step.Id, evidence, succeeded, outcome.TimedOut, outcome.ExitCode);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    private async Task<StepOutcome> ExecuteAsync(
        VerificationStep step,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IIsolatedWorkArea? area,
        StreamWriter writer,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        var isolated = step.Isolation == ProcessIsolation.LowIntegrity;
        if (isolated && area is null)
        {
            return StepOutcome.NotStarted("The step is isolated but has no work area; it was not run.");
        }

        var spec = isolated
            ? new ProcessSpec(step.Executable, arguments, workingDirectory, IsolatedStepEnvironment(area!), Isolation: ProcessIsolation.LowIntegrity)
            : new ProcessSpec(step.Executable, arguments, workingDirectory, StepEnvironment);

        // Created before the start, so the timeout also covers a start that hangs.
        using var timeout = new CancellationTokenSource(step.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        IProcessSession session;
        try
        {
            session = await _processes.StartAsync(spec, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // ProcessIsolationException is an InvalidOperationException: a start that cannot be isolated is a failed step.
            return StepOutcome.NotStarted(ex.Message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new StepOutcome(Started: false, ExitCode: -1, TimedOut: true, Problem: "The step did not start before its timeout.");
        }

        // Disposing a session that still runs kills its whole tree (also when the run is cancelled).
        await using (session.ConfigureAwait(false))
        {
            try
            {
                await foreach (var line in session.Output.WithCancellation(linked.Token).ConfigureAwait(false))
                {
                    await WriteOutputLineAsync(writer, line).ConfigureAwait(false);
                    output?.Report(line);
                }

                var exitCode = await session.Exited.WaitAsync(linked.Token).ConfigureAwait(false);
                return new StepOutcome(Started: true, exitCode, TimedOut: false, Problem: null);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await session.KillAsync().ConfigureAwait(false);
                await DrainAsync(session, writer, output).ConfigureAwait(false);
                return new StepOutcome(Started: true, ExitCode: -1, TimedOut: true, Problem: null);
            }
        }
    }

    // The lines the process wrote before it was killed are still evidence.
    private static async Task DrainAsync(IProcessSession session, StreamWriter writer, IProgress<ProcessOutputLine>? output)
    {
        using var limit = new CancellationTokenSource(DrainLimit);
        try
        {
            await foreach (var line in session.Output.WithCancellation(limit.Token).ConfigureAwait(false))
            {
                await WriteOutputLineAsync(writer, line).ConfigureAwait(false);
                output?.Report(line);
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            await writer.WriteLineAsync("# output: not fully drained after the kill").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes a report left at the report path of a step without isolation, so only a report written by this run can
    /// count. An isolated step never gets here: its report is in a results folder that did not exist before the step.
    /// </summary>
    private static StepOutcome? ClearReport(string? reportPath)
    {
        if (reportPath is null || !File.Exists(reportPath))
        {
            return null;
        }

        try
        {
            File.Delete(reportPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StepOutcome.NotStarted($"An earlier report at '{reportPath}' could not be removed, so the step was not run: {ex.Message}");
        }
    }

    private static (TestRunSummary Summary, string? Problem) ReadReport(TestReportFormat format, string path, IIsolatedWorkArea? area, int exitCode)
    {
        try
        {
            if (area is null)
            {
                var summary = format == TestReportFormat.Trx
                    ? TrxReportParser.ParseFile(path, exitCode)
                    : JUnitReportParser.ParseFile(path, exitCode);
                return (summary, null);
            }

            // The processes of the step have ended (their session is disposed): only a regular file of the area is read.
            using var report = area.OpenReport(path, MaxIsolatedReportBytes);
            var parsed = format == TestReportFormat.Trx
                ? TrxReportParser.Parse(report, exitCode)
                : JUnitReportParser.Parse(report, exitCode);
            return (parsed, null);
        }
        catch (TestReportException ex)
        {
            // A report that cannot be read proves nothing: no passes, so the step can never count as a success.
            return (new TestRunSummary(0, 0, 0, exitCode), ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new TestRunSummary(0, 0, 0, exitCode), $"The {format} report '{path}' cannot be read: {ex.Message}");
        }
    }

    private static bool UsesResultsFolder(string value) => value.Contains(ResultsFolderToken, StringComparison.Ordinal);

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static Task WriteOutputLineAsync(StreamWriter writer, ProcessOutputLine line) =>
        writer.WriteLineAsync((line.Stream == ProcessStream.StandardError ? ErrorPrefix : OutputPrefix) + line.Text);

    private async Task WriteHeaderAsync(
        StreamWriter writer,
        VerificationStep step,
        IReadOnlyList<string> arguments,
        string directory,
        string workingDirectory,
        string? repository,
        IsolatedPlace? place)
    {
        await writer.WriteLineAsync($"# Agent Studio · verification step '{step.Id}' ({step.Title})").ConfigureAwait(false);
        await writer.WriteLineAsync("# command: " + Display(step.Executable, arguments)).ConfigureAwait(false);
        if (step.Isolation == ProcessIsolation.None)
        {
            await writer.WriteLineAsync("# isolation: none (the profile turns it off for this step: it runs with the rights of the user, in the working directory)").ConfigureAwait(false);
        }
        else if (place?.Area is { } area)
        {
            await writer.WriteLineAsync("# isolation: low integrity (S-1-16-4096): Windows refuses its writes to whatever is labelled Medium or above, such as the user's repositories and data; folders labelled Low, the work area and others such as LocalLow, stay writable").ConfigureAwait(false);
            await writer.WriteLineAsync($"# source: commit {area.Commit}, copied with git archive from the objects of the repository '{repository}', never through the .git of '{directory}', which is not written").ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("# isolation: low integrity, which could not be applied: the step is not run").ConfigureAwait(false);
        }

        await writer.WriteLineAsync("# working directory: " + workingDirectory).ConfigureAwait(false);
        await writer.WriteLineAsync("# timeout: " + step.Timeout.ToString("c", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteLineAsync("# started: " + _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    private async Task WriteFooterAsync(
        StreamWriter writer,
        VerificationStep step,
        string? reportPath,
        StepOutcome outcome,
        TestRunSummary? summary,
        string? reportProblem,
        bool succeeded)
    {
        if (outcome.Problem is not null)
        {
            await writer.WriteLineAsync("# start problem: " + outcome.Problem).ConfigureAwait(false);
        }

        await writer.WriteLineAsync("# ended: " + _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteLineAsync("# exit code: " + outcome.ExitCode.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteLineAsync("# timed out: " + (outcome.TimedOut ? "yes" : "no")).ConfigureAwait(false);
        if (step.Report != TestReportFormat.None)
        {
            await writer.WriteLineAsync($"# report ({step.Report}): {reportPath}").ConfigureAwait(false);
            if (reportProblem is not null)
            {
                await writer.WriteLineAsync("# report problem: " + reportProblem).ConfigureAwait(false);
            }
            else if (summary is not null && outcome.Started)
            {
                await writer.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"# tests: passed {summary.Passed}, failed {summary.Failed}, skipped {summary.Skipped}")).ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync("# result: " + (succeeded ? "succeeded" : "failed")).ConfigureAwait(false);
    }

    // For people reading the evidence only: the process gets the arguments as a list, never as this text.
    private static string Display(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover scratch folder outside every worktree is harmless.
        }
    }

    private readonly record struct StepOutcome(bool Started, int ExitCode, bool TimedOut, string? Problem)
    {
        public static StepOutcome NotStarted(string problem) => new(Started: false, ExitCode: -1, TimedOut: false, problem);
    }

    /// <summary>Where the isolated steps of a run go: its work area, or why there is none.</summary>
    private sealed record IsolatedPlace(IIsolatedWorkArea? Area, string? Problem);

    /// <summary>
    /// The isolation of one run: the gate taken and the work area created on the first isolated step (the factory waits for
    /// the lock of the user), both given back when the run ends, after every process of the area has ended and its reports
    /// have been read: disposing the area deletes it and then lets the lock go.
    /// </summary>
    private sealed class IsolatedRun(VerificationRunner owner, string? repository, string? commit) : IAsyncDisposable
    {
        private const string OnlyNone = "a step only runs without isolation when the profile says \"isolation\": \"none\" for it.";

        private IsolatedPlace? _place;
        private bool _entered;

        public async Task<IsolatedPlace> EnsureAsync(CancellationToken cancellationToken)
        {
            if (_place is not null)
            {
                return _place;
            }

            await owner._isolationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _entered = true;
            if (owner._isolation is null || owner._git is not { } git)
            {
                return _place = new IsolatedPlace(
                    null,
                    $"No isolated work area can be created here (no IIsolatedWorkAreaFactory or no IWorktreeManager to copy the commit), so the isolated step was not run; {OnlyNone}");
            }

            if (repository is null || commit is null)
            {
                return _place = new IsolatedPlace(
                    null,
                    $"An isolated step runs on a copy of the commit it verifies, taken from the project's repository, never through the .git of the working directory; the scope names no {(commit is null ? "commit" : "repository")}, so the step was not run; {OnlyNone}");
            }

            try
            {
                var area = await owner._isolation.CreateAsync(commit, (folder, ct) => git.ArchiveCommitAsync(repository, commit, folder, ct), cancellationToken).ConfigureAwait(false);
                return _place = new IsolatedPlace(area, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                return _place = new IsolatedPlace(null, "The isolated work area could not be prepared, so the step was not run: " + ex.Message);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_place?.Area is { } area)
                {
                    await area.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (_entered)
                {
                    owner._isolationGate.Release();
                }
            }
        }
    }
}
