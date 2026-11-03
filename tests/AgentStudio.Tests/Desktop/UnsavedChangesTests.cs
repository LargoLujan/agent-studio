using System.IO;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop;

/// <summary>Unsaved edits must never be lost without an explicit choice: tab close, project switch and window close.</summary>
public sealed class UnsavedChangesTests
{
    private enum Save
    {
        Ok,
        Fails,
        StaysDirty,
        NoHandler,
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Fixture(UnsavedChangesChoice answer)
        {
            Prompt = new FakeUnsavedChangesPrompt(answer);
            Vm = ShellFactory.CreateMain(Prompt);
        }

        public FakeUnsavedChangesPrompt Prompt { get; }

        public MainViewModel Vm { get; }

        public int SaveCalls { get; private set; }

        public string Project => _temp.Combine("a");

        public string OtherProject => _temp.Combine("b");

        public async Task InitializeAsync()
        {
            _temp.CreateDirectory("a");
            _temp.CreateDirectory("b");
            await Vm.InitializeAsync();
            await Vm.OpenProjectAsync(Project);
        }

        /// <summary>Opens a file tab, marks it dirty and gives it the requested save behaviour.</summary>
        public async Task<FileDocumentViewModel> OpenDirtyAsync(string name, Save save = Save.Ok, bool dirty = true)
        {
            var path = _temp.WriteFile("a/" + name, "original");
            await Vm.OpenFileAsync(path);
            var document = Assert.IsType<FileDocumentViewModel>(Vm.Documents.Last());
            Assert.NotNull(document.Editor);
            document.Editor.SetDirty(dirty);
            Assert.Equal(dirty, document.IsDirty);

            if (save != Save.NoHandler)
            {
                document.SetSaveHandler(_ =>
                {
                    SaveCalls++;
                    switch (save)
                    {
                        case Save.Fails:
                            throw new IOException("disco lleno");
                        case Save.Ok:
                            document.Editor.SetDirty(false);
                            break;
                    }

                    return Task.CompletedTask;
                });
            }

            return document;
        }

        public void Dispose() => _temp.Dispose();
    }

    // ---- Closing a tab ----

    [Fact]
    public async Task CloseTab_Clean_ClosesWithoutAsking()
    {
        using var f = new Fixture(UnsavedChangesChoice.Cancel);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt", dirty: false);

        document.CloseCommand.Execute(null);

        Assert.DoesNotContain(document, f.Vm.Documents);
        Assert.Empty(f.Prompt.Calls);
    }

    [Fact]
    public async Task CloseTab_Dirty_Save_SavesThenCloses()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt");

        var closed = await f.Vm.CloseDocumentAsync(document);

        Assert.True(closed);
        Assert.Equal(1, f.SaveCalls);
        Assert.DoesNotContain(document, f.Vm.Documents);
        var call = Assert.Single(f.Prompt.Calls);
        Assert.Equal(["a.txt"], call.Titles);
        Assert.True(call.CanSave);
    }

    [Fact]
    public async Task CloseTab_Dirty_SaveFails_KeepsTheTabAndReportsTheReason()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt", Save.Fails);

        var closed = await f.Vm.CloseDocumentAsync(document);

        Assert.False(closed);
        Assert.Contains(document, f.Vm.Documents);
        Assert.True(document.IsDirty);
        Assert.Contains("a.txt", f.Vm.StatusMessage);
        Assert.Contains("disco lleno", f.Vm.StatusMessage);
    }

    [Fact]
    public async Task CloseTab_Dirty_SaveThatLeavesItDirty_KeepsTheTab()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt", Save.StaysDirty);

        Assert.False(await f.Vm.CloseDocumentAsync(document));

        Assert.Contains(document, f.Vm.Documents);
        Assert.NotNull(f.Vm.StatusMessage);
    }

    [Fact]
    public async Task CloseTab_Dirty_Discard_ClosesWithoutSaving()
    {
        using var f = new Fixture(UnsavedChangesChoice.Discard);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt");

        Assert.True(await f.Vm.CloseDocumentAsync(document));

        Assert.Equal(0, f.SaveCalls);
        Assert.DoesNotContain(document, f.Vm.Documents);
    }

    [Fact]
    public async Task CloseTab_Dirty_Cancel_KeepsTheTab()
    {
        using var f = new Fixture(UnsavedChangesChoice.Cancel);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt");

        Assert.False(await f.Vm.CloseDocumentAsync(document));

        Assert.Equal(0, f.SaveCalls);
        Assert.Contains(document, f.Vm.Documents);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public async Task CloseTab_Dirty_WithoutSaveHandler_OffersNoSaveAndSaveAnswerKeepsTheTab()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt", Save.NoHandler);

        Assert.False(await f.Vm.CloseDocumentAsync(document));

        Assert.False(Assert.Single(f.Prompt.Calls).CanSave);
        Assert.Contains(document, f.Vm.Documents);

        f.Prompt.Answer = UnsavedChangesChoice.Discard;
        Assert.True(await f.Vm.CloseDocumentAsync(document));
        Assert.DoesNotContain(document, f.Vm.Documents);
    }

    // ---- Switching project ----

    [Fact]
    public async Task SwitchProject_NoDirtyDocuments_DoesNotAsk()
    {
        using var f = new Fixture(UnsavedChangesChoice.Cancel);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt", dirty: false);

        await f.Vm.OpenProjectAsync(f.OtherProject);

        Assert.Empty(f.Prompt.Calls);
        Assert.Equal(f.OtherProject, f.Vm.ProjectPath);
    }

    [Fact]
    public async Task SwitchProject_Cancel_AbortsBeforeAnythingChanges()
    {
        using var f = new Fixture(UnsavedChangesChoice.Cancel);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt");
        var changing = 0;
        f.Vm.ProjectChanging += (_, _) => changing++;

        await f.Vm.OpenProjectAsync(f.OtherProject);

        Assert.Equal(f.Project, f.Vm.ProjectPath);
        Assert.Equal(0, changing);
        Assert.Contains(document, f.Vm.Documents);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public async Task SwitchProject_Save_SavesEveryDirtyFileAndAsksOnce()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt");
        await f.OpenDirtyAsync("b.txt");
        await f.OpenDirtyAsync("clean.txt", dirty: false);

        await f.Vm.OpenProjectAsync(f.OtherProject);

        Assert.Equal(2, f.SaveCalls);
        var call = Assert.Single(f.Prompt.Calls);
        Assert.Equal(["a.txt", "b.txt"], call.Titles);
        Assert.Equal(f.OtherProject, f.Vm.ProjectPath);
        Assert.IsType<WelcomeDocumentViewModel>(Assert.Single(f.Vm.Documents));
    }

    [Fact]
    public async Task SwitchProject_SaveFails_AbortsAndReports()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt", Save.Fails);

        await f.Vm.OpenProjectAsync(f.OtherProject);

        Assert.Equal(f.Project, f.Vm.ProjectPath);
        Assert.Contains(document, f.Vm.Documents);
        Assert.Contains("disco lleno", f.Vm.StatusMessage);
    }

    [Fact]
    public async Task SwitchProject_Discard_SwitchesWithoutSaving()
    {
        using var f = new Fixture(UnsavedChangesChoice.Discard);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt");

        await f.Vm.OpenProjectAsync(f.OtherProject);

        Assert.Equal(0, f.SaveCalls);
        Assert.Equal(f.OtherProject, f.Vm.ProjectPath);
        Assert.IsType<WelcomeDocumentViewModel>(Assert.Single(f.Vm.Documents));
    }

    [Fact]
    public async Task SwitchProject_WithoutSaveHandler_OffersOnlyDiscard()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        var document = await f.OpenDirtyAsync("a.txt", Save.NoHandler);

        await f.Vm.OpenProjectAsync(f.OtherProject);

        Assert.False(Assert.Single(f.Prompt.Calls).CanSave);
        Assert.Equal(f.Project, f.Vm.ProjectPath);
        Assert.Contains(document, f.Vm.Documents);
    }

    // ---- Closing the window (MainWindow.OnClosing calls these two members) ----

    [Fact]
    public async Task Window_NoDirtyDocuments_HasNothingToConfirm()
    {
        using var f = new Fixture(UnsavedChangesChoice.Cancel);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt", dirty: false);

        Assert.False(f.Vm.HasUnsavedChanges);
        Assert.True(await f.Vm.ConfirmDiscardOrSaveAsync(f.Vm.Documents.ToList(), TestContext.Current.CancellationToken));
        Assert.Empty(f.Prompt.Calls);
    }

    [Fact]
    public async Task Window_Dirty_Save_SavesAllAndAllowsClosing()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt");
        await f.OpenDirtyAsync("b.txt");
        Assert.True(f.Vm.HasUnsavedChanges);

        Assert.True(await f.Vm.ConfirmDiscardOrSaveAsync(f.Vm.Documents.ToList(), TestContext.Current.CancellationToken));

        Assert.Equal(2, f.SaveCalls);
        Assert.False(f.Vm.HasUnsavedChanges);
        Assert.Single(f.Prompt.Calls);
    }

    [Fact]
    public async Task Window_Dirty_SaveFails_BlocksClosing()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt", Save.Fails);

        Assert.False(await f.Vm.ConfirmDiscardOrSaveAsync(f.Vm.Documents.ToList(), TestContext.Current.CancellationToken));

        Assert.True(f.Vm.HasUnsavedChanges);
        Assert.Contains("disco lleno", f.Vm.StatusMessage);
    }

    [Fact]
    public async Task Window_Dirty_Discard_AllowsClosingWithoutSaving()
    {
        using var f = new Fixture(UnsavedChangesChoice.Discard);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt");

        Assert.True(await f.Vm.ConfirmDiscardOrSaveAsync(f.Vm.Documents.ToList(), TestContext.Current.CancellationToken));

        Assert.Equal(0, f.SaveCalls);
    }

    [Fact]
    public async Task Window_Dirty_Cancel_BlocksClosing()
    {
        using var f = new Fixture(UnsavedChangesChoice.Cancel);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt");

        Assert.False(await f.Vm.ConfirmDiscardOrSaveAsync(f.Vm.Documents.ToList(), TestContext.Current.CancellationToken));

        Assert.Equal(0, f.SaveCalls);
        Assert.True(f.Vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Window_Dirty_WithoutSaveHandler_BlocksOnSaveAnswer()
    {
        using var f = new Fixture(UnsavedChangesChoice.Save);
        await f.InitializeAsync();
        await f.OpenDirtyAsync("a.txt", Save.NoHandler);

        Assert.False(await f.Vm.ConfirmDiscardOrSaveAsync(f.Vm.Documents.ToList(), TestContext.Current.CancellationToken));

        Assert.False(Assert.Single(f.Prompt.Calls).CanSave);
    }
}
