using System.Windows;
using System.Windows.Interop;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Theming;
using AgentStudio.Desktop.Views;
using AgentStudio.Desktop.Views.Background;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Tests.Desktop.Orchestrator;
using AvalonDock.Controls;

namespace AgentStudio.Tests.Desktop.Theming;

/// <summary>
/// The dark title bar of every window (<see cref="DarkTitleBar"/>, <see cref="ThemedWindows"/>): which DWM call it makes, that
/// it fails without a sound where DWM does not take it, and that the registration of App reaches the windows created after
/// it. The windows here get a handle (<see cref="WindowInteropHelper.EnsureHandle"/>) but are never shown nor activated:
/// nothing appears on the screen and nothing takes the focus.
/// </summary>
public sealed class ThemedWindowsTests
{
    private const int EInvalidArg = unchecked((int)0x80070057);

    /// <summary>What the DWM call was given, and what it answers.</summary>
    private sealed class FakeDwm(params int[] answers)
    {
        public List<(nint Hwnd, int Attribute, int Value, int Size)> Calls { get; } = [];

        public int Set(nint hwnd, int attribute, ref int value, int size)
        {
            Calls.Add((hwnd, attribute, value, size));
            return Calls.Count <= answers.Length ? answers[Calls.Count - 1] : 0;
        }
    }

    [Fact]
    public void The_title_bar_asks_DWM_for_the_immersive_dark_mode_of_the_window()
    {
        var dwm = new FakeDwm(0);

        var taken = new DarkTitleBar(dwm.Set).Apply(0x1234);

        Assert.True(taken);
        // DWMWA_USE_IMMERSIVE_DARK_MODE (20) set to TRUE, a BOOL of 4 bytes.
        Assert.Equal([((nint)0x1234, 20, 1, 4)], dwm.Calls);
        Assert.Equal(20, DarkTitleBar.ImmersiveDarkModeAttribute);
    }

    [Fact]
    public void Where_DWM_refuses_attribute_20_it_tries_19_of_the_builds_before_Windows_10_2004()
    {
        var dwm = new FakeDwm(EInvalidArg, 0);

        var taken = new DarkTitleBar(dwm.Set).Apply(0x1234);

        Assert.True(taken);
        Assert.Equal([((nint)0x1234, 20, 1, 4), ((nint)0x1234, 19, 1, 4)], dwm.Calls);
    }

    [Fact]
    public void Where_DWM_takes_neither_the_title_bar_stays_as_it_is_without_an_error()
    {
        var dwm = new FakeDwm(EInvalidArg, EInvalidArg);

        Assert.False(new DarkTitleBar(dwm.Set).Apply(0x1234));
        Assert.Equal(2, dwm.Calls.Count);
    }

    [Fact]
    public void Without_DWM_at_all_it_fails_in_silence()
    {
        Assert.False(new DarkTitleBar((nint _, int _, ref int _, int _) => throw new DllNotFoundException("dwmapi.dll")).Apply(0x1234));
        Assert.False(new DarkTitleBar((nint _, int _, ref int _, int _) => throw new EntryPointNotFoundException("DwmSetWindowAttribute")).Apply(0x1234));
    }

    [Fact]
    public void A_window_without_a_handle_asks_nothing()
    {
        var dwm = new FakeDwm();

        Assert.False(new DarkTitleBar(dwm.Set).Apply(0));
        Assert.Empty(dwm.Calls);
    }

    [Fact]
    public void The_registration_reaches_every_window_created_after_it_once_it_has_a_handle()
    {
        var (calls, handles, untilTheHandle) = WpfHost.Run(() =>
        {
            var dwm = new FakeDwm();
            ThemedWindows.Register(new DarkTitleBar(dwm.Set));
            try
            {
                // A window of the app (it derives from Window, as MainWindow does) and a plain one: no line in either.
                var found = new List<nint>();
                var asked = new List<int>();
                foreach (var window in new[] { new UnsavedChangesWindow(), new Window() })
                {
                    // Before the handle exists there is nothing to ask DWM for: the window is tried again later.
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    asked.Add(dwm.Calls.Count);
                    found.Add(new WindowInteropHelper(window).EnsureHandle());

                    // What WPF raises once the window is laid out and loaded, twice: the call is made once.
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    window.Close();
                }

                return (dwm.Calls.ToList(), found, asked);
            }
            finally
            {
                ThemedWindows.Register(new DarkTitleBar());
            }
        });

        Assert.Equal([0, 1], untilTheHandle);
        Assert.Equal([(handles[0], 20, 1, 4), (handles[1], 20, 1, 4)], calls);
        Assert.All(handles, handle => Assert.NotEqual(0, handle));
    }

    [Fact]
    public void Only_the_dialogs_and_panels_of_the_app_take_the_first_focus_not_MainWindow_nor_the_floating_panes_of_the_dock()
    {
        using var temp = new TempDirectory();
        var (unsaved, exit, panel, approval, main, plain) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            return (
                ThemedWindows.TakesFirstFocus(new UnsavedChangesWindow()),
                ThemedWindows.TakesFirstFocus(new ExitPromptWindow()),
                ThemedWindows.TakesFirstFocus(new StatusPanelWindow()),
                ThemedWindows.TakesFirstFocus(new ApprovalWindow()),
                ThemedWindows.TakesFirstFocus(new MainWindow(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Path)))),
                ThemedWindows.TakesFirstFocus(new Window()));
        });

        Assert.True(unsaved);
        Assert.True(exit);
        Assert.True(panel);
        Assert.True(approval);
        // MainWindow keeps the keyboard where its user left it; «Abrir carpeta» does not take it on every activation.
        Assert.False(main);
        Assert.False(plain);
        Assert.NotEqual(typeof(ThemedWindows).Assembly, typeof(LayoutAnchorableFloatingWindowControl).Assembly);
    }
}
