using System.IO;
using System.Windows;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Services;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Phase8;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// V19: the main window remembers where it was, maximized or not, between sessions; a placement is restored only while it
/// still falls on a monitor (enough of its title bar to grab it). Pure: work areas in device-independent pixels.
/// </summary>
public sealed class WindowPlacementTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1040);
    private static readonly Rect Secondary = new(1920, 0, 1280, 984);
    private static readonly Size Minimum = new(900, 560);

    [Fact]
    public void Without_a_placement_the_window_opens_centred_on_the_primary_work_area_and_fits_it()
    {
        // A laptop at 125 %: 1536 x 824 DIP of work area. The default 1280 x 800 does not fit with the margin: it shrinks.
        var small = MainWindow.FirstPlacement(new Rect(0, 0, 1536, 824), new Size(1280, 800), Minimum);
        Assert.Equal(new Rect(128, 24, 1280, 776), small);

        // A large monitor: the default size, centred.
        var large = MainWindow.FirstPlacement(Primary, new Size(1280, 800), Minimum);
        Assert.Equal(new Rect(320, 120, 1280, 800), large);

        // Never below the minimum, even on a tiny work area; it starts at the work area's corner.
        var tiny = MainWindow.FirstPlacement(new Rect(10, 20, 800, 500), new Size(1280, 800), Minimum);
        Assert.Equal(new Rect(10, 20, 900, 560), tiny);
    }

    [Fact]
    public void A_placement_on_a_monitor_is_kept_as_it_is()
    {
        var placement = new WindowPlacement(100, 50, 1400, 900, Maximized: true);

        Assert.Equal(placement, placement.FitTo([Primary, Secondary], Minimum));
    }

    [Fact]
    public void A_placement_on_a_second_monitor_is_kept_only_while_that_monitor_is_connected()
    {
        var placement = new WindowPlacement(2000, 40, 1000, 700, Maximized: false);

        Assert.Equal(placement, placement.FitTo([Primary, Secondary], Minimum));
        Assert.Null(placement.FitTo([Primary], Minimum));
    }

    [Theory]
    [InlineData(-5000, 100)]
    [InlineData(100, 2000)]
    [InlineData(1870, 100)]
    [InlineData(100, -25)]
    public void A_placement_whose_title_bar_is_not_on_a_monitor_enough_to_grab_it_is_not_restored(double left, double top) =>
        Assert.Null(new WindowPlacement(left, top, 1280, 800, Maximized: false).FitTo([Primary], Minimum));

    [Fact]
    public void A_placement_bigger_than_its_monitor_shrinks_to_it_and_its_title_bar_stays_below_the_top_edge() =>
        Assert.Equal(
            new WindowPlacement(10, 0, 1920, 1040, Maximized: false),
            new WindowPlacement(10, -8, 2500, 1600, Maximized: false).FitTo([Primary], Minimum));

    [Fact]
    public void A_placement_smaller_than_the_minimum_of_the_window_grows_to_it() =>
        Assert.Equal(
            new WindowPlacement(100, 100, 900, 560, Maximized: false),
            new WindowPlacement(100, 100, 300, 200, Maximized: false).FitTo([Primary], Minimum));

    [Fact]
    public void Across_two_monitors_it_fits_to_the_one_with_more_of_its_title_bar()
    {
        var fitted = new WindowPlacement(1800, 100, 1200, 1000, Maximized: false).FitTo([Primary, Secondary], Minimum);

        Assert.Equal(new WindowPlacement(1800, 100, 1200, 984, Maximized: false), fitted);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1280, 800)]
    [InlineData(0, double.PositiveInfinity, 1280, 800)]
    [InlineData(0, 0, 0, 800)]
    [InlineData(0, 0, 1280, -1)]
    public void A_placement_that_is_not_one_is_not_restored(double left, double top, double width, double height)
    {
        var placement = new WindowPlacement(left, top, width, height, Maximized: false);

        Assert.False(placement.IsValid);
        Assert.Null(placement.FitTo([Primary], Minimum));
    }

    [Fact]
    public void Without_monitors_nothing_is_restored() =>
        Assert.Null(new WindowPlacement(100, 50, 1280, 800, Maximized: false).FitTo([], Minimum));
}

/// <summary>The placement in the settings file: a writer of its own, which the other two keep, and tolerant of a hand-edited file.</summary>
public sealed class WindowPlacementSettingsTests
{
    private static readonly WindowPlacement Placement = new(120.5, 60, 1400, 900, Maximized: true);

    [Fact]
    public void SaveWindowPlacement_then_Load_round_trips_it_and_keeps_the_rest()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths, null);
        store.Save(AppSettings.Default.WithProjectOpened(a));
        store.SavePreferences(store.Load() with { CodexQuotaReadEnabled = true });

        store.SaveWindowPlacement(Placement);

        var loaded = new JsonAppSettingsStore(paths, null).Load();
        Assert.Equal(Placement, loaded.MainWindowPlacement);
        Assert.Equal([a], loaded.RecentProjects);
        Assert.True(loaded.CodexQuotaReadEnabled);
    }

    /// <summary>The shell saves the copy it loaded at startup whenever a project opens: neither it nor the panels undo the placement.</summary>
    [Fact]
    public void Save_and_SavePreferences_keep_the_placement_on_disk()
    {
        using var temp = new TempDirectory();
        var b = temp.CreateDirectory("b");
        var store = new JsonAppSettingsStore(new AppDataPaths(temp.Combine("data")), null);
        var loadedAtStartup = store.Load();
        store.SaveWindowPlacement(Placement);

        store.Save(loadedAtStartup.WithProjectOpened(b));
        store.SavePreferences(AppSettings.Default with { IdleLockEnabled = true });

        var loaded = store.Load();
        Assert.Equal(Placement, loaded.MainWindowPlacement);
        Assert.Equal([b], loaded.RecentProjects);
        Assert.True(loaded.IdleLockEnabled);
    }

    /// <summary>A placement that makes no sense in a hand-edited file is none (the window opens centred), and only it: the rest stays.</summary>
    [Theory]
    [InlineData("\"mainWindowPlacement\":\"centro\"")]
    [InlineData("\"mainWindowPlacement\":{\"left\":\"a\",\"top\":0,\"width\":1280,\"height\":800}")]
    [InlineData("\"mainWindowPlacement\":{\"left\":0,\"top\":0,\"width\":1280}")]
    [InlineData("\"mainWindowPlacement\":{\"left\":0,\"top\":0,\"width\":-1280,\"height\":800}")]
    [InlineData("\"mainWindowPlacement\":{\"left\":1e999,\"top\":0,\"width\":1280,\"height\":800}")]
    [InlineData("\"mainWindowPlacement\":[1,2,3,4]")]
    [InlineData("\"mainWindowPlacement\":null")]
    public void Load_a_hand_edited_placement_that_is_not_one_is_none_and_the_rest_stays(string field)
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"lastProjectPath\":\"C:\\\\p1\"," + field + ",\"idleLockEnabled\":true}");

        var settings = new JsonAppSettingsStore(paths, null).Load();

        Assert.Null(settings.MainWindowPlacement);
        Assert.Equal(@"C:\p1", settings.LastProjectPath);
        Assert.True(settings.IdleLockEnabled);
    }

    [Fact]
    public void Load_reads_the_placement_whatever_the_case_of_its_names()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"MainWindowPlacement\":{\"Left\":10,\"TOP\":20,\"width\":1000,\"height\":700,\"Maximized\":true}}");

        Assert.Equal(new WindowPlacement(10, 20, 1000, 700, Maximized: true), new JsonAppSettingsStore(paths, null).Load().MainWindowPlacement);
    }

    [Fact]
    public void A_placement_that_is_not_one_is_saved_as_none_and_the_file_stays_readable()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var paths = new AppDataPaths(temp.Combine("data"));
        var store = new JsonAppSettingsStore(paths, null);
        store.Save(AppSettings.Default.WithProjectOpened(a));

        store.SaveWindowPlacement(new WindowPlacement(double.NaN, 0, 1280, 800, Maximized: false));

        var loaded = new JsonAppSettingsStore(paths, null).Load();
        Assert.Null(loaded.MainWindowPlacement);
        Assert.Equal([a], loaded.RecentProjects);
    }

    [Fact]
    public void A_file_from_before_the_placement_has_none()
    {
        using var temp = new TempDirectory();
        var paths = new AppDataPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{\"lastProjectPath\":\"C:\\\\p1\",\"idleLockEnabled\":true}");

        Assert.Null(new JsonAppSettingsStore(paths, null).Load().MainWindowPlacement);
    }
}

/// <summary>
/// The real MainWindow, never shown, with the settings and fixed monitors: the placement of the last session is restored at
/// startup, and saved on the real exit, never when the window hides in the tray.
/// </summary>
public sealed class MainWindowPlacementTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1040);

    private sealed class FixedScreens(params Rect[] areas) : IScreenWorkAreas
    {
        public IReadOnlyList<Rect> Current(DpiScale dpi) => areas;
    }

    /// <summary>
    /// The monitors of this machine, read with user32 (no Windows Forms, ADR-0036): at least one, with a size, and in
    /// device-independent pixels, so twice the DPI gives half the size.
    /// </summary>
    [Fact]
    public void The_monitors_of_the_system_are_read_and_converted_with_the_dpi_of_the_window()
    {
        var screens = new SystemScreenWorkAreas();

        var at96 = screens.Current(new DpiScale(1, 1));
        var at192 = screens.Current(new DpiScale(2, 2));

        Assert.NotEmpty(at96);
        Assert.All(at96, area => Assert.True(area.Width > 0 && area.Height > 0, $"A work area without size: {area}."));
        Assert.Equal(at96.Count, at192.Count);
        Assert.Equal(at96.Select(a => a.Width / 2), at192.Select(a => a.Width));
    }

    private static MainWindow Create(TempDirectory temp, IAppSettingsStore settings) =>
        new(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Combine("data"))), settings: settings, screens: new FixedScreens(Primary));

    [Fact]
    public void At_startup_the_placement_of_the_last_session_is_restored_maximized_if_it_was()
    {
        using var temp = new TempDirectory();
        var store = new InMemorySettingsStore(AppSettings.Default with { MainWindowPlacement = new WindowPlacement(100, 50, 1000, 700, Maximized: true) });

        var (location, bounds, state) = WpfHost.Run(() =>
        {
            var window = Create(temp, store);
            return (window.WindowStartupLocation, new Rect(window.Left, window.Top, window.Width, window.Height), window.WindowState);
        });

        Assert.Equal(WindowStartupLocation.Manual, location);
        Assert.Equal(new Rect(100, 50, 1000, 700), bounds);
        Assert.Equal(WindowState.Maximized, state);
    }

    [Fact]
    public void At_startup_a_placement_on_a_monitor_that_is_gone_opens_the_window_on_the_primary_work_area_with_a_size_that_fits()
    {
        using var temp = new TempDirectory();
        var store = new InMemorySettingsStore(AppSettings.Default with { MainWindowPlacement = new WindowPlacement(2000, 40, 1000, 700, Maximized: true) });

        var (location, bounds, state, expected) = WpfHost.Run(() =>
        {
            var window = Create(temp, store);
            var first = MainWindow.FirstPlacement(SystemParameters.WorkArea, new Size(1280, 800), new Size(window.MinWidth, window.MinHeight));
            return (window.WindowStartupLocation, new Rect(window.Left, window.Top, window.Width, window.Height), window.WindowState, first);
        });

        // Never «CenterScreen»: it centres on the monitor under the mouse, where the default size may not fit.
        Assert.Equal(WindowStartupLocation.Manual, location);
        Assert.Equal(expected, bounds);
        Assert.Equal(WindowState.Normal, state);
    }

    [Fact]
    public void The_real_exit_saves_where_the_window_is()
    {
        using var temp = new TempDirectory();
        var store = new InMemorySettingsStore();

        var closed = WpfHost.Run(() =>
        {
            var window = Create(temp, store);
            window.Left = 30;
            window.Top = 40;
            window.Width = 1100;
            window.Height = 720;
            var wasClosed = false;
            window.Closed += (_, _) => wasClosed = true;

            // Without the tray, closing the window ends the app.
            window.Close();
            return wasClosed;
        });

        Assert.True(closed);
        Assert.Equal(new WindowPlacement(30, 40, 1100, 720, Maximized: false), store.Current.MainWindowPlacement);
    }

    [Fact]
    public void A_window_closed_where_it_opened_saves_its_first_placement()
    {
        using var temp = new TempDirectory();
        var store = new InMemorySettingsStore();

        var expected = WpfHost.Run(() =>
        {
            var window = Create(temp, store);
            var first = MainWindow.FirstPlacement(SystemParameters.WorkArea, new Size(1280, 800), new Size(window.MinWidth, window.MinHeight));
            window.Close();
            return first;
        });

        Assert.Equal(new WindowPlacement(expected.Left, expected.Top, expected.Width, expected.Height, Maximized: false), store.Current.MainWindowPlacement);
    }

    [Fact]
    public void Hiding_the_window_in_the_tray_saves_nothing()
    {
        using var temp = new TempDirectory();
        var store = new InMemorySettingsStore(AppSettings.Default with { CloseWindow = CloseWindowAction.KeepInTray });
        var shell = new FakeShellWindow();

        var placement = WpfHost.Run(() =>
        {
            var window = Create(temp, store);
            window.Left = 30;
            window.Top = 40;
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), shell: shell);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, store);
            host.Work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });

            // «Seguir en la bandeja»: the window hides, the app and its work go on.
            window.Close();
            WpfHost.FlushBindings();
            return store.Current.MainWindowPlacement;
        });

        Assert.Equal(1, shell.Hidden);
        Assert.Null(placement);
    }

    [Fact]
    public void Closing_through_the_managed_exit_saves_the_placement_once_the_app_really_ends()
    {
        using var temp = new TempDirectory();
        var store = new InMemorySettingsStore();
        var shutdown = new FakeAppShutdown();

        var (decided, ended, size) = WpfHost.Run(() =>
        {
            var window = Create(temp, store);
            window.Left = 30;
            window.Top = 40;
            var openedWith = new Size(window.Width, window.Height);
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), prompt: new FakeExitPrompt(ExitChoice.ExitNow), shutdown: shutdown);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, store);

            // Without work, the managed exit ends the app (the double does not end the test host); ending it closes the window.
            window.Close();
            WpfHost.FlushBindings();
            var placementWhenDecided = store.Current.MainWindowPlacement;
            window.Close();
            return (placementWhenDecided, store.Current.MainWindowPlacement, openedWith);
        });

        Assert.Equal(1, shutdown.Calls);
        Assert.Null(decided);
        Assert.Equal(new WindowPlacement(30, 40, size.Width, size.Height, Maximized: false), ended);
    }
}
