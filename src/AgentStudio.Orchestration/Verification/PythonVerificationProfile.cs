using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// A proposed verification profile for a Python repository (docs/17 D-26, step 5; ADR-0055), detected from the names of its
/// files and nothing else: a <c>pyproject.toml</c> or a <c>requirements.txt</c> in the root. Two steps the system runs itself,
/// each isolated on a fresh copy of the commit:
/// <list type="number">
/// <item><c>build</c>: <c>python -m compileall -q -x &lt;folders&gt; .</c>, which compiles every module and fails on a syntax error.
/// It leaves <c>__pycache__</c> folders in the copy (that is what it is for; the copy is deleted when the run ends), and skips
/// <c>node_modules</c>, <c>.venv</c>, <c>venv</c> and <c>.git</c> (<see cref="ExcludedFolders"/>).</item>
/// <item><c>test</c>: <c>python -B -m pytest -q -p no:cacheprovider --junitxml={results}/test.xml</c>, the JUnit report that
/// pytest writes, read from the fresh results folder of the step. Zero tests is a failure like for every report. It requires the
/// build.</item>
/// </list>
/// The profile installs nothing: <c>pip</c> is never run, and the packages the tests need (pytest first) are the ones of the
/// Python of the computer. A project that needs a virtual environment or other flags edits the profile, which is the person's.
/// <para>
/// <b>The interpreter:</b> when the <c>python</c> the path finds is the alias of the Microsoft Store (in
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>), which may not start at Low integrity, or there is none, the <c>build</c> step is
/// replaced by one that fails and says why (<see cref="StackStepSupport.Failing"/>): never a blind failure to start. Installing
/// Python from python.org (or editing the profile) fixes it.
/// </para>
/// Nothing here runs <c>python</c>: building a proposal only looks at file names.
/// </summary>
public static class PythonVerificationProfile
{
    public const string ProfileName = "python";

    /// <summary>The regular expression of <c>compileall -x</c>: a path with one of these folders in it is not compiled.</summary>
    public const string ExcludedFolders = @"(^|[\\/])(node_modules|\.venv|venv|\.git)([\\/]|$)";

    /// <summary>Whether the root of the repository holds a <c>pyproject.toml</c> or a <c>requirements.txt</c>; <paramref name="relativeFiles"/> are the paths of its files, with <c>/</c> or <c>\</c>.</summary>
    public static bool Detect(IEnumerable<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        return relativeFiles.Any(f => IsRootFile(f, "pyproject.toml") || IsRootFile(f, "requirements.txt"));
    }

    /// <summary>
    /// The profile for the repository whose files are <paramref name="relativeFiles"/>, with the interpreter the <paramref name="locator"/>
    /// finds checked; null when it is not a Python repository (<see cref="Detect"/>).
    /// </summary>
    public static VerificationProfile? ProposeFor(IReadOnlyList<string> relativeFiles, IExecutableLocator locator)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        ArgumentNullException.ThrowIfNull(locator);
        return Detect(relativeFiles) ? Propose(InterpreterProblem(locator.Find("python"))) : null;
    }

    /// <summary>Why the interpreter at <paramref name="python"/> (the <c>python</c> of the path, null when none) cannot be used for an isolated step; null when it can.</summary>
    public static string? InterpreterProblem(string? python) =>
        string.IsNullOrWhiteSpace(python)
            ? "python was not found on the PATH. Install Python, or edit this profile."
            : StackStepSupport.IsStoreAlias(python)
                ? "The python on the PATH is the alias of the Microsoft Store, which may not start at Low integrity. Install Python from python.org, or edit this profile."
                : null;

    /// <param name="interpreterProblem">Why <c>python</c> cannot be used (<see cref="InterpreterProblem"/>); the build step then only says it.</param>
    public static VerificationProfile Propose(string? interpreterProblem = null) => new(
        ProfileName,
        [
            interpreterProblem is not null
                ? StackStepSupport.Failing("build", "Compilar", EvidenceKind.BuildLog, interpreterProblem)
                : new VerificationStep(
                    "build",
                    "Compilar",
                    "python",
                    ["-m", "compileall", "-q", "-x", ExcludedFolders, "."],
                    EvidenceKind.BuildLog,
                    TestReportFormat.None,
                    null,
                    TimeSpan.FromMinutes(10)),
            new VerificationStep(
                "test",
                "Pruebas",
                "python",
                ["-B", "-m", "pytest", "-q", "-p", "no:cacheprovider", "--junitxml=" + VerificationRunner.ResultsFolderToken + "/test.xml"],
                EvidenceKind.TestRun,
                TestReportFormat.JUnit,
                VerificationRunner.ResultsFolderToken + "/test.xml",
                TimeSpan.FromMinutes(20),
                Requires: ["build"]),
        ]);

    private static bool IsRootFile(string? path, string name) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(path.Trim().Replace('\\', '/').TrimStart('/'), name, StringComparison.OrdinalIgnoreCase);
}
