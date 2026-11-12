using System.IO;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Verification;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// F13-12 (docs/17 D-26, step 5): the profiles of Node and Python, detected from the names of the files of a temporary repository
/// and run by the system like the one of .NET. Nothing real runs: no <c>npm</c>, no <c>pip</c>, no <c>python</c>; the processes
/// are scripted, and the JUnit report is written where the real tool would write it.
/// </summary>
public sealed class StackVerificationProfileTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static readonly EvidenceScope Scope = new("proyecto-1", new RunId("20261110-a1b2c3"), new TaskId("T1"), 1, Commit, @"C:\proyectos\app");

    private readonly string _repository = Directory.CreateTempSubdirectory("as-stack-repo-").FullName;
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("as-stack-wd-").FullName;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "as-stack-scratch-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _areas = Path.Combine(Path.GetTempPath(), "as-stack-areas-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A Node installation as the file names say it: node.exe with node_modules\npm\bin\npm-cli.js beside it. Empty files; nothing runs.
    private readonly string _node = MakeNode(withNpmCli: true);

    private string NodeExe => Path.Combine(_node, "node.exe");

    private string NpmCli => Path.Combine(_node, "node_modules", "npm", "bin", "npm-cli.js");

    private static string MakeNode(bool withNpmCli)
    {
        var folder = Directory.CreateTempSubdirectory("as-stack-node-").FullName;
        File.WriteAllText(Path.Combine(folder, "node.exe"), string.Empty);
        if (withNpmCli)
        {
            Directory.CreateDirectory(Path.Combine(folder, "node_modules", "npm", "bin"));
            File.WriteAllText(Path.Combine(folder, "node_modules", "npm", "bin", "npm-cli.js"), string.Empty);
        }

        return folder;
    }

    private sealed class FakeLocator(string? node, string? python) : IExecutableLocator
    {
        public string? Find(string command) => command switch { "node" => node, "python" => python, _ => null };
    }

    private IExecutableLocator Locator(string? python = @"C:\Python312\python.exe") => new FakeLocator(NodeExe, python);

    private NpmLaunch Npm() => NpmLaunch.From(NodeExe);

    public void Dispose()
    {
        foreach (var folder in new[] { _repository, _workingDirectory, _scratch, _areas, _node })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        _gate.Dispose();
    }

    /// <summary>Makes a repository of empty files and lists them as the explorer does: relative paths with <c>/</c>.</summary>
    private IReadOnlyList<string> Repository(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_repository, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        return [.. Directory.EnumerateFiles(_repository, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_repository, f).Replace('\\', '/'))];
    }

    // ---- detection by the names of the files --------------------------------------------------------------------

    [Fact]
    public void Node_is_detected_by_a_package_json_in_the_root_and_not_by_one_deeper_or_by_python_files()
    {
        Assert.True(NodeVerificationProfile.Detect(Repository("package.json", "src/index.js")));
        Assert.True(NodeVerificationProfile.Detect(["PACKAGE.JSON"]));
        Assert.True(NodeVerificationProfile.Detect(["\\package.json"]));
        Assert.False(NodeVerificationProfile.Detect(["web/package.json", "pyproject.toml"]));
        Assert.False(NodeVerificationProfile.Detect(["", "package.json.bak"]));
        Assert.Null(NodeVerificationProfile.ProposeFor(["main.py"], Locator()));
    }

    [Theory]
    [InlineData("pyproject.toml")]
    [InlineData("requirements.txt")]
    public void Python_is_detected_by_its_files_in_the_root(string marker)
    {
        Assert.True(PythonVerificationProfile.Detect(Repository(marker, "app/main.py")));
        Assert.False(PythonVerificationProfile.Detect(["sub/" + marker, "package.json"]));
        Assert.Null(PythonVerificationProfile.ProposeFor(["package.json"], Locator()));
    }

    [Fact]
    public void A_lock_file_makes_the_install_step_a_clean_install_and_without_one_it_never_writes_a_lock()
    {
        var locked = NodeVerificationProfile.ProposeFor(["package.json", "package-lock.json"], Locator())!.Find("install")!;
        var unlocked = NodeVerificationProfile.ProposeFor(["package.json"], Locator())!.Find("install")!;

        Assert.Equal("ci", locked.Arguments[1]);
        Assert.DoesNotContain("--no-package-lock", locked.Arguments);
        Assert.Equal("install", unlocked.Arguments[1]);
        Assert.Contains("--no-package-lock", unlocked.Arguments);

        // npm starts through node and its npm-cli.js; no script of a package runs, in either.
        Assert.All(new[] { locked, unlocked }, step =>
        {
            Assert.Contains("--ignore-scripts", step.Arguments);
            Assert.Equal("node", step.Executable);
            Assert.Equal(NpmCli, step.Arguments[0]);
        });
    }

    // ---- the tools start, or the step says why they cannot ---------------------------------------------------------

    [Fact]
    public void Npm_starts_through_node_and_the_npm_cli_beside_it_never_through_its_cmd_shim()
    {
        var profile = NodeVerificationProfile.ProposeFor(["package.json"], Locator())!;

        Assert.Empty(VerificationRules.Check(profile));
        Assert.All(profile.Steps, s => Assert.Equal("node", s.Executable));
        Assert.All(profile.Steps, s => Assert.DoesNotContain(s.Arguments.Prepend(s.Executable), a => a.Contains("npm.cmd", StringComparison.OrdinalIgnoreCase) || a.Equals("cmd", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal([NpmCli, "run", "build", "--if-present"], profile.Find("build")!.Arguments);
    }

    [Fact]
    public void Without_npm_cli_beside_node_the_steps_that_need_npm_fail_and_say_why()
    {
        var bare = MakeNode(withNpmCli: false);
        try
        {
            var profile = NodeVerificationProfile.ProposeFor(["package.json"], new FakeLocator(Path.Combine(bare, "node.exe"), null))!;

            Assert.Empty(VerificationRules.Check(profile));
            foreach (var id in new[] { "install", "build" })
            {
                var step = profile.Find(id)!;
                Assert.Equal("cmd", step.Executable);
                Assert.Contains("npm-cli.js was not found next to node.exe", step.Arguments[^1], StringComparison.Ordinal);
                Assert.EndsWith("exit /b 1", step.Arguments[^1], StringComparison.Ordinal);
            }

            Assert.Equal(["install", "build", "test"], profile.StepsToRun(["test"]).Select(s => s.Id));

            var noNode = NodeVerificationProfile.ProposeFor(["package.json"], new FakeLocator(null, null))!;
            Assert.Contains("node was not found on the PATH", noNode.Find("install")!.Arguments[^1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Users\ana\AppData\Local\Microsoft\WindowsApps\python.exe", true)]
    [InlineData(@"c:/users/ana/appdata/local/microsoft/windowsapps/python.exe", true)]
    [InlineData(@"C:\Users\ana\AppData\Local\Programs\Python\Python312\python.exe", false)]
    [InlineData(@"C:\Python312\python.exe", false)]
    public void The_python_of_the_microsoft_store_is_told_apart_from_a_real_interpreter(string path, bool alias)
    {
        Assert.Equal(alias, StackStepSupport.IsStoreAlias(path));
        Assert.Equal(alias, PythonVerificationProfile.InterpreterProblem(path) is not null);
    }

    [Fact]
    public void With_the_store_alias_or_no_python_the_build_says_why_and_with_a_real_one_it_is_the_plain_profile()
    {
        var alias = PythonVerificationProfile.ProposeFor(["pyproject.toml"], Locator(@"C:\Users\ana\AppData\Local\Microsoft\WindowsApps\python.exe"))!;
        var missing = PythonVerificationProfile.ProposeFor(["pyproject.toml"], Locator(python: null))!;
        var real = PythonVerificationProfile.ProposeFor(["pyproject.toml"], Locator())!;

        Assert.Empty(VerificationRules.Check(alias));
        Assert.Equal("cmd", alias.Find("build")!.Executable);
        Assert.Contains("alias of the Microsoft Store", alias.Find("build")!.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("python was not found on the PATH", missing.Find("build")!.Arguments[^1], StringComparison.Ordinal);
        Assert.Equal("python", real.Find("build")!.Executable);
        Assert.Equal(PythonVerificationProfile.Propose().ContentHash, real.ContentHash);
        Assert.Equal("python", alias.Find("test")!.Executable);
    }

    [Theory]
    [InlineData('&')]
    [InlineData('|')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('^')]
    [InlineData('%')]
    [InlineData('"')]
    public void A_reason_with_a_character_cmd_reads_gives_a_failing_step_that_cannot_inject_anything(char character)
    {
        var reason = $@"C:\odd{character}dir\node.exe{character}calc";

        var step = StackStepSupport.Failing("build", "Compilar", EvidenceKind.BuildLog, reason);

        Assert.Equal("cmd", step.Executable);
        Assert.Equal(["/d", "/v:off", "/c"], step.Arguments.Take(3));
        var line = step.Arguments[^1];
        Assert.EndsWith(" 1>&2 & exit /b 1", line, StringComparison.Ordinal);
        var echoed = line["echo ".Length..^" 1>&2 & exit /b 1".Length];
        Assert.Equal(StackStepSupport.SafeMessage(reason), echoed);

        // Six of the seven characters come escaped with a caret; the double quote is printed as a single one.
        var escaped = character == '"' ? "'" : "^" + character;
        Assert.Equal(@"C:\odd" + escaped + @"dir\node.exe" + escaped + "calc", echoed);
        Assert.Empty(VerificationRules.Check(new VerificationProfile("x", [step])));
    }

    [Fact]
    public void A_blank_reason_or_one_with_a_line_break_still_gives_one_line_that_fails()
    {
        Assert.Contains("no reason was given", StackStepSupport.Failing("x", "x", EvidenceKind.BuildLog, "  ").Arguments[^1], StringComparison.Ordinal);
        var multiline = StackStepSupport.Failing("x", "x", EvidenceKind.BuildLog, "a\r\n& calc").Arguments[^1];
        Assert.DoesNotContain('\n', multiline);
        Assert.DoesNotContain('\r', multiline);
        Assert.Equal("echo a  ^& calc 1>&2 & exit /b 1", multiline);
    }

    [Fact]
    public void An_odd_node_path_in_the_reason_does_not_throw_when_a_profile_is_proposed()
    {
        var odd = Path.Combine(Directory.CreateTempSubdirectory("as-odd&dir^%x-").FullName, "node.exe");
        try
        {
            File.WriteAllText(odd, string.Empty);

            var profile = NodeVerificationProfile.ProposeFor(["package.json"], new FakeLocator(odd, null), _ => null)!;

            Assert.Equal("cmd", profile.Find("install")!.Executable);
            Assert.Empty(VerificationRules.Check(profile));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(odd)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("18.18.2", false)]
    [InlineData("18.19.0", true)]
    [InlineData("19.9.0", false)]
    [InlineData("20.7.0", false)]
    [InlineData("20.8.0", true)]
    [InlineData("21.0.0", true)]
    [InlineData("24.14.0", true)]
    [InlineData("16.20.2", false)]
    public void The_junit_reporter_of_node_test_exists_from_18_19_20_8_and_21(string version, bool expected) =>
        Assert.Equal(expected, NodeVerificationProfile.HasJUnitReporter(Version.Parse(version)));

    [Fact]
    public void With_a_node_without_the_junit_reporter_the_test_step_fails_and_says_which_version_it_needs()
    {
        var old = NodeVerificationProfile.ProposeFor(["package.json"], Locator(), _ => new Version(20, 7, 0))!;
        var current = NodeVerificationProfile.ProposeFor(["package.json"], Locator(), _ => new Version(20, 8, 0))!;
        var unknown = NodeVerificationProfile.ProposeFor(["package.json"], Locator(), _ => null)!;

        var test = old.Find("test")!;
        Assert.Equal("cmd", test.Executable);
        Assert.Contains("Node 20.7.0 has no JUnit test reporter", test.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("20.8.0", test.Arguments[^1], StringComparison.Ordinal);
        Assert.Equal(["build"], test.Requires);
        Assert.Equal(TestReportFormat.None, test.Report);
        Assert.Empty(VerificationRules.Check(old));
        Assert.Equal("node", old.Find("install")!.Executable);
        Assert.Equal("node", current.Find("test")!.Executable);
        Assert.Equal(TestReportFormat.JUnit, current.Find("test")!.Report);
        Assert.Equal(current.ContentHash, unknown.ContentHash);
    }

    [Fact]
    public void A_node_executable_that_carries_no_version_resource_is_of_unknown_version()
    {
        // The real Node on this machine, if any: its version resource is read, nothing is run.
        var real = new AgentStudio.Infrastructure.Processes.ExecutableLocator().Find("node");
        if (real is not null && NodeVerificationProfile.InstalledVersion(real) is { } version)
        {
            Assert.True(version.Major >= 1);
        }

        Assert.Null(NodeVerificationProfile.InstalledVersion(NodeExe));
        Assert.Null(NodeVerificationProfile.InstalledVersion(null));
        Assert.Null(NodeVerificationProfile.InstalledVersion(Path.Combine(_node, "missing.exe")));
    }

    [Fact]
    public void Compileall_skips_the_folders_that_are_not_the_project_and_the_comment_no_longer_says_it_writes_nothing()
    {
        var build = PythonVerificationProfile.Propose().Find("build")!;

        Assert.Equal(["-m", "compileall", "-q", "-x", PythonVerificationProfile.ExcludedFolders, "."], build.Arguments);
        var excluded = new System.Text.RegularExpressions.Regex(PythonVerificationProfile.ExcludedFolders);
        foreach (var skipped in new[] { "node_modules/x/a.py", @"web\node_modules\a.py", ".venv/lib/a.py", "venv/a.py", ".git/hooks/a.py", "sub/.venv" })
        {
            Assert.Matches(excluded, skipped);
        }

        foreach (var compiled in new[] { "src/a.py", "tests/venv_helpers/a.py", "my.venv.py", "gitlike/a.py" })
        {
            Assert.DoesNotMatch(excluded, compiled);
        }
    }

    // ---- the environment of each profile points at the area of the step --------------------------------------------

    [Fact]
    public async Task A_node_step_gets_the_home_the_caches_and_an_empty_user_config_in_the_area()
    {
        var area = await new FakeIsolatedWorkAreaFactory(_areas).CreateAsync(Commit, (_, _) => Task.CompletedTask, Ct);
        using (area as IDisposable)
        {
            foreach (var executable in new[] { "node", "npm", "NPX.cmd" })
            {
                var environment = VerificationRunner.IsolatedStepEnvironment(area, executable);

                var home = area.CliHomeDirectory;
                Assert.Equal(home, environment["HOME"]);
                Assert.Equal(home, environment["USERPROFILE"]);
                Assert.Equal(home, environment["APPDATA"]);
                Assert.Equal(home, environment["LOCALAPPDATA"]);
                Assert.Equal(Path.Combine(home, "npm-cache"), environment["npm_config_cache"]);
                Assert.Equal(Path.Combine(home, "npm-prefix"), environment["npm_config_prefix"]);
                Assert.Equal("false", environment["npm_config_update_notifier"]);
                Assert.Equal("false", environment["npm_config_fund"]);
                Assert.Equal(Path.Combine(home, "npm-userconfig"), environment["npm_config_userconfig"]);
                Assert.True(File.Exists(environment["npm_config_userconfig"]));
                Assert.Equal(0, new FileInfo(environment["npm_config_userconfig"]!).Length);

                // What every isolated step gets is still there, and nothing of Python.
                Assert.Equal(area.TempDirectory, environment["TEMP"]);
                Assert.Equal(area.NuGetPackagesDirectory, environment["NUGET_PACKAGES"]);
                Assert.False(environment.ContainsKey("PIP_CACHE_DIR"));
            }
        }
    }

    [Fact]
    public async Task A_python_step_gets_the_home_the_caches_and_an_empty_pip_config_in_the_area_and_no_bytecode()
    {
        var area = await new FakeIsolatedWorkAreaFactory(_areas).CreateAsync(Commit, (_, _) => Task.CompletedTask, Ct);
        using (area as IDisposable)
        {
            var environment = VerificationRunner.IsolatedStepEnvironment(area, "python");

            var home = area.CliHomeDirectory;
            Assert.Equal(home, environment["HOME"]);
            Assert.Equal(home, environment["USERPROFILE"]);
            Assert.Equal(home, environment["APPDATA"]);
            Assert.Equal(home, environment["LOCALAPPDATA"]);
            Assert.Equal(Path.Combine(home, "pip-cache"), environment["PIP_CACHE_DIR"]);
            Assert.Equal(Path.Combine(home, "python-user"), environment["PYTHONUSERBASE"]);
            Assert.Equal("1", environment["PYTHONDONTWRITEBYTECODE"]);
            Assert.Equal(0, new FileInfo(environment["PIP_CONFIG_FILE"]!).Length);
            Assert.True(File.Exists(environment["PIP_CONFIG_FILE"]));
            Assert.False(environment.ContainsKey("npm_config_cache"));
            Assert.Equal(area.TempDirectory, environment["TEMP"]);
        }
    }

    [Fact]
    public async Task A_dotnet_step_or_one_with_no_executable_keeps_the_environment_it_had_and_an_existing_config_is_not_written()
    {
        var area = await new FakeIsolatedWorkAreaFactory(_areas).CreateAsync(Commit, (_, _) => Task.CompletedTask, Ct);
        using (area as IDisposable)
        {
            var plain = VerificationRunner.IsolatedStepEnvironment(area);
            var dotnet = VerificationRunner.IsolatedStepEnvironment(area, "dotnet");

            Assert.Equal(plain.OrderBy(p => p.Key, StringComparer.Ordinal), dotnet.OrderBy(p => p.Key, StringComparer.Ordinal));
            Assert.False(plain.ContainsKey("npm_config_cache"));
            Assert.False(plain.ContainsKey("PIP_CACHE_DIR"));

            // A config the area already has (a Low step may have written one) is read as it is, never replaced.
            File.WriteAllText(Path.Combine(area.CliHomeDirectory, "npm-userconfig"), "registry=x");
            VerificationRunner.IsolatedStepEnvironment(area, "npm");
            Assert.Equal("registry=x", File.ReadAllText(Path.Combine(area.CliHomeDirectory, "npm-userconfig")));
        }
    }

    // ---- the profiles follow the rules of a profile and chain their steps ----------------------------------------

    [Fact]
    public void The_node_proposal_installs_builds_and_tests_with_a_JUnit_report_each_step_requiring_the_one_before()
    {
        var profile = NodeVerificationProfile.Propose(true, Npm());

        Assert.Empty(VerificationRules.Check(profile));
        Assert.Equal("node", profile.Name);
        Assert.Equal(["install", "build", "test"], profile.Steps.Select(s => s.Id));
        Assert.Equal(["install"], profile.Find("build")!.Requires);
        Assert.Equal(["build"], profile.Find("test")!.Requires);
        Assert.Equal(["install", "build", "test"], profile.StepsToRun(["test"]).Select(s => s.Id));

        var test = profile.Find("test")!;
        Assert.Equal(["--test", "--test-reporter=junit", "--test-reporter-destination={results}/test.xml"], test.Arguments);
        Assert.Equal((EvidenceKind.TestRun, TestReportFormat.JUnit, "{results}/test.xml"), (test.Kind, test.Report, test.ReportPath));
        Assert.All(profile.Steps, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
    }

    [Fact]
    public void The_python_proposal_compiles_and_runs_pytest_with_a_JUnit_report_and_installs_nothing()
    {
        var profile = PythonVerificationProfile.Propose();

        Assert.Empty(VerificationRules.Check(profile));
        Assert.Equal("python", profile.Name);
        Assert.Equal(["build", "test"], profile.Steps.Select(s => s.Id));
        Assert.Equal(["build"], profile.Find("test")!.Requires);
        Assert.Equal(["build", "test"], profile.StepsToRun(["test"]).Select(s => s.Id));

        var test = profile.Find("test")!;
        Assert.Equal("python", test.Executable);
        Assert.Contains("pytest", test.Arguments);
        Assert.Contains("--junitxml={results}/test.xml", test.Arguments);
        Assert.Equal((EvidenceKind.TestRun, TestReportFormat.JUnit, "{results}/test.xml"), (test.Kind, test.Report, test.ReportPath));
        Assert.All(profile.Steps, s => Assert.DoesNotContain(s.Arguments.Prepend(s.Executable), a => a.Contains("pip", StringComparison.OrdinalIgnoreCase)));
        Assert.All(profile.Steps, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
    }

    [Fact]
    public async Task Each_proposal_is_only_a_proposal_until_it_is_saved_and_reads_back_with_the_same_hash()
    {
        var node = NodeVerificationProfile.ProposeFor(Repository("package.json", "package-lock.json"), Locator())!;
        var python = PythonVerificationProfile.ProposeFor(["pyproject.toml"], Locator())!;
        var root = Directory.CreateTempSubdirectory("as-stack-save-").FullName;
        try
        {
            Assert.Equal(VerificationProfileStatus.NotFound, (await VerificationProfileFile.LoadAsync(root, Ct)).Status);

            foreach (var profile in new[] { node, python })
            {
                await VerificationProfileFile.SaveAsync(root, profile, new ProjectStorageFiles(), Ct);

                var load = await VerificationProfileFile.LoadAsync(root, Ct);
                Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
                Assert.Equal(profile.ContentHash, load.Profile!.ContentHash);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- the system runs the steps like those of .NET -----------------------------------------------------------

    private async Task<(IReadOnlyList<VerificationResult> Results, ScriptedProcessRunner Processes)> RunTestStepAsync(VerificationProfile profile, string reportPrefix, string junit)
    {
        // The step that holds the report writes it where its argument says, as the real tool would; the others just exit.
        var processes = new ScriptedProcessRunner(spec =>
        {
            var argument = spec.Arguments.FirstOrDefault(a => a.StartsWith(reportPrefix, StringComparison.Ordinal));
            return argument is null
                ? ProcessScript.Exit(0, "ok")
                : ProcessScript.Exit(0) with
                {
                    OnStart = _ =>
                    {
                        var file = argument[reportPrefix.Length..];
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, junit, new UTF8Encoding(false));
                    },
                };
        });
        var runner = new VerificationRunner(
            processes,
            new InMemoryEvidenceStore(),
            new VerificationRunnerOptions(_scratch, _gate),
            new FakeTimeProvider(),
            new FakeIsolatedWorkAreaFactory(_areas),
            new FakeCommitArchive());
        var results = await runner.RunAsync(_workingDirectory, profile.StepsToRun(["test"]), Scope, null, Ct);
        return (results, processes);
    }

    private const string TwoPassingTests = "<testsuites><testsuite name=\"s\"><testcase name=\"a\" /><testcase name=\"b\" /></testsuite></testsuites>";

    [Fact]
    public async Task The_system_runs_the_node_steps_in_order_in_isolation_and_reads_the_JUnit_report_of_the_test()
    {
        var (results, processes) = await RunTestStepAsync(NodeVerificationProfile.Propose(true, Npm()), "--test-reporter-destination=", TwoPassingTests);

        Assert.Equal(["install", "build", "test"], results.Select(r => r.StepId));
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(2, results[2].Evidence.TestRun!.Passed);
        Assert.All(processes.Started, s => Assert.Equal("node", s.FileName));
        Assert.All(processes.Started, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
        Assert.Equal([NpmCli, "ci", "--ignore-scripts", "--no-audit", "--no-fund"], processes.Started[0].Arguments.Take(5));
    }

    [Fact]
    public async Task The_system_runs_the_python_steps_in_order_in_isolation_and_a_run_without_tests_is_not_a_success()
    {
        var (results, processes) = await RunTestStepAsync(PythonVerificationProfile.Propose(), "--junitxml=", TwoPassingTests);

        Assert.Equal(["build", "test"], results.Select(r => r.StepId));
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(2, results[1].Evidence.TestRun!.Passed);
        Assert.All(processes.Started, s => Assert.Equal("python", s.FileName));
        Assert.All(processes.Started, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));

        var (empty, _) = await RunTestStepAsync(PythonVerificationProfile.Propose(), "--junitxml=", "<testsuites><testsuite name=\"s\" /></testsuites>");

        Assert.False(empty.Single(r => r.StepId == "test").Succeeded);
    }
}
