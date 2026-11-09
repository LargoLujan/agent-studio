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

    public void Dispose()
    {
        foreach (var folder in new[] { _repository, _workingDirectory, _scratch, _areas })
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
        Assert.Null(NodeVerificationProfile.ProposeFor(["main.py"]));
    }

    [Theory]
    [InlineData("pyproject.toml")]
    [InlineData("requirements.txt")]
    public void Python_is_detected_by_its_files_in_the_root(string marker)
    {
        Assert.True(PythonVerificationProfile.Detect(Repository(marker, "app/main.py")));
        Assert.False(PythonVerificationProfile.Detect(["sub/" + marker, "package.json"]));
        Assert.Null(PythonVerificationProfile.ProposeFor(["package.json"]));
    }

    [Fact]
    public void A_lock_file_makes_the_install_step_a_clean_install_and_without_one_it_never_writes_a_lock()
    {
        var locked = NodeVerificationProfile.ProposeFor(["package.json", "package-lock.json"])!.Find("install")!;
        var unlocked = NodeVerificationProfile.ProposeFor(["package.json"])!.Find("install")!;

        Assert.Equal("ci", locked.Arguments[0]);
        Assert.DoesNotContain("--no-package-lock", locked.Arguments);
        Assert.Equal("install", unlocked.Arguments[0]);
        Assert.Contains("--no-package-lock", unlocked.Arguments);

        // No script of a package runs, in either, and the cache is in the results folder of the step.
        Assert.All(new[] { locked, unlocked }, step =>
        {
            Assert.Contains("--ignore-scripts", step.Arguments);
            Assert.Contains("{results}/npm-cache", step.Arguments);
            Assert.Equal("npm", step.Executable);
        });
    }

    // ---- the profiles follow the rules of a profile and chain their steps ----------------------------------------

    [Fact]
    public void The_node_proposal_installs_builds_and_tests_with_a_JUnit_report_each_step_requiring_the_one_before()
    {
        var profile = NodeVerificationProfile.Propose(hasLockFile: true);

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
        var node = NodeVerificationProfile.ProposeFor(Repository("package.json", "package-lock.json"))!;
        var python = PythonVerificationProfile.ProposeFor(["pyproject.toml"])!;
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
        var (results, processes) = await RunTestStepAsync(NodeVerificationProfile.Propose(hasLockFile: true), "--test-reporter-destination=", TwoPassingTests);

        Assert.Equal(["install", "build", "test"], results.Select(r => r.StepId));
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(2, results[2].Evidence.TestRun!.Passed);
        Assert.Equal(["npm", "npm", "node"], processes.Started.Select(s => s.FileName));
        Assert.All(processes.Started, s => Assert.Equal(ProcessIsolation.LowIntegrity, s.Isolation));
        Assert.Equal(["ci", "--ignore-scripts", "--no-audit", "--no-fund"], processes.Started[0].Arguments.Take(4));
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
