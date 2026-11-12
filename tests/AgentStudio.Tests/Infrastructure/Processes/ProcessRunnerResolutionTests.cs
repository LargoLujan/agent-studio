using System.ComponentModel;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Processes;

/// <summary>
/// WP-0 of Phase 6 (ADR-0018, addendum of 2026-11-12): a program named without a path (<c>dotnet</c>, <c>node</c>) is resolved by
/// the runner outside the untrusted roots, not by CreateProcess, which would also take it from any absolute PATH entry of the project.
/// The programs are copies of <c>whoami.exe</c> under another name: harmless, and they answer with exit code 0.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class ProcessRunnerResolutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedRoots(params string[] roots) : IUntrustedRoots
    {
        public IReadOnlyList<string> Roots { get; } = roots;
    }

    private static string Plant(string folder, string name = "tool-del-proyecto.exe")
    {
        Directory.CreateDirectory(folder);
        var planted = Path.Combine(folder, name);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), planted);
        return planted;
    }

    [Fact]
    public async Task A_bare_name_found_only_in_an_untrusted_folder_of_the_PATH_is_not_started()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        var bin = Path.Combine(project, "bin");
        Plant(bin);
        var runner = new ProcessRunner(new FixedRoots(project), () => bin);

        var failure = await Assert.ThrowsAsync<Win32Exception>(() =>
            runner.RunAsync(new ProcessSpec("tool-del-proyecto", ["/user"]), Token));

        Assert.Equal(2, failure.NativeErrorCode);
        Assert.Contains("project folder", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bare_name_resolves_outside_the_project_even_when_the_working_folder_is_the_project()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        var trusted = Path.Combine(temp.Path, "sistema");
        Plant(trusted);
        Plant(Path.Combine(project, "bin"));
        var path = Path.Combine(project, "bin") + Path.PathSeparator + trusted;
        var runner = new ProcessRunner(new FixedRoots(project), () => path);

        var result = await runner.RunAsync(new ProcessSpec("tool-del-proyecto", ["/user"], project), Token);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Without_roots_a_bare_name_of_the_PATH_resolves_as_before()
    {
        using var temp = new TempDirectory();
        var tools = Path.Combine(temp.Path, "herramientas");
        Plant(tools);

        var result = await new ProcessRunner(null, () => tools).RunAsync(new ProcessSpec("tool-del-proyecto", ["/user"]), Token);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_name_with_a_path_is_the_choice_of_the_caller_and_is_not_filtered()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        var planted = Plant(Path.Combine(project, "bin"));
        var runner = new ProcessRunner(new FixedRoots(project), () => string.Empty);

        var result = await runner.RunAsync(new ProcessSpec(planted, ["/user"]), Token);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_bare_name_of_the_system_folder_is_found_even_with_an_empty_PATH()
    {
        var runner = new ProcessRunner(new FixedRoots(Path.GetTempPath()), () => string.Empty);

        var result = await runner.RunAsync(new ProcessSpec("whoami", ["/user"]), Token);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_bare_name_that_exists_nowhere_fails_to_start_in_the_streaming_runner_too()
    {
        var runner = new StreamingProcessRunner(roots: new FixedRoots(Path.GetTempPath()), pathVariable: () => string.Empty);

        var failure = await Assert.ThrowsAsync<Win32Exception>(() => runner.StartAsync(new ProcessSpec("programa-que-no-existe", []), Token));

        Assert.Equal(2, failure.NativeErrorCode);
    }
}
