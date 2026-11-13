using System.IO;
using System.Reflection;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>The door for tools (docs/20 D-02 and D-09): the closed catalog, the allow list of the environment, the neutral folder, the limits and the queue.</summary>
public sealed class DoctorToolRunnerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private DoctorFolders Folders => DoctorFolders.Under(_temp.Combine("local"));

    public void Dispose() => _temp.Dispose();

    private static IReadOnlyDictionary<string, string> Ambient(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private DoctorToolRunner Door(RecordingProcessRunner runner, FakeLocator? locator = null, IReadOnlyDictionary<string, string>? ambient = null) =>
        new(locator ?? new FakeLocator(c => @"C:\Tools\" + c + ".exe"), runner, Folders, () => ambient ?? Ambient(), () => _storePython);

    private bool _storePython;

    [Fact]
    public void The_catalog_has_one_entry_per_tool_with_the_exact_arguments_of_the_design()
    {
        var catalog = DoctorToolCatalog.All.ToDictionary(e => e.Tool, e => $"{e.Command} {string.Join(' ', e.Arguments)}");

        Assert.Equal(Enum.GetValues<DoctorTool>().Length, catalog.Count);
        Assert.Equal("dotnet --list-sdks", catalog[DoctorTool.DotNetListSdks]);
        Assert.Equal("dotnet --list-runtimes", catalog[DoctorTool.DotNetListRuntimes]);
        Assert.Equal("node --version", catalog[DoctorTool.NodeVersion]);
        Assert.Equal("npm --version", catalog[DoctorTool.NpmVersion]);
        Assert.Equal("python --version", catalog[DoctorTool.PythonVersion]);
        Assert.Equal("py --version", catalog[DoctorTool.PyVersion]);
        Assert.Equal("git --version", catalog[DoctorTool.GitVersion]);
        Assert.Equal("docker version --format json", catalog[DoctorTool.DockerVersionJson]);
        Assert.Equal("wsl --status", catalog[DoctorTool.WslStatus]);
        Assert.Equal("docker --host npipe:////./pipe/dockerDesktopLinuxEngine version --format json", catalog[DoctorTool.DockerVersionDesktopPipe]);
        Assert.Equal("gh --version", catalog[DoctorTool.GhVersion]);
    }

    [Fact]
    public void The_door_has_no_way_to_pass_an_executable_or_an_argument()
    {
        var methods = typeof(IDoctorToolRunner).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var only = Assert.Single(methods);
        Assert.Equal(nameof(IDoctorToolRunner.RunAsync), only.Name);
        Assert.Equal([typeof(DoctorTool), typeof(string), typeof(CancellationToken)], only.GetParameters().Select(p => p.ParameterType).ToArray());
        // The class has no other public way in either: one method, the same one.
        var implemented = typeof(DoctorToolRunner).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name).Order().ToArray();
        Assert.Equal([nameof(DoctorToolRunner.Dispose), nameof(DoctorToolRunner.DisposeAsync), nameof(DoctorToolRunner.RunAsync)], implemented);
    }

    [Fact]
    public async Task Every_tool_starts_in_the_neutral_folder_with_its_own_arguments_and_the_limits_of_the_design()
    {
        var runner = new RecordingProcessRunner();
        await using var door = Door(runner);

        foreach (var tool in Enum.GetValues<DoctorTool>())
        {
            await door.RunAsync(tool, _temp.Combine("repo"), CancellationToken.None);
        }

        Assert.Equal(Enum.GetValues<DoctorTool>().Length, runner.Specs.Count);
        Assert.All(runner.Specs, spec =>
        {
            Assert.Equal(Folders.WorkingFolder, spec.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(10), spec.Timeout);
            Assert.Equal(64_000, spec.MaxOutputCharacters);
            Assert.Null(spec.StandardInput);
            Assert.Equal(ProcessIsolation.None, spec.Isolation);
        });
        Assert.Equal(["--list-sdks"], runner.Specs[0].Arguments);
        Assert.Equal(["version", "--format", "json"], runner.Specs[7].Arguments);
        Assert.True(Directory.Exists(Folders.WorkingFolder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Folders.WorkingFolder));
    }

    [Fact]
    public async Task The_environment_is_an_allow_list_every_other_variable_of_the_process_is_removed()
    {
        var planted = Ambient(
            ("Path", @"C:\Windows"), ("SystemRoot", @"C:\Windows"), ("USERPROFILE", @"C:\Users\x"), ("LocalAppData", @"C:\Users\x\AppData\Local"),
            ("NODE_OPTIONS", "--require evil.js"), ("DOTNET_ROOT", @"C:\repo\.dotnet"), ("DOCKER_HOST", "tcp://evil:2375"),
            ("DOCKER_CONTEXT", "evil"), ("GIT_DIR", @"C:\repo\.git"), ("PYTHONSTARTUP", @"C:\repo\startup.py"), ("npm_config_prefix", @"C:\repo"),
            ("COMPLUS_Version", "v1"), ("MSBUILDENABLEALLPROPERTYFUNCTIONS", "1"), ("SOME_FUTURE_VARIABLE", "1"));
        var runner = new RecordingProcessRunner();
        await using var door = Door(runner, ambient: planted);

        await door.RunAsync(DoctorTool.NodeVersion, null, CancellationToken.None);

        var env = runner.Specs.Single().Environment!;
        foreach (var name in new[] { "NODE_OPTIONS", "DOTNET_ROOT", "DOCKER_HOST", "DOCKER_CONTEXT", "GIT_DIR", "PYTHONSTARTUP", "npm_config_prefix", "COMPLUS_Version", "MSBUILDENABLEALLPROPERTYFUNCTIONS", "SOME_FUTURE_VARIABLE" })
        {
            Assert.True(env.ContainsKey(name) && env[name] is null, $"{name} must be removed");
        }

        // The ones on the list are left as they are (not named), and PATH is never named: the runner of processes sets the clean one.
        Assert.DoesNotContain(env.Keys, k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env.Keys, k => k.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env.Keys, k => k.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Folders.TempFolder, env["TEMP"]);
        Assert.Equal(Folders.TempFolder, env["TMP"]);
        Assert.Equal("1", env["DOTNET_NOLOGO"]);
        Assert.Equal("1", env["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.False(env.ContainsKey("DOCKER_CONFIG"));
        Assert.False(env.ContainsKey("WSL_UTF8"));
    }

    [Fact]
    public async Task The_names_of_the_variables_are_compared_ignoring_case()
    {
        var runner = new RecordingProcessRunner();
        await using var door = Door(runner, ambient: Ambient(("path", @"C:\Windows"), ("systemroot", @"C:\Windows"), ("node_options", "x")));

        await door.RunAsync(DoctorTool.NodeVersion, null, CancellationToken.None);

        var env = runner.Specs.Single().Environment!;
        Assert.DoesNotContain(env.Keys, k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env.Keys, k => k.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase));
        Assert.Null(env["NODE_OPTIONS"]);
    }

    [Fact]
    public async Task Docker_gets_its_own_empty_configuration_so_the_context_of_the_person_is_not_used_and_wsl_gets_utf8()
    {
        var runner = new RecordingProcessRunner();
        await using var door = Door(runner, ambient: Ambient(("DOCKER_CONTEXT", "remote"), ("DOCKER_CONFIG", @"C:\Users\x\.docker")));

        await door.RunAsync(DoctorTool.DockerVersionJson, null, CancellationToken.None);
        await door.RunAsync(DoctorTool.WslStatus, null, CancellationToken.None);
        await door.RunAsync(DoctorTool.DockerVersionDesktopPipe, null, CancellationToken.None);

        // The pipe of Docker Desktop is also asked with the empty configuration: the context of the person is never read.
        Assert.Equal(Folders.DockerConfigFolder, runner.Specs[2].Environment!["DOCKER_CONFIG"]);
        var docker = runner.Specs[0].Environment!;
        Assert.Equal(Folders.DockerConfigFolder, docker["DOCKER_CONFIG"]);
        Assert.Null(docker["DOCKER_CONTEXT"]);
        Assert.False(docker.ContainsKey("WSL_UTF8"));
        Assert.True(Directory.Exists(Folders.DockerConfigFolder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Folders.DockerConfigFolder));
        var wsl = runner.Specs[1].Environment!;
        Assert.Equal("1", wsl["WSL_UTF8"]);
        Assert.True(!wsl.TryGetValue("DOCKER_CONFIG", out var inherited) || inherited is null);
    }

    [Fact]
    public async Task A_program_found_only_inside_the_project_is_not_started()
    {
        var runner = new RecordingProcessRunner();
        var locator = new FakeLocator(_ => null, inside: _ => @"C:\repo\dotnet.cmd");
        await using var door = Door(runner, locator);

        var result = await door.RunAsync(DoctorTool.DotNetListSdks, @"C:\repo", CancellationToken.None);

        Assert.Equal(DoctorToolOutcome.InsideProject, result.Outcome);
        Assert.Equal(@"C:\repo\dotnet.cmd", result.ExecutablePath);
        Assert.Empty(runner.Specs);
    }

    [Fact]
    public async Task A_result_that_lies_inside_the_project_of_the_caller_is_refused_again_by_the_door()
    {
        var repo = _temp.CreateDirectory("repo");
        var planted = _temp.WriteFile("repo/bin/node.exe");
        var runner = new RecordingProcessRunner();
        // A locator that was fooled (it trusts everything) is not enough: the door compares with the root it was given.
        await using var door = Door(runner, new FakeLocator(_ => planted));

        var result = await door.RunAsync(DoctorTool.NodeVersion, repo, CancellationToken.None);

        Assert.Equal(DoctorToolOutcome.InsideProject, result.Outcome);
        Assert.Empty(runner.Specs);
    }

    [Fact]
    public async Task A_program_that_is_not_installed_says_so()
    {
        var runner = new RecordingProcessRunner();
        await using var door = Door(runner, new FakeLocator(_ => null));

        var result = await door.RunAsync(DoctorTool.WslStatus, null, CancellationToken.None);

        Assert.Equal(DoctorToolOutcome.NotInstalled, result.Outcome);
        Assert.Empty(runner.Specs);
    }

    [Fact]
    public async Task The_alias_of_the_store_for_python_is_not_started_and_the_launcher_is()
    {
        var alias = @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe";
        var runner = new RecordingProcessRunner(_ => RecordingProcessRunner.Ok("Python 3.13.1"));
        await using var door = Door(runner, new FakeLocator(c => c == "python" ? alias : @"C:\Windows\" + c + ".exe"));

        var python = await door.RunAsync(DoctorTool.PythonVersion, null, CancellationToken.None);
        var launcher = await door.RunAsync(DoctorTool.PyVersion, null, CancellationToken.None);

        Assert.Equal(DoctorToolOutcome.NotInstalled, python.Outcome);
        Assert.Equal(alias, python.ExecutablePath);
        Assert.Equal(DoctorToolOutcome.Completed, launcher.Outcome);
        Assert.Single(runner.Specs);
    }

    [Fact]
    public async Task A_python_of_the_store_with_its_package_in_the_profile_is_the_real_one_and_is_asked()
    {
        var alias = @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe";
        var runner = new RecordingProcessRunner(_ => RecordingProcessRunner.Ok("Python 3.13.1"));
        _storePython = true;
        await using var door = Door(runner, new FakeLocator(c => c == "python" ? alias : null));

        var python = await door.RunAsync(DoctorTool.PythonVersion, null, CancellationToken.None);

        Assert.Equal(DoctorToolOutcome.Completed, python.Outcome);
        Assert.Equal(alias, runner.Specs.Single().FileName);
    }

    [Fact]
    public async Task What_the_tool_writes_is_returned_and_the_error_output_is_used_only_when_the_output_is_blank_for_a_version()
    {
        var runner = new RecordingProcessRunner(spec => spec.Arguments[0] == "version"
            ? RecordingProcessRunner.Ok("""{"Client":{"Version":"1.0"}}""", "error text that must not be mixed into the json")
            : RecordingProcessRunner.Ok("", "Python 2.7.18"));
        await using var door = Door(runner);

        var docker = await door.RunAsync(DoctorTool.DockerVersionJson, null, CancellationToken.None);
        var python = await door.RunAsync(DoctorTool.PythonVersion, null, CancellationToken.None);

        Assert.Equal("""{"Client":{"Version":"1.0"}}""", docker.Output);
        Assert.Equal("Python 2.7.18", python.Output);
    }

    [Fact]
    public async Task A_timeout_a_failure_to_start_and_an_output_over_the_limit_are_answers_not_exceptions()
    {
        var runner = new RecordingProcessRunner(spec => spec.Arguments[0] switch
        {
            "--list-sdks" => new ProcessResult(-1, "", "", TimeSpan.FromSeconds(10), TimedOut: true),
            "--list-runtimes" => throw new System.ComponentModel.Win32Exception(5, "Access is denied."),
            _ => new ProcessResult(-1, new string('x', 64_000), "", TimeSpan.FromSeconds(1), TimedOut: false, OutputLimitExceeded: true),
        });
        await using var door = Door(runner);

        var timedOut = await door.RunAsync(DoctorTool.DotNetListSdks, null, CancellationToken.None);
        var failed = await door.RunAsync(DoctorTool.DotNetListRuntimes, null, CancellationToken.None);
        var big = await door.RunAsync(DoctorTool.NodeVersion, null, CancellationToken.None);

        Assert.Equal(DoctorToolOutcome.TimedOut, timedOut.Outcome);
        Assert.Equal(DoctorToolOutcome.FailedToStart, failed.Outcome);
        Assert.Equal(DoctorToolOutcome.Completed, big.Outcome);
        Assert.True(big.Truncated);
    }

    [Fact]
    public async Task Questions_are_asked_one_at_a_time_in_the_order_they_come()
    {
        var runner = new RecordingProcessRunner { Delay = TimeSpan.FromMilliseconds(30) };
        await using var door = Door(runner);

        var tasks = Enum.GetValues<DoctorTool>().Select(t => door.RunAsync(t, null, CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, runner.MostAtOnce);
        Assert.Equal(Enum.GetValues<DoctorTool>().Length, runner.Specs.Count);
        Assert.Equal(["--list-sdks", "--list-runtimes"], runner.Specs.Take(2).Select(s => s.Arguments[0]).ToArray());
    }

    [Fact]
    public async Task A_question_cancelled_before_its_turn_is_not_started()
    {
        var runner = new RecordingProcessRunner { Delay = TimeSpan.FromMilliseconds(100) };
        await using var door = Door(runner);
        using var cancel = new CancellationTokenSource();

        var first = door.RunAsync(DoctorTool.GitVersion, null, CancellationToken.None);
        var second = door.RunAsync(DoctorTool.NodeVersion, null, cancel.Token);
        await cancel.CancelAsync();

        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Single(runner.Specs);
    }
}
