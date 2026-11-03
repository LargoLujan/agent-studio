using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Verification;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// The real <see cref="VerificationRunner"/> over the real <see cref="StreamingProcessRunner"/>, <c>dotnet build</c>
/// and <c>dotnet test</c> on a tiny xUnit v3 project in a temporary folder, restored from the local NuGet cache with
/// the package versions of this repository. The evidence store is the in-memory double (the SQLite one is another
/// package's).
/// </summary>
/// <remarks>
/// Every step here says <see cref="ProcessIsolation.None"/>: the project is built once by the fixture and the tests run
/// <c>--no-build</c> on it in later runs, and the hanging test leaves its PID in the project's output folder, which only
/// holds when the steps run in place. They are the steps "without isolation" of ADR-0017: they still run as before, and
/// their evidence says so. The isolated path has its own tests (VerificationRunnerIsolationLocalToolsTests).
/// </remarks>
[Trait("Category", "LocalTools")]
public sealed class VerificationRunnerLocalToolsTests(TinyDotNetProject project) : IClassFixture<TinyDotNetProject>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EvidenceScope Scope => new(TinyDotNetProject.ProjectId, new RunId("20261016-c0ffee"), new TaskId("T1"), 1);

    /// <summary>
    /// The test step of the proposed .NET profile, limited to one test class of the tiny project. It requires nothing here:
    /// the fixture built the project in place once, and these tests hand the step to the runner alone, which would refuse a
    /// step whose required build is not before it (the profile's own test requires its build).
    /// </summary>
    private static VerificationStep TestStep(string testClass, bool zeroTestsPolicy = true, TimeSpan? timeout = null)
    {
        var step = DotNetVerificationProfile.Propose().Find("test")!;
        IEnumerable<string> arguments = zeroTestsPolicy
            ? step.Arguments
            : step.Arguments.Take(step.Arguments.Count - 2);
        return step with
        {
            Arguments = [.. arguments, "--filter-class", "Tiny.Tests." + testClass],
            Timeout = timeout ?? TimeSpan.FromMinutes(3),
            Isolation = ProcessIsolation.None,
            Requires = null,
        };
    }

    [Fact]
    public async Task The_proposed_profile_builds_runs_the_tests_and_its_evidence_matches_its_hash()
    {
        var store = new InMemoryEvidenceStore();
        var build = DotNetVerificationProfile.Propose().Find("build")! with { Timeout = TimeSpan.FromMinutes(5), Isolation = ProcessIsolation.None };

        var results = await project.Runner(store).RunAsync(project.Root, [build, TestStep("Passing")], Scope, null, Ct);

        Assert.Equal(["build", "test"], results.Select(r => r.StepId));
        Assert.All(results, r => Assert.True(r.Succeeded, TinyDotNetProject.Describe(store, r)));
        var test = results[1];
        Assert.Equal(new TestRunSummary(Passed: 2, Failed: 0, Skipped: 0, ExitCode: 0), test.Evidence.TestRun);

        foreach (var result in results)
        {
            await using var content = await store.OpenContentAsync(TinyDotNetProject.ProjectId, result.Evidence.Id, Ct);
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, Ct);
            Assert.Equal(result.Evidence.ContentSha256, ContentHash.Sha256Hex(copy.ToArray()));
            Assert.True(result.Evidence.Matches(copy.ToArray()));
            Assert.Equal(EvidenceOrigin.CapturedBySystem, result.Evidence.Origin);
        }

        Assert.Equal(EvidenceKind.BuildLog, results[0].Evidence.Kind);
        Assert.Contains("# tests: passed 2, failed 0, skipped 0", store.Text(test.Evidence.Id), StringComparison.Ordinal);

        // Turning isolation off is visible in the evidence.
        Assert.All(results, r => Assert.EndsWith(VerificationRunner.WithoutIsolationTitleSuffix, store.Single(r.Evidence.Id).Draft.Title, StringComparison.Ordinal));
        Assert.All(results, r => Assert.Contains("# isolation: none", store.Text(r.Evidence.Id), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_test_fails_the_step_and_the_report_counts_it()
    {
        var store = new InMemoryEvidenceStore();

        var result = Assert.Single(await project.Runner(store).RunAsync(project.Root, [TestStep("Failing")], Scope, null, Ct));

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(new TestRunSummary(Passed: 1, Failed: 1, Skipped: 0, ExitCode: 2), result.Evidence.TestRun);
    }

    [Theory]
    [InlineData("NoExiste", true, 8)]
    [InlineData("Skipped", false, 0)]
    public async Task A_run_in_which_no_test_passed_is_not_a_success(string testClass, bool zeroTestsPolicy, int exitCode)
    {
        var store = new InMemoryEvidenceStore();

        var result = Assert.Single(await project.Runner(store).RunAsync(project.Root, [TestStep(testClass, zeroTestsPolicy)], Scope, null, Ct));

        // With only skipped tests and no zero-tests policy dotnet test exits with 0: the report decides.
        Assert.Equal(exitCode, result.ExitCode);
        Assert.False(result.Succeeded, TinyDotNetProject.Describe(store, result));
        Assert.Equal(0, result.Evidence.TestRun!.Passed);
    }

    [Fact]
    public async Task A_test_run_that_outlives_its_timeout_is_killed_with_its_whole_tree()
    {
        var store = new InMemoryEvidenceStore();
        var pidFile = Path.Combine(project.OutputFolder, "hanging.pid");
        File.Delete(pidFile);

        // The step's clock is fake: how long a loaded machine takes to start the test host never races the timeout, which
        // elapses only once the hanging test is known to run.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timeout = TimeSpan.FromSeconds(25);
        var run = project.Runner(store, time: time).RunAsync(project.Root, [TestStep("Hanging", timeout: timeout)], Scope, null, Ct);

        // The test host is a grandchild of the dotnet process the runner started.
        int pid;
        try
        {
            pid = await TinyDotNetProject.WaitForPidAsync(pidFile, run, TimeSpan.FromMinutes(3));
            Assert.True(pid > 0, "The hanging test never started. " + (run.IsCompleted ? TinyDotNetProject.Describe(store, (await run)[0]) : "Still starting after 3 minutes."));
            Assert.True(IsRunning(pid), $"The test host (PID {pid}) is not running before the timeout.");
            Assert.False(run.IsCompleted, "The step ended before its timeout.");
        }
        catch
        {
            // A failed check must not leave the step, and its tree, waiting for a clock nobody advances: past every timeout.
            time.Advance(TimeSpan.FromHours(1));
            throw;
        }

        var stopwatch = Stopwatch.StartNew();
        time.Advance(timeout);
        var result = Assert.Single(await run.WaitAsync(TimeSpan.FromMinutes(2), Ct));

        Assert.True(result.TimedOut, TinyDotNetProject.Describe(store, result));
        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(1), $"The tree was killed {stopwatch.Elapsed} after the timeout.");
        Assert.True(await WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(30)), $"The test host (PID {pid}) survived the timeout.");
        Assert.Contains("# timed out: yes", store.Text(result.Evidence.Id), StringComparison.Ordinal);
    }

    private static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit)
        {
            if (!IsRunning(pid))
            {
                return true;
            }

            await Task.Delay(100, Ct);
        }

        return !IsRunning(pid);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// A tiny xUnit v3 test project on Microsoft.Testing.Platform, written to a temporary folder (outside this repository,
/// so nothing of its build configuration is inherited) and built once through the runner itself. Its test classes:
/// <c>Passing</c> (2 tests), <c>Failing</c> (1 passes, 1 fails), <c>Skipped</c> (1 skipped) and <c>Hanging</c> (writes
/// its process id and sleeps).
/// </summary>
public sealed class TinyDotNetProject : IAsyncLifetime
{
    public const string ProjectId = "tiny-project";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public TinyDotNetProject() => Root = Path.Combine(Path.GetTempPath(), "as-tiny-" + Guid.NewGuid().ToString("N")[..8]);

    public string Root { get; }

    public string OutputFolder => Path.Combine(Root, "bin", "Debug", "net10.0");

    private string Scratch => Root + "-scratch";

    internal VerificationRunner Runner(InMemoryEvidenceStore store, IIsolatedWorkAreaFactory? isolation = null, TimeProvider? time = null) =>
        new(new StreamingProcessRunner(), store, new VerificationRunnerOptions(Scratch), time, isolation);

    /// <summary>The captured output of a result, for assertion messages.</summary>
    internal static string Describe(InMemoryEvidenceStore store, VerificationResult result) =>
        $"Step '{result.StepId}' (exit {result.ExitCode}, timed out {result.TimedOut}):\n{store.Text(result.Evidence.Id)}";

    /// <summary>
    /// Waits, in real time, until a process writes its id into <paramref name="pidFile"/> (fully: the file can exist
    /// before its content does), or until <paramref name="run"/> ends first. Returns the id, or -1.
    /// </summary>
    internal static async Task<int> WaitForPidAsync(string pidFile, Task run, TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit && !run.IsCompleted)
        {
            try
            {
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0)
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // Still being written.
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return -1;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        var repository = RepositoryRoot();
        var versions = PackageVersions(Path.Combine(repository, "Directory.Packages.props"));

        // The SDK and the test runner of this repository; empty build files stop MSBuild from looking further up.
        File.Copy(Path.Combine(repository, "global.json"), Path.Combine(Root, "global.json"));
        Write("Directory.Build.props", "<Project />\n");
        Write("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        Write("Tiny.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{versions["Microsoft.NET.Test.Sdk"]}" />
                <PackageReference Include="xunit.v3" Version="{versions["xunit.v3"]}" />
                <PackageReference Include="xunit.runner.visualstudio" Version="{versions["xunit.runner.visualstudio"]}" />
              </ItemGroup>
              <ItemGroup>
                <Using Include="Xunit" />
              </ItemGroup>
            </Project>

            """);
        Write("TinyTests.cs", """
            namespace Tiny.Tests;

            public sealed class Passing
            {
                [Fact]
                public void Adds() => Assert.Equal(4, 2 + 2);

                [Fact]
                public void Concatenates() => Assert.Equal("ab", "a" + "b");
            }

            public sealed class Failing
            {
                [Fact]
                public void Passes() => Assert.True(true);

                [Fact]
                public void Fails() => Assert.Equal(1, 2);
            }

            public sealed class Skipped
            {
                [Fact]
                public void IsSkipped() => Assert.Skip("motivo de omision");
            }

            public sealed class Hanging
            {
                [Fact]
                public void WritesItsProcessIdAndSleeps()
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "hanging.pid"), Environment.ProcessId.ToString());
                    Thread.Sleep(TimeSpan.FromMinutes(10));
                }
            }

            """);

        var store = new InMemoryEvidenceStore();
        var build = DotNetVerificationProfile.Propose().Find("build")! with { Timeout = TimeSpan.FromMinutes(5), Isolation = ProcessIsolation.None };
        var result = Assert.Single(await Runner(store).RunAsync(Root, [build], new EvidenceScope(ProjectId, null, null, null), null, TestContext.Current.CancellationToken));
        Assert.True(result.Succeeded, "The tiny project did not build. " + Describe(store, result));
    }

    public ValueTask DisposeAsync()
    {
        ForceDelete(Root);
        ForceDelete(Scratch);
        return ValueTask.CompletedTask;
    }

    private void Write(string relativePath, string content) =>
        File.WriteAllText(Path.Combine(Root, relativePath), content.ReplaceLineEndings("\n"), Utf8);

    private static string RepositoryRoot([CallerFilePath] string callerFile = "")
    {
        // tests/AgentStudio.Tests/Orchestration/Verification/<this file>
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(root, "global.json")), $"Repository root not found from {callerFile}.");
        return root;
    }

    private static Dictionary<string, string> PackageVersions(string propsFile) =>
        XDocument.Load(propsFile)
            .Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.Ordinal);

    private static void ForceDelete(string directory)
    {
        for (var attempt = 1; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(200 * attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover temporary folder is harmless.
                return;
            }
        }
    }
}
