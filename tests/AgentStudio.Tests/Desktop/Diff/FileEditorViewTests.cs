using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;

namespace AgentStudio.Tests.Desktop.Diff;

/// <summary>The comparison toolbar and shortcuts of a file tab. The view is built but never shown.</summary>
public sealed class FileEditorViewTests
{
    /// <summary>The text a TextBlock built from runs shows (its Text property stays empty in that case).</summary>
    private static string TextOf(TextBlock block) => new TextRange(block.ContentStart, block.ContentEnd).Text;

    private static async Task<FileDocumentViewModel> LoadAsync(TempDirectory temp)
    {
        var document = new FileDocumentViewModel(temp.WriteFile("main.cs", "class A {}\r\n"));
        await document.LoadAsync(TestContext.Current.CancellationToken);
        return document;
    }

    [Fact]
    public async Task BindingAnEditableDocument_GivesItTheDiffCapability()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);

        var (saved, file) = WpfHost.Run(() =>
        {
            _ = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            return (document.CompareWithSavedCommand.CanExecute(null), document.CompareWithFileCommand.CanExecute(null));
        });

        Assert.True(saved);
        Assert.True(file);
        Assert.False(document.CloseDiffCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheToolbar_IsShownOnlyForFilesThatOpenInTheEditor()
    {
        using var temp = new TempDirectory();
        var editable = await LoadAsync(temp);
        var binary = new FileDocumentViewModel(temp.Combine("data.bin"));
        await File.WriteAllBytesAsync(binary.FilePath!, [1, 0, 2], TestContext.Current.CancellationToken);
        await binary.LoadAsync(TestContext.Current.CancellationToken);

        var (withEditor, withoutEditor) = WpfHost.Run(() =>
        {
            var editableView = new FileEditorView { DataContext = editable };
            var binaryView = new FileEditorView { DataContext = binary };
            WpfHost.FlushBindings();
            return (((FrameworkElement)editableView.FindName("DiffToolbar")).Visibility, ((FrameworkElement)binaryView.FindName("DiffToolbar")).Visibility);
        });

        Assert.Equal(Visibility.Visible, withEditor);
        Assert.Equal(Visibility.Collapsed, withoutEditor);
        Assert.False(binary.CompareWithSavedCommand.CanExecute(null));
    }

    [Fact]
    public async Task BackToEditorAndTheLegend_AppearOnlyWhileComparing()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);

        var (idle, comparing) = await WpfHost.RunAsync(async () =>
        {
            var view = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            var back = (Button)view.FindName("BackToEditorButton");
            var legend = (FrameworkElement)view.FindName("DiffLegend");
            var original = (TextBlock)view.FindName("DiffOriginalText");
            var modified = (TextBlock)view.FindName("DiffModifiedText");
            var before = (back.Visibility, legend.Visibility);

            // Stand in for the editor page, which a view that is never shown cannot have.
            document.SetDiffHandler((_, _) => Task.CompletedTask);
            await document.CompareWithSavedCommand.ExecuteAsync(null);

            return (before, (back.Visibility, legend.Visibility, TextOf(original), TextOf(modified), back.Command == document.CloseDiffCommand));
        });

        Assert.Equal((Visibility.Collapsed, Visibility.Collapsed), idle);
        Assert.Equal(Visibility.Visible, comparing.Item1);
        Assert.Equal(Visibility.Visible, comparing.Item2);
        Assert.Equal("Original: guardado en disco", comparing.Item3);
        Assert.Equal("Modificado: editor", comparing.Item4);
        Assert.True(comparing.Item5);
    }

    [Fact]
    public async Task AFailedComparison_ShowsItsReasonInTheToolbar()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);

        var (visible, text) = await WpfHost.RunAsync(async () =>
        {
            var view = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            var error = (TextBlock)view.FindName("DiffErrorText");
            var hiddenBefore = error.Visibility == Visibility.Collapsed;
            document.SetDiffHandler((_, _) => Task.FromException(new TimeoutException("slow")));

            await document.CompareWithSavedCommand.ExecuteAsync(null);

            return (hiddenBefore && error.Visibility == Visibility.Visible, error.Text);
        });

        Assert.True(visible);
        Assert.Contains("slow", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheShortcuts_RunTheComparisonCommands()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);

        var bindings = WpfHost.Run(() =>
        {
            var view = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            return view.InputBindings.OfType<KeyBinding>()
                .Select(b => (b.Key, b.Modifiers, Command: b.Command))
                .ToList();
        });

        Assert.Contains(bindings, b => b is { Key: Key.D, Modifiers: ModifierKeys.Control | ModifierKeys.Alt } && ReferenceEquals(b.Command, document.CompareWithSavedCommand));
        Assert.Contains(bindings, b => b is { Key: Key.F, Modifiers: ModifierKeys.Control | ModifierKeys.Alt } && ReferenceEquals(b.Command, document.CompareWithFileCommand));

        // Seen in the real app: a KeyBinding for Esc swallows the key even while its command cannot run, so Monaco never
        // got its own Esc (find widget, suggestions). Esc is handled in code instead, and only during a comparison.
        Assert.DoesNotContain(bindings, b => b.Key == Key.Escape);
    }

    [Fact]
    public async Task Escape_WhileComparing_ReturnsToTheEditor()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);
        var requests = new List<DiffRequest>();

        var (handled, stillComparing) = await WpfHost.RunAsync(async () =>
        {
            var view = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            // Stand in for the editor page, which a view that is never shown cannot have.
            document.SetDiffHandler((request, _) =>
            {
                requests.Add(request);
                return Task.CompletedTask;
            });
            await document.CompareWithSavedCommand.ExecuteAsync(null);

            var wasHandled = TestKeys.PreviewKeyDown(view, Key.Escape);

            return (wasHandled, document.IsComparing);
        });

        Assert.True(handled);
        Assert.False(stillComparing);
        Assert.IsType<HideDiffRequest>(requests[^1]);
    }

    [Fact]
    public async Task Escape_WhenNothingIsBeingCompared_IsLeftToMonaco()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);
        var requests = new List<DiffRequest>();

        var handled = WpfHost.Run(() =>
        {
            var view = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            document.SetDiffHandler((request, _) =>
            {
                requests.Add(request);
                return Task.CompletedTask;
            });

            return TestKeys.PreviewKeyDown(view, Key.Escape);
        });

        Assert.False(handled, "Esc must reach the page: Monaco uses it to close its find widget and suggestions.");
        Assert.Empty(requests);
    }

    [Fact]
    public async Task OtherKeys_AreNeverHandledByTheTab()
    {
        using var temp = new TempDirectory();
        var document = await LoadAsync(temp);

        var handled = await WpfHost.RunAsync(async () =>
        {
            var view = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            document.SetDiffHandler((_, _) => Task.CompletedTask);
            await document.CompareWithSavedCommand.ExecuteAsync(null);

            return (TestKeys.PreviewKeyDown(view, Key.A), TestKeys.PreviewKeyDown(view, Key.Enter), document.IsComparing);
        });

        Assert.Equal((false, false, true), handled);
    }

    [Fact]
    public async Task ClosingTheTab_DropsTheDiffCapability()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proj");
        var file = temp.WriteFile("proj/main.cs", "class A {}");
        var vm = ShellFactory.CreateMain();
        await vm.InitializeAsync();
        await vm.OpenProjectAsync(project);
        await vm.OpenFileAsync(file);
        var document = Assert.IsType<FileDocumentViewModel>(vm.ActiveContent);

        var (before, after) = await WpfHost.RunAsync(async () =>
        {
            _ = new FileEditorView { DataContext = document };
            WpfHost.FlushBindings();
            var available = document.CompareWithSavedCommand.CanExecute(null);

            await vm.CloseDocumentAsync(document);

            return (available, document.CompareWithSavedCommand.CanExecute(null));
        });

        Assert.True(before);
        Assert.False(after);
    }
}
