using System.IO;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>The roots that count as «inside the repository» (docs/21 D-05), computed from what is on disk with no <c>rev-parse</c>.</summary>
public sealed class RemoteConfigRootsTests : IDisposable
{
    private readonly string _container = Path.Combine(Path.GetTempPath(), "as raíces " + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() => TempFolders.Delete(_container);

    private string Make(params string[] folders)
    {
        var path = Path.Combine(_container, Path.Combine(folders));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void A_plain_repository_has_its_working_tree_and_its_git_directory_as_roots()
    {
        var repo = Make("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var worktrees = Make("wt");

        var roots = RemoteConfigRoots.TryCreate(repo, worktrees);

        Assert.NotNull(roots);
        Assert.Equal(Path.GetFullPath(repo), roots.RepositoryRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(repo), ".git"), roots.CommonDirectory);
        Assert.Null(roots.GitDirectory);
        Assert.Contains(Path.GetFullPath(worktrees), roots.Roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_origin_of_a_file_inside_any_root_is_inside_and_one_outside_or_not_a_file_is_not()
    {
        var repo = Make("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var worktrees = Make("wt");
        var outside = Make("home");
        var roots = RemoteConfigRoots.TryCreate(repo, worktrees)!;

        Assert.True(roots.IsInside("file:" + Path.Combine(repo, "extra.cfg")));
        Assert.True(roots.IsInside("file:" + Path.Combine(repo, "sub", "deep", "extra.cfg").Replace('\\', '/')));
        Assert.True(roots.IsInside("file:" + Path.Combine(repo, ".git", "config")));
        Assert.True(roots.IsInside("file:.git/config"));
        Assert.True(roots.IsInside("file:" + Path.Combine(worktrees, "x", "y.cfg")));
        Assert.False(roots.IsInside("file:" + Path.Combine(outside, ".gitconfig")));
        Assert.False(roots.IsInside("file:" + Path.Combine(outside, ".gitconfig").Replace('\\', '/')));
        Assert.False(roots.IsInside("command line:"));
        Assert.False(roots.IsInside("standard input:"));
        Assert.False(roots.IsInside("blob:abc"));
        Assert.True(roots.IsInside("file:"), "A file origin with no path fails closed.");
    }

    [Fact]
    public void A_folder_next_to_the_repository_with_the_same_beginning_is_not_inside()
    {
        var repo = Make("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var neighbour = Make("repo-neighbour");
        var roots = RemoteConfigRoots.TryCreate(repo, Make("wt"))!;

        Assert.False(roots.IsInside("file:" + Path.Combine(neighbour, "x.cfg")));
    }

    [Fact]
    public void A_project_folder_inside_a_repository_finds_the_working_tree_above()
    {
        var repo = Make("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var sub = Make("repo", "src", "app");

        var roots = RemoteConfigRoots.TryCreate(sub, Make("wt"))!;

        Assert.Equal(Path.GetFullPath(repo), roots.RepositoryRoot);
        Assert.Equal(Path.GetFullPath(sub), roots.WorkingDirectory);
        Assert.True(roots.IsInside("file:" + Path.Combine(repo, "other", "x.cfg")));
    }

    [Fact]
    public void A_linked_worktree_reads_its_gitfile_and_the_common_directory_and_counts_the_main_working_tree()
    {
        var main = Make("main");
        var common = Path.Combine(main, ".git");
        var admin = Path.Combine(common, "worktrees", "w1");
        Directory.CreateDirectory(admin);
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
        var linked = Make("linked");
        File.WriteAllText(Path.Combine(linked, ".git"), "gitdir: " + admin.Replace('\\', '/') + "\n");

        var roots = RemoteConfigRoots.TryCreate(linked, Make("wt"))!;

        Assert.Equal(Path.GetFullPath(linked), roots.RepositoryRoot);
        Assert.Equal(Path.GetFullPath(common), roots.CommonDirectory);
        Assert.Equal(Path.GetFullPath(admin), roots.GitDirectory);
        Assert.True(roots.IsInside("file:" + Path.Combine(main, "somefile.cfg")), "The main working tree is a root too.");
        Assert.True(roots.IsInside("file:" + Path.Combine(common, "config")));
        Assert.True(roots.IsInside("file:" + Path.Combine(linked, "x.cfg")));
    }

    [Fact]
    public void A_relative_gitdir_of_a_gitfile_is_read_from_the_folder_of_the_worktree()
    {
        var main = Make("main");
        var admin = Path.Combine(main, ".git", "worktrees", "w1");
        Directory.CreateDirectory(admin);
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..");
        var linked = Make("linked");
        File.WriteAllText(Path.Combine(linked, ".git"), "gitdir: ../main/.git/worktrees/w1");

        var roots = RemoteConfigRoots.TryCreate(linked, Make("wt"));

        Assert.NotNull(roots);
        Assert.Equal(Path.Combine(Path.GetFullPath(main), ".git"), roots.CommonDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a gitfile")]
    [InlineData("gitdir:")]
    [InlineData("gitdir: ../does-not-exist")]
    public void A_gitfile_that_says_nothing_usable_is_not_a_repository(string content)
    {
        var linked = Make("linked");
        File.WriteAllText(Path.Combine(linked, ".git"), content);

        Assert.Null(RemoteConfigRoots.TryCreate(linked, Make("wt")));
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_or_does_not_exist_or_is_relative_has_no_roots()
    {
        var plain = Make("plain");

        Assert.Null(RemoteConfigRoots.TryCreate(plain, Make("wt")));
        Assert.Null(RemoteConfigRoots.TryCreate(Path.Combine(_container, "missing"), Make("wt")));
        Assert.Null(RemoteConfigRoots.TryCreate("relative", Make("wt")));
        Assert.Null(RemoteConfigRoots.TryCreate(string.Empty, Make("wt")));
    }
}
