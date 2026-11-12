using System.IO;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>What the probe of the project answers from names and from the PATH, without opening a file of the project (docs/20 D-03).</summary>
public sealed class ProjectEnvironmentProbeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_folder_with_git_and_hooks_lists_the_hooks_that_are_not_samples()
    {
        var repo = _temp.CreateDirectory("repo");
        _temp.WriteFile("repo/.git/hooks/pre-commit.sample", "#!/bin/sh");
        _temp.WriteFile("repo/.git/hooks/pre-push", "#!/bin/sh");
        _temp.WriteFile("repo/.git/hooks/post-merge", "#!/bin/sh");

        var layout = new ProjectEnvironmentProbe().ReadLayout(repo);

        Assert.True(layout.HasGit);
        Assert.True(layout.HooksReadable);
        Assert.Equal(["post-merge", "pre-push"], layout.HookNames.ToArray());
    }

    [Fact]
    public void A_repository_without_hooks_and_a_folder_without_git_are_told_apart()
    {
        var repo = _temp.CreateDirectory("repo/.git");
        var none = _temp.CreateDirectory("plain");

        var withGit = new ProjectEnvironmentProbe().ReadLayout(Path.GetDirectoryName(repo)!);
        var without = new ProjectEnvironmentProbe().ReadLayout(none);

        Assert.True(withGit.HasGit);
        Assert.True(withGit.HooksReadable);
        Assert.Empty(withGit.HookNames);
        Assert.False(without.HasGit);
    }

    [Fact]
    public void The_git_file_of_a_linked_worktree_has_git_but_its_hooks_are_not_read()
    {
        var repo = _temp.CreateDirectory("worktree");
        _temp.WriteFile("worktree/.git", "gitdir: ../main/.git/worktrees/w");

        var layout = new ProjectEnvironmentProbe().ReadLayout(repo);

        Assert.True(layout.HasGit);
        Assert.False(layout.HooksReadable);
    }

    [Fact]
    public void A_hooks_folder_that_is_a_junction_is_not_read()
    {
        var repo = _temp.CreateDirectory("repo/.git");
        var outside = _temp.CreateDirectory("outside");
        _temp.WriteFile("outside/pre-commit", "#!/bin/sh");
        var link = Path.Combine(repo, "hooks");
        Directory.CreateSymbolicLink(link, outside);

        var layout = new ProjectEnvironmentProbe().ReadLayout(Path.GetDirectoryName(repo)!);

        Assert.True(layout.HasGit);
        Assert.False(layout.HooksReadable);
        Assert.Empty(layout.HookNames);
    }

    [Fact]
    public void The_folders_of_the_path_that_lie_inside_the_project_are_named()
    {
        var repo = _temp.CreateDirectory("repo");
        var bin = _temp.CreateDirectory("repo/node_modules/.bin");
        var system = _temp.CreateDirectory("tools");
        var path = string.Join(Path.PathSeparator, bin, system, "relative", bin);

        var inside = new ProjectEnvironmentProbe(() => path).PathFoldersInside(repo);

        Assert.Equal([bin], inside.ToArray());
        Assert.Empty(new ProjectEnvironmentProbe(() => system).PathFoldersInside(repo));
        Assert.Empty(new ProjectEnvironmentProbe(() => null).PathFoldersInside(repo));
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("compose.yaml")]
    [InlineData("docker-compose.yml")]
    [InlineData(".devcontainer/devcontainer.json")]
    [InlineData(".devcontainer.json")]
    public void A_project_declares_containers_when_one_of_the_closed_names_exists(string file)
    {
        var repo = _temp.CreateDirectory("repo");
        _temp.WriteFile("repo/" + file, "ignored: the probe does not read it");

        Assert.True(new ProjectEnvironmentProbe().DeclaresContainers(repo));
    }

    [Fact]
    public void A_project_with_other_files_does_not_declare_containers()
    {
        var repo = _temp.CreateDirectory("repo");
        _temp.WriteFile("repo/src/Dockerfile", "a subfolder is not searched");
        _temp.WriteFile("repo/README.md", "# repo");

        Assert.False(new ProjectEnvironmentProbe().DeclaresContainers(repo));
    }
}
