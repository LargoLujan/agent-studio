using System.IO;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>
/// The door over the real tools of this computer, with the real locator and the real runner of processes (docs/20 F6-1 and F6-9). It only
/// asks <c>--version</c>, <c>--list-sdks</c>, <c>docker version</c> and <c>wsl --status</c>: nothing that spends quota, nothing that starts an engine.
/// Without a tool the check says so, so none of these asserts that a tool is there; they assert that the answer is understood or honestly «no».
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class DoctorToolsLocalTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private DoctorToolRunner RealDoor() =>
        new(new ExecutableLocator(roots: new FixedRoots(_temp.Combine("repo"))), new ProcessRunner(new FixedRoots(_temp.Combine("repo"))), DoctorFolders.Under(_temp.Combine("local")));

    [Fact]
    public async Task Every_tool_of_the_catalog_answers_in_time_and_what_it_says_is_understood_or_it_is_not_there()
    {
        _temp.CreateDirectory("repo");
        await using var door = RealDoor();
        var snapshotProvider = new ToolSnapshotProvider(door, new WebView2RegistryProbe());

        var snapshot = await snapshotProvider.SnapshotAsync(_temp.Combine("repo"), CancellationToken.None);

        // A tool that ran must be understood (Found) or be reported as not understood (Unreadable): never an exception.
        Assert.All(
            new[] { snapshot.Node.State, snapshot.Npm.State, snapshot.Git.State, snapshot.DotNetSdks.State, snapshot.Docker.State, snapshot.Wsl.State },
            state => Assert.NotEqual(ToolState.InsideProject, state));
        if (snapshot.DotNetSdks.State == ToolState.Found)
        {
            Assert.NotEmpty(snapshot.DotNetSdks.Value!);
        }

        if (snapshot.Docker.State == ToolState.Found)
        {
            Assert.NotNull(snapshot.Docker.Value!.Client);
        }

        var installed = snapshot.ToInstalledTools();
        Assert.Equal(snapshot.DotNetSdks.IsFound ? snapshot.DotNetSdks.Value!.Count : 0, installed.DotNetSdks.Count);
    }

    [Fact]
    public async Task The_door_leaves_the_neutral_folder_empty()
    {
        _temp.CreateDirectory("repo");
        var folders = DoctorFolders.Under(_temp.Combine("local"));
        await using var door = new DoctorToolRunner(new ExecutableLocator(), new ProcessRunner(), folders);

        await door.RunAsync(DoctorTool.GitVersion, null, CancellationToken.None);

        Assert.True(Directory.Exists(folders.WorkingFolder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folders.WorkingFolder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folders.DockerConfigFolder));
    }

    [Fact]
    public void The_registry_probe_of_webview2_never_throws()
    {
        var version = new WebView2RegistryProbe().Read();

        Assert.True(version is null || version.Numbers.Any(n => n != 0));
    }
}
