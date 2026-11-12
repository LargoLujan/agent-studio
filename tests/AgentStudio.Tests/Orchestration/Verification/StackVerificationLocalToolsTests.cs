using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// The profiles of Node and Python launched for real at Low integrity (ADR-0017, ADR-0055), over the real work areas and a git
/// repository: <c>node --test</c> with its JUnit reporter, npm through <c>node npm-cli.js</c> on a <c>package.json</c> with no
/// dependencies and <c>--ignore-scripts</c>, and <c>compileall</c> and <c>pytest</c> on a minimal repository. Local, with no
/// network and no quota. Each test is skipped when its tool is not installed.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class StackVerificationLocalToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData('&')]
    [InlineData('|')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('^')]
    [InlineData('%')]
    [InlineData('"')]
    public async Task A_failing_step_with_a_cmd_character_in_its_reason_prints_the_reason_and_runs_nothing(char character)
    {
        var marker = Path.Combine(Path.GetTempPath(), "as-injected-" + Guid.NewGuid().ToString("N")[..8]);
        var reason = $"odd{character}dir{character}x {character}PATH{character} {character}{character} calc & echo hi > {marker}";
        var step = StackStepSupport.Failing("build", "Compilar", AgentStudio.Domain.Evidence.EvidenceKind.BuildLog, reason);

        var result = await new ProcessRunner().RunAsync(new AgentStudio.Application.Execution.ProcessSpec(step.Executable, step.Arguments, Path.GetTempPath(), Timeout: TimeSpan.FromSeconds(30)), Ct);

        try
        {
            Assert.Equal(1, result.ExitCode);
            Assert.False(File.Exists(marker), "the reason was read by cmd: " + result.StandardError);
            // Intact, but for the double quote: it prints as a single one (see StackStepSupport.SafeMessage).
            Assert.Equal(reason.Replace('"', '\''), result.StandardError.Trim());
        }
        finally
        {
            File.Delete(marker);
        }
    }

    private static VerificationRunner Runner(TempGitRepo repo, InMemoryEvidenceStore store)
    {
        var git = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(repo.WorktreeRoot));
        return new VerificationRunner(new StreamingProcessRunner(), store, new VerificationRunnerOptions(repo.Root + "-scratch"), null, new LowIntegrityWorkAreaFactory(), git);
    }

    private static EvidenceScope Scope(TempGitRepo repo) => new("stack-local", new RunId("20261110-abcabc"), new TaskId("T1"), 1, repo.Head, repo.Root);

    private static string Describe(InMemoryEvidenceStore store, VerificationResult result) =>
        $"Step '{result.StepId}' (exit {result.ExitCode}, timed out {result.TimedOut}):\n{store.Text(result.Evidence.Id)}";

    private static bool Runs(string executable, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(30_000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Delete(TempGitRepo repo)
    {
        TempFolders.Delete(repo.Root + "-scratch");
        repo.Dispose();
    }

    [Fact]
    public async Task Node_installs_builds_and_tests_at_low_integrity_with_npm_started_through_node_and_a_junit_report()
    {
        var locator = new ExecutableLocator();
        var npm = NpmLaunch.From(locator.Find("node"));
        Assert.SkipUnless(npm.CliScript is not null, "node with npm is not installed: " + npm.Problem);
        Assert.SkipUnless(Runs("node", "--version"), "node does not start.");

        using var repo = TempGitRepo.Create();
        try
        {
            repo.Write("package.json", "{\"name\":\"tiny\",\"version\":\"1.0.0\",\"private\":true}\n");
            repo.Write("sample.test.js", "const test = require('node:test');\nconst assert = require('node:assert');\ntest('adds', () => assert.strictEqual(2 + 2, 4));\ntest('joins', () => assert.strictEqual('a' + 'b', 'ab'));\n");
            repo.Run("add", "-A");
            repo.Run("commit", "-q", "-m", "proyecto node");
            var profile = NodeVerificationProfile.ProposeFor(["package.json", "sample.test.js"], locator)!;
            var store = new InMemoryEvidenceStore();

            var results = await Runner(repo, store).RunAsync(repo.Root, profile.StepsToRun(["test"]), Scope(repo), null, Ct);

            Assert.Equal(["install", "build", "test"], results.Select(r => r.StepId));
            Assert.All(results, r => Assert.True(r.Succeeded, Describe(store, r)));
            Assert.Contains("# isolation: low integrity", store.Text(results[0].Evidence.Id), StringComparison.Ordinal);
            Assert.Equal(2, results[2].Evidence.TestRun!.Passed);
            Assert.Equal(0, results[2].Evidence.TestRun!.Failed);
        }
        finally
        {
            Delete(repo);
        }
    }

    [Fact]
    public async Task Without_npm_cli_beside_node_the_isolated_step_runs_and_says_why_in_its_evidence()
    {
        Assert.SkipUnless(Runs("cmd", "/d", "/c", "exit /b 0"), "cmd does not start.");
        using var repo = TempGitRepo.Create();
        try
        {
            repo.Write("package.json", "{\"name\":\"tiny\",\"version\":\"1.0.0\"}\n");
            repo.Run("add", "-A");
            repo.Run("commit", "-q", "-m", "proyecto node");
            var profile = NodeVerificationProfile.Propose(true, NpmLaunch.From(@"C:\no-such-node\node.exe"));
            var store = new InMemoryEvidenceStore();

            var results = await Runner(repo, store).RunAsync(repo.Root, profile.StepsToRun(["install"]), Scope(repo), null, Ct);

            var install = Assert.Single(results);
            Assert.False(install.Succeeded);
            Assert.Equal(1, install.ExitCode);
            Assert.Contains("npm-cli.js was not found next to node.exe", store.Text(install.Evidence.Id), StringComparison.Ordinal);
        }
        finally
        {
            Delete(repo);
        }
    }

    [Fact]
    public async Task Python_compiles_skipping_the_folders_that_are_not_the_project_and_a_syntax_error_fails_the_build()
    {
        var python = new ExecutableLocator().Find("python");
        Assert.SkipUnless(python is not null && !StackStepSupport.IsStoreAlias(python), "a real python is not installed.");
        Assert.SkipUnless(Runs(python, "--version"), "python does not start.");

        using var repo = TempGitRepo.Create();
        try
        {
            repo.Write("pyproject.toml", "[project]\nname = \"tiny\"\nversion = \"0.1\"\n");
            repo.Write("app/main.py", "def add(a, b):\n    return a + b\n");
            repo.Write("node_modules/pkg/broken.py", "def broken(:\n");
            repo.Write(".venv/lib/broken.py", "def broken(:\n");
            repo.Run("add", "-A");
            repo.Run("commit", "-q", "-m", "proyecto python");
            var profile = PythonVerificationProfile.ProposeFor(["pyproject.toml"], new ExecutableLocator())!;
            var store = new InMemoryEvidenceStore();

            var good = Assert.Single(await Runner(repo, store).RunAsync(repo.Root, [profile.Find("build")!], Scope(repo), null, Ct));

            Assert.True(good.Succeeded, Describe(store, good));
            Assert.Contains("# isolation: low integrity", store.Text(good.Evidence.Id), StringComparison.Ordinal);

            repo.Write("app/broken.py", "def broken(:\n");
            repo.Run("add", "-A");
            repo.Run("commit", "-q", "-m", "error de sintaxis");
            var bad = Assert.Single(await Runner(repo, store).RunAsync(repo.Root, [profile.Find("build")!], Scope(repo), null, Ct));

            Assert.False(bad.Succeeded);
            Assert.Contains("broken.py", store.Text(bad.Evidence.Id), StringComparison.Ordinal);
        }
        finally
        {
            Delete(repo);
        }
    }

    [Fact]
    public async Task Pytest_runs_at_low_integrity_and_its_junit_report_is_read()
    {
        var python = new ExecutableLocator().Find("python");
        Assert.SkipUnless(python is not null && !StackStepSupport.IsStoreAlias(python), "a real python is not installed.");
        Assert.SkipUnless(Runs(python, "-m", "pytest", "--version"), "pytest is not installed for that python.");

        using var repo = TempGitRepo.Create();
        try
        {
            repo.Write("pyproject.toml", "[project]\nname = \"tiny\"\nversion = \"0.1\"\n");
            repo.Write("test_sample.py", "def test_adds():\n    assert 2 + 2 == 4\n\n\ndef test_joins():\n    assert 'a' + 'b' == 'ab'\n");
            repo.Run("add", "-A");
            repo.Run("commit", "-q", "-m", "proyecto python");
            var profile = PythonVerificationProfile.ProposeFor(["pyproject.toml"], new ExecutableLocator())!;
            var store = new InMemoryEvidenceStore();

            var results = await Runner(repo, store).RunAsync(repo.Root, profile.StepsToRun(["test"]), Scope(repo), null, Ct);

            Assert.Equal(["build", "test"], results.Select(r => r.StepId));
            Assert.All(results, r => Assert.True(r.Succeeded, Describe(store, r)));
            Assert.Equal(2, results[1].Evidence.TestRun!.Passed);
        }
        finally
        {
            Delete(repo);
        }
    }
}
