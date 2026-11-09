using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// A proposed verification profile for a Python repository (docs/17 D-26, step 5; ADR-0055), detected from the names of its
/// files and nothing else: a <c>pyproject.toml</c> or a <c>requirements.txt</c> in the root. Two steps the system runs itself,
/// each isolated on a fresh copy of the commit:
/// <list type="number">
/// <item><c>build</c>: <c>python -B -m compileall -q .</c>, which compiles every module and fails on a syntax error (<c>-B</c>
/// and <c>-q</c> keep it quiet and without bytecode files beside the sources).</item>
/// <item><c>test</c>: <c>python -B -m pytest -q -p no:cacheprovider --junitxml={results}/test.xml</c>, the JUnit report that
/// pytest writes, read from the fresh results folder of the step. Zero tests is a failure like for every report. It requires the
/// build.</item>
/// </list>
/// The profile installs nothing: <c>pip</c> is never run, and the packages the tests need (pytest first) are the ones of the
/// Python of the computer. A project that needs a virtual environment or other flags edits the profile, which is the person's.
/// Nothing here runs <c>python</c>: building a proposal only reads file names.
/// </summary>
public static class PythonVerificationProfile
{
    public const string ProfileName = "python";

    /// <summary>Whether the root of the repository holds a <c>pyproject.toml</c> or a <c>requirements.txt</c>; <paramref name="relativeFiles"/> are the paths of its files, with <c>/</c> or <c>\</c>.</summary>
    public static bool Detect(IEnumerable<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        return relativeFiles.Any(f => IsRootFile(f, "pyproject.toml") || IsRootFile(f, "requirements.txt"));
    }

    /// <summary>The profile for the repository whose files are <paramref name="relativeFiles"/>; null when it is not a Python repository (<see cref="Detect"/>).</summary>
    public static VerificationProfile? ProposeFor(IReadOnlyList<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        return Detect(relativeFiles) ? Propose() : null;
    }

    public static VerificationProfile Propose() => new(
        ProfileName,
        [
            new VerificationStep(
                "build",
                "Compilar",
                "python",
                ["-B", "-m", "compileall", "-q", "."],
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
