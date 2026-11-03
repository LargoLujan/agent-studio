using System.Text.RegularExpressions;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>
/// The line that says why a verification step failed, read from its evidence: the log the verification runner writes, with
/// its header and footer lines (<c># command: …</c>, <c># exit code: …</c>, <c># start problem: …</c>, <c># report problem:
/// …</c>) and each line of the process prefixed with its stream (<c>stdout| </c>, <c>stderr| </c>). What the monitor shows
/// under a failed task, briefly: it never replaces the evidence, which keeps the whole output.
/// </summary>
public static partial class StepLogText
{
    /// <summary>The longest line shown; a longer one is cut with an ellipsis.</summary>
    public const int MaxLength = 200;

    private const string OutputPrefix = "stdout| ";
    private const string ErrorPrefix = "stderr| ";
    private const string StartProblem = "# start problem: ";
    private const string ReportProblem = "# report problem: ";

    /// <summary>
    /// The main error line of a step's log, or null when it has none: why the step could not start; else the first line of
    /// the process that reads as an error (an <c>error</c> of the compiler or of MSBuild, a failed test, «No test projects were
    /// found», an exception…), on the error stream first; else why its test report could not be read; else the first line
    /// of its error stream. Colours of the terminal are removed and the project a line of MSBuild ends with is left out.
    /// </summary>
    public static string? KeyLine(string? log)
    {
        if (string.IsNullOrEmpty(log))
        {
            return null;
        }

        string? start = null;
        string? report = null;
        string? firstError = null;
        string? errorStreamMatch = null;
        string? outputStreamMatch = null;
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(StartProblem, StringComparison.Ordinal))
            {
                start ??= line[StartProblem.Length..];
            }
            else if (line.StartsWith(ReportProblem, StringComparison.Ordinal))
            {
                report ??= line[ReportProblem.Length..];
            }
            else if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal) && Clean(line[ErrorPrefix.Length..]) is { Length: > 0 } error)
            {
                firstError ??= error;
                if (errorStreamMatch is null && ReadsAsError(error))
                {
                    errorStreamMatch = error;
                }
            }
            else if (outputStreamMatch is null && line.StartsWith(OutputPrefix, StringComparison.Ordinal)
                && Clean(line[OutputPrefix.Length..]) is { Length: > 0 } output && ReadsAsError(output))
            {
                outputStreamMatch = output;
            }
        }

        var key = start ?? errorStreamMatch ?? outputStreamMatch ?? report ?? firstError;
        return key is null ? null : Shorten(key);
    }

    /// <summary>
    /// The ids of the verification steps a failure names, in its order: the engine says <c>La verificación falló en: build,
    /// test</c> for the steps of a task and <c>El paso lint falló.</c> for a tool. Empty for any other reason.
    /// </summary>
    public static IReadOnlyList<string> FailedSteps(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return [];
        }

        if (VerificationFailed().Match(reason) is { Success: true } verification)
        {
            return [.. verification.Groups["steps"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        return ToolFailed().Match(reason) is { Success: true } tool ? [tool.Groups["step"].Value] : [];
    }

    /// <summary>A line that names an error, and not a count of none («0 Error(s)», «failed: 0»).</summary>
    private static bool ReadsAsError(string line) => ErrorPattern().IsMatch(line) && !NoneCount().IsMatch(line);

    private static string Clean(string text)
    {
        var plain = AnsiEscape().Replace(text, string.Empty).Trim();

        // MSBuild ends the line of an error with the project it belongs to: what failed is said before it.
        return MsBuildProject().Replace(plain, string.Empty).TrimEnd();
    }

    private static string Shorten(string text)
    {
        var line = text.Trim();
        return line.Length <= MaxLength ? line : line[..(MaxLength - 1)] + "…";
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"\s\[[^\[\]]+\.(cs|vb|fs)proj(::[^\]]*)?\]$", RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildProject();

    [GeneratedRegex(@"\berror\b|\bfailed\b|\bfailure\b|\bexception\b|no test|not found|could not|cannot|\bfatal\b|\bfallid|\bno se (pudo|encontr)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"\b0\s+(errors?|error\(s\)|warnings?|failed|failures?)(\W|$)|\b(errors?|failed|failures?)\s*:\s*0\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoneCount();

    [GeneratedRegex(@"verificación falló en:\s*(?<steps>[^.]+)")]
    private static partial Regex VerificationFailed();

    [GeneratedRegex(@"El paso (?<step>\S+) falló")]
    private static partial Regex ToolFailed();
}
