using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Services;
using AgentStudio.Tests.Desktop.Background;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// Closing the main window once the background is attached (docs/16 D-14, F8-7; ADR-0036, decision 2): the close is cancelled
/// and the managed exit decides with the setting <see cref="AppSettings.CloseWindow"/>; while the app is exiting it is not
/// intercepted, and only then are the layout saved and the terminal released. The real window, never shown; the background
/// over doubles, so nothing is hidden, asked or ended for real.
/// </summary>
public sealed class MainWindowClosingTests
{
    [Fact]
    public void Kept_in_the_tray_closing_hides_the_window_and_writes_nothing()
    {
        using var temp = new TempDirectory();
        var shell = new FakeShellWindow();
        var shutdown = new FakeAppShutdown();
        var recorder = new FakeRunExitRecorder();

        var (closed, saved) = WpfHost.Run(() =>
        {
            var store = new FileLayoutStore(new AppDataPaths(temp.Combine("data")));
            var vm = ShellFactory.CreateMain();
            vm.ProjectPath = temp.CreateDirectory("proyecto");
            var window = new MainWindow(vm, store);
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), shell: shell, shutdown: shutdown, recorder: recorder);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, new InMemorySettingsStore(AppSettings.Default with { CloseWindow = CloseWindowAction.KeepInTray }));
            var wasClosed = false;
            window.Closed += (_, _) => wasClosed = true;
            host.Work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });

            window.Close();
            WpfHost.FlushBindings();
            return (wasClosed, store.Load(vm.ProjectPath) is not null);
        });

        Assert.False(closed, "The window is never closed by the person: it hides, and the app lives in the tray.");
        Assert.Equal((1, 0), (shell.Hidden, shutdown.Calls));
        Assert.Empty(recorder.Recorded);
        Assert.False(saved, "The layout is saved when the app really ends, not when the window hides.");
    }

    [Fact]
    public void Asking_with_work_going_on_offers_the_tray_and_keeping_it_there_hides_the_window()
    {
        using var temp = new TempDirectory();
        var shell = new FakeShellWindow();
        var prompt = new FakeExitPrompt(ExitChoice.KeepInTray);

        var closed = WpfHost.Run(() =>
        {
            var window = new MainWindow(ShellFactory.CreateMain(), new FileLayoutStore(new AppDataPaths(temp.Combine("data"))));
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), prompt: prompt, shell: shell);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, new InMemorySettingsStore());
            var wasClosed = false;
            window.Closed += (_, _) => wasClosed = true;
            host.Work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });

            window.Close();
            WpfHost.FlushBindings();
            return wasClosed;
        });

        var asked = Assert.Single(prompt.Asked);
        Assert.True(asked.OffersKeepInTray, "Closing the window offers «Seguir en la bandeja».");
        Assert.False(closed);
        Assert.Equal(1, shell.Hidden);
    }

    [Fact]
    public void Without_work_closing_exits_through_the_managed_exit_and_then_the_window_closes_saving_its_layout()
    {
        using var temp = new TempDirectory();
        var shutdown = new FakeAppShutdown();
        var prompt = new FakeExitPrompt(ExitChoice.ExitNow);

        var (afterFirst, exiting, afterSecond, saved) = WpfHost.Run(() =>
        {
            var store = new FileLayoutStore(new AppDataPaths(temp.Combine("data")));
            var vm = ShellFactory.CreateMain();
            vm.ProjectPath = temp.CreateDirectory("proyecto");
            var window = new MainWindow(vm, store);
            using var services = MainWindowLockTests.BackgroundServices(new FakeTrayIcon(), prompt: prompt, shutdown: shutdown);
            using var host = BackgroundHost.Create(services, BackgroundShell.None);
            window.AttachBackground(host, new InMemorySettingsStore());
            var wasClosed = false;
            window.Closed += (_, _) => wasClosed = true;

            // «Preguntar» without work: nothing is asked, and the app exits (the double does not end the test host).
            window.Close();
            WpfHost.FlushBindings();
            var first = wasClosed;

            // Ending the app closes its windows: now the close is not intercepted.
            window.Close();
            return (first, host.Exit.IsExiting, wasClosed, store.Load(vm.ProjectPath) is not null);
        });

        Assert.False(afterFirst, "The first close is the managed exit's to decide.");
        Assert.Empty(prompt.Asked);
        Assert.Equal(1, shutdown.Calls);
        Assert.True(exiting);
        Assert.True(afterSecond);
        Assert.True(saved, "The layout of the project is saved on the real exit.");
    }

    [Fact]
    public void Before_the_background_exists_the_window_closes_as_before_phase_8()
    {
        using var temp = new TempDirectory();

        var (closed, saved) = WpfHost.Run(() =>
        {
            var store = new FileLayoutStore(new AppDataPaths(temp.Combine("data")));
            var vm = ShellFactory.CreateMain();
            vm.ProjectPath = temp.CreateDirectory("proyecto");
            var window = new MainWindow(vm, store);
            var wasClosed = false;
            window.Closed += (_, _) => wasClosed = true;

            window.Close();
            return (wasClosed, store.Load(vm.ProjectPath) is not null);
        });

        Assert.True(closed);
        Assert.True(saved);
    }
}
