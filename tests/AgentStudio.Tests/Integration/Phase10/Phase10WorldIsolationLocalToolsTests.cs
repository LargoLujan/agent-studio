using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Integration.Phase9;

namespace AgentStudio.Tests.Integration.Phase10;

[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class Phase10WorldIsolationLocalToolsTests
{
    [Fact]
    public async Task The_real_low_factory_keeps_the_commit_copy_and_all_its_folders_in_the_worlds_temporary_tree()
    {
        await using var world = await Phase10World.OpenAsync();
        var factory = Assert.IsType<LowIntegrityWorkAreaFactory>(world.App.Get<IIsolatedWorkAreaFactory>());
        var commit = world.App.Repo.Head;
        var git = world.App.Get<IWorktreeManager>();

        // Use the same public copy port as the verifier; creating the area still checks its real Windows labels.
        await using var area = await factory.CreateAsync(
            commit,
            (destination, ct) => git.ArchiveCommitAsync(world.Project.RootPath, commit, destination, ct),
            TestContext.Current.CancellationToken);

        var fixtureRoot = Path.GetDirectoryName(world.Bare)!;
        Assert.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, Path.GetFullPath(area.RootDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(commit, area.Commit);
        string[] folders =
        [
            area.SourceDirectory, area.TempDirectory, area.CliHomeDirectory, area.NuGetPackagesDirectory,
            area.NuGetHttpCacheDirectory, area.NuGetPluginsCacheDirectory, area.CreateResultsDirectory(),
        ];
        Assert.All(folders, folder => Assert.StartsWith(
            area.RootDirectory + Path.DirectorySeparatorChar,
            Path.GetFullPath(folder),
            StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Path.Combine(area.SourceDirectory, ".git")));
        Assert.Equal(Phase5Runs.CheckScript.ReplaceLineEndings("\n"), File.ReadAllText(Path.Combine(area.SourceDirectory, "check.ps1")));
    }
}
