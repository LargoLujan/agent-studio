using System.IO;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop;

public sealed class ExplorerFilterTests
{
    [Theory]
    [InlineData(".git")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".vs")]
    [InlineData("BIN")]
    public void Directories_OnTheHiddenList_AreHidden(string name) =>
        Assert.True(ExplorerFilter.IsHidden(name, isDirectory: true, "proj"));

    [Theory]
    [InlineData("src")]
    [InlineData("binary")]
    [InlineData(".agentstudio")]
    [InlineData(".github")]
    public void OtherDirectories_AreShown(string name) =>
        Assert.False(ExplorerFilter.IsHidden(name, isDirectory: true, "proj"));

    [Theory]
    [InlineData("agentstudio.db", ".agentstudio", true)]
    [InlineData("agentstudio.db-wal", ".agentstudio", true)]
    [InlineData("agentstudio.db-shm", ".agentstudio", true)]
    [InlineData("settings.json", ".agentstudio", false)]
    [InlineData("data.db", "src", false)]
    public void Databases_AreHiddenOnlyInsideDotAgentStudio(string name, string parent, bool hidden) =>
        Assert.Equal(hidden, ExplorerFilter.IsHidden(name, isDirectory: false, parent));

    [Fact]
    public void FilesNamedLikeHiddenDirectories_AreShown() =>
        Assert.False(ExplorerFilter.IsHidden("bin", isDirectory: false, "proj"));

    [Fact]
    public void TheGitPointerFileOfAWorktree_IsHiddenToo() =>
        Assert.True(ExplorerFilter.IsHidden(".git", isDirectory: false, "proj"));
}

public sealed class ExplorerViewModelTests
{
    private static ExplorerNodeViewModel Root(ExplorerViewModel explorer) => Assert.Single(explorer.Roots);

    private static List<string> Names(ExplorerNodeViewModel node) => node.Children.Select(c => c.Name).ToList();

    [Fact]
    public void Load_ShowsTheProjectFolderAsExpandedRoot()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("readme.md");
        var explorer = new ExplorerViewModel();

        explorer.Load(temp.Path);

        Assert.True(explorer.HasProject);
        Assert.False(explorer.HasNoProject);
        Assert.True(Root(explorer).IsExpanded);
        Assert.Equal(["readme.md"], Names(Root(explorer)));
    }

    [Fact]
    public void Load_Null_ClearsTheTree()
    {
        using var temp = new TempDirectory();
        var explorer = new ExplorerViewModel();
        explorer.Load(temp.Path);

        explorer.Load(null);

        Assert.Empty(explorer.Roots);
        Assert.False(explorer.HasProject);
    }

    [Fact]
    public void Children_AreOrderedFoldersFirstThenFilesIgnoringCase()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("b.txt");
        temp.WriteFile("A.txt");
        temp.CreateDirectory("zeta");
        temp.CreateDirectory("Alpha");
        var explorer = new ExplorerViewModel();

        explorer.Load(temp.Path);

        Assert.Equal(["Alpha", "zeta", "A.txt", "b.txt"], Names(Root(explorer)));
    }

    [Fact]
    public void Children_HideGitBinObjNodeModulesVsAndAgentStudioDatabases()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        temp.CreateDirectory("bin");
        temp.CreateDirectory("obj");
        temp.CreateDirectory("node_modules");
        temp.CreateDirectory(".vs");
        temp.CreateDirectory("src");
        temp.WriteFile(".agentstudio/agentstudio.db");
        temp.WriteFile(".agentstudio/agentstudio.db-wal");
        temp.WriteFile(".agentstudio/layout-notes.md");
        temp.WriteFile(".agentstudio/bitacora/2026-10-15.md");
        var explorer = new ExplorerViewModel();

        explorer.Load(temp.Path);
        var root = Root(explorer);
        var dotAgentStudio = root.Children.Single(c => c.Name == ".agentstudio");
        dotAgentStudio.IsExpanded = true;

        Assert.Equal([".agentstudio", "src"], Names(root));
        Assert.Equal(["bitacora", "layout-notes.md"], Names(dotAgentStudio));
    }

    [Fact]
    public void Folders_LoadTheirChildrenOnlyWhenExpanded()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/Program.cs");
        var explorer = new ExplorerViewModel();
        explorer.Load(temp.Path);
        var src = Root(explorer).Children.Single();

        Assert.False(src.IsLoaded);
        Assert.True(src.Children.Single().IsMessage);

        src.IsExpanded = true;

        Assert.True(src.IsLoaded);
        Assert.Equal(["Program.cs"], Names(src));
        Assert.All(src.Children, c => Assert.False(c.IsMessage));
    }

    [Fact]
    public void Refresh_PicksUpNewAndRemovedFilesKeepingExpandedFolders()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/old.cs");
        temp.WriteFile("keep.txt");
        var explorer = new ExplorerViewModel();
        explorer.Load(temp.Path);
        var src = Root(explorer).Children.Single(c => c.Name == "src");
        src.IsExpanded = true;

        File.WriteAllText(temp.Combine("src", "new.cs"), "");
        File.Delete(temp.Combine("src", "old.cs"));
        File.WriteAllText(temp.Combine("added.txt"), "");
        explorer.RefreshCommand.Execute(null);

        Assert.Equal(["src", "added.txt", "keep.txt"], Names(Root(explorer)));
        Assert.Same(src, Root(explorer).Children[0]);
        Assert.True(src.IsExpanded);
        Assert.Equal(["new.cs"], Names(src));
    }

    [Fact]
    public void Refresh_WithoutProject_CannotExecute() =>
        Assert.False(new ExplorerViewModel().RefreshCommand.CanExecute(null));

    [Fact]
    public void Open_OnAFile_RaisesFileActivated_ButNotOnFoldersOrMessages()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("src/Program.cs");
        temp.CreateDirectory("empty");
        var explorer = new ExplorerViewModel();
        var opened = new List<string>();
        explorer.FileActivated += (_, path) => opened.Add(path);
        explorer.Load(temp.Path);
        var src = Root(explorer).Children.Single(c => c.Name == "src");
        var placeholder = Root(explorer).Children.Single(c => c.Name == "empty").Children.Single();
        src.IsExpanded = true;

        explorer.OpenCommand.Execute(src);
        explorer.OpenCommand.Execute(placeholder);
        explorer.OpenCommand.Execute(null);
        explorer.OpenCommand.Execute(src.Children.Single());

        Assert.Equal([file], opened);
    }

    [Fact]
    public void EmptyFolder_LosesItsPlaceholderWhenExpanded()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("empty");
        var explorer = new ExplorerViewModel();
        explorer.Load(temp.Path);
        var empty = Root(explorer).Children.Single();

        empty.IsExpanded = true;

        Assert.Empty(empty.Children);
    }
}
