using AgentStudio.Application.Decisions;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Tests.Desktop.Background;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The unattended work put together on the shell, as App starts it once the window is shown (<see cref="UnattendedShell"/>):
/// the tray over the window and its view model, «Bloquear» locking the interface, the settings read again when saved, and the
/// consented resumes suspended while the app leaves and for good once the session ends. The composition of the app over
/// temporary folders and the doubles of <see cref="Phase8Doubles"/>; the real window, never shown.
/// </summary>
public sealed class UnattendedShellTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Started_the_icon_is_up_its_menu_opens_the_recent_projects_and_bloquear_locks_the_interface()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("tienda");
        var doubles = new Phase8Doubles();
        await using var provider = doubles.BuildApp(temp);
        provider.GetRequiredService<IAppSettingsStore>().Save(AppSettings.Default.WithProjectOpened(project));

        var seen = await WpfHost.RunAsync(async () =>
        {
            var window = Window(provider, temp);
            using var shell = UnattendedShell.Start(provider, window);
            var menu = doubles.Tray.OpenMenu();
            var recents = Menus.Find(menu, "Abrir proyecto").Children!.Select(c => c.Text).ToList();

            await Menus.Choose(menu, "Bloquear");
            WpfHost.FlushBindings();
            var locked = (window.ShowsLockScreen, shell.Background.Tray.IsLocked, shell.Background.Panel.IsLocked, doubles.Tray.ToolTip);

            await provider.GetRequiredService<InterfaceLock>().UnlockWithHelloAsync(Ct);
            WpfHost.FlushBindings();
            return (doubles.Tray.Shown, recents, locked, (window.ShowsLockScreen, shell.Background.Tray.IsLocked));
        });

        Assert.True(seen.Shown, "The icon goes to the notification area (here, its double).");
        Assert.Equal(["tienda", string.Empty, "Abrir carpeta…"], seen.recents);
        Assert.True(seen.locked.ShowsLockScreen);
        Assert.True(seen.locked.Item2);
        Assert.True(seen.locked.Item3);
        Assert.Contains("bloqueado", seen.locked.ToolTip, StringComparison.Ordinal);
        Assert.Equal(0, doubles.Session.Locks);
        Assert.Equal((false, false), seen.Item4);
    }

    [Fact]
    public async Task Saving_the_unattended_settings_reads_keeping_awake_again()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        await using var provider = doubles.BuildApp(temp);

        var (before, after, stored) = WpfHost.Run(() =>
        {
            using var shell = UnattendedShell.Start(provider, Window(provider, temp));
            shell.Background.Work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
            var held = doubles.KeepAwake.Held;

            // «Trabajo desatendido», saved by the «Guardar» of «Ajustes».
            var settings = provider.GetRequiredService<AgentStudio.Desktop.Settings.SettingsDocumentViewModel>();
            settings.Unattended!.PreventSleepWhileWorking = true;
            settings.Unattended.Choose(CloseWindowAction.KeepInTray);
            settings.SaveCommand.Execute(null);
            return (held, doubles.KeepAwake.Held, provider.GetRequiredService<IAppSettingsStore>().Load());
        });

        Assert.Equal(0, before);
        Assert.Equal(1, after);
        Assert.True(stored.PreventSleepWhileWorking);
        Assert.Equal(CloseWindowAction.KeepInTray, stored.CloseWindow);
    }

    [Fact]
    public async Task The_managed_exit_of_the_shell_suspends_the_consented_resumes_while_the_app_leaves_and_for_good_when_the_session_ends()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        doubles.Prompt.Answer = ExitChoice.PauseAndExitWhenIdle;
        await using var provider = doubles.BuildApp(temp);

        var (idle, waiting, back, ended, recorded) = await WpfHost.RunAsync(async () =>
        {
            var shell = UnattendedShell.Start(provider, Window(provider, temp));
            var exit = shell.Background.Exit;
            shell.Background.Work.Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });
            var beforeExit = exit.SuspendsResumes;

            await exit.ExitAsync();
            var whileWaiting = exit.SuspendsResumes;

            exit.CancelPendingExit();
            var afterNoSalir = exit.SuspendsResumes;

            shell.SessionEnding();
            var afterSession = exit.SuspendsResumes;
            shell.Dispose();
            return (beforeExit, whileWaiting, afterNoSalir, afterSession, doubles.ExitRecorder.Recorded.Select(r => r.Choice).ToList());
        });

        Assert.False(idle);
        Assert.True(waiting, "Waiting to exit, the consented resumes stay suspended.");
        Assert.False(back, "«No salir» releases the suspension.");
        Assert.True(ended, "The end of the session suspends them for good.");

        // «Pausar y salir…» was the person's choice in this session of the app: the end of the session does not write over it.
        Assert.Equal([RunExitChoice.PauseAndExitWhenIdle], recorded);
    }

    [Fact]
    public async Task The_consented_resumes_run_only_once_the_shell_started_its_managed_exit()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        await using var provider = doubles.BuildApp(temp);
        var resumes = provider.GetRequiredService<ConsentedResumes>();

        var (before, after) = WpfHost.Run(() =>
        {
            var heldBefore = resumes.IsHeld;
            using var shell = UnattendedShell.Start(provider, Window(provider, temp));
            return (heldBefore, resumes.IsHeld);
        });

        Assert.True(before, "Before the tray, no pass runs: a close would write no choice the scheduler could read.");
        Assert.False(after);
    }

    [Fact]
    public async Task A_failure_once_the_icon_is_up_leaves_the_app_as_it_is_without_its_tray()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        await using var provider = doubles.BuildApp(temp, services =>
            services.AddSingleton<IdleLockMonitor>(_ => throw new InvalidOperationException("The idle input could not be read.")));

        var (thrown, closed, held) = WpfHost.Run(() =>
        {
            var window = Window(provider, temp);
            var closings = 0;
            window.Closed += (_, _) => closings++;
            var failure = Record.Exception(() => UnattendedShell.Start(provider, window));
            provider.GetRequiredService<BackgroundWorkRegistry>().Track(new FakeWork(BackgroundWorkKind.Run, ProjectA, "20261021-aaaaaa") { AttemptsInFlight = 1 });

            // Without its tray the window closes the app, as before Phase 8: no question of the managed exit.
            window.Close();
            return (failure, closings, provider.GetRequiredService<ConsentedResumes>().IsHeld);
        });

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.True(doubles.Tray.Disposed, "The icon left the notification area.");
        Assert.Equal(1, closed);
        Assert.Empty(doubles.Prompt.Asked);
        Assert.Equal(0, doubles.Shutdown.Calls);
        Assert.True(held, "No consented resume runs without the managed exit.");
    }

    /// <summary>The real main window of the composition, never shown, with its floating panes and its list of files as doubles.</summary>
    private static MainWindow Window(IServiceProvider provider, TempDirectory temp) => new(
        provider.GetRequiredService<MainViewModel>(),
        new FileLayoutStore(new AppDataPaths(temp.Combine("layouts"))),
        quickOpenPresenter: new MainWindowLockTests.RecordingPresenter(),
        lockScreen: provider.GetRequiredService<LockScreenViewModel>(),
        interfaceLock: provider.GetRequiredService<IInterfaceLock>(),
        floatingPanes: new MainWindowLockTests.CountingPanes());
}
