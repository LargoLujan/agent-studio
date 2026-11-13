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

        // A program the locator finds outside the project must come back understood through the allow list of the environment, the neutral folder and the empty configuration of
        // Docker: «Unreadable» for a tool that exists would mean the door breaks it (the states are in the message, for the report).
        var locator = new ExecutableLocator();
        var states = string.Join(", ", new[]
        {
            $"dotnet={snapshot.DotNetSdks.State}", $"node={snapshot.Node.State}", $"npm={snapshot.Npm.State}", $"python={snapshot.Python.State}",
            $"git={snapshot.Git.State}", $"docker={snapshot.Docker.State}", $"wsl={snapshot.Wsl.State}", $"webview2={snapshot.WebView2.State}",
        });
        TestContext.Current.SendDiagnosticMessage("Auto Doctor, real door: " + states);
        foreach (var (command, state) in new[]
        {
            ("dotnet", snapshot.DotNetSdks.State), ("git", snapshot.Git.State), ("node", snapshot.Node.State), ("npm", snapshot.Npm.State),
            ("docker", snapshot.Docker.State), ("wsl", snapshot.Wsl.State),
        })
        {
            if (locator.Find(command) is not null)
            {
                Assert.True(state == ToolState.Found, $"{command} is installed outside the project and the door did not read it: {states}");
            }
        }

        Assert.NotEqual(ToolState.InsideProject, snapshot.Node.State);
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
