using System.IO;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>
/// F6-7, the half that belongs to Auto Doctor (docs/20 section 8): no process starts inside the repository or with an executable of it. The
/// repository plants every program the doctor could ask for and puts its folder first on the PATH; a recording runner sees every start.
/// Git, in the section «Proyecto», is the exception of decision 13: its layer starts <c>git</c> in the repository, with the executable of the system.
/// </summary>
public sealed class NoProcessInsideTheRepositoryTests : IDisposable
{
    private static readonly string[] Planted =
    [
        "dotnet.cmd", "dotnet.exe", "node.exe", "npm.cmd", "python.exe", "py.exe", "git.exe", "docker.exe", "wsl.exe", "claude.cmd", "codex.cmd", "gemini.cmd",
    ];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private (string Repo, string Outside, string Path) Layout()
    {
        var repo = _temp.CreateDirectory("repo");
        var outside = _temp.CreateDirectory("system-tools");
        foreach (var name in Planted)
        {
            _temp.WriteFile("repo/" + name, "planted");
            _temp.WriteFile("repo/bin/" + name, "planted");
        }

        _temp.WriteFile("repo/.venv/Scripts/python.exe", "planted");
        _temp.WriteFile("repo/global.json", """{"sdk":{"version":"10.0.303","paths":[".dotnet","$host$"]}}""");

        // The stand-ins the system has: the same names, with the extensions the real ones have.
        foreach (var name in new[] { "dotnet.exe", "node.exe", "npm.cmd", "python.exe", "py.exe", "git.exe", "docker.exe", "wsl.exe", "claude.cmd", "codex.cmd" })
        {
            _temp.WriteFile("system-tools/" + name, "system");
        }

        // The repository folder is first on the PATH, with its own subfolder: both are inside the project.
        var path = string.Join(System.IO.Path.PathSeparator, Path.Combine(repo, "bin"), repo, outside);
        return (repo, outside, path);
    }

    private static bool Inside(string path, string root) =>
        System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task No_tool_the_doctor_asks_for_starts_in_the_repository_or_with_a_program_of_it()
    {
        var (repo, outside, path) = Layout();
        var roots = new FixedRoots(repo);
        var locator = new ExecutableLocator(() => path, () => ".COM;.EXE;.BAT;.CMD", roots);
        var runner = new RecordingProcessRunner(spec => RecordingProcessRunner.Ok("v1.0.0"));
        // The process has variables that point into the repository (and its own PATH): none may reach a tool.
        var ambient = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NODE_PATH"] = repo, ["DOTNET_ROOT"] = Path.Combine(repo, ".dotnet"), ["PATH"] = path, ["PYTHONHOME"] = repo };
        await using var door = new DoctorToolRunner(locator, runner, DoctorFolders.Under(_temp.Combine("local")), () => ambient);

        var results = new List<DoctorToolResult>();
        foreach (var tool in Enum.GetValues<DoctorTool>())
        {
            results.Add(await door.RunAsync(tool, repo, CancellationToken.None));
        }

        // Not vacuous: every catalog entry started (the system has a stand-in of each), so there is a spec to look at for each one.
        Assert.Equal(Enum.GetValues<DoctorTool>().Length, runner.Specs.Count);
        Assert.Equal(DoctorToolOutcome.Completed, results[(int)DoctorTool.DotNetListSdks].Outcome);
        Assert.All(runner.Specs, spec =>
        {
            Assert.False(Inside(spec.FileName, repo), $"{spec.FileName} is inside the repository");
            Assert.StartsWith(outside, spec.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.False(Inside(spec.WorkingDirectory!, repo), $"{spec.WorkingDirectory} is inside the repository");
            Assert.False(string.Equals(repo, spec.WorkingDirectory, StringComparison.OrdinalIgnoreCase));

            // The environment of the spec: PATH is not named (the runner of processes sets the clean one), what is named is removed, and nothing it sets points into the repository.
            var env = spec.Environment!;
            Assert.DoesNotContain(env.Keys, k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase));
            Assert.Null(env["NODE_PATH"]);
            Assert.Null(env["DOTNET_ROOT"]);
            Assert.Null(env["PYTHONHOME"]);
            Assert.All(env.Where(e => e.Value is not null), e => Assert.False(e.Value!.Contains(repo, StringComparison.OrdinalIgnoreCase), e.Key));
        });
    }

    [Fact]
    public async Task The_probes_of_the_provider_programs_start_outside_the_repository_in_the_neutral_folder()
    {
        var (repo, outside, path) = Layout();
        var roots = new FixedRoots(repo);
        var locator = new ExecutableLocator(() => path, () => ".COM;.EXE;.BAT;.CMD", roots);
        var runner = new RecordingProcessRunner(spec => RecordingProcessRunner.Ok("2.1.290"));
        var neutral = _temp.Combine("local", "doctor", "cwd");
        var probes = new CliProbe[]
        {
            new ClaudeCodeProbe(locator, runner) { WorkingDirectory = neutral },
            new CodexCliProbe(locator, runner) { WorkingDirectory = neutral },
        };

        foreach (var probe in probes)
        {
            await probe.ProbeAsync(CancellationToken.None);
        }

        // claude --version, claude auth status, codex --version, codex login status: all four started, none in the repository.
        Assert.Equal(4, runner.Specs.Count);
        Assert.Contains(runner.Specs, s => s.Arguments.SequenceEqual(["auth", "status"]));
        Assert.Contains(runner.Specs, s => s.Arguments.SequenceEqual(["login", "status"]));
        Assert.All(runner.Specs, spec =>
        {
            Assert.False(Inside(spec.FileName, repo), $"{spec.FileName} is inside the repository");
            Assert.StartsWith(outside, spec.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(neutral, spec.WorkingDirectory);
        });
    }

    [Fact]
    public void The_program_git_resolves_to_is_the_one_of_the_system_and_not_the_planted_one()
    {
        var (repo, outside, path) = Layout();
        var locator = new ExecutableLocator(() => path, () => ".COM;.EXE;.BAT;.CMD", new FixedRoots(repo));

        var git = locator.Find("git");

        Assert.NotNull(git);
        Assert.StartsWith(outside, git, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(locator.FindInUntrustedRoot("git"));
    }

    [Fact]
    public async Task With_only_the_planted_programs_nothing_starts_and_the_door_says_they_are_inside_the_project()
    {
        var repo = _temp.CreateDirectory("repo");
        foreach (var name in Planted)
        {
            _temp.WriteFile("repo/" + name, "planted");
        }

        var locator = new ExecutableLocator(() => repo, () => ".COM;.EXE;.BAT;.CMD", new FixedRoots(repo));
        var runner = new RecordingProcessRunner();
        await using var door = new DoctorToolRunner(locator, runner, DoctorFolders.Under(_temp.Combine("local")), () => new Dictionary<string, string>());

        foreach (var tool in Enum.GetValues<DoctorTool>())
        {
            var result = await door.RunAsync(tool, repo, CancellationToken.None);
            Assert.Equal(DoctorToolOutcome.InsideProject, result.Outcome);
        }

        Assert.Empty(runner.Specs);
    }
}
