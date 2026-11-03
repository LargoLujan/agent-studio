using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LowIlVerification.Native;

namespace LowIlVerification;

/// <summary>
/// Drives the spike. <c>run</c> prepares Low folders and runs the proofs a-e (only Low-IL children; no Medium dotnet).
/// <c>overhead</c> measures restore/build/test under Low IL against a normal (Medium) baseline.
/// </summary>
internal static partial class Harness
{
    private static readonly List<string> Log = [];
    private static readonly List<(string Id, string Title, bool Passed, string Detail)> Proofs = [];

    /// <summary>The environment every Low-IL dotnet child gets (the folders are filled in per run).</summary>
    private static Dictionary<string, string?> LowEnvironment(string lowNuget, string lowHome, string lowTemp) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = lowNuget,
            ["DOTNET_CLI_HOME"] = lowHome,
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            // The SDK first run tries to persist <DOTNET_CLI_HOME>\.dotnet\tools in the user's PATH (HKCU\Environment) and
            // to create a dev certificate; Low IL blocks both, but they are switched off so nothing is even attempted.
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["UseSharedCompilation"] = "false",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["TEMP"] = lowTemp,
            ["TMP"] = lowTemp,
        };

    /// <summary>
    /// The Medium baseline: same switches, a fresh packages folder, but the user's own (already initialised) CLI home, so
    /// no SDK first run happens at Medium (a first run at Medium with a fresh DOTNET_CLI_HOME writes the user's PATH).
    /// </summary>
    private static Dictionary<string, string> MediumEnvironment(string packages) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = packages,
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["UseSharedCompilation"] = "false",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        };

    public static int Run()
    {
        var selfExe = Environment.ProcessPath ?? throw new InvalidOperationException("Environment.ProcessPath is null.");
        var dotnet = FindOnPath("dotnet.exe") ?? throw new FileNotFoundException("dotnet.exe not found on PATH.");

        var baseDir = NewBaseDir("run");
        var lowWork = Path.Combine(baseDir, "low-work");
        var lowNuget = Path.Combine(baseDir, "low-nuget");
        var lowHome = Path.Combine(baseDir, "low-home");
        var lowTemp = Path.Combine(baseDir, "low-temp");
        var mediumRepo = Path.Combine(baseDir, "medium-repo");
        var logs = Path.Combine(baseDir, "logs");
        foreach (var dir in new[] { lowWork, lowNuget, lowHome, lowTemp, mediumRepo, logs })
        {
            Directory.CreateDirectory(dir);
        }

        Banner("run", baseDir, selfExe, dotnet);

        // --- Prepare Low-IL writable locations with SetNamedSecurityInfo. ---
        Line("== Etiquetado de carpetas Low (SetNamedSecurityInfo) ==");
        foreach (var dir in new[] { lowWork, lowNuget, lowHome, lowTemp })
        {
            IntegrityLabeler.ApplyLow(dir);
            Line($"labelled Low: {dir}");
            Line("  icacls: " + MandatoryLabelLine(dir));
        }

        Line("medium-repo (sin etiqueta): " + (MandatoryLabelLine(mediumRepo) is { Length: > 0 } ml ? ml : "(ninguna -> Medium por defecto)"));
        File.WriteAllText(Path.Combine(mediumRepo, "readable.txt"), "secret-readable");
        Line(string.Empty);

        using var launcher = LowIntegrityLauncher.Create();
        var lowEnv = LowEnvironment(lowNuget, lowHome, lowTemp);

        // --- The child really runs at Low IL. ---
        var token = launcher.Run(selfExe, Quote(selfExe) + " probe-token", baseDir, lowEnv, Path.Combine(logs, "probe-token.log"), TimeSpan.FromSeconds(30));
        Dump("probe-token", token);
        Proof("token", "El hijo corre en integridad Low (S-1-16-4096)",
            token.ExitCode == 0 && token.Output.Contains("INTEGRITY_SID=S-1-16-4096", StringComparison.Ordinal),
            FirstLine(token.Output, "INTEGRITY_SID="));

        // --- d (network): can a Low-IL process open sockets? ---
        var net = launcher.Run(selfExe, Quote(selfExe) + " probe-net", baseDir, lowEnv, Path.Combine(logs, "probe-net.log"), TimeSpan.FromSeconds(30));
        Dump("probe-net", net);
        Proof("d-net", "Un proceso Low resuelve DNS y abre TCP (riesgo de exfiltracion)",
            net.ExitCode == 0 && net.Output.Contains("CONNECTED", StringComparison.Ordinal),
            FirstLine(net.Output, "TCP api.nuget.org"));

        // --- b/c (direct, non-test process): write blocked up, allowed in Low. ---
        var write = launcher.Run(selfExe, $"{Quote(selfExe)} probe-write {Quote(lowWork)} {Quote(mediumRepo)}", baseDir, lowEnv, Path.Combine(logs, "probe-write.log"), TimeSpan.FromSeconds(30));
        Dump("probe-write", write);
        Proof("b-direct", "Proceso Low: escribe en Low, bloqueado en Medium",
            write.Output.Contains("WRITE_LOW ok", StringComparison.Ordinal) && write.Output.Contains("WRITE_MEDIUM blocked", StringComparison.Ordinal),
            FirstLine(write.Output, "WRITE_MEDIUM"));

        // --- Copy the tiny test project into the Low work folder. ---
        var project = Path.Combine(lowWork, "TinyTests");
        CopyDirectory(FixturesDir(), project);
        Line($"copied TinyTests -> {project}");
        Line(string.Empty);

        // --- a: cold restore + build + test under Low IL, with the same flags as DotNetVerificationProfile. ---
        var restoreLow = launcher.Run(dotnet, $"{Quote(dotnet)} restore {Quote(project)} --no-http-cache", project, lowEnv, Path.Combine(logs, "restore-low.log"), TimeSpan.FromMinutes(6));
        Dump("restore-low", restoreLow, tail: true);
        Proof("a-restore", "dotnet restore (en frio, cache NuGet Low dedicada) bajo Low IL", restoreLow.ExitCode == 0,
            $"exit={restoreLow.ExitCode} elapsed={restoreLow.Elapsed.TotalSeconds:F1}s nuget={NuGetRestoreSeconds(restoreLow.Output)}");

        var buildLow = launcher.Run(dotnet, $"{Quote(dotnet)} build {Quote(project)} --no-restore -c Debug", project, lowEnv, Path.Combine(logs, "build-low.log"), TimeSpan.FromMinutes(5));
        Dump("build-low", buildLow, tail: true);
        Proof("a-build", "dotnet build bajo Low IL", buildLow.ExitCode == 0, $"exit={buildLow.ExitCode} elapsed={buildLow.Elapsed.TotalSeconds:F1}s");

        var lowResults = Path.Combine(lowWork, "trx");
        var testEnv = new Dictionary<string, string?>(lowEnv, StringComparer.OrdinalIgnoreCase)
        {
            ["LOW_WORK_DIR"] = lowWork,
            ["EVIL_MEDIUM_DIR"] = mediumRepo,
            ["READ_MEDIUM_FILE"] = Path.Combine(mediumRepo, "readable.txt"),
        };
        var testLow = launcher.Run(
            dotnet,
            $"{Quote(dotnet)} test {Quote(project)} --no-build -c Debug --results-directory {Quote(lowResults)} -- --report-xunit-trx --report-xunit-trx-filename tiny.trx --zero-tests-policy strict",
            project,
            testEnv,
            Path.Combine(logs, "test-low.log"),
            TimeSpan.FromMinutes(5));
        Dump("test-low", testLow, tail: true);
        var trxPath = Path.Combine(lowResults, "tiny.trx");
        var trx = ReadTrx(trxPath);
        Proof("a-test", "dotnet test bajo Low IL escribe el TRX y pasa",
            testLow.ExitCode == 0 && trx.Problem is null && trx.Passed == 3 && trx.Failed == 0,
            $"exit={testLow.ExitCode} passed={trx.Passed} failed={trx.Failed} skipped={trx.Skipped} {trx.Problem}");
        foreach (var (name, outcome) in trx.Outcomes)
        {
            Line($"   trx: {name} -> {outcome}");
        }

        Proof("b-test", "Un metodo de test NO puede escribir en Medium (no-write-up)",
            trx.Outcomes.Any(o => o.Name.EndsWith("MediumFolderIsNotWritable", StringComparison.Ordinal) && o.Outcome == "Passed")
                && !File.Exists(Path.Combine(mediumRepo, "pwned-by-test.txt")),
            "MediumFolderIsNotWritable=Passed (UnauthorizedAccessException) y pwned-by-test.txt " + (File.Exists(Path.Combine(mediumRepo, "pwned-by-test.txt")) ? "EXISTE" : "ausente"));
        Proof("c-test", "Un metodo de test escribe en la carpeta Low",
            trx.Outcomes.Any(o => o.Name.EndsWith("LowWorkFolderIsWritable", StringComparison.Ordinal) && o.Outcome == "Passed")
                && File.Exists(Path.Combine(lowWork, "low-write-proof.txt")),
            "LowWorkFolderIsWritable=Passed y low-write-proof.txt " + (File.Exists(Path.Combine(lowWork, "low-write-proof.txt")) ? "existe" : "ausente"));
        Proof("d-read", "Un metodo de test lee un fichero Medium (Low no impide leer)",
            trx.Outcomes.Any(o => o.Name.EndsWith("MediumFileIsReadable", StringComparison.Ordinal) && o.Outcome == "Passed"),
            "MediumFileIsReadable=Passed");

        // --- b (build target): a malicious BeforeTargets="Build" writing to Medium fails with access denied. ---
        var evilMedium = Path.Combine(mediumRepo, "pwned-build.txt");
        var buildEvil = launcher.Run(
            dotnet,
            $"{Quote(dotnet)} build {Quote(project)} --no-restore -c Debug -p:EnableEvilTarget=true -p:EvilPath={Quote(evilMedium)}",
            project,
            lowEnv,
            Path.Combine(logs, "build-evil-medium.log"),
            TimeSpan.FromMinutes(5));
        Dump("build-evil-medium", buildEvil, tail: true);
        Proof("b-build", "Target MSBuild BeforeTargets=Build que escribe en Medium falla (access denied)",
            buildEvil.ExitCode != 0
                && buildEvil.Output.Contains("EVIL target attempting to write", StringComparison.Ordinal)
                && buildEvil.Output.Contains("MSB3491", StringComparison.Ordinal)
                && buildEvil.Output.Contains("is denied", StringComparison.Ordinal)
                && !File.Exists(evilMedium),
            $"exit={buildEvil.ExitCode} MSB3491 access denied, pwned-build.txt " + (File.Exists(evilMedium) ? "EXISTE" : "ausente"));

        // Control: the same target pointing at a Low path succeeds (the target logic is sound; only the Medium path fails).
        var evilLow = Path.Combine(lowWork, "pwned-build-low.txt");
        var buildEvilLow = launcher.Run(
            dotnet,
            $"{Quote(dotnet)} build {Quote(project)} --no-restore -c Debug -p:EnableEvilTarget=true -p:EvilPath={Quote(evilLow)}",
            project,
            lowEnv,
            Path.Combine(logs, "build-evil-low.log"),
            TimeSpan.FromMinutes(5));
        Dump("build-evil-low", buildEvilLow, tail: true);
        Proof("c-build", "El mismo target apuntando a la carpeta Low si escribe (control)",
            buildEvilLow.ExitCode == 0 && File.Exists(evilLow),
            $"exit={buildEvilLow.ExitCode} pwned-build-low.txt " + (File.Exists(evilLow) ? "existe" : "ausente"));

        // --- e: the Job Object still contains the Low-IL child and kills its tree on close. ---
        ProveJobKill(launcher, selfExe, baseDir, lowEnv, logs);

        Line("medium-repo al final: " + string.Join(", ", Directory.GetFiles(mediumRepo).Select(Path.GetFileName)));
        return Summarize(baseDir);
    }

    private static void ProveJobKill(LowIntegrityLauncher launcher, string selfExe, string baseDir, IReadOnlyDictionary<string, string?> env, string logs)
    {
        Line("== Job Object kill-on-close sobre el hijo Low ==");
        var treeLog = Path.Combine(logs, "spawn-tree.log");
        var job = new KillOnCloseJob();
        using var child = launcher.Start(selfExe, Quote(selfExe) + " spawn-tree 60", baseDir, env, treeLog, job.Handle);

        var childPid = (int)child.ProcessId;
        var grandPid = -1;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var g = FindPid(LowIntegrityLauncher.SafeReadLog(treeLog), "GRANDCHILD_PID=");
            if (g > 0)
            {
                grandPid = g;
                break;
            }

            Thread.Sleep(250);
        }

        var runningBefore = child.IsRunning() && grandPid > 0 && IsAlive(grandPid);
        Line($"child PID={childPid} grandchild PID={grandPid} running-before-close={runningBefore}");

        job.Dispose(); // Close the only job handle -> the OS kills everything in the job.

        bool childDead = false, grandDead = false;
        var deadline2 = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline2)
        {
            childDead = !child.IsRunning();
            grandDead = grandPid <= 0 || !IsAlive(grandPid);
            if (childDead && grandDead)
            {
                break;
            }

            Thread.Sleep(200);
        }

        Line($"after job close: child dead={childDead} (exit code {child.ExitCode()}), grandchild dead={grandDead}");
        Proof("e-job", "El Job Object contiene al hijo Low y mata su arbol al cerrarse",
            runningBefore && childDead && grandDead,
            $"antes: vivos={runningBefore}; despues: hijo muerto={childDead}, nieto muerto={grandDead}");
        Line(string.Empty);
    }

    /// <summary>Cold/warm restore, build and test: Low IL versus a normal Medium baseline, alternating the order.</summary>
    public static int Overhead()
    {
        var dotnet = FindOnPath("dotnet.exe") ?? throw new FileNotFoundException("dotnet.exe not found on PATH.");
        var baseDir = NewBaseDir("overhead");
        var lowRoot = Path.Combine(baseDir, "low");
        var mediumRoot = Path.Combine(baseDir, "medium");
        var logs = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(lowRoot);
        Directory.CreateDirectory(mediumRoot);
        Directory.CreateDirectory(logs);
        IntegrityLabeler.ApplyLow(lowRoot); // Everything created below inherits the Low label (OICI).

        var lowHome = Directory.CreateDirectory(Path.Combine(lowRoot, "home")).FullName;
        var lowTemp = Directory.CreateDirectory(Path.Combine(lowRoot, "temp")).FullName;
        var lowProject = Path.Combine(lowRoot, "work", "TinyTests");
        var mediumProject = Path.Combine(mediumRoot, "work", "TinyTests");
        CopyDirectory(FixturesDir(), lowProject);
        CopyDirectory(FixturesDir(), mediumProject);
        var mediumControl = Directory.CreateDirectory(Path.Combine(mediumRoot, "control-repo")).FullName;
        File.WriteAllText(Path.Combine(mediumControl, "readable.txt"), "secret-readable");

        Banner("overhead", baseDir, Environment.ProcessPath ?? string.Empty, dotnet);
        Line("low root icacls: " + MandatoryLabelLine(lowRoot));
        Line("low project icacls (heredada): " + MandatoryLabelLine(lowProject));
        Line(string.Empty);

        using var launcher = LowIntegrityLauncher.Create();
        var rows = new List<(string Phase, string Side, double Seconds, string Extra)>();

        LowRunResult LowRestore(string packages, string label, bool force = false)
        {
            var env = LowEnvironment(packages, lowHome, lowTemp);
            var result = launcher.Run(
                dotnet,
                $"{Quote(dotnet)} restore {Quote(lowProject)} --no-http-cache{(force ? " --force" : string.Empty)}",
                lowProject,
                env,
                Path.Combine(logs, label + ".log"),
                TimeSpan.FromMinutes(6));
            Line($"{label}: exit={result.ExitCode} wall={result.Elapsed.TotalSeconds:F1}s nuget={NuGetRestoreSeconds(result.Output)}");
            return result;
        }

        (int Exit, TimeSpan Elapsed, string Output) MediumRestore(string packages, string label, bool force = false)
        {
            var result = RunMedium(dotnet, $"restore {Quote(mediumProject)} --no-http-cache{(force ? " --force" : string.Empty)}", mediumProject, MediumEnvironment(packages), TimeSpan.FromMinutes(6));
            File.WriteAllText(Path.Combine(logs, label + ".log"), result.Output);
            Line($"{label}: exit={result.Exit} wall={result.Elapsed.TotalSeconds:F1}s nuget={NuGetRestoreSeconds(result.Output)}");
            return result;
        }

        // Warm-up: initialise the fresh Low CLI home (SDK first run) and the network path on both sides; not measured.
        Line("== warm-up (no se mide) ==");
        LowRestore(Path.Combine(lowRoot, "nuget-warmup"), "warmup-low");
        MediumRestore(Path.Combine(mediumRoot, "nuget-warmup"), "warmup-medium");
        Line(string.Empty);

        // Cold restores into fresh packages folders, alternating which side goes first to cancel network drift.
        Line("== restore en frio (carpeta de paquetes vacia, --no-http-cache) ==");
        string lastLowPackages = string.Empty, lastMediumPackages = string.Empty;
        for (var round = 1; round <= 2; round++)
        {
            var lowPackages = Path.Combine(lowRoot, $"nuget-cold-{round}");
            var mediumPackages = Path.Combine(mediumRoot, $"nuget-cold-{round}");
            if (round % 2 == 1)
            {
                var l = LowRestore(lowPackages, $"cold-low-{round}");
                var m = MediumRestore(mediumPackages, $"cold-medium-{round}");
                rows.Add(("restore en frio #" + round, "Low", l.Elapsed.TotalSeconds, NuGetRestoreSeconds(l.Output)));
                rows.Add(("restore en frio #" + round, "Medium", m.Elapsed.TotalSeconds, NuGetRestoreSeconds(m.Output)));
            }
            else
            {
                var m = MediumRestore(mediumPackages, $"cold-medium-{round}");
                var l = LowRestore(lowPackages, $"cold-low-{round}");
                rows.Add(("restore en frio #" + round, "Medium", m.Elapsed.TotalSeconds, NuGetRestoreSeconds(m.Output)));
                rows.Add(("restore en frio #" + round, "Low", l.Elapsed.TotalSeconds, NuGetRestoreSeconds(l.Output)));
            }

            lastLowPackages = lowPackages;
            lastMediumPackages = mediumPackages;
        }

        Line(string.Empty);
        Line("== restore en caliente (paquetes ya presentes, --force) ==");
        var warmLow = LowRestore(lastLowPackages, "warm-low", force: true);
        var warmMedium = MediumRestore(lastMediumPackages, "warm-medium", force: true);
        rows.Add(("restore en caliente", "Low", warmLow.Elapsed.TotalSeconds, NuGetRestoreSeconds(warmLow.Output)));
        rows.Add(("restore en caliente", "Medium", warmMedium.Elapsed.TotalSeconds, NuGetRestoreSeconds(warmMedium.Output)));

        Line(string.Empty);
        Line("== build y test ==");
        var lowEnv = LowEnvironment(lastLowPackages, lowHome, lowTemp);
        var buildLow = launcher.Run(dotnet, $"{Quote(dotnet)} build {Quote(lowProject)} --no-restore -c Debug", lowProject, lowEnv, Path.Combine(logs, "build-low.log"), TimeSpan.FromMinutes(5));
        Line($"build-low: exit={buildLow.ExitCode} wall={buildLow.Elapsed.TotalSeconds:F1}s");
        var buildMedium = RunMedium(dotnet, $"build {Quote(mediumProject)} --no-restore -c Debug", mediumProject, MediumEnvironment(lastMediumPackages), TimeSpan.FromMinutes(5));
        File.WriteAllText(Path.Combine(logs, "build-medium.log"), buildMedium.Output);
        Line($"build-medium: exit={buildMedium.Exit} wall={buildMedium.Elapsed.TotalSeconds:F1}s");
        rows.Add(("build (primero)", "Low", buildLow.Elapsed.TotalSeconds, $"exit {buildLow.ExitCode}"));
        rows.Add(("build (primero)", "Medium", buildMedium.Elapsed.TotalSeconds, $"exit {buildMedium.Exit}"));

        var lowTestEnv = new Dictionary<string, string?>(lowEnv, StringComparer.OrdinalIgnoreCase)
        {
            ["LOW_WORK_DIR"] = Path.Combine(lowRoot, "work"),
            ["EVIL_MEDIUM_DIR"] = mediumControl,
            ["READ_MEDIUM_FILE"] = Path.Combine(mediumControl, "readable.txt"),
        };
        var lowTrxDir = Path.Combine(lowRoot, "work", "trx");
        var testLow = launcher.Run(
            dotnet,
            $"{Quote(dotnet)} test {Quote(lowProject)} --no-build -c Debug --results-directory {Quote(lowTrxDir)} -- --report-xunit-trx --report-xunit-trx-filename tiny.trx",
            lowProject,
            lowTestEnv,
            Path.Combine(logs, "test-low.log"),
            TimeSpan.FromMinutes(5));
        var lowTrx = ReadTrx(Path.Combine(lowTrxDir, "tiny.trx"));
        Line($"test-low: exit={testLow.ExitCode} wall={testLow.Elapsed.TotalSeconds:F1}s passed={lowTrx.Passed} failed={lowTrx.Failed}");

        // Same test project at Medium (today's behaviour). MediumFolderIsNotWritable is EXPECTED to fail here: without the
        // Low token the test's write into the stand-in repository succeeds. That is the counterfactual for proof b.
        var mediumTrxDir = Path.Combine(mediumRoot, "work", "trx");
        var mediumTestEnv = new Dictionary<string, string>(MediumEnvironment(lastMediumPackages), StringComparer.OrdinalIgnoreCase)
        {
            ["LOW_WORK_DIR"] = Path.Combine(mediumRoot, "work"),
            ["EVIL_MEDIUM_DIR"] = mediumControl,
            ["READ_MEDIUM_FILE"] = Path.Combine(mediumControl, "readable.txt"),
        };
        var testMedium = RunMedium(
            dotnet,
            $"test {Quote(mediumProject)} --no-build -c Debug --results-directory {Quote(mediumTrxDir)} -- --report-xunit-trx --report-xunit-trx-filename tiny.trx",
            mediumProject,
            mediumTestEnv,
            TimeSpan.FromMinutes(5));
        File.WriteAllText(Path.Combine(logs, "test-medium.log"), testMedium.Output);
        var mediumTrx = ReadTrx(Path.Combine(mediumTrxDir, "tiny.trx"));
        Line($"test-medium: exit={testMedium.Exit} wall={testMedium.Elapsed.TotalSeconds:F1}s passed={mediumTrx.Passed} failed={mediumTrx.Failed}");
        foreach (var (name, outcome) in mediumTrx.Outcomes)
        {
            Line($"   medium trx: {name} -> {outcome}");
        }

        Line("control-repo tras el test Medium: " + string.Join(", ", Directory.GetFiles(mediumControl).Select(Path.GetFileName)));
        rows.Add(("test", "Low", testLow.Elapsed.TotalSeconds, $"exit {testLow.ExitCode}, {lowTrx.Passed} ok/{lowTrx.Failed} ko"));
        rows.Add(("test", "Medium", testMedium.Elapsed.TotalSeconds, $"exit {testMedium.Exit}, {mediumTrx.Passed} ok/{mediumTrx.Failed} ko"));

        Line(string.Empty);
        Line("== TABLA ==");
        Line("| Fase | Lado | Reloj (s) | Detalle |");
        Line("|---|---|---|---|");
        foreach (var (phase, side, seconds, extra) in rows)
        {
            Line($"| {phase} | {side} | {seconds.ToString("F1", CultureInfo.InvariantCulture)} | {extra} |");
        }

        var logPath = Path.Combine(baseDir, "overhead.log");
        File.WriteAllText(logPath, string.Join(Environment.NewLine, Log), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"full log: {logPath}");
        return 0;
    }

    private static int Summarize(string baseDir)
    {
        Line(string.Empty);
        Line("== RESUMEN ==");
        var failures = 0;
        foreach (var (id, title, ok, detail) in Proofs)
        {
            Line($"[{(ok ? "PASS" : "FAIL")}] {id,-10} {title} | {detail}");
            if (!ok)
            {
                failures++;
            }
        }

        Line($"proofs: {Proofs.Count}, failures: {failures}");
        var logPath = Path.Combine(baseDir, "harness.log");
        File.WriteAllText(logPath, string.Join(Environment.NewLine, Log), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"full log: {logPath}");
        return failures;
    }

    private static string NewBaseDir(string verb)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(Path.GetTempPath(), "as-lowil-spike", $"{stamp}-{verb}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Banner(string verb, string baseDir, string selfExe, string dotnet)
    {
        Line($"Agent Studio - spike de verificacion aislada (Low IL) - {verb} - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"base dir: {baseDir}");
        Line($"parent integrity SID: {LowIntegrityLauncher.CurrentIntegritySid()}");
        Line($"self exe: {selfExe}");
        Line($"dotnet:   {dotnet}");
        Line(string.Empty);
    }

    private static (int Exit, TimeSpan Elapsed, string Output) RunMedium(string exe, string arguments, string workingDirectory, IReadOnlyDictionary<string, string> env, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        var sb = new StringBuilder();
        using var job = new KillOnCloseJob();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };
        var start = DateTimeOffset.UtcNow;
        process.Start();
        NativeMethods.AssignProcessToJobObject(job.Handle, process.Handle); // Leftovers die with the job.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var finished = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (finished)
        {
            process.WaitForExit();
        }

        var elapsed = DateTimeOffset.UtcNow - start;
        lock (sb)
        {
            return (finished ? process.ExitCode : -1, elapsed, sb.ToString());
        }
    }

    private static (int Passed, int Failed, int Skipped, string? Problem, List<(string Name, string Outcome)> Outcomes) ReadTrx(string path)
    {
        var outcomes = new List<(string Name, string Outcome)>();
        try
        {
            if (!File.Exists(path))
            {
                return (0, 0, 0, "TRX ausente", outcomes);
            }

            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            int passed = 0, failed = 0, skipped = 0;
            foreach (var r in XDocument.Load(path).Descendants(ns + "UnitTestResult"))
            {
                var outcome = (string?)r.Attribute("outcome") ?? "?";
                outcomes.Add(((string?)r.Attribute("testName") ?? "?", outcome));
                switch (outcome)
                {
                    case "Passed": passed++; break;
                    case "NotExecuted":
                    case "Inconclusive": skipped++; break;
                    default: failed++; break;
                }
            }

            return (passed, failed, skipped, null, outcomes);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return (0, 0, 0, "TRX ilegible: " + ex.Message, outcomes);
        }
    }

    private static string NuGetRestoreSeconds(string output)
    {
        var match = RestoredIn().Match(output);
        if (!match.Success)
        {
            return "n/a";
        }

        var value = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var seconds = match.Groups[2].Value == "ms" ? value / 1000 : value;
        return seconds.ToString("F2", CultureInfo.InvariantCulture) + "s";
    }

    [GeneratedRegex(@"\(in ([0-9]+(?:[.,][0-9]+)?) (sec|ms)\)")]
    private static partial Regex RestoredIn();

    private static string MandatoryLabelLine(string path)
    {
        var (_, _, output) = RunMedium(Path.Combine(Environment.SystemDirectory, "icacls.exe"), Quote(path), Environment.CurrentDirectory, new Dictionary<string, string>(), TimeSpan.FromSeconds(20));
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.Contains("Mandatory Label", StringComparison.OrdinalIgnoreCase) || t.Contains("obligatori", StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        return string.Empty;
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p))
                {
                    return p;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        var def = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "dotnet", exe);
        return File.Exists(def) ? def : null;
    }

    private static string FixturesDir()
    {
        for (var cur = new DirectoryInfo(AppContext.BaseDirectory); cur is not null; cur = cur.Parent)
        {
            var candidate = Path.Combine(cur.FullName, "Fixtures", "TinyTests");
            if (File.Exists(Path.Combine(candidate, "TinyTests.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Fixtures/TinyTests not found next to the harness.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj")
            {
                continue; // Never copy build output into the work folder.
            }

            CopyDirectory(dir, Path.Combine(destination, name));
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int FindPid(string text, string marker)
    {
        foreach (var line in text.Split('\n'))
        {
            var i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0 && int.TryParse(line.AsSpan(i + marker.Length).Trim(), out var pid))
            {
                return pid;
            }
        }

        return -1;
    }

    private static string FirstLine(string text, string marker)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }

        return string.Empty;
    }

    private static void Dump(string label, LowRunResult result, bool tail = false)
    {
        Line($"-- {label}: exit={result.ExitCode} timedOut={result.TimedOut} elapsed={result.Elapsed.TotalSeconds:F1}s");
        var lines = result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Length > 0).ToArray();
        var show = tail && lines.Length > 25 ? lines[^25..] : lines.Length > 40 ? lines[..40] : lines;
        foreach (var l in show)
        {
            Line("   " + l);
        }
    }

    private static void Proof(string id, string title, bool passed, string detail)
    {
        Proofs.Add((id, title, passed, detail));
        Line($"[{(passed ? "PASS" : "FAIL")}] {id}: {title} ({detail})");
    }

    private static void Line(string text)
    {
        Log.Add(text);
        Console.WriteLine(text);
    }

    private static string Quote(string value) =>
        value.Length > 0 && !value.Contains(' ', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal)
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
