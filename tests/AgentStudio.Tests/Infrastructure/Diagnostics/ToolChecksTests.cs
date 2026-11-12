using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

internal sealed class FakeEnvironmentProbe : IProjectEnvironmentProbe
{
    public ProjectLayout Layout { get; set; } = new(true, true, []);

    public IReadOnlyList<string> Inside { get; set; } = [];

    public bool Containers { get; set; }

    public ProjectLayout ReadLayout(string projectRoot) => Layout;

    public IReadOnlyList<string> PathFoldersInside(string projectRoot) => Inside;

    public bool DeclaresContainers(string projectRoot) => Containers;
}

/// <summary>F6-1 and F6-9: one test per check of the sections «Herramientas» and «Contenedores», over a door with scripted answers (docs/20 section 8).</summary>
public sealed class ToolChecksTests
{
    private static readonly ProjectContext Project = new("p1", @"C:\work\calc");

    private static async Task<IReadOnlyDictionary<string, DoctorCheck>> ToolsAsync(ScriptedDoor door, string? webView2 = "130.0.2849.80", ProjectContext? project = null)
    {
        var provider = new ToolSnapshotProvider(door, new FixedWebView2(webView2));
        var checks = await new ToolsCheckSource(provider).CheckAsync(project, CancellationToken.None);
        return checks.ToDictionary(c => c.Code);
    }

    private static async Task<IReadOnlyDictionary<string, DoctorCheck>> ContainersAsync(ScriptedDoor door, bool declared)
    {
        var provider = new ToolSnapshotProvider(door, new FixedWebView2(null));
        var checks = await new ContainersCheckSource(provider, new FakeEnvironmentProbe { Containers = declared }).CheckAsync(Project, CancellationToken.None);
        return checks.ToDictionary(c => c.Code);
    }

    [Fact]
    public async Task On_a_computer_like_the_development_machine_every_tool_is_correct_with_its_version()
    {
        var checks = await ToolsAsync(ScriptedDoor.LikeTheDevelopmentMachine());

        Assert.Equal(["doctor.dotnet.sdk", "doctor.dotnet.runtime", "doctor.node", "doctor.npm", "doctor.python", "doctor.git", "doctor.webview2"], checks.Keys.ToArray());
        Assert.All(checks.Values, c => Assert.Equal(DoctorStatus.Ok, c.Status));
        Assert.All(checks.Values, c => Assert.Equal(DoctorSection.Tools, c.Section));
        Assert.Equal("11.0.100-preview.2.26159.112", checks["doctor.dotnet.sdk"].Fact(DoctorFactNames.Version));
        Assert.Equal("3", checks["doctor.dotnet.sdk"].Fact("count"));
        Assert.Equal("24.14.0", checks["doctor.node"].Fact(DoctorFactNames.Version));
        Assert.Equal("2.45.2", checks["doctor.git"].Fact(DoctorFactNames.Version));
        Assert.Equal("3.12.5", checks["doctor.python"].Fact(DoctorFactNames.Version));
        Assert.Equal("130.0.2849.80", checks["doctor.webview2"].Fact(DoctorFactNames.Version));
        Assert.All(checks.Values, c => Assert.Null(c.FixCode));
    }

    [Fact]
    public async Task On_a_computer_with_nothing_installed_each_tool_is_missing_with_its_guide()
    {
        var checks = await ToolsAsync(new ScriptedDoor(), webView2: null);

        Assert.Equal((DoctorStatus.Warning, DoctorFixCatalog.InstallDotNet), (checks["doctor.dotnet.sdk"].Status, checks["doctor.dotnet.sdk"].FixCode));
        Assert.Equal((DoctorStatus.Warning, DoctorFixCatalog.InstallDotNet), (checks["doctor.dotnet.runtime"].Status, checks["doctor.dotnet.runtime"].FixCode));
        Assert.Equal((DoctorStatus.Warning, DoctorFixCatalog.InstallNode), (checks["doctor.node"].Status, checks["doctor.node"].FixCode));
        Assert.Equal((DoctorStatus.Warning, DoctorFixCatalog.InstallNode), (checks["doctor.npm"].Status, checks["doctor.npm"].FixCode));
        Assert.Equal((DoctorStatus.Warning, DoctorFixCatalog.InstallPython), (checks["doctor.python"].Status, checks["doctor.python"].FixCode));
        Assert.Equal((DoctorStatus.Error, DoctorFixCatalog.InstallGit), (checks["doctor.git"].Status, checks["doctor.git"].FixCode));
        Assert.Equal((DoctorStatus.Error, DoctorFixCatalog.InstallWebView2), (checks["doctor.webview2"].Status, checks["doctor.webview2"].FixCode));
        Assert.All(checks.Values, c => Assert.NotNull(DoctorFixCatalog.Find(c.FixCode)));
    }

    [Fact]
    public async Task An_empty_list_of_sdks_is_a_missing_sdk_but_an_answer_that_is_not_the_list_is_not_checked()
    {
        var empty = await ToolsAsync(new ScriptedDoor().Says(DoctorTool.DotNetListSdks, ""));
        var garbage = await ToolsAsync(new ScriptedDoor().Says(DoctorTool.DotNetListSdks, "The command could not be loaded."));

        Assert.Equal(DoctorStatus.Warning, empty["doctor.dotnet.sdk"].Status);
        Assert.Equal(DoctorStatus.Unknown, garbage["doctor.dotnet.sdk"].Status);
        Assert.Null(garbage["doctor.dotnet.sdk"].FixCode);
    }

    [Fact]
    public async Task A_failure_a_timeout_and_a_nonzero_exit_are_not_checked_never_correct()
    {
        var door = new ScriptedDoor()
            .Fails(DoctorTool.NodeVersion, DoctorToolOutcome.TimedOut, @"C:\tools\node.exe")
            .Fails(DoctorTool.GitVersion, DoctorToolOutcome.FailedToStart)
            .Says(DoctorTool.NpmVersion, "10.8.2", exit: 1);

        var checks = await ToolsAsync(door);

        Assert.Equal(DoctorStatus.Unknown, checks["doctor.node"].Status);
        Assert.Equal(DoctorStatus.Unknown, checks["doctor.git"].Status);
        Assert.Equal(DoctorStatus.Unknown, checks["doctor.npm"].Status);
    }

    [Fact]
    public async Task A_tool_found_only_inside_the_project_is_a_warning_that_names_the_folder_and_is_not_used()
    {
        var door = new ScriptedDoor().Fails(DoctorTool.NodeVersion, DoctorToolOutcome.InsideProject, @"C:\work\calc\node.exe");

        var checks = await ToolsAsync(door, project: Project);

        Assert.Equal(DoctorStatus.Warning, checks["doctor.node"].Status);
        Assert.Equal(@"C:\work\calc\node.exe", checks["doctor.node"].Fact("inside-project"));
        Assert.Equal(@"C:\work\calc", door.Asked.First().ProjectRoot);
    }

    [Fact]
    public async Task The_alias_of_the_store_is_not_an_installed_python()
    {
        var alias = @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe";
        var door = new ScriptedDoor().Fails(DoctorTool.PythonVersion, DoctorToolOutcome.NotInstalled, alias);

        var checks = await ToolsAsync(door);

        // Not «missing»: the empty alias and a Python that was never run look the same, so it is not checked.
        Assert.Equal(DoctorStatus.Unknown, checks["doctor.python"].Status);
        Assert.Equal(alias, checks["doctor.python"].Fact("alias"));
        Assert.Equal(DoctorFixCatalog.InstallPython, checks["doctor.python"].FixCode);
    }

    [Fact]
    public async Task Python_is_found_through_the_launcher_when_the_command_is_the_alias()
    {
        var door = new ScriptedDoor()
            .Fails(DoctorTool.PythonVersion, DoctorToolOutcome.NotInstalled, @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe")
            .Says(DoctorTool.PyVersion, "Python 3.13.14");

        var checks = await ToolsAsync(door);

        Assert.Equal(DoctorStatus.Ok, checks["doctor.python"].Status);
        Assert.Equal("3.13.14", checks["doctor.python"].Fact(DoctorFactNames.Version));
    }

    [Fact]
    public async Task Docker_with_the_engine_stopped_is_a_warning_with_the_guide_when_the_project_declares_containers_and_not_applicable_when_not()
    {
        var door = ScriptedDoor.LikeTheDevelopmentMachine();

        var declared = (await ContainersAsync(door, declared: true))["doctor.docker"];
        var undeclared = (await ContainersAsync(door, declared: false))["doctor.docker"];

        Assert.Equal(DoctorStatus.Warning, declared.Status);
        Assert.Equal(DoctorFixCatalog.StartDocker, declared.FixCode);
        Assert.Equal("27.3.1", declared.Fact("client"));
        Assert.Null(declared.Fact("server"));
        Assert.Equal(DoctorStatus.NotApplicable, undeclared.Status);
        Assert.Equal(DoctorSection.Containers, declared.Section);
    }

    [Fact]
    public async Task Docker_running_is_correct_with_both_versions_whether_or_not_the_project_declares_containers()
    {
        var door = new ScriptedDoor().Says(DoctorTool.DockerVersionJson, DoctorFixtures.Read("handmade-docker-version-running.json"));

        foreach (var declared in new[] { true, false })
        {
            var check = (await ContainersAsync(door, declared))["doctor.docker"];
            Assert.Equal(DoctorStatus.Ok, check.Status);
            Assert.Equal("27.3.1", check.Fact("client"));
            Assert.Equal("27.3.1", check.Fact("server"));
            Assert.Null(check.FixCode);
        }
    }

    [Fact]
    public async Task Docker_not_installed_is_an_error_with_its_guide_when_it_matters()
    {
        var door = new ScriptedDoor();

        var declared = (await ContainersAsync(door, declared: true))["doctor.docker"];
        var undeclared = (await ContainersAsync(door, declared: false))["doctor.docker"];

        Assert.Equal((DoctorStatus.Error, DoctorFixCatalog.InstallDocker), (declared.Status, declared.FixCode));
        Assert.Equal(DoctorStatus.NotApplicable, undeclared.Status);
    }

    [Fact]
    public async Task Docker_that_answers_with_text_is_not_checked_whatever_the_project_declares()
    {
        var door = new ScriptedDoor().Says(DoctorTool.DockerVersionJson, DoctorFixtures.Read("handmade-docker-version-garbage.txt"), exit: 1);

        Assert.Equal(DoctorStatus.Unknown, (await ContainersAsync(door, declared: true))["doctor.docker"].Status);
        Assert.Equal(DoctorStatus.Unknown, (await ContainersAsync(door, declared: false))["doctor.docker"].Status);
    }

    [Fact]
    public async Task Wsl_with_only_the_distribution_of_docker_has_no_distribution_of_its_own_and_offers_the_guide()
    {
        var wsl = (await ContainersAsync(ScriptedDoor.LikeTheDevelopmentMachine(), declared: true))["doctor.wsl"];

        Assert.Equal(DoctorStatus.Warning, wsl.Status);
        Assert.Equal(DoctorFixCatalog.InstallWslDistribution, wsl.FixCode);
        Assert.Equal("docker-desktop", wsl.Fact("distribution"));
    }

    [Theory]
    [InlineData("handmade-wsl-status-es-ubuntu.txt", DoctorStatus.Ok, null)]
    [InlineData("handmade-wsl-status-en-ubuntu.txt", DoctorStatus.Ok, null)]
    [InlineData("handmade-wsl-status-es-no-distribution.txt", DoctorStatus.Warning, DoctorFixCatalog.InstallWslDistribution)]
    [InlineData("handmade-wsl-status-en-no-distribution.txt", DoctorStatus.Warning, DoctorFixCatalog.InstallWslDistribution)]
    [InlineData("handmade-wsl-status-en-ubuntu-wsl1.txt", DoctorStatus.Warning, null)]
    [InlineData("handmade-wsl-status-garbage.txt", DoctorStatus.Error, DoctorFixCatalog.InstallWsl)]
    public async Task Wsl_has_its_state_and_its_guide_in_both_languages(string fixture, DoctorStatus status, string? fix)
    {
        var door = new ScriptedDoor().Says(DoctorTool.WslStatus, DoctorFixtures.Read(fixture));

        var wsl = (await ContainersAsync(door, declared: true))["doctor.wsl"];

        Assert.Equal(status, wsl.Status);
        Assert.Equal(fix, wsl.FixCode);
    }

    [Fact]
    public async Task Wsl_without_wsl_ends_with_an_error_code_and_its_message_and_that_is_not_installed_while_another_failure_is_not_checked()
    {
        var notInstalled = await ContainersAsync(new ScriptedDoor().Says(DoctorTool.WslStatus, "WSL no está instalado. Ejecuta wsl.exe --install.", exit: 1), declared: true);
        var other = await ContainersAsync(new ScriptedDoor().Says(DoctorTool.WslStatus, "Something went wrong.", exit: 1), declared: true);

        Assert.Equal((DoctorStatus.Error, DoctorFixCatalog.InstallWsl), (notInstalled["doctor.wsl"].Status, notInstalled["doctor.wsl"].FixCode));
        Assert.Equal(DoctorStatus.Unknown, other["doctor.wsl"].Status);
    }

    [Fact]
    public async Task An_engine_that_listens_only_on_the_pipe_of_docker_desktop_is_not_reported_as_stopped()
    {
        var door = ScriptedDoor.LikeTheDevelopmentMachine().Says(DoctorTool.DockerVersionDesktopPipe, DoctorFixtures.Read("handmade-docker-version-running.json"));

        var docker = (await ContainersAsync(door, declared: true))["doctor.docker"];

        Assert.Equal(DoctorStatus.Ok, docker.Status);
        Assert.Equal("27.3.1", docker.Fact("server"));
        Assert.Contains(door.Asked, a => a.Tool == DoctorTool.DockerVersionDesktopPipe);
    }

    [Fact]
    public async Task The_pipe_of_docker_desktop_is_asked_only_when_the_default_context_does_not_show_an_engine()
    {
        var running = new ScriptedDoor().Says(DoctorTool.DockerVersionJson, DoctorFixtures.Read("handmade-docker-version-running.json"));
        var missing = new ScriptedDoor();

        await ContainersAsync(running, declared: true);
        await ContainersAsync(missing, declared: true);

        Assert.DoesNotContain(running.Asked, a => a.Tool == DoctorTool.DockerVersionDesktopPipe);
        Assert.DoesNotContain(missing.Asked, a => a.Tool == DoctorTool.DockerVersionDesktopPipe);
    }

    [Fact]
    public async Task A_cancelled_read_stops_asking_and_the_whole_read_has_a_budget()
    {
        var door = new ScriptedDoor();
        var provider = new ToolSnapshotProvider(door, new FixedWebView2(null));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SnapshotAsync(null, cancel.Token));
        Assert.True(ToolSnapshotProvider.Budget <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Wsl_not_installed_is_an_error_with_its_guide_and_a_failing_status_is_not_checked()
    {
        var missing = (await ContainersAsync(new ScriptedDoor(), declared: true))["doctor.wsl"];
        var failing = (await ContainersAsync(new ScriptedDoor().Says(DoctorTool.WslStatus, DoctorFixtures.Read("handmade-wsl-status-en-ubuntu.txt"), exit: 1), declared: true))["doctor.wsl"];

        Assert.Equal((DoctorStatus.Error, DoctorFixCatalog.InstallWsl), (missing.Status, missing.FixCode));
        Assert.Equal(DoctorStatus.Unknown, failing.Status);
    }

    [Fact]
    public async Task The_inventory_of_the_verifier_and_the_checks_read_the_computer_once_per_run()
    {
        var door = ScriptedDoor.LikeTheDevelopmentMachine();
        var provider = new ToolSnapshotProvider(door, new FixedWebView2("130.0.2849.80"));
        provider.Begin();

        await new ToolsCheckSource(provider).CheckAsync(Project, CancellationToken.None);
        await new ContainersCheckSource(provider, new FakeEnvironmentProbe()).CheckAsync(Project, CancellationToken.None);
        var installed = await provider.ReadAsync(Project.RootPath, CancellationToken.None);
        provider.End();

        // Every tool once, except the launcher of Python, which is only asked when «python» is not found.
        Assert.Equal(Enum.GetValues<DoctorTool>().Length - 1, door.Asked.Count);
        Assert.Equal(3, installed.DotNetSdks.Count);
        Assert.Equal(AgentStudio.Domain.Environments.DockerState.EngineStopped, installed.Docker.State);
        Assert.Equal(AgentStudio.Domain.Environments.WslState.NoDistribution, installed.Wsl.State);

        // Outside a run nothing is kept: a later reading asks again.
        var before = door.Asked.Count;
        await provider.ReadAsync(Project.RootPath, CancellationToken.None);
        Assert.True(door.Asked.Count > before);
    }
}
