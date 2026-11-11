using System.IO;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop.Diff;

/// <summary>
/// The comparison commands of a file tab. The view supplies the capability to show a diff (like the save handler);
/// the handler used here records the requests, because those requests are the contract between view model and view.
/// </summary>
public sealed class FileDocumentViewModelDiffTests
{
    private sealed class RecordingDiffHandler
    {
        public List<DiffRequest> Requests { get; } = [];

        public Exception? FailWith { get; set; }

        public Task HandleAsync(DiffRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return FailWith is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    private static async Task<FileDocumentViewModel> LoadAsync(TempDirectory temp, string name = "main.cs", string content = "class A {}\r\n")
    {
        var document = new FileDocumentViewModel(temp.WriteFile(name, content));
        await document.LoadAsync(TestContext.Current.CancellationToken);
        return document;
    }

    private static async Task<(FileDocumentViewModel Document, RecordingDiffHandler Handler)> LoadWithHandlerAsync(TempDirectory temp)
    {
        var document = await LoadAsync(temp);
        var handler = new RecordingDiffHandler();
        document.SetDiffHandler(handler.HandleAsync);
        return (document, handler);
    }

    [Fact]
    public async Task Commands_NeedTheViewToSupplyTheDiffCapability()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);

        Assert.NotNull(document.Editor);
        Assert.False(document.CompareWithSavedCommand.CanExecute(null));
        Assert.False(document.CompareWithFileCommand.CanExecute(null));
        Assert.False(document.CloseDiffCommand.CanExecute(null));
    }

    [Fact]
    public async Task Commands_NeedAnEditableFile()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("data.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 0, 3], TestContext.Current.CancellationToken);
        var document = new FileDocumentViewModel(path);
        await document.LoadAsync(TestContext.Current.CancellationToken);

        document.SetDiffHandler(new RecordingDiffHandler().HandleAsync);

        Assert.Null(document.Editor);
        Assert.False(document.CompareWithSavedCommand.CanExecute(null));
        Assert.False(document.CompareWithFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Commands_AreAvailable_OnceTheEditorAndTheHandlerExist()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);
        var changes = 0;
        document.CompareWithSavedCommand.CanExecuteChanged += (_, _) => changes++;

        document.SetDiffHandler(new RecordingDiffHandler().HandleAsync);

        Assert.True(document.CompareWithSavedCommand.CanExecute(null));
        Assert.True(document.CompareWithFileCommand.CanExecute(null));
        Assert.False(document.CloseDiffCommand.CanExecute(null));
        Assert.True(changes > 0, "The toolbar must be told that the commands became available.");
    }

    [Fact]
    public async Task CompareWithSaved_AsksTheViewToCompareTheSavedTextWithTheEditor()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);

        await document.CompareWithSavedCommand.ExecuteAsync(null);

        Assert.IsType<CompareWithSavedRequest>(Assert.Single(handler.Requests));
        Assert.True(document.IsComparing);
        Assert.Null(document.DiffError);
        Assert.Equal("guardado en disco", document.DiffOriginalLabel);
        Assert.Equal("editor", document.DiffModifiedLabel);
        Assert.True(document.CloseDiffCommand.CanExecute(null));
    }

    [Fact]
    public async Task CompareWithSaved_SaysWhenTheEditorHasUnsavedChanges()
    {
        using var temp = new TempDirectory();
        var (document, _) = await LoadWithHandlerAsync(temp);
        document.Editor!.SetDirty(true);

        await document.CompareWithSavedCommand.ExecuteAsync(null);

        Assert.Equal("guardado en disco", document.DiffOriginalLabel);
        Assert.Equal("editor (con cambios sin guardar)", document.DiffModifiedLabel);
    }

    [Fact]
    public async Task CompareWithFileCommand_OnlyAsksTheOwnerForAFile()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        var asked = 0;
        document.CompareWithFileRequested += (_, _) => asked++;

        document.CompareWithFileCommand.Execute(null);

        Assert.Equal(1, asked);
        Assert.Empty(handler.Requests);
        Assert.False(document.IsComparing);
    }

    [Fact]
    public async Task CompareWithFileAsync_SendsTheTextOfTheOtherFileAsTheOriginalSide()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        var other = temp.WriteFile("old/main.cs", "class Old {}\n");

        await document.CompareWithFileAsync(other, @"old\main.cs", TestContext.Current.CancellationToken);

        var request = Assert.IsType<CompareWithTextRequest>(Assert.Single(handler.Requests));
        Assert.Equal("class Old {}\n", request.OriginalText);
        Assert.True(document.IsComparing);
        Assert.Equal(@"old\main.cs", document.DiffOriginalLabel);
        Assert.Equal("main.cs (editor)", document.DiffModifiedLabel);
    }

    [Fact]
    public async Task CompareWithFileAsync_WithoutALabel_NamesTheOtherFileByItsName()
    {
        using var temp = new TempDirectory();
        var (document, _) = await LoadWithHandlerAsync(temp);
        var other = temp.WriteFile("backup/notes.txt", "x");

        await document.CompareWithFileAsync(other, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("notes.txt", document.DiffOriginalLabel);
        Assert.Equal("main.cs (editor)", document.DiffModifiedLabel);
    }

    [Fact]
    public async Task CompareWithFileAsync_StripsTheByteOrderMark()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        var other = temp.Combine("bom.txt");
        await File.WriteAllBytesAsync(other, [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'], TestContext.Current.CancellationToken);

        await document.CompareWithFileAsync(other, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hi", Assert.IsType<CompareWithTextRequest>(Assert.Single(handler.Requests)).OriginalText);
    }

    [Fact]
    public async Task CompareWithFileAsync_ABinaryFile_IsRefusedWithAReason()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        var binary = temp.Combine("data.bin");
        await File.WriteAllBytesAsync(binary, [1, 2, 0, 3], TestContext.Current.CancellationToken);

        await document.CompareWithFileAsync(binary, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.False(document.IsComparing);
        Assert.Contains("data.bin", document.DiffError, StringComparison.Ordinal);
        Assert.Contains("binario", document.DiffError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareWithFileAsync_AFileOverTheSizeLimit_IsRefused()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        var big = temp.Combine("big.txt");
        await File.WriteAllBytesAsync(big, new byte[FileDocumentViewModel.MaxBytes + 1], TestContext.Current.CancellationToken);

        await document.CompareWithFileAsync(big, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Contains("MB", document.DiffError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareWithFileAsync_AMissingFile_IsRefused()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);

        await document.CompareWithFileAsync(temp.Combine("gone.txt"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Contains("gone.txt", document.DiffError, StringComparison.Ordinal);
        Assert.Contains("no existe", document.DiffError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareWithFileAsync_AFileThatIsNotUtf8_IsRefused()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        var utf16 = temp.Combine("utf16.txt");
        await File.WriteAllBytesAsync(utf16, [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0], TestContext.Current.CancellationToken);

        await document.CompareWithFileAsync(utf16, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Contains("utf16.txt", document.DiffError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloseDiff_AsksTheViewToShowTheEditorAgain()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        await document.CompareWithSavedCommand.ExecuteAsync(null);

        await document.CloseDiffCommand.ExecuteAsync(null);

        Assert.Equal(2, handler.Requests.Count);
        Assert.IsType<HideDiffRequest>(handler.Requests[1]);
        Assert.False(document.IsComparing);
        Assert.Null(document.DiffOriginalLabel);
        Assert.Null(document.DiffModifiedLabel);
        Assert.False(document.CloseDiffCommand.CanExecute(null));
    }

    [Fact]
    public async Task ComparingAgain_ReplacesTheComparisonInPlace()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        var other = temp.WriteFile("other.cs", "x");
        await document.CompareWithSavedCommand.ExecuteAsync(null);

        await document.CompareWithFileAsync(other, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(document.IsComparing);
        Assert.Equal("other.cs", document.DiffOriginalLabel);
        Assert.Equal("main.cs (editor)", document.DiffModifiedLabel);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r is HideDiffRequest);
    }

    [Fact]
    public async Task WhenTheViewFails_TheReasonIsReported_AndNoComparisonIsShown()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        handler.FailWith = new TimeoutException("The editor did not return its content in time.");

        await document.CompareWithSavedCommand.ExecuteAsync(null);

        Assert.False(document.IsComparing);
        Assert.Null(document.DiffOriginalLabel);
        Assert.Null(document.DiffModifiedLabel);
        Assert.StartsWith("No se pudo mostrar la comparación", document.DiffError, StringComparison.Ordinal);
        Assert.Contains("did not return its content", document.DiffError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewComparison_ClearsTheError()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        handler.FailWith = new TimeoutException("slow");
        await document.CompareWithSavedCommand.ExecuteAsync(null);
        Assert.NotNull(document.DiffError);

        handler.FailWith = null;
        await document.CompareWithSavedCommand.ExecuteAsync(null);

        Assert.Null(document.DiffError);
        Assert.True(document.IsComparing);
    }

    [Fact]
    public async Task WhenTheEditorGoesAway_NoComparisonRemainsAndTheCommandsAreDisabled()
    {
        using var temp = new TempDirectory();
        var (document, _) = await LoadWithHandlerAsync(temp);
        await document.CompareWithSavedCommand.ExecuteAsync(null);

        document.SetDiffHandler(null);

        Assert.False(document.IsComparing);
        Assert.Null(document.DiffOriginalLabel);
        Assert.Null(document.DiffModifiedLabel);
        Assert.False(document.CompareWithSavedCommand.CanExecute(null));
        Assert.False(document.CompareWithFileCommand.CanExecute(null));
        Assert.False(document.CloseDiffCommand.CanExecute(null));
    }

    [Fact]
    public async Task Saving_WhileComparingWithTheSavedVersion_ReturnsToTheEditor()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        await document.CompareWithSavedCommand.ExecuteAsync(null);

        await document.Editor!.SaveAsync("class B {}\r\n", TestContext.Current.CancellationToken);

        Assert.False(document.IsComparing);
        Assert.IsType<HideDiffRequest>(handler.Requests[^1]);
    }

    [Fact]
    public async Task Saving_WhileComparingWithAnotherFile_KeepsTheComparison()
    {
        using var temp = new TempDirectory();
        var (document, handler) = await LoadWithHandlerAsync(temp);
        await document.CompareWithFileAsync(temp.WriteFile("other.cs", "x"), cancellationToken: TestContext.Current.CancellationToken);

        await document.Editor!.SaveAsync("class B {}\r\n", TestContext.Current.CancellationToken);

        Assert.True(document.IsComparing);
        Assert.DoesNotContain(handler.Requests, r => r is HideDiffRequest);
    }

    [Fact]
    public async Task CompareWithFileAsync_WithoutTheDiffCapability_DoesNothing()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);
        var other = temp.WriteFile("other.cs", "x");

        await document.CompareWithFileAsync(other, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(document.IsComparing);
        Assert.Null(document.DiffError);
    }
}
