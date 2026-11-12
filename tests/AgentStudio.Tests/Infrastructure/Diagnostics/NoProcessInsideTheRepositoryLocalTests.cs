using System.IO;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>
/// F6-7 with real processes: a tool of the system that calls another one by its name must not find the one the repository planted, because the PATH the child gets is the clean one
/// (docs/20 D-02). The «npm» is a batch file outside the repository that runs <c>node</c> by name; the repository has a <c>node.cmd</c> that writes a sentinel.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class NoProcessInsideTheRepositoryLocalTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_tool_that_calls_another_by_name_runs_the_one_outside_the_repository_and_the_planted_one_never_runs()
    {
        var repo = _temp.CreateDirectory("repo");
        var outside = _temp.CreateDirectory("system-tools");
        var sentinel = Path.Combine(repo, "sentinel.txt");
        _temp.WriteFile("repo/node.cmd", "@echo off\r\necho planted> \"" + sentinel + "\"\r\necho v6.6.6\r\n");
        _temp.WriteFile("system-tools/node.cmd", "@echo off\r\necho v1.2.3\r\n");
        _temp.WriteFile("system-tools/npm.cmd", "@echo off\r\nnode --version\r\n");
        var path = string.Join(Path.PathSeparator, repo, outside);
        var roots = new FixedRoots(repo);
        await using var door = new DoctorToolRunner(new ExecutableLocator(() => path, roots: roots), new ProcessRunner(roots, () => path), DoctorFolders.Under(_temp.Combine("local")));

        var result = await door.RunAsync(DoctorTool.NpmVersion, repo, CancellationToken.None);

        Assert.Equal(DoctorToolOutcome.Completed, result.Outcome);
        Assert.StartsWith(outside, result.ExecutablePath!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v1.2.3", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(sentinel), "the node.cmd of the repository ran");
    }
}
