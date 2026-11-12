using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>
/// ADR-0018, second addendum of 2026-11-12: what Agent Studio launches does not look for its own programs where the project can plant
/// them. A <c>codex.cmd</c> shim found outside the project still calls <c>node</c> by name, and cmd.exe would look first in the current
/// folder (the worktree, which holds what the repository versions) and then in every folder of the inherited PATH. So every child gets
/// <c>NoDefaultCurrentDirectoryInExePath=1</c> and a PATH without relative entries and without the untrusted roots. The programs here are
/// batch files that only leave a marker file; nothing is run that the test did not write.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class ChildEnvironmentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedRoots(params string[] roots) : IUntrustedRoots
    {
        public IReadOnlyList<string> Roots { get; } = roots;
    }

    private static void Marker(string folder, string name, string marker)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), $"@echo off\r\necho ran> \"%~dp0{marker}\"\r\n");
    }

    private sealed class Scene
    {
        public Scene(TempDirectory temp)
        {
            Project = temp.CreateDirectory("proyecto");
            ProjectBin = temp.CreateDirectory(Path.Combine("proyecto", "bin"));
            Trusted = temp.CreateDirectory("fuera");
            Shim = Path.Combine(Trusted, "shim.cmd");
            File.WriteAllText(Shim, "@echo off\r\nnode\r\n");
            Marker(Trusted, "node.cmd", "trusted-ran.txt");
            PathValue = ProjectBin + Path.PathSeparator + Trusted + Path.PathSeparator + Environment.SystemDirectory;
        }

        public string Project { get; }

        public string ProjectBin { get; }

        public string Trusted { get; }

        public string Shim { get; }

        public string PathValue { get; }

        public bool Ran(string folder, string marker) => File.Exists(Path.Combine(folder, marker));
    }

    [Fact]
    public async Task A_shim_that_calls_node_by_name_does_not_run_a_node_cmd_of_the_working_folder()
    {
        using var temp = new TempDirectory();
        var scene = new Scene(temp);
        Marker(scene.Project, "node.cmd", "cwd-ran.txt");
        var runner = new ProcessRunner(new FixedRoots(scene.Project), () => scene.PathValue);

        var result = await runner.RunAsync(new ProcessSpec(scene.Shim, [], scene.Project), Token);

        Assert.Equal(0, result.ExitCode);
        Assert.False(scene.Ran(scene.Project, "cwd-ran.txt"), "cmd.exe took node from the current folder.");
        Assert.True(scene.Ran(scene.Trusted, "trusted-ran.txt"), "The shim should have found the node outside the project.");
    }

    [Fact]
    public async Task A_shim_that_calls_node_by_name_does_not_run_a_node_cmd_of_a_project_folder_on_the_PATH()
    {
        using var temp = new TempDirectory();
        var scene = new Scene(temp);
        Marker(scene.ProjectBin, "node.cmd", "path-ran.txt");
        var runner = new ProcessRunner(new FixedRoots(scene.Project), () => scene.PathValue);

        var result = await runner.RunAsync(new ProcessSpec(scene.Shim, [], scene.Project), Token);

        Assert.Equal(0, result.ExitCode);
        Assert.False(scene.Ran(scene.ProjectBin, "path-ran.txt"), "The shim took node from a project folder of the PATH.");
        Assert.True(scene.Ran(scene.Trusted, "trusted-ran.txt"));
    }

    [Fact]
    public async Task The_streaming_runner_closes_the_same_two_ways()
    {
        using var temp = new TempDirectory();
        var scene = new Scene(temp);
        Marker(scene.Project, "node.cmd", "cwd-ran.txt");
        Marker(scene.ProjectBin, "node.cmd", "path-ran.txt");
        var runner = new StreamingProcessRunner(roots: new FixedRoots(scene.Project), pathVariable: () => scene.PathValue);

        await using var session = await runner.StartAsync(new ProcessSpec(scene.Shim, [], scene.Project), Token);
        await foreach (var _ in session.Output.WithCancellation(Token))
        {
        }

        Assert.Equal(0, await session.Exited);
        Assert.False(scene.Ran(scene.Project, "cwd-ran.txt"));
        Assert.False(scene.Ran(scene.ProjectBin, "path-ran.txt"));
        Assert.True(scene.Ran(scene.Trusted, "trusted-ran.txt"));
    }

    [Fact]
    public async Task Control_without_the_fix_in_the_environment_the_marker_of_the_working_folder_is_left()
    {
        using var temp = new TempDirectory();
        var scene = new Scene(temp);
        Marker(scene.Project, "node.cmd", "cwd-ran.txt");

        // The caller's own environment wins: this is the launch as it was before, and it proves the markers detect the exploit.
        var raw = new Dictionary<string, string?> { ["NoDefaultCurrentDirectoryInExePath"] = null, ["PATH"] = scene.PathValue };
        var result = await new ProcessRunner(new FixedRoots(scene.Project), () => scene.PathValue)
            .RunAsync(new ProcessSpec(scene.Shim, [], scene.Project, raw), Token);

        Assert.Equal(0, result.ExitCode);
        Assert.True(scene.Ran(scene.Project, "cwd-ran.txt"), "The control should have run the node.cmd of the working folder.");
    }

    [Fact]
    public async Task The_child_sees_a_PATH_without_relative_entries_nor_project_folders_and_the_flag()
    {
        using var temp = new TempDirectory();
        var scene = new Scene(temp);
        var path = "." + Path.PathSeparator + "relativa" + Path.PathSeparator + scene.PathValue;
        var runner = new ProcessRunner(new FixedRoots(scene.Project), () => path);

        var result = await runner.RunAsync(new ProcessSpec("cmd.exe", ["/c", "echo [%PATH%] [%NoDefaultCurrentDirectoryInExePath%]"]), Token);

        var line = result.StandardOutput.Trim();
        Assert.DoesNotContain(scene.ProjectBin, line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scene.Trusted, line, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("[" + scene.Trusted, line, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("] [1]", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_isolated_child_gets_the_clean_PATH_and_keeps_the_current_folder_lookup_of_its_copy()
    {
        using var temp = new TempDirectory();
        var scene = new Scene(temp);
        var path = "." + Path.PathSeparator + scene.PathValue;
        var runner = new StreamingProcessRunner(roots: new FixedRoots(scene.Project), pathVariable: () => path);

        // Decision (ADR-0018): the isolated copy is the working folder of the step on purpose, at Low integrity and without writes;
        // so its lookup in the current folder stays, and only the PATH is cleaned. (The flag itself is not asserted: the person's own environment may already carry it.)
        await using var session = await runner.StartAsync(
            new ProcessSpec("cmd.exe", ["/c", "echo [%PATH%] [%NoDefaultCurrentDirectoryInExePath%]"], Isolation: ProcessIsolation.LowIntegrity), Token);
        var lines = new List<string>();
        await foreach (var line in session.Output.WithCancellation(Token))
        {
            lines.Add(line.Text);
        }

        var text = string.Join('\n', lines);
        Assert.DoesNotContain(scene.ProjectBin, text, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("[" + scene.Trusted, text, StringComparison.OrdinalIgnoreCase);
    }
}
