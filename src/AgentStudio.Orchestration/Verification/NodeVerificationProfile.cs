using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>How npm is started: through <c>node</c> and the <c>npm-cli.js</c> beside it, or why it cannot be.</summary>
/// <param name="CliScript">The absolute path of <c>npm-cli.js</c>; null when it cannot be found.</param>
/// <param name="Problem">Why npm cannot be started, in English, for the output of the step that says it; null when <paramref name="CliScript"/> is there.</param>
public sealed record NpmLaunch(string? CliScript, string? Problem)
{
    /// <summary>
    /// npm as it is found from <paramref name="nodeExecutable"/>, the <c>node.exe</c> on the path: <c>node_modules\npm\bin\npm-cli.js</c> in
    /// its folder. Only file names are looked at; nothing is run.
    /// </summary>
    public static NpmLaunch From(string? nodeExecutable)
    {
        if (string.IsNullOrWhiteSpace(nodeExecutable))
        {
            return new NpmLaunch(null, "node was not found on the PATH, so npm cannot be started. Install Node.js with npm, or edit this profile.");
        }

        var script = Path.Combine(Path.GetDirectoryName(nodeExecutable) ?? string.Empty, "node_modules", "npm", "bin", "npm-cli.js");
        return File.Exists(script)
            ? new NpmLaunch(script, null)
            : new NpmLaunch(null, "npm-cli.js was not found next to node.exe, so npm cannot be started without its cmd shim. Install Node.js with npm, or edit this profile.");
    }
}

/// <summary>
/// A proposed verification profile for a Node repository (docs/17 D-26, step 5; ADR-0055), detected from the names of its
/// files and nothing else: a <c>package.json</c> in the root. It follows the shape of <see cref="DotNetVerificationProfile"/>:
/// three steps the system runs itself, each one isolated on a fresh copy of the commit, and the report read from a JUnit file
/// in the fresh results folder of the step (<see cref="VerificationRunner.ResultsFolderToken"/>).
/// <list type="number">
/// <item><c>install</c>: <c>npm ci</c> when the repository has a <c>package-lock.json</c> (the exact versions it names), else
/// <c>npm install --no-package-lock</c>; always <c>--ignore-scripts</c> (no script of a package runs) and without audit or
/// funding notices.</item>
/// <item><c>build</c>: <c>npm run build --if-present</c>, which does nothing when the project has no <c>build</c> script.</item>
/// <item><c>test</c>: Node's own runner with its JUnit reporter (<c>node --test --test-reporter=junit</c>), zero tests being a
/// failure like for every report. Each step requires the one before it (<see cref="VerificationStep.Requires"/>), so a plan that
/// names only <c>test</c> still gets its dependencies installed and built first.</item>
/// </list>
/// <para>
/// <b>npm is started through node:</b> <c>node &lt;folder of node.exe&gt;\node_modules\npm\bin\npm-cli.js …</c>. On Windows
/// <c>npm</c> is a <c>.cmd</c> shim, which the isolated launch does not find (it only adds <c>.exe</c>) and which we do not want to
/// pass through <c>cmd</c>. When <c>npm-cli.js</c> is not there, the <c>install</c> and <c>build</c> steps are replaced by one that
/// fails and says why (<see cref="StackStepSupport.Failing"/>), never a blind failure to start.
/// </para>
/// It is never applied on its own: the person reviews it and it is written through the confirmation of Windows Hello. A project
/// on Jest, Vitest or Mocha needs the flags of its own JUnit reporter in the <c>test</c> step; the profile is theirs to edit.
/// Nothing here runs <c>npm</c>: building a proposal only looks at file names.
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

    /// <summary>
    /// The profile for the repository whose files are <paramref name="relativeFiles"/>, with npm found from the <c>node</c> that
    /// <paramref name="locator"/> finds; null when it is not a Node repository (<see cref="Detect"/>).
    /// </summary>
    public static VerificationProfile? ProposeFor(IReadOnlyList<string> relativeFiles, IExecutableLocator locator)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        ArgumentNullException.ThrowIfNull(locator);
        return Detect(relativeFiles) ? Propose(relativeFiles.Any(f => IsRootFile(f, LockFileName)), NpmLaunch.From(locator.Find("node"))) : null;
    }

    public static VerificationProfile Propose(bool hasLockFile, NpmLaunch npm)
    {
        ArgumentNullException.ThrowIfNull(npm);
        List<string> install = [hasLockFile ? "ci" : "install", "--ignore-scripts", "--no-audit", "--no-fund"];
        if (!hasLockFile)
        {
            install.Add("--no-package-lock");
        }

        var test = new VerificationStep(
            "test",
            "Pruebas",
            "node",
            ["--test", "--test-reporter=junit", "--test-reporter-destination=" + VerificationRunner.ResultsFolderToken + "/test.xml"],
            EvidenceKind.TestRun,
            TestReportFormat.JUnit,
            VerificationRunner.ResultsFolderToken + "/test.xml",
            TimeSpan.FromMinutes(20),
            Requires: ["build"]);
        if (npm.CliScript is not { } script)
        {
            return new VerificationProfile(
                ProfileName,
                [
                    StackStepSupport.Failing("install", "Instalar dependencias", EvidenceKind.CommandOutput, npm.Problem ?? "npm cannot be started."),
                    StackStepSupport.Failing("build", "Compilar", EvidenceKind.BuildLog, npm.Problem ?? "npm cannot be started.") with { Requires = ["install"] },
                    test,
                ]);
        }

        return new VerificationProfile(
            ProfileName,
            [
                new VerificationStep(
                    "install",
                    "Instalar dependencias",
                    "node",
                    [script, .. install],
                    EvidenceKind.CommandOutput,
                    TestReportFormat.None,
                    null,
                    TimeSpan.FromMinutes(15)),
                new VerificationStep(
                    "build",
                    "Compilar",
                    "node",
                    [script, "run", "build", "--if-present"],
                    EvidenceKind.BuildLog,
                    TestReportFormat.None,
                    null,
                    TimeSpan.FromMinutes(10),
                    Requires: ["install"]),
                test,
            ]);
    }

    private static bool IsRootFile(string? path, string name) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(path.Trim().Replace('\\', '/').TrimStart('/'), name, StringComparison.OrdinalIgnoreCase);
}
