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
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// The real <see cref="VerificationRunner"/> isolating real <c>dotnet build</c> and <c>dotnet test</c> at Low integrity
/// (ADR-0017), over the real <see cref="LowIntegrityWorkAreaFactory"/> and a git repository. It proves the green path,
/// that a malicious MSBuild target and a malicious test are both denied the write into a Medium folder standing in for
/// the user's repository, that the Job Object kills an isolated grandchild, and that a step the profile marks
/// <c>none</c> runs as before and is flagged. These restore from nuget.org into the Low cache of each run, so they need
/// the network; they are the slowest tests, one isolated run at a time.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class VerificationRunnerIsolationLocalToolsTests : IClassFixture<IsolatedTinyProject>
{
    private readonly IsolatedTinyProject _project;

    public VerificationRunnerIsolationLocalToolsTests(IsolatedTinyProject project) => _project = project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EvidenceScope Scope(string commit) => new("tiny-isolated", new RunId("20261016-abcabc"), new TaskId("T1"), 1, commit, _project.Root);

    [Fact]
    public async Task A_build_and_a_trx_test_run_green_under_isolation_and_the_report_is_read_from_the_Low_area()
    {
        var store = new InMemoryEvidenceStore();

        var results = await _project.Runner(store).RunAsync(
            _project.Root, [IsolatedTinyProject.Build(), IsolatedTinyProject.Test("Passing")], Scope(_project.Head), null, Ct);

        Assert.Equal(["build", "test"], results.Select(r => r.StepId));
        Assert.All(results, r => Assert.True(r.Succeeded, IsolatedTinyProject.Describe(store, r)));
        Assert.All(results, r => Assert.Equal(_project.Head, r.Evidence.SubjectCommit));
        var test = results[1];
        Assert.Equal(new TestRunSummary(Passed: 2, Failed: 0, Skipped: 0, ExitCode: 0), test.Evidence.TestRun);

        var buildLog = store.Text(results[0].Evidence.Id);
        Assert.Contains("# isolation: low integrity", buildLog, StringComparison.Ordinal);
        Assert.Contains("# source: commit " + _project.Head, buildLog, StringComparison.Ordinal);
        Assert.DoesNotContain(VerificationRunner.WithoutIsolationTitleSuffix, store.Single(results[0].Evidence.Id).Draft.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_that_names_only_the_test_gets_its_build_first_on_the_fresh_copy_and_runs_green()
    {
        // B6, run 20261029-ff3333: on a fresh copy of the commit «dotnet test --no-build» alone finds nothing built (no TRX);
        // the test step of the proposed profile requires its build, so the steps to run of a task naming only it build first.
        var store = new InMemoryEvidenceStore();
        var test = IsolatedTinyProject.Test("Passing");
        var profile = new VerificationProfile("tiny", [IsolatedTinyProject.Build(), test]);

        var alone = Assert.Single(await _project.Runner(store).RunAsync(_project.Root, [test with { Requires = null }], Scope(_project.Head), null, Ct));
        var results = await _project.Runner(store).RunAsync(_project.Root, profile.StepsToRun(["test"]), Scope(_project.Head), null, Ct);

        Assert.False(alone.Succeeded, IsolatedTinyProject.Describe(store, alone));
        Assert.Equal(new TestRunSummary(Passed: 0, Failed: 0, Skipped: 0, ExitCode: alone.ExitCode), alone.Evidence.TestRun);
        Assert.Equal(["build", "test"], results.Select(r => r.StepId));
        Assert.All(results, r => Assert.True(r.Succeeded, IsolatedTinyProject.Describe(store, r)));
        Assert.Equal(new TestRunSummary(Passed: 2, Failed: 0, Skipped: 0, ExitCode: 0), results[1].Evidence.TestRun);
    }

    [Fact]
    public async Task A_malicious_MSBuild_target_is_denied_the_write_into_the_stand_in_repository_and_that_folder_stays_intact()
    {
        var store = new InMemoryEvidenceStore();
        var victim = _project.NewVictimFolder();
        var target = Path.Combine(victim, "pwned-build.txt");
        var build = IsolatedTinyProject.Build() with
        {
            Arguments = ["build", "-p:EnableEvilTarget=true", $"-p:EvilPath={target}"],
        };

        var result = Assert.Single(await _project.Runner(store).RunAsync(_project.Root, [build], Scope(_project.Head), null, Ct));

        Assert.False(result.Succeeded, IsolatedTinyProject.Describe(store, result));
        Assert.False(File.Exists(target), "A Low MSBuild target wrote into the Medium stand-in repository.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
        var log = store.Text(result.Evidence.Id);
        Assert.Contains("EVIL target attempting to write", log, StringComparison.Ordinal);
        Assert.Contains("denied", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_malicious_test_is_denied_the_write_into_the_stand_in_repository_and_that_folder_stays_intact()
    {
        var store = new InMemoryEvidenceStore();
        var victim = _project.NewVictimFolder();
        var target = Path.Combine(victim, "pwned-by-test.txt");

        // Build in the area, then the Attacker test runs --no-build on those binaries.
        var results = await _project.Runner(store, extraEnvironment: new() { ["AS_ATTACK_PATH"] = target })
            .RunAsync(_project.Root, [IsolatedTinyProject.Build(), IsolatedTinyProject.Test("Attacker")], Scope(_project.Head), null, Ct);

        Assert.Equal(["build", "test"], results.Select(r => r.StepId));
        var result = results[1];
        Assert.False(result.Succeeded, IsolatedTinyProject.Describe(store, result));
        Assert.False(File.Exists(target), "A Low test wrote into the Medium stand-in repository.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
        // One test caught UnauthorizedAccessException and passed; the other fails on purpose, so the step fails and the
        // attack is seen to have left nothing.
        Assert.Equal(new TestRunSummary(Passed: 1, Failed: 1, Skipped: 0, ExitCode: result.ExitCode), result.Evidence.TestRun);
    }

    [Fact]
    public async Task The_Job_Object_kills_an_isolated_grandchild_when_the_step_times_out()
    {
        var store = new InMemoryEvidenceStore();
        var written = Path.Combine(_project.SharedLowPidDirectory, "grandchild.pid");
        File.Delete(written);

        // The steps' clock is fake: neither the cold restore nor the start of the test host races the timeout, which
        // elapses only once the grandchild is known to run.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timeout = TimeSpan.FromSeconds(40);
        var test = IsolatedTinyProject.Test("SpawnsGrandchild", timeout: timeout);
        var run = _project.Runner(store, extraEnvironment: new() { ["AS_PID_DIR"] = _project.SharedLowPidDirectory }, time: time)
            .RunAsync(_project.Root, [IsolatedTinyProject.Build(), test], Scope(_project.Head), null, Ct);

        int pid;
        try
        {
            pid = await TinyDotNetProject.WaitForPidAsync(written, run, TimeSpan.FromMinutes(5));
            Assert.True(pid > 0, "The grandchild never started. " + (run.IsCompleted ? string.Join("\n", (await run).Select(r => IsolatedTinyProject.Describe(store, r))) : "Still starting after 5 minutes."));
            Assert.False(run.IsCompleted, "The step ended before its timeout.");
        }
        catch
        {
            // A failed check must not leave a step, and its tree, waiting for a clock nobody advances: past every timeout.
            time.Advance(TimeSpan.FromHours(1));
            throw;
        }

        time.Advance(timeout);
        var results = await run.WaitAsync(TimeSpan.FromMinutes(2), Ct);

        var result = results[^1];
        Assert.Equal("test", result.StepId);
        Assert.True(result.TimedOut, IsolatedTinyProject.Describe(store, result));
        Assert.Equal(-1, result.ExitCode);
        Assert.True(await WaitUntilGoneAsync(pid, TimeSpan.FromSeconds(30)), $"The isolated grandchild (PID {pid}) survived the timeout.");
    }

    [Fact]
    public async Task A_step_the_profile_marks_none_runs_in_the_working_directory_as_before_and_is_flagged()
    {
        var store = new InMemoryEvidenceStore();
        var build = IsolatedTinyProject.Build() with { Isolation = ProcessIsolation.None };

        var result = Assert.Single(await _project.Runner(store).RunAsync(_project.Root, [build], Scope(_project.Head), null, Ct));

        Assert.True(result.Succeeded, IsolatedTinyProject.Describe(store, result));
        var log = store.Text(result.Evidence.Id);
        Assert.Contains("# isolation: none", log, StringComparison.Ordinal);
        Assert.Contains("# working directory: " + _project.Root, log, StringComparison.Ordinal);
        Assert.EndsWith(VerificationRunner.WithoutIsolationTitleSuffix, store.Single(result.Evidence.Id).Draft.Title, StringComparison.Ordinal);

        // Built in place: the isolated build never leaves bin/ in the working directory, this one does.
        Assert.True(Directory.Exists(Path.Combine(_project.Root, "bin")), "The step without isolation did not build in the working directory.");
    }

    private static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100, Ct);
        }

        return false;
    }
}

/// <summary>
/// A tiny xUnit v3 project committed to a throwaway git repository, for the isolated verification tests. Its classes:
/// <c>Passing</c> (2 pass), <c>Attacker</c> (one test tries to write the Medium path <c>AS_ATTACK_PATH</c> and asserts it
/// is denied, one fails on purpose so the step fails and the attack is seen to have left nothing) and
/// <c>SpawnsGrandchild</c> (writes its ping's PID into the Low <c>AS_PID_DIR</c> and hangs). Its csproj has a malicious
/// <c>BeforeTargets="Build"</c> target guarded by a property. The build is not shared: each test restores and builds in
/// its own Low area.
/// </summary>
public sealed class IsolatedTinyProject : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly string _victims;

    public IsolatedTinyProject()
    {
        _victims = Path.Combine(Path.GetDirectoryName(_repo.Root)!, "victims");
        Directory.CreateDirectory(_victims);
        SharedLowPidDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "as-isolation-pids-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(SharedLowPidDirectory);
        WriteProject();
    }

    /// <summary>A Low-labelled folder (under LocalLow) the isolated grandchild can write its PID into.</summary>
    public string SharedLowPidDirectory { get; }

    public string Root => _repo.Root;

    public string Head => _repo.Head;

    internal VerificationRunner Runner(InMemoryEvidenceStore store, Dictionary<string, string?>? extraEnvironment = null, TimeProvider? time = null)
    {
        IStreamingProcessRunner processes = new StreamingProcessRunner();
        if (extraEnvironment is not null)
        {
            processes = new EnvironmentAddingRunner(processes, extraEnvironment);
        }

        // The real Low areas, with the copy of the commit made by the real git layer from the repository of the project.
        var git = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));
        return new VerificationRunner(processes, store, new VerificationRunnerOptions(_repo.Root + "-scratch"), time, new LowIntegrityWorkAreaFactory(), git);
    }

    public string NewVictimFolder()
    {
        var folder = Path.Combine(_victims, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal static VerificationStep Build() => DotNetVerificationProfile.Propose().Find("build")! with { Timeout = TimeSpan.FromMinutes(6) };

    internal static VerificationStep Test(string testClass, TimeSpan? timeout = null)
    {
        var step = DotNetVerificationProfile.Propose().Find("test")!;
        return step with
        {
            Arguments = [.. step.Arguments, "--filter-class", "Tiny.Tests." + testClass],
            Timeout = timeout ?? TimeSpan.FromMinutes(6),
        };
    }

    internal static string Describe(InMemoryEvidenceStore store, VerificationResult result) =>
        $"Step '{result.StepId}' (exit {result.ExitCode}, timed out {result.TimedOut}):\n{store.Text(result.Evidence.Id)}";

    public void Dispose()
    {
        _repo.Dispose();
        foreach (var folder in new[] { _victims, SharedLowPidDirectory, _repo.Root + "-scratch" })
        {
            TempFolders.Delete(folder);
        }
    }

    private void WriteProject()
    {
        var repositoryRoot = RepositoryRoot();
        var versions = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .ToDictionary(e => (string)e.Attribute("Include")!, e => (string)e.Attribute("Version")!, StringComparer.Ordinal);

        _repo.Write("global.json", File.ReadAllText(Path.Combine(repositoryRoot, "global.json")));
        _repo.Write(".gitignore", "bin/\nobj/\n");
        _repo.Write("Directory.Build.props", "<Project />\n");
        _repo.Write("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        _repo.Write("nuget.config", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\" />\n  </packageSources>\n</configuration>\n");
        _repo.Write("Tiny.Tests.csproj", $"""
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
              <Target Name="EvilBeforeBuild" BeforeTargets="Build" Condition="'$(EnableEvilTarget)' == 'true'">
                <Message Importance="high" Text="EVIL target attempting to write $(EvilPath)" />
                <WriteLinesToFile File="$(EvilPath)" Lines="pwned by MSBuild" Overwrite="true" />
              </Target>
            </Project>

            """);
        _repo.Write("Tests.cs", """
            using System;
            using System.Diagnostics;
            using System.IO;

            namespace Tiny.Tests;

            public sealed class Passing
            {
                [Fact]
                public void Adds() => Assert.Equal(4, 2 + 2);

                [Fact]
                public void Concatenates() => Assert.Equal("ab", "a" + "b");
            }

            public sealed class Attacker
            {
                [Fact]
                public void CannotWriteTheUsersRepository()
                {
                    var path = Environment.GetEnvironmentVariable("AS_ATTACK_PATH")!;
                    var error = Record.Exception(() => File.WriteAllText(path, "pwned-by-test"));
                    Assert.IsType<UnauthorizedAccessException>(error);
                    Assert.False(File.Exists(path));
                }

                [Fact]
                public void FailsOnPurposeSoTheStepFails() => Assert.True(false, "the attack above must leave the step failed");
            }

            public sealed class SpawnsGrandchild
            {
                [Fact]
                public void StartsAPingAndHangs()
                {
                    var info = new ProcessStartInfo("ping", "-n 120 127.0.0.1") { UseShellExecute = false, RedirectStandardOutput = true };
                    var child = Process.Start(info)!;
                    var dir = Environment.GetEnvironmentVariable("AS_PID_DIR")!;
                    File.WriteAllText(Path.Combine(dir, "grandchild.pid"), child.Id.ToString());
                    child.WaitForExit();
                }
            }

            """);
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-m", "proyecto de prueba aislada");
    }

    private static string RepositoryRoot([CallerFilePath] string callerFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(root, "global.json")), $"Repository root not found from {callerFile}.");
        return root;
    }

    /// <summary>Adds environment variables to the spec of every process, so a test can give its target to the isolated step.</summary>
    private sealed class EnvironmentAddingRunner(IStreamingProcessRunner inner, Dictionary<string, string?> extra) : IStreamingProcessRunner
    {
        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            var environment = new Dictionary<string, string?>(spec.Environment ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in extra)
            {
                environment[key] = value;
            }

            return inner.StartAsync(spec with { Environment = environment }, cancellationToken);
        }
    }
}
