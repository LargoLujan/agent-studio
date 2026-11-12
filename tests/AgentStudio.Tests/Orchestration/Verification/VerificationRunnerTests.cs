using System.ComponentModel;
using System.IO;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Orchestration.Verification.Reports;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Verification;

public sealed class VerificationRunnerTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private const string Repository = @"C:\proyectos\calc";

    // The engine and the integrator name the commit under verification and the project's repository it comes from.
    private static readonly EvidenceScope Scope = new("proyecto-1", new RunId("20261015-a1b2c3"), new TaskId("T1"), 2, Commit, Repository);

    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("as-verify-wd-").FullName;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "as-verify-scratch-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _areas = Path.Combine(Path.GetTempPath(), "as-verify-areas-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly InMemoryEvidenceStore _store = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeIsolatedWorkAreaFactory _isolation;
    private readonly FakeCommitArchive _archive = new();

    // A gate of these tests alone: the one the process shares is held by the slow isolated runs of other test classes.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VerificationRunnerTests() => _isolation = new FakeIsolatedWorkAreaFactory(_areas);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Every step is isolated unless it says otherwise (ADR-0017), so the runner gets a work area factory and the git layer
    // that copies the commit into each area: fake ones here.
    private VerificationRunner Runner(ScriptedProcessRunner processes, IIsolatedWorkAreaFactory? isolation = null, FakeCommitArchive? archive = null) =>
        new(processes, _store, new VerificationRunnerOptions(_scratch, _gate), _time, isolation ?? _isolation, archive ?? _archive);

    private static VerificationStep Build() => new(
        "build", "Compilar", "dotnet", ["build", "App.slnx"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(10));

    private static VerificationStep TrxTest(string reportPath = "{results}/test.trx") => new(
        "test",
        "Pruebas",
        "dotnet",
        ["test", "--no-build", "--results-directory", "{results}", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "test.trx"],
        EvidenceKind.TestRun,
        TestReportFormat.Trx,
        reportPath,
        TimeSpan.FromMinutes(20));

    private static string Trx(params string[] outcomes) =>
        $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results>"
        + string.Concat(outcomes.Select((outcome, index) => $"<UnitTestResult testName=\"prueba{index}\" outcome=\"{outcome}\" />"))
        + "</Results></TestRun>";

    /// <summary>Writes the report into the folder given after <c>--results-directory</c>, as <c>dotnet test</c> does.</summary>
    private static Action<ProcessSpec> WritesReport(string fileName, string content) => spec =>
    {
        var arguments = spec.Arguments.ToList();
        var folder = arguments[arguments.IndexOf("--results-directory") + 1];
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), content, new UTF8Encoding(false));
    };

    // ---- what runs, and where ---------------------------------------------------------------------------------

    [Fact]
    public async Task By_default_a_step_runs_at_Low_integrity_in_a_copy_of_the_commit_with_the_environment_of_its_work_area()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0, "Build succeeded."));

        await Runner(processes).RunAsync(_workingDirectory, [Build()], Scope, null, Ct);

        var spec = Assert.Single(processes.Started);
        var area = Assert.Single(_isolation.Areas);
        Assert.Equal([Commit], _isolation.Calls);
        Assert.Equal(ProcessIsolation.LowIntegrity, spec.Isolation);
        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal(["build", "App.slnx"], spec.Arguments);
        Assert.Equal(area.SourceDirectory, spec.WorkingDirectory);
        Assert.Null(spec.StandardInput);
        Assert.NotNull(spec.Environment);
        Assert.Equal("1", spec.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", spec.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("false", spec.Environment["UseSharedCompilation"]);
        Assert.Equal("en", spec.Environment["DOTNET_CLI_UI_LANGUAGE"]);

        // Spike: everything the SDK, NuGet and the test host write goes to the Low folders of this run alone.
        Assert.Equal(area.NuGetPackagesDirectory, spec.Environment["NUGET_PACKAGES"]);
        Assert.Equal(area.NuGetHttpCacheDirectory, spec.Environment["NUGET_HTTP_CACHE_PATH"]);
        Assert.Equal(area.NuGetPluginsCacheDirectory, spec.Environment["NUGET_PLUGINS_CACHE_PATH"]);
        Assert.Equal(area.CliHomeDirectory, spec.Environment["DOTNET_CLI_HOME"]);
        Assert.Equal(area.TempDirectory, spec.Environment["TEMP"]);
        Assert.Equal(area.TempDirectory, spec.Environment["TMP"]);
        Assert.Equal("1", spec.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
        Assert.Equal("1", spec.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", spec.Environment["DOTNET_NOLOGO"]);
        Assert.Equal("false", spec.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]);
        Assert.Equal("false", spec.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"]);

        // The area lives as long as the run.
        Assert.True(area.Disposed);
        Assert.False(Directory.Exists(area.RootDirectory));
    }

    [Fact]
    public async Task The_copy_of_an_isolated_step_comes_from_the_repository_the_scope_names_never_from_the_working_directory()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));

        var result = Assert.Single(await Runner(processes).RunAsync(_workingDirectory, [Build()], Scope, null, Ct));

        // The git layer copies the commit from the project's repository into the copy of the area, and nothing else.
        var area = Assert.Single(_isolation.Areas);
        Assert.Equal([(Repository, Commit, area.SourceDirectory)], _archive.Calls);
        Assert.Contains(
            $"# source: commit {Commit}, copied with git archive from the objects of the repository '{Repository}', never through the .git of '{_workingDirectory}'",
            _store.Text(result.Evidence.Id),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("repository")]
    public async Task An_isolated_step_whose_scope_names_no_commit_or_no_repository_is_not_run(string missing)
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));
        var scope = missing == "commit" ? Scope with { SubjectCommit = null } : Scope with { RepositoryRoot = null };

        var result = Assert.Single(await Runner(processes).RunAsync(_workingDirectory, [Build(), TrxTest()], scope, null, Ct));

        Assert.Empty(processes.Started);
        Assert.Empty(_isolation.Calls);
        Assert.Empty(_archive.Calls);
        Assert.False(result.Succeeded);
        Assert.Contains($"the scope names no {missing}", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_git_layer_an_isolated_step_is_not_run_and_never_runs_without_isolation()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));
        var runner = new VerificationRunner(processes, _store, new VerificationRunnerOptions(_scratch, _gate), _time, _isolation);

        var result = Assert.Single(await runner.RunAsync(_workingDirectory, [Build()], Scope, null, Ct));

        Assert.Empty(processes.Started);
        Assert.Empty(_isolation.Calls);
        Assert.False(result.Succeeded);
        Assert.Contains("IWorktreeManager", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_whose_profile_turns_isolation_off_runs_as_before_in_the_working_directory_with_the_fixed_environment()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0, "Build succeeded."));

        await Runner(processes).RunAsync(_workingDirectory, [Build() with { Isolation = ProcessIsolation.None }], Scope, null, Ct);

        var spec = Assert.Single(processes.Started);
        Assert.Empty(_isolation.Calls);
        Assert.Equal(ProcessIsolation.None, spec.Isolation);
        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal(["build", "App.slnx"], spec.Arguments);
        Assert.Equal(_workingDirectory, spec.WorkingDirectory);
        Assert.Null(spec.StandardInput);
        Assert.Equal(VerificationRunner.StepEnvironment.OrderBy(e => e.Key), spec.Environment!.OrderBy(e => e.Key));
    }

    [Fact]
    public async Task Turning_isolation_off_shows_in_the_title_and_in_the_header_of_the_evidence()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0));

        var results = await Runner(processes).RunAsync(
            _workingDirectory, [Build() with { Isolation = ProcessIsolation.None }, Build() with { Id = "lint", Title = "Revisar" }], Scope, null, Ct);

        Assert.Equal("Compilar (build) · sin aislamiento", _store.Single(results[0].Evidence.Id).Draft.Title);
        Assert.Contains("# isolation: none", _store.Text(results[0].Evidence.Id), StringComparison.Ordinal);
        Assert.Equal("Revisar (lint)", _store.Single(results[1].Evidence.Id).Draft.Title);
        var isolated = _store.Text(results[1].Evidence.Id);
        Assert.Contains("# isolation: low integrity", isolated, StringComparison.Ordinal);
        Assert.Contains("# source: commit " + Commit, isolated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_steps_of_one_run_share_one_work_area_so_a_test_step_finds_what_the_build_step_produced()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0) with { OnStart = WritesReport("test.trx", Trx("Passed")) });

        var results = await Runner(processes).RunAsync(_workingDirectory, [Build(), TrxTest()], Scope, null, Ct);

        Assert.All(results, r => Assert.True(r.Succeeded, _store.Text(r.Evidence.Id)));
        var area = Assert.Single(_isolation.Areas);
        Assert.All(processes.Started, s => Assert.Equal(area.SourceDirectory, s.WorkingDirectory));
        Assert.All(results, r => Assert.Equal(Commit, r.Evidence.SubjectCommit));
    }

    [Fact]
    public async Task Without_a_work_area_factory_an_isolated_step_is_not_run_at_all_and_never_runs_without_isolation()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0));
        var runner = new VerificationRunner(processes, _store, new VerificationRunnerOptions(_scratch, _gate), _time);

        var result = Assert.Single(await runner.RunAsync(_workingDirectory, [Build(), TrxTest()], Scope, null, Ct));

        Assert.Empty(processes.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("isolat", _store.Text(result.Evidence.Id), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task When_the_work_area_cannot_be_prepared_nothing_runs_and_the_evidence_says_why()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));
        var failing = new FakeIsolatedWorkAreaFactory(_areas) { Failure = new ProcessIsolationException("git archive no pudo copiar el commit") };

        var result = Assert.Single(await Runner(processes, failing).RunAsync(_workingDirectory, [Build(), TrxTest()], Scope, null, Ct));

        Assert.Empty(processes.Started);
        Assert.False(result.Succeeded);
        Assert.Contains("git archive no pudo copiar el commit", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_that_cannot_be_isolated_fails_the_step_with_evidence()
    {
        var processes = new ScriptedProcessRunner(new ProcessScript(StartFailure: new ProcessIsolationException("Agent Studio runs elevated")));

        var result = Assert.Single(await Runner(processes).RunAsync(_workingDirectory, [Build()], Scope, null, Ct));

        Assert.False(result.Succeeded);
        Assert.Contains("Agent Studio runs elevated", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_isolated_step_whose_report_path_leaves_the_work_area_is_not_run()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));
        var step = TrxTest(Path.Combine(_scratch, "fuera.trx")) with { Arguments = ["test", "--no-build"] };

        var result = Assert.Single(await Runner(processes).RunAsync(_workingDirectory, [step], Scope, null, Ct));

        Assert.Empty(processes.Started);
        Assert.False(result.Succeeded);
        Assert.Contains("work area", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Isolated_runs_happen_one_at_a_time_even_from_different_runners()
    {
        var factory = new FakeIsolatedWorkAreaFactory(_areas);
        var first = new ScriptedProcessRunner(ProcessScript.Hanging("primera"));
        var second = new ScriptedProcessRunner(ProcessScript.Exit(0));
        using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var running = Runner(first, factory).RunAsync(_workingDirectory, [Build()], Scope, null, stopFirst.Token);
        await first.FirstStart.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var waiting = Runner(second, factory).RunAsync(_workingDirectory, [Build()], Scope, null, Ct);
        await Task.Delay(300, Ct);

        Assert.Single(factory.Calls);
        Assert.Empty(second.Started);

        await stopFirst.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var result = Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.True(result.Succeeded);
        Assert.Equal(1, factory.MostAliveAtOnce);
        Assert.All(factory.Areas, a => Assert.True(a.Disposed));
    }

    [Fact]
    public async Task A_step_that_exits_with_zero_succeeds_and_its_whole_output_is_captured_as_system_evidence()
    {
        var script = new ProcessScript(0, [ProcessScript.Out("linea 1"), ProcessScript.Err("aviso"), ProcessScript.Out("linea 2")]);
        var processes = new ScriptedProcessRunner(script);

        var result = Assert.Single(await Runner(processes).RunAsync(_workingDirectory, [Build()], Scope, null, Ct));

        Assert.Equal("build", result.StepId);
        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);

        var item = Assert.Single(_store.Items);
        Assert.Equal(item.Record, result.Evidence);
        Assert.Equal("proyecto-1", item.ProjectId);
        Assert.Equal(EvidenceKind.BuildLog, item.Draft.Kind);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, item.Draft.Origin);
        Assert.Equal("Compilar (build)", item.Draft.Title);
        Assert.Equal("agent-studio", item.Draft.ProducedBy);
        Assert.Equal("20261015-a1b2c3", item.Draft.RunId);
        Assert.Equal("T1", item.Draft.TaskId);
        Assert.Equal(2, item.Draft.Attempt);
        Assert.Equal("text/plain; charset=utf-8", item.Draft.MediaType);
        Assert.Null(item.Draft.TestRun);

        var text = _store.Text(result.Evidence.Id);
        var first = text.IndexOf("linea 1", StringComparison.Ordinal);
        var warning = text.IndexOf("stderr| aviso", StringComparison.Ordinal);
        var second = text.IndexOf("linea 2", StringComparison.Ordinal);
        Assert.True(first >= 0 && warning > first && second > warning, text);
        Assert.Contains("# exit code: 0", text, StringComparison.Ordinal);
        Assert.Contains("# result: succeeded", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_step_is_bound_to_the_commit_the_scope_says_the_working_directory_is_on()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var report = Trx("Passed");
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0) with { OnStart = WritesReport("test.trx", report) });

        var results = await Runner(processes).RunAsync(_workingDirectory, [Build(), TrxTest()], Scope with { SubjectCommit = commit }, null, Ct);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(commit, r.Evidence.SubjectCommit));
        Assert.All(_store.Items, i => Assert.Equal(commit, i.Draft.SubjectCommit));
    }

    [Fact]
    public async Task Evidence_of_a_scope_that_names_no_commit_verifies_no_commit()
    {
        var step = Build() with { Isolation = ProcessIsolation.None };

        var result = Assert.Single(await Runner(new ScriptedProcessRunner(ProcessScript.Exit(0))).RunAsync(_workingDirectory, [step], Scope with { SubjectCommit = null }, null, Ct));

        Assert.True(result.Succeeded);
        Assert.Null(result.Evidence.SubjectCommit);
    }

    [Fact]
    public async Task A_step_that_exits_with_another_code_fails_and_the_steps_after_it_do_not_run()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(1, "error CS1002"), ProcessScript.Exit(0));

        var results = await Runner(processes).RunAsync(_workingDirectory, [Build(), TrxTest()], Scope, null, Ct);

        var result = Assert.Single(results);
        Assert.Equal("build", result.StepId);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Single(processes.Started);
        Assert.Contains("# result: failed", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_lines_are_reported_as_they_arrive()
    {
        var script = new ProcessScript(0, [ProcessScript.Out("uno"), ProcessScript.Err("dos")]);
        var seen = new List<ProcessOutputLine>();

        await Runner(new ScriptedProcessRunner(script)).RunAsync(_workingDirectory, [Build()], Scope, new Collect(seen), Ct);

        Assert.Equal([(ProcessStream.StandardOutput, "uno"), (ProcessStream.StandardError, "dos")], seen.Select(l => (l.Stream, l.Text)));
    }

    // ---- test reports -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_test_step_reads_its_TRX_report_and_succeeds_with_a_passed_test()
    {
        var script = ProcessScript.Exit(0, "Test run summary: Passed!") with { OnStart = WritesReport("test.trx", Trx("Passed", "Passed", "NotExecuted")) };

        var result = Assert.Single(await Runner(new ScriptedProcessRunner(script)).RunAsync(_workingDirectory, [TrxTest()], Scope, null, Ct));

        Assert.True(result.Succeeded);
        Assert.Equal(EvidenceKind.TestRun, result.Evidence.Kind);
        Assert.Equal(new TestRunSummary(Passed: 2, Failed: 0, Skipped: 1, ExitCode: 0), result.Evidence.TestRun);
        Assert.Contains("# tests: passed 2, failed 0, skipped 1", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_step_with_a_failed_test_fails()
    {
        var script = ProcessScript.Exit(2) with { OnStart = WritesReport("test.trx", Trx("Passed", "Failed")) };

        var result = Assert.Single(await Runner(new ScriptedProcessRunner(script)).RunAsync(_workingDirectory, [TrxTest()], Scope, null, Ct));

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(new TestRunSummary(1, 1, 0, 2), result.Evidence.TestRun);
    }

    [Fact]
    public async Task A_failed_test_step_carries_the_names_of_the_tests_its_report_says_failed()
    {
        var report = $"<TestRun xmlns=\"{TrxReportParser.Namespace}\"><Results>"
            + "<UnitTestResult testName=\"Suma_devuelve_cuatro\" outcome=\"Passed\" />"
            + "<UnitTestResult testName=\"Resta_devuelve_cero\" outcome=\"Failed\"><Output><ErrorInfo><Message>Assert.Equal() Failure: esperado 0</Message><StackTrace>en Calc.Resta</StackTrace></ErrorInfo></Output></UnitTestResult>"
            + "</Results></TestRun>";
        var script = ProcessScript.Exit(2) with { OnStart = WritesReport("test.trx", report) };

        var result = Assert.Single(await Runner(new ScriptedProcessRunner(script)).RunAsync(_workingDirectory, [TrxTest()], Scope, null, Ct));

        var failed = Assert.Single(result.FailedTests!);
        Assert.Equal("Resta_devuelve_cero", failed.Name);
        Assert.Equal("Assert.Equal() Failure: esperado 0", failed.Message);
    }

    [Fact]
    public async Task A_step_that_passes_or_has_no_report_carries_no_failed_tests()
    {
        var passing = ProcessScript.Exit(0) with { OnStart = WritesReport("test.trx", Trx("Passed", "Passed")) };

        var passed = Assert.Single(await Runner(new ScriptedProcessRunner(passing)).RunAsync(_workingDirectory, [TrxTest()], Scope, null, Ct));
        var build = Assert.Single(await Runner(new ScriptedProcessRunner(ProcessScript.Exit(0))).RunAsync(_workingDirectory, [Build()], Scope, null, Ct));

        Assert.Empty(passed.FailedTests!);
        Assert.Empty(build.FailedTests!);
    }

    [Theory]
    [InlineData("", 8)]
    [InlineData("", 0)]
    [InlineData("NotExecuted", 0)]
    public async Task A_test_step_that_passed_no_test_is_not_a_success_whatever_its_exit_code(string outcomes, int exitCode)
    {
        var report = Trx(outcomes.Length == 0 ? [] : outcomes.Split(','));
        var script = ProcessScript.Exit(exitCode) with { OnStart = WritesReport("test.trx", report) };

        var result = Assert.Single(await Runner(new ScriptedProcessRunner(script)).RunAsync(_workingDirectory, [TrxTest()], Scope, null, Ct));

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Evidence.TestRun!.Passed);
    }

    [Fact]
    public async Task A_JUnit_report_is_read_as_well()
    {
        var step = TrxTest("{results}/test.xml") with { Report = TestReportFormat.JUnit };
        const string junit = "<testsuites><testsuite><testcase name=\"a\" /><testcase name=\"b\"><failure message=\"x\" /></testcase></testsuite></testsuites>";
        var script = ProcessScript.Exit(2) with { OnStart = WritesReport("test.xml", junit) };

        var result = Assert.Single(await Runner(new ScriptedProcessRunner(script)).RunAsync(_workingDirectory, [step], Scope, null, Ct));

        Assert.Equal(new TestRunSummary(1, 1, 0, 2), result.Evidence.TestRun);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_missing_report_fails_the_step_even_with_exit_code_zero_and_the_evidence_says_why()
    {
        var result = Assert.Single(await Runner(new ScriptedProcessRunner(ProcessScript.Exit(0))).RunAsync(_workingDirectory, [TrxTest()], Scope, null, Ct));

        Assert.False(result.Succeeded);
        Assert.Equal(new TestRunSummary(0, 0, 0, 0), result.Evidence.TestRun);
        var text = _store.Text(result.Evidence.Id);
        Assert.Contains("# report problem:", text, StringComparison.Ordinal);
        Assert.Contains("# result: failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_left_at_the_report_path_before_a_step_without_isolation_is_deleted_and_never_counted()
    {
        var step = TrxTest("out/test.trx") with { Arguments = ["test", "--no-build"], Isolation = ProcessIsolation.None };
        var planted = Path.Combine(_workingDirectory, "out", "test.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllText(planted, Trx("Passed", "Passed"));

        var result = Assert.Single(await Runner(new ScriptedProcessRunner(ProcessScript.Exit(0))).RunAsync(_workingDirectory, [step], Scope, null, Ct));

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(planted), "A report written before the step must be removed, not read.");
    }

    [Theory]
    [InlineData("out/test.trx")]
    [InlineData("{results}/../src/out/test.trx")]
    public async Task An_isolated_step_whose_report_is_not_in_its_fresh_results_folder_is_not_run(string reportPath)
    {
        // A path in the copy may be a link an earlier step left: the runner never clears it with the rights of the user.
        var step = TrxTest(reportPath);
        var planted = Path.Combine(_workingDirectory, "out", "test.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllText(planted, Trx("Passed", "Passed"));
        var copying = new FakeCommitArchive { CopyFrom = _workingDirectory };
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));

        var result = Assert.Single(await Runner(processes, archive: copying).RunAsync(_workingDirectory, [step], Scope, null, Ct));

        Assert.Empty(processes.Started);
        Assert.False(result.Succeeded);
        Assert.Contains("not inside the fresh results folder", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
        Assert.True(File.Exists(planted), "Verification must not write the working directory.");
    }

    [Fact]
    public async Task The_results_token_becomes_a_fresh_folder_outside_the_working_directory_that_is_removed_afterwards()
    {
        string? folder = null;
        var wasEmpty = false;
        var script = ProcessScript.Exit(0) with
        {
            OnStart = spec =>
            {
                var arguments = spec.Arguments.ToList();
                folder = arguments[arguments.IndexOf("--results-directory") + 1];
                wasEmpty = Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any();
                WritesReport("test.trx", Trx("Passed"))(spec);
            },
        };
        var processes = new ScriptedProcessRunner(script);

        var result = Assert.Single(await Runner(processes).RunAsync(_workingDirectory, [TrxTest()], Scope, null, Ct));

        Assert.True(result.Succeeded);
        Assert.NotNull(folder);
        Assert.True(Path.IsPathFullyQualified(folder));
        Assert.True(wasEmpty, "The results folder must exist and be empty when the step starts.");
        Assert.False(Path.GetFullPath(folder).StartsWith(_workingDirectory, StringComparison.OrdinalIgnoreCase), folder);
        Assert.DoesNotContain(processes.Started.Single().Arguments, a => a.Contains("{results}", StringComparison.Ordinal));
        Assert.False(Directory.Exists(folder), "The results folder must be removed when the step ends.");
        Assert.Empty(Directory.Exists(_scratch) ? Directory.EnumerateFileSystemEntries(_scratch) : []);
    }

    // ---- timeouts, cancellation and failures to start ---------------------------------------------------------

    [Fact]
    public async Task A_step_that_outlives_its_timeout_is_killed_and_reported_as_timed_out()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Hanging("esperando"));
        var step = Build() with { Timeout = TimeSpan.FromMinutes(5) };

        var run = Runner(processes).RunAsync(_workingDirectory, [step], Scope, null, Ct);
        await processes.FirstStart.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await Task.Delay(100, Ct);
        Assert.False(processes.Sessions.Single().Killed, "The step was killed before its timeout.");

        _time.Advance(TimeSpan.FromSeconds(1));
        var result = Assert.Single(await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.True(processes.Sessions.Single().Killed);
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        var text = _store.Text(result.Evidence.Id);
        Assert.Contains("esperando", text, StringComparison.Ordinal);
        Assert.Contains("# timed out: yes", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_run_kills_the_step_and_captures_nothing()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Hanging("trabajando"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = Runner(processes).RunAsync(_workingDirectory, [Build()], Scope, null, cancellation.Token);
        await processes.FirstStart.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(processes.Sessions.Single().Killed);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task A_step_whose_program_cannot_start_fails_with_evidence_and_stops_the_run()
    {
        var processes = new ScriptedProcessRunner(new ProcessScript(StartFailure: new Win32Exception(2, "No se encuentra el fichero dotnet")));

        var results = await Runner(processes).RunAsync(_workingDirectory, [Build(), TrxTest()], Scope, null, Ct);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        Assert.Single(processes.Started);
        Assert.Contains("No se encuentra el fichero dotnet", _store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    // ---- what is refused ----------------------------------------------------------------------------------------

    public static TheoryData<string> InvalidSteps() =>
    [
        "test-run-without-report", "report-without-test-run", "report-without-path", "path-without-report", "zero-timeout",
        "negative-timeout", "bad-id", "duplicate-id", "blank-executable", "argument-with-newline", "document-kind", "unknown-isolation",
        "requirement-not-in-the-list", "requirement-after-it", "requirement-with-another-isolation", "requires-itself",
    ];

    [Theory]
    [MemberData(nameof(InvalidSteps))]
    public async Task Invalid_steps_are_refused_before_anything_runs(string problem)
    {
        VerificationStep[] steps = problem switch
        {
            "test-run-without-report" => [TrxTest() with { Report = TestReportFormat.None, ReportPath = null }],
            "report-without-test-run" => [TrxTest() with { Kind = EvidenceKind.BuildLog }],
            "report-without-path" => [TrxTest() with { ReportPath = " " }],
            "path-without-report" => [Build() with { ReportPath = "x.trx" }],
            "zero-timeout" => [Build() with { Timeout = TimeSpan.Zero }],
            "negative-timeout" => [Build() with { Timeout = TimeSpan.FromSeconds(-1) }],
            "bad-id" => [Build() with { Id = "-build" }],
            "duplicate-id" => [Build(), Build() with { Title = "Otra" }],
            "blank-executable" => [Build() with { Executable = " " }],
            "argument-with-newline" => [Build() with { Arguments = ["build", "a\nb"] }],
            "unknown-isolation" => [Build() with { Isolation = (ProcessIsolation)7 }],

            // B6: a test with --no-build never runs where its build did not run first.
            "requirement-not-in-the-list" => [TrxTest() with { Requires = ["build"] }],
            "requirement-after-it" => [TrxTest() with { Requires = ["build"] }, Build()],
            "requirement-with-another-isolation" => [Build() with { Isolation = ProcessIsolation.None }, TrxTest() with { Requires = ["build"] }],
            "requires-itself" => [TrxTest() with { Requires = ["test"] }],
            _ => [Build() with { Kind = EvidenceKind.Document }],
        };
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0));

        await Assert.ThrowsAsync<ArgumentException>(() => Runner(processes).RunAsync(_workingDirectory, steps, Scope, null, Ct));

        Assert.Empty(processes.Started);
        Assert.Empty(_isolation.Calls);
    }

    [Fact]
    public async Task A_test_that_requires_the_build_runs_after_it_in_the_same_copy_and_not_at_all_when_it_fails()
    {
        var steps = DotNetVerificationProfile.Propose().StepsToRun(["test"]);
        var failing = new ScriptedProcessRunner(ProcessScript.Exit(1, "error CS1002"));

        var results = await Runner(failing).RunAsync(_workingDirectory, steps, Scope, null, Ct);

        Assert.Equal(["build", "test"], steps.Select(s => s.Id));
        var build = Assert.Single(results);
        Assert.Equal("build", build.StepId);
        Assert.False(build.Succeeded);
        Assert.Equal(["build"], Assert.Single(failing.Started).Arguments);

        var passing = new ScriptedProcessRunner(ProcessScript.Exit(0), ProcessScript.Exit(0) with { OnStart = WritesReport("test.trx", Trx("Passed")) });
        var both = await Runner(passing).RunAsync(_workingDirectory, steps, Scope, null, Ct);

        Assert.Equal(["build", "test"], both.Select(r => r.StepId));
        Assert.All(both, r => Assert.True(r.Succeeded));
        Assert.Equal(passing.Started[0].WorkingDirectory, passing.Started[1].WorkingDirectory);
    }

    [Fact]
    public async Task The_working_directory_must_exist()
    {
        var processes = new ScriptedProcessRunner(ProcessScript.Exit(0));
        var missing = Path.Combine(_workingDirectory, "no-existe");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Runner(processes).RunAsync(missing, [Build()], Scope, null, Ct));

        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task No_steps_run_nothing()
    {
        var processes = new ScriptedProcessRunner();

        Assert.Empty(await Runner(processes).RunAsync(_workingDirectory, [], Scope, null, Ct));
        Assert.Empty(processes.Started);
        Assert.Empty(_isolation.Calls);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _workingDirectory, _scratch, _areas })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        _gate.Dispose();
    }

    /// <summary>A synchronous <see cref="IProgress{T}"/> (Progress&lt;T&gt; would post to a synchronization context).</summary>
    private sealed class Collect(List<ProcessOutputLine> lines) : IProgress<ProcessOutputLine>
    {
        public void Report(ProcessOutputLine value) => lines.Add(value);
    }
}
