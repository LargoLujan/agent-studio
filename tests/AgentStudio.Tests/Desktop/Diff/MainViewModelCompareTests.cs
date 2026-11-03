using System.IO;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop.Diff;

/// <summary>How the shell answers a tab that asks to be compared with another file of the project.</summary>
public sealed class MainViewModelCompareTests
{
    private sealed class RecordingDiffHandler
    {
        public List<DiffRequest> Requests { get; } = [];

        public Task HandleAsync(DiffRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    /// <summary>The shell opens the list in the background (like a click on a tab button), so wait for it to be ready.</summary>
    private static async Task WaitForListAsync(MainViewModel vm)
    {
        for (var i = 0; i < 200 && !(vm.QuickOpen.IsOpen && !vm.QuickOpen.IsIndexing); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(vm.QuickOpen.IsOpen && !vm.QuickOpen.IsIndexing, "The file list never became ready.");
    }

    private static async Task<(MainViewModel Vm, FileDocumentViewModel Document, RecordingDiffHandler Handler)> OpenProjectWithTabAsync(TempDirectory temp)
    {
        var project = temp.CreateDirectory("proj");
        var file = temp.WriteFile("proj/src/main.cs", "class Main {}");
        temp.WriteFile("proj/old/main.cs", "class Old {}");
        temp.WriteFile("proj/README.md", "# hola");
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();
        await vm.OpenProjectAsync(project);
        await vm.OpenFileAsync(file);
        var document = Assert.IsType<FileDocumentViewModel>(vm.ActiveContent);
        var handler = new RecordingDiffHandler();
        document.SetDiffHandler(handler.HandleAsync);
        return (vm, document, handler);
    }

    [Fact]
    public async Task CompareWithFile_OffersTheProjectFiles_AndComparesWithTheChosenOne()
    {
        using var temp = new TempDirectory();
        var (vm, document, handler) = await OpenProjectWithTabAsync(temp);

        document.CompareWithFileCommand.Execute(null);
        await WaitForListAsync(vm);
        Assert.Equal("Comparar main.cs con…", vm.QuickOpen.Title);
        vm.QuickOpen.Query = "old/main";
        await vm.QuickOpen.AcceptCommand.ExecuteAsync(null);

        var request = Assert.IsType<CompareWithTextRequest>(Assert.Single(handler.Requests));
        Assert.Equal("class Old {}", request.OriginalText);
        Assert.True(document.IsComparing);
        Assert.Equal(@"old\main.cs", document.DiffOriginalLabel);
        Assert.Equal("main.cs (editor)", document.DiffModifiedLabel);
        Assert.False(vm.QuickOpen.IsOpen);
    }

    [Fact]
    public async Task CompareWithFile_Cancelled_ChangesNothing()
    {
        using var temp = new TempDirectory();
        var (vm, document, handler) = await OpenProjectWithTabAsync(temp);
        document.CompareWithFileCommand.Execute(null);
        await WaitForListAsync(vm);

        vm.QuickOpen.CloseCommand.Execute(null);

        Assert.Empty(handler.Requests);
        Assert.False(document.IsComparing);
        Assert.False(vm.QuickOpen.IsOpen);
    }

    [Fact]
    public async Task CompareWithFile_OfAFileThatCannotBeCompared_ShowsTheReasonInTheTab()
    {
        using var temp = new TempDirectory();
        var (vm, document, handler) = await OpenProjectWithTabAsync(temp);
        await File.WriteAllBytesAsync(temp.Combine("proj", "data.bin"), [1, 0, 2], TestContext.Current.CancellationToken);
        document.CompareWithFileCommand.Execute(null);
        await WaitForListAsync(vm);
        vm.QuickOpen.Query = "data.bin";

        await vm.QuickOpen.AcceptCommand.ExecuteAsync(null);

        Assert.Empty(handler.Requests);
        Assert.Contains("data.bin", document.DiffError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClosedTab_NoLongerAsksForAFile()
    {
        using var temp = new TempDirectory();
        var (vm, document, _) = await OpenProjectWithTabAsync(temp);
        await vm.CloseDocumentAsync(document);

        document.CompareWithFileCommand.Execute(null);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(vm.QuickOpen.IsOpen);
    }
}
