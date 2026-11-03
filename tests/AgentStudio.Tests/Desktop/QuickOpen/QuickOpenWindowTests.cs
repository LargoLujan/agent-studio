using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views;

namespace AgentStudio.Tests.Desktop.QuickOpen;

/// <summary>The keyboard and binding side of the "go to file" window; it is built but never shown.</summary>
public sealed class QuickOpenWindowTests
{
    /// <summary>Raises the tunnelling key-down event the window listens to; returns whether the window handled it.</summary>
    private static bool Press(Window window, Key key) => TestKeys.PreviewKeyDown(window, key);

    private static async Task<(QuickOpenViewModel Vm, QuickOpenWindow Window, List<string> Chosen)> OpenAsync(TempDirectory temp)
    {
        var chosen = new List<string>();
        var vm = new QuickOpenViewModel();
        var window = new QuickOpenWindow { DataContext = vm };
        WpfHost.FlushBindings();
        await vm.OpenAsync(temp.Path, "Ir al fichero", path =>
        {
            chosen.Add(path);
            return Task.CompletedTask;
        });
        WpfHost.FlushBindings();
        return (vm, window, chosen);
    }

    [Fact]
    public async Task DownAndUp_MoveTheHighlight_AndTheListFollows()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a1.txt");
        temp.WriteFile("a2.txt");

        var (down, highlighted, up, back) = await WpfHost.RunAsync(async () =>
        {
            var (vm, window, _) = await OpenAsync(temp);
            var list = (ListBox)window.FindName("ResultList");
            var handledDown = Press(window, Key.Down);
            var afterDown = (vm.SelectedIndex, list.SelectedIndex);
            var handledUp = Press(window, Key.Up);
            return (handledDown, afterDown, handledUp, list.SelectedIndex);
        });

        Assert.True(down);
        Assert.Equal((1, 1), highlighted);
        Assert.True(up);
        Assert.Equal(0, back);
    }

    [Fact]
    public async Task Enter_ChoosesTheHighlightedFile()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a1.txt");
        var second = temp.WriteFile("a22.txt");

        var (handled, chosen, isOpen) = await WpfHost.RunAsync(async () =>
        {
            var (vm, window, picked) = await OpenAsync(temp);
            Press(window, Key.Down);
            var enter = Press(window, Key.Enter);
            return (enter, picked, vm.IsOpen);
        });

        Assert.True(handled);
        Assert.Equal([second], chosen);
        Assert.False(isOpen);
    }

    [Fact]
    public async Task Escape_ClosesWithoutChoosing()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");

        var (handled, chosen, isOpen) = await WpfHost.RunAsync(async () =>
        {
            var (vm, window, picked) = await OpenAsync(temp);
            var escape = Press(window, Key.Escape);
            return (escape, picked, vm.IsOpen);
        });

        Assert.True(handled);
        Assert.Empty(chosen);
        Assert.False(isOpen);
    }

    [Fact]
    public async Task TypingKeys_AreLeftToTheSearchBox()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("a.txt");

        var handled = await WpfHost.RunAsync(async () =>
        {
            var (_, window, _) = await OpenAsync(temp);
            return (Press(window, Key.A), Press(window, Key.Left), Press(window, Key.Home));
        });

        Assert.Equal((false, false, false), handled);
    }

    [Fact]
    public async Task TheSearchBox_FiltersTheList()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("alpha.txt");
        temp.WriteFile("beta.txt");

        var (query, names) = await WpfHost.RunAsync(async () =>
        {
            var (vm, window, _) = await OpenAsync(temp);
            var box = (TextBox)window.FindName("SearchBox");
            var list = (ListBox)window.FindName("ResultList");

            box.Text = "bet";

            return (vm.Query, list.Items.Cast<QuickOpenItem>().Select(i => i.FileName).ToList());
        });

        Assert.Equal("bet", query);
        Assert.Equal(["beta.txt"], names);
    }

    [Fact]
    public async Task TheList_IsHiddenWhileThereIsNothingToShow()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("alpha.txt");

        var (shown, hidden) = await WpfHost.RunAsync(async () =>
        {
            var (vm, window, _) = await OpenAsync(temp);
            var list = (ListBox)window.FindName("ResultList");
            var withResults = list.Visibility;

            vm.Query = "zzz";

            return (withResults, list.Visibility);
        });

        Assert.Equal(Visibility.Visible, shown);
        Assert.Equal(Visibility.Collapsed, hidden);
    }
}
