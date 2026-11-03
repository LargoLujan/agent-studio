using System.IO;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop.QuickOpen;

public sealed class QuickOpenItemTests
{
    [Fact]
    public void SplitsTheRelativePathIntoFileNameAndDirectory()
    {
        var item = new QuickOpenItem("src/Views/Main.xaml", @"C:\p\src\Views\Main.xaml");

        Assert.Equal("Main.xaml", item.FileName);
        Assert.Equal(@"src\Views", item.Directory);
    }

    [Fact]
    public void AFileInTheProjectRoot_HasNoDirectory()
    {
        var item = new QuickOpenItem("README.md", @"C:\p\README.md");

        Assert.Equal("README.md", item.FileName);
        Assert.Equal(string.Empty, item.Directory);
    }
}

public sealed class QuickOpenViewModelTests
{
    private static readonly Task Done = Task.CompletedTask;

    private static async Task<QuickOpenViewModel> OpenAsync(string root, Func<string, Task>? onChosen = null)
    {
        var vm = new QuickOpenViewModel();
        await vm.OpenAsync(root, "Ir al fichero", onChosen ?? (_ => Done));
        return vm;
    }

    /// <summary>An indexer the test releases by hand, to observe the state while the project is being indexed.</summary>
    private sealed class GatedIndexer(ProjectFileIndex result)
    {
        private readonly ManualResetEventSlim _gate = new();

        public ProjectFileIndex Index(string root, CancellationToken cancellationToken)
        {
            // Bounded, so a broken test fails instead of hanging the run.
            _gate.Wait(TimeSpan.FromSeconds(30));
            return result;
        }

        public void Release() => _gate.Set();
    }

    [Fact]
    public async Task OpenAsync_ListsTheProjectFiles_ShortestFirst_AndSelectsTheFirst()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/Program.cs");
        temp.WriteFile("a.txt");
        temp.WriteFile("docs/readme.md");

        var vm = await OpenAsync(temp.Path);

        Assert.True(vm.IsOpen);
        Assert.False(vm.IsIndexing);
        Assert.Equal("Ir al fichero", vm.Title);
        Assert.Equal(string.Empty, vm.Query);
        Assert.Equal(["a.txt", "docs/readme.md", "src/Program.cs"], vm.Results.Select(r => r.RelativePath));
        Assert.Equal(0, vm.SelectedIndex);
        Assert.Same(vm.Results[0], vm.SelectedItem);
    }

    [Fact]
    public async Task Results_CarryTheFullPathOfTheFile()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("src/Program.cs");

        var vm = await OpenAsync(temp.Path);

        Assert.Equal(file, Assert.Single(vm.Results).FullPath);
    }

    [Fact]
    public async Task Query_FiltersAndRanksTheResults()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/ViewModels/MainViewModel.cs");
        temp.WriteFile("src/Views/MainWindow.xaml");
        temp.WriteFile("docs/notes.md");
        var vm = await OpenAsync(temp.Path);

        vm.Query = "main";

        Assert.Equal(["src/Views/MainWindow.xaml", "src/ViewModels/MainViewModel.cs"], vm.Results.Select(r => r.RelativePath));
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public async Task Query_WithNoMatch_EmptiesTheListAndSaysSo()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        var vm = await OpenAsync(temp.Path);

        vm.Query = "zzz";

        Assert.Empty(vm.Results);
        Assert.Equal(-1, vm.SelectedIndex);
        Assert.Null(vm.SelectedItem);
        Assert.Equal("Ninguna coincidencia.", vm.Notice);
        Assert.False(vm.AcceptCommand.CanExecute(null));
    }

    [Fact]
    public async Task Query_ChangingItPutsTheSelectionBackOnTheBestMatch()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a1.txt");
        temp.WriteFile("a2.txt");
        temp.WriteFile("a3.txt");
        var vm = await OpenAsync(temp.Path);
        vm.MoveSelection(2);
        Assert.Equal(2, vm.SelectedIndex);

        vm.Query = "a";

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public async Task Results_AreCapped_AndTheNoticeSaysHowManyFilesMatched()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 130; i++)
        {
            temp.WriteFile($"file{i:000}.txt");
        }

        var vm = await OpenAsync(temp.Path);
        vm.Query = "file";

        Assert.Equal(QuickOpenViewModel.MaxResults, vm.Results.Count);
        Assert.Equal("Mostrando 100 de 130 coincidencias; sigue escribiendo para afinar.", vm.Notice);
    }

    [Fact]
    public async Task Notice_CountsTheFilesWhileThereIsNoQuery()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        temp.WriteFile("b.txt");

        var vm = await OpenAsync(temp.Path);

        Assert.Equal("2 ficheros", vm.Notice);
    }

    [Fact]
    public async Task Notice_WithAQuery_CountsTheMatches()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        temp.WriteFile("b.txt");
        temp.WriteFile("ab.md");
        var vm = await OpenAsync(temp.Path);

        vm.Query = "a";

        Assert.Equal("2 coincidencias", vm.Notice);
    }

    [Fact]
    public async Task Notice_UsesTheSingularForASingleFileOrMatch()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("only.txt");
        var vm = await OpenAsync(temp.Path);
        Assert.Equal("1 fichero", vm.Notice);

        vm.Query = "only";

        Assert.Equal("1 coincidencia", vm.Notice);
    }

    [Fact]
    public async Task MoveSelection_WrapsAroundTheList()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a1.txt");
        temp.WriteFile("a2.txt");
        temp.WriteFile("a3.txt");
        var vm = await OpenAsync(temp.Path);

        vm.MoveSelection(1);
        Assert.Equal(1, vm.SelectedIndex);
        vm.MoveSelection(1);
        Assert.Equal(2, vm.SelectedIndex);
        vm.MoveSelection(1);
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveSelection(-1);
        Assert.Equal(2, vm.SelectedIndex);
    }

    [Fact]
    public async Task MoveSelection_WithNoResults_DoesNothing()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        var vm = await OpenAsync(temp.Path);
        vm.Query = "zzz";

        vm.MoveSelection(1);
        vm.MoveSelection(-1);

        Assert.Equal(-1, vm.SelectedIndex);
    }

    [Fact]
    public async Task Accept_ClosesTheListAndHandsOverTheFullPathOfTheSelectedFile()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        var second = temp.WriteFile("src/b.txt");
        var chosen = new List<string>();
        var vm = await OpenAsync(temp.Path, path =>
        {
            chosen.Add(path);
            return Done;
        });
        vm.MoveSelection(1);

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.Equal([second], chosen);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public async Task Accept_WithNothingSelected_KeepsTheListOpenAndChoosesNothing()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        var chosen = new List<string>();
        var vm = await OpenAsync(temp.Path, path =>
        {
            chosen.Add(path);
            return Done;
        });
        vm.Query = "zzz";

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.Empty(chosen);
        Assert.True(vm.IsOpen);
    }

    [Fact]
    public async Task Close_ClosesWithoutChoosingAnything()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        var chosen = new List<string>();
        var vm = await OpenAsync(temp.Path, path =>
        {
            chosen.Add(path);
            return Done;
        });

        vm.CloseCommand.Execute(null);
        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.False(vm.IsOpen);
        Assert.Empty(chosen);
    }

    [Fact]
    public async Task OpeningAgain_StartsFromAnEmptyQueryAndTheFreshFiles()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");
        var vm = await OpenAsync(temp.Path);
        vm.Query = "a";
        vm.CloseCommand.Execute(null);
        temp.WriteFile("new.txt");

        await vm.OpenAsync(temp.Path, "Otra vez", _ => Done);

        Assert.Equal(string.Empty, vm.Query);
        Assert.Equal("Otra vez", vm.Title);
        Assert.Equal(["a.txt", "new.txt"], vm.Results.Select(r => r.RelativePath));
    }

    [Fact]
    public async Task WhileIndexing_ItIsOpenAndBusy_AndTypingWaitsForTheIndex()
    {
        var indexer = new GatedIndexer(new ProjectFileIndex(["src/Program.cs", "README.md"], false));
        var vm = new QuickOpenViewModel(indexer.Index);

        var opening = vm.OpenAsync(@"C:\p", "Ir al fichero", _ => Done);
        Assert.True(vm.IsOpen);
        Assert.True(vm.IsIndexing);
        Assert.Equal("Indexando ficheros…", vm.Notice);
        Assert.Empty(vm.Results);

        vm.Query = "prog";
        Assert.Empty(vm.Results);

        indexer.Release();
        await opening;

        Assert.False(vm.IsIndexing);
        Assert.Equal(["src/Program.cs"], vm.Results.Select(r => r.RelativePath));
    }

    [Fact]
    public async Task ClosingWhileIndexing_DiscardsTheIndexWhenItArrives()
    {
        var indexer = new GatedIndexer(new ProjectFileIndex(["a.txt"], false));
        var vm = new QuickOpenViewModel(indexer.Index);
        var opening = vm.OpenAsync(@"C:\p", "Ir al fichero", _ => Done);

        vm.CloseCommand.Execute(null);
        indexer.Release();
        await opening;

        Assert.False(vm.IsOpen);
        Assert.False(vm.IsIndexing);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task OpeningASecondSessionWhileIndexing_IgnoresTheFirstIndex()
    {
        var first = new GatedIndexer(new ProjectFileIndex(["old.txt"], false));
        var second = new GatedIndexer(new ProjectFileIndex(["new.txt"], false));
        var vm = new QuickOpenViewModel((root, token) => (root == @"C:\a" ? first : second).Index(root, token));

        var openingFirst = vm.OpenAsync(@"C:\a", "Primero", _ => Done);
        var openingSecond = vm.OpenAsync(@"C:\b", "Segundo", _ => Done);
        second.Release();
        await openingSecond;
        first.Release();
        await openingFirst;

        Assert.Equal("Segundo", vm.Title);
        Assert.Equal(["new.txt"], vm.Results.Select(r => r.RelativePath));
    }

    [Fact]
    public async Task AnIndexCutAtTheCap_IsSaidInTheTruncationNotice()
    {
        var vm = new QuickOpenViewModel((_, _) => new ProjectFileIndex(["a.txt"], IsTruncated: true));

        await vm.OpenAsync(@"C:\p", "Ir al fichero", _ => Done);

        Assert.True(vm.IsTruncated);
        Assert.Equal("Solo se han indexado los primeros 20.000 ficheros: el proyecto tiene más y no aparecen aquí.", vm.TruncationNotice);
    }

    [Fact]
    public async Task AnIndexThatFitsTheCap_HasNoTruncationNotice()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");

        var vm = await OpenAsync(temp.Path);

        Assert.False(vm.IsTruncated);
        Assert.Null(vm.TruncationNotice);
    }

    [Fact]
    public async Task AFolderThatCannotBeRead_IsReportedInTheNotice_AndTheListStaysOpen()
    {
        using var temp = new TempDirectory();
        var vm = new QuickOpenViewModel((root, _) => throw new DirectoryNotFoundException("gone"));

        await vm.OpenAsync(temp.Combine("gone"), "Ir al fichero", _ => Done);

        Assert.True(vm.IsOpen);
        Assert.False(vm.IsIndexing);
        Assert.Empty(vm.Results);
        Assert.StartsWith("No se pudo leer el proyecto", vm.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyProject_SaysThereIsNothingToShow()
    {
        using var temp = new TempDirectory();

        var vm = await OpenAsync(temp.Path);

        Assert.Empty(vm.Results);
        Assert.Equal("El proyecto no tiene ficheros que mostrar.", vm.Notice);
    }

    [Fact]
    public async Task OpenAsync_RejectsMissingArguments()
    {
        var vm = new QuickOpenViewModel();

        await Assert.ThrowsAsync<ArgumentException>(() => vm.OpenAsync(" ", "t", _ => Done));
        await Assert.ThrowsAsync<ArgumentNullException>(() => vm.OpenAsync(@"C:\p", "t", null!));
    }
}
