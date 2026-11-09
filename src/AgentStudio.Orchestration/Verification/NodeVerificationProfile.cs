using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// A proposed verification profile for a Node repository (docs/17 D-26, step 5; ADR-0055), detected from the names of its
/// files and nothing else: a <c>package.json</c> in the root. It follows the shape of <see cref="DotNetVerificationProfile"/>:
/// three steps the system runs itself, each one isolated on a fresh copy of the commit, and the report read from a JUnit file
/// in the fresh results folder of the step (<see cref="VerificationRunner.ResultsFolderToken"/>).
/// <list type="number">
/// <item><c>install</c>: <c>npm ci</c> when the repository has a <c>package-lock.json</c> (the exact versions it names), else
/// <c>npm install --no-package-lock</c>; always <c>--ignore-scripts</c> (no script of a package runs), without audit or funding
/// notices and with the npm cache in the results folder, since an isolated step cannot write the user's profile.</item>
/// <item><c>build</c>: <c>npm run build --if-present</c>, which does nothing when the project has no <c>build</c> script.</item>
/// <item><c>test</c>: Node's own runner with its JUnit reporter (<c>node --test --test-reporter=junit</c>), zero tests being a
/// failure like for every report. Each step requires the one before it (<see cref="VerificationStep.Requires"/>), so a plan that
/// names only <c>test</c> still gets its dependencies installed and built first.</item>
/// </list>
/// It is never applied on its own: the person reviews it and it is written through the confirmation of Windows Hello. A project
/// on Jest, Vitest or Mocha needs the flags of its own JUnit reporter in the <c>test</c> step; the profile is theirs to edit.
/// Nothing here runs <c>npm</c>: building a proposal only reads file names.
/// </summary>
public static class NodeVerificationProfile
{
    public const string ProfileName = "node";

    /// <summary>The lock file whose presence makes the install step <c>npm ci</c>.</summary>
    public const string LockFileName = "package-lock.json";

    /// <summary>Whether the root of the repository holds a <c>package.json</c>; <paramref name="relativeFiles"/> are the paths of its files, with <c>/</c> or <c>\</c>.</summary>
    public static bool Detect(IEnumerable<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        return relativeFiles.Any(f => IsRootFile(f, "package.json"));
    }

    /// <summary>The profile for the repository whose files are <paramref name="relativeFiles"/>; null when it is not a Node repository (<see cref="Detect"/>).</summary>
    public static VerificationProfile? ProposeFor(IReadOnlyList<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        return Detect(relativeFiles) ? Propose(relativeFiles.Any(f => IsRootFile(f, LockFileName))) : null;
    }

    public static VerificationProfile Propose(bool hasLockFile)
    {
        List<string> install = [hasLockFile ? "ci" : "install", "--ignore-scripts", "--no-audit", "--no-fund"];
        if (!hasLockFile)
        {
            install.Add("--no-package-lock");
        }

        install.AddRange(["--cache", VerificationRunner.ResultsFolderToken + "/npm-cache"]);
        return new VerificationProfile(
            ProfileName,
            [
                new VerificationStep(
                    "install",
                    "Instalar dependencias",
                    "npm",
                    install,
                    EvidenceKind.CommandOutput,
                    TestReportFormat.None,
                    null,
                    TimeSpan.FromMinutes(15)),
                new VerificationStep(
                    "build",
                    "Compilar",
                    "npm",
                    ["run", "build", "--if-present"],
                    EvidenceKind.BuildLog,
                    TestReportFormat.None,
                    null,
                    TimeSpan.FromMinutes(10),
                    Requires: ["install"]),
                new VerificationStep(
                    "test",
                    "Pruebas",
                    "node",
                    ["--test", "--test-reporter=junit", "--test-reporter-destination=" + VerificationRunner.ResultsFolderToken + "/test.xml"],
                    EvidenceKind.TestRun,
                    TestReportFormat.JUnit,
                    VerificationRunner.ResultsFolderToken + "/test.xml",
                    TimeSpan.FromMinutes(20),
                    Requires: ["build"]),
            ]);
    }

    private static bool IsRootFile(string? path, string name) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(path.Trim().Replace('\\', '/').TrimStart('/'), name, StringComparison.OrdinalIgnoreCase);
}
